using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Sim;

namespace Yaat.Client.Services;

/// <summary>The authenticated VATSIM identity resolved by the server from a YAAT session token.</summary>
public sealed record VatsimIdentity(string Cid, string Name, string Rating, string? Subdivision, bool IsMentor);

/// <summary>
/// Client side of the server-mediated VATSIM Connect flow. Drives the system-browser + loopback
/// handoff against a server's <c>/auth/vatsim/start</c>, falls back to the dev token issuer when a
/// server doesn't require auth, refreshes the short-lived access token, and persists per-server
/// sessions so the controller doesn't re-login every launch. Tokens are server-scoped because each
/// deployed server is its own VATSIM client with its own signing key.
/// </summary>
public sealed class VatsimAuthClient
{
    private readonly ILogger _log = AppLog.CreateLogger<VatsimAuthClient>();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly object _gate = new();
    private readonly Dictionary<string, StoredSession> _sessions;
    private readonly string _sessionFilePath = YaatPaths.Combine("auth-sessions.json");

    public VatsimAuthClient()
    {
        _sessions = LoadSessions();
    }

    /// <summary>
    /// Ensures a valid session for the server, running the VATSIM login (or dev token issuance) if
    /// needed. Returns the resolved identity, or null if login failed or was cancelled.
    /// </summary>
    public async Task<VatsimIdentity?> EnsureSignedInAsync(string serverUrl, CancellationToken ct)
    {
        var key = NormalizeServer(serverUrl);

        // Reuse a stored session if its refresh token still mints an access token.
        if (TryGetSession(key, out var existing) && await RefreshAsync(key, existing.RefreshToken, ct) is { } refreshed)
        {
            return ToIdentity(refreshed);
        }

        var required = await IsAuthRequiredAsync(key, ct);
        var session = required ? await BrowserLoginAsync(key, ct) : await DevLoginAsync(key, ct);
        if (session is null)
        {
            return null;
        }

        Store(key, session);
        return ToIdentity(session);
    }

    /// <summary>
    /// Returns a valid access token for the server, refreshing it when close to expiry, or null when
    /// there is no usable session (the caller should re-run <see cref="EnsureSignedInAsync"/>).
    /// </summary>
    public async Task<string?> GetValidAccessTokenAsync(string serverUrl)
    {
        var key = NormalizeServer(serverUrl);
        if (!TryGetSession(key, out var session))
        {
            return null;
        }

        if (DateTime.UtcNow < session.AccessExpiresUtc - TimeSpan.FromMinutes(2))
        {
            return session.AccessToken;
        }

        var refreshed = await RefreshAsync(key, session.RefreshToken, CancellationToken.None);
        return refreshed?.AccessToken;
    }

    public VatsimIdentity? GetIdentity(string serverUrl)
    {
        return TryGetSession(NormalizeServer(serverUrl), out var session) ? ToIdentity(session) : null;
    }

    public void SignOut(string serverUrl)
    {
        var key = NormalizeServer(serverUrl);
        string? refreshToken = null;
        lock (_gate)
        {
            if (_sessions.TryGetValue(key, out var session))
            {
                refreshToken = session.RefreshToken;
            }

            _sessions.Remove(key);
            SaveSessions();
        }

        if (!string.IsNullOrEmpty(refreshToken))
        {
            _ = RevokeRefreshTokenAsync(key, refreshToken);
        }
    }

    // Best-effort server-side revocation so a sign-out invalidates the refresh token network-side, not
    // just locally. Failures are swallowed — the local session has already been cleared.
    private async Task RevokeRefreshTokenAsync(string serverUrl, string refreshToken)
    {
        try
        {
            using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["refreshToken"] = refreshToken });
            using var response = await _http.PostAsync($"{serverUrl}/auth/logout", content, CancellationToken.None);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _log.LogWarning(ex, "Server-side logout failed for {Server}", serverUrl);
        }
    }

    private async Task<bool> IsAuthRequiredAsync(string serverUrl, CancellationToken ct)
    {
        try
        {
            using var doc = await GetJsonAsync($"{serverUrl}/auth/required", ct);
            return doc is not null && doc.RootElement.TryGetProperty("required", out var r) && r.ValueKind == JsonValueKind.True;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // Default to requiring auth — fail secure if the probe fails.
            _log.LogWarning(ex, "auth/required probe failed for {Server}; assuming auth is required", serverUrl);
            return true;
        }
    }

    private async Task<StoredSession?> BrowserLoginAsync(string serverUrl, CancellationToken ct)
    {
        var port = PickFreeLoopbackPort();
        var returnUrl = $"http://127.0.0.1:{port}/cb";

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        var startUrl = $"{serverUrl}/auth/vatsim/start?return={Uri.EscapeDataString(returnUrl)}";
        _log.LogInformation("Opening VATSIM login for {Server}", serverUrl);
        Process.Start(new ProcessStartInfo { FileName = startUrl, UseShellExecute = true });

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(5));

            var contextTask = listener.GetContextAsync();
            var completed = await Task.WhenAny(contextTask, Task.Delay(Timeout.Infinite, timeoutCts.Token));
            if (completed != contextTask)
            {
                _log.LogWarning("VATSIM login timed out or was cancelled for {Server}", serverUrl);
                return null;
            }

            var context = await contextTask;
            var query = context.Request.QueryString;
            var code = query["code"];
            await RespondAndCloseAsync(context, !string.IsNullOrEmpty(code));

            if (query["error"] is { } error)
            {
                _log.LogWarning("VATSIM login returned error '{Error}' for {Server}", error, serverUrl);
                return null;
            }

            if (string.IsNullOrEmpty(code))
            {
                _log.LogWarning("VATSIM login returned no exchange code for {Server}", serverUrl);
                return null;
            }

            return await ExchangeCodeAsync(serverUrl, code, ct);
        }
        finally
        {
            listener.Stop();
        }
    }

    // Trades the single-use loopback exchange code for the actual session tokens over a POST, so the
    // access/refresh tokens never travel in the loopback redirect URL.
    private async Task<StoredSession?> ExchangeCodeAsync(string serverUrl, string code, CancellationToken ct)
    {
        try
        {
            using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["code"] = code });
            using var response = await _http.PostAsync($"{serverUrl}/auth/exchange", content, ct);
            if (!response.IsSuccessStatusCode)
            {
                _log.LogWarning("Token exchange failed with status {Status} for {Server}", (int)response.StatusCode, serverUrl);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            return SessionFromJson(doc.RootElement);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _log.LogWarning(ex, "Token exchange failed for {Server}", serverUrl);
            return null;
        }
    }

    private async Task<StoredSession?> DevLoginAsync(string serverUrl, CancellationToken ct)
    {
        try
        {
            using var content = new StringContent("");
            using var response = await _http.PostAsync($"{serverUrl}/auth/dev", content, ct);
            if (!response.IsSuccessStatusCode)
            {
                _log.LogWarning("Dev token request failed with status {Status} for {Server}", (int)response.StatusCode, serverUrl);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            return SessionFromJson(doc.RootElement);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _log.LogWarning(ex, "Dev login failed for {Server}", serverUrl);
            return null;
        }
    }

    private async Task<StoredSession?> RefreshAsync(string serverUrl, string refreshToken, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(refreshToken))
        {
            return null;
        }

        try
        {
            using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["refreshToken"] = refreshToken });
            using var response = await _http.PostAsync($"{serverUrl}/auth/refresh", content, ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (!doc.RootElement.TryGetProperty("accessToken", out var at) || at.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var accessToken = at.GetString()!;

            // The server rotates the refresh token on each refresh and revokes the one just used, so we must
            // store the new refresh token or the next refresh will be rejected.
            var newRefreshToken =
                doc.RootElement.TryGetProperty("refreshToken", out var rt) && rt.ValueKind == JsonValueKind.String ? rt.GetString()! : refreshToken;

            StoredSession? updated = null;
            lock (_gate)
            {
                if (_sessions.TryGetValue(serverUrl, out var session))
                {
                    updated = session with { AccessToken = accessToken, AccessExpiresUtc = ReadExpiry(accessToken), RefreshToken = newRefreshToken };
                    _sessions[serverUrl] = updated;
                    SaveSessions();
                }
            }

            return updated;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _log.LogWarning(ex, "Token refresh failed for {Server}", serverUrl);
            return null;
        }
    }

    private static StoredSession? SessionFromJson(JsonElement root)
    {
        var accessToken = ReadString(root, "accessToken");
        var refreshToken = ReadString(root, "refreshToken");
        var cid = ReadString(root, "cid");
        if (accessToken is null || refreshToken is null || cid is null)
        {
            return null;
        }

        return new StoredSession(
            accessToken,
            refreshToken,
            ReadExpiry(accessToken),
            cid,
            ReadString(root, "name") ?? "",
            ReadString(root, "rating") ?? "",
            ReadString(root, "subdivision"),
            root.TryGetProperty("isMentor", out var m) && m.ValueKind == JsonValueKind.True
        );
    }

    private static async Task RespondAndCloseAsync(HttpListenerContext context, bool success)
    {
        var body = success
            ? "<html><body style='font-family:sans-serif;background:#252529;color:#ddd'><h2>YAAT sign-in complete</h2><p>You can close this tab and return to YAAT.</p></body></html>"
            : "<html><body style='font-family:sans-serif;background:#252529;color:#ddd'><h2>YAAT sign-in failed</h2><p>Return to YAAT and try again.</p></body></html>";
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Response.ContentType = "text/html";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    private async Task<JsonDocument?> GetJsonAsync(string url, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    // Decodes the JWT 'exp' (unix seconds) without validating the signature — the client only needs to
    // know when to refresh; the server is authoritative on validity.
    private static DateTime ReadExpiry(string jwt)
    {
        try
        {
            var parts = jwt.Split('.');
            if (parts.Length < 2)
            {
                return DateTime.UtcNow;
            }

            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = (payload.Length % 4) switch
            {
                2 => payload + "==",
                3 => payload + "=",
                _ => payload,
            };

            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            if (doc.RootElement.TryGetProperty("exp", out var exp) && exp.TryGetInt64(out var seconds))
            {
                return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
            }
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            // Unparseable token — treat as already expired so the next call refreshes.
        }

        return DateTime.UtcNow;
    }

    private static int PickFreeLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static string NormalizeServer(string serverUrl) => serverUrl.TrimEnd('/');

    private static VatsimIdentity ToIdentity(StoredSession s) => new(s.Cid, s.Name, s.Rating, s.Subdivision, s.IsMentor);

    private bool TryGetSession(string key, out StoredSession session)
    {
        lock (_gate)
        {
            return _sessions.TryGetValue(key, out session!);
        }
    }

    private void Store(string key, StoredSession session)
    {
        lock (_gate)
        {
            _sessions[key] = session;
            SaveSessions();
        }
    }

    private Dictionary<string, StoredSession> LoadSessions()
    {
        try
        {
            if (File.Exists(_sessionFilePath))
            {
                var json = Encoding.UTF8.GetString(Unprotect(File.ReadAllBytes(_sessionFilePath)));
                var loaded = JsonSerializer.Deserialize<Dictionary<string, StoredSession>>(json);
                if (loaded is not null)
                {
                    return new Dictionary<string, StoredSession>(loaded, StringComparer.OrdinalIgnoreCase);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or CryptographicException)
        {
            _log.LogWarning(ex, "Failed to load saved auth sessions; starting fresh");
        }

        return new Dictionary<string, StoredSession>(StringComparer.OrdinalIgnoreCase);
    }

    private void SaveSessions()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_sessionFilePath)!);
            File.WriteAllBytes(_sessionFilePath, Protect(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(_sessions))));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            _log.LogWarning(ex, "Failed to persist auth sessions");
        }
    }

    // Session tokens are encrypted at rest with Windows DPAPI (per-user scope). DPAPI is unavailable on
    // non-Windows platforms, so there the file is written as-is — it still lives under the user's profile
    // directory, readable only by that user.
    private static byte[] Protect(byte[] plaintext) =>
        OperatingSystem.IsWindows() ? ProtectedData.Protect(plaintext, optionalEntropy: null, DataProtectionScope.CurrentUser) : plaintext;

    private static byte[] Unprotect(byte[] stored) =>
        OperatingSystem.IsWindows() ? ProtectedData.Unprotect(stored, optionalEntropy: null, DataProtectionScope.CurrentUser) : stored;

    private static string? ReadString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private sealed record StoredSession(
        string AccessToken,
        string RefreshToken,
        DateTime AccessExpiresUtc,
        string Cid,
        string Name,
        string Rating,
        string? Subdivision,
        bool IsMentor
    );
}
