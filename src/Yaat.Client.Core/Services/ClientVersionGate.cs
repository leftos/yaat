using System.Net.Http;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;

namespace Yaat.Client.Services;

/// <summary>What a server's version gate says about the running build.</summary>
/// <param name="IsBlocked">The server refuses this client; <paramref name="Message" /> says why.</param>
/// <param name="IsUpdateRecommended">The client may connect but is behind the recommended version.</param>
/// <param name="Message">User-facing text, or null when the client is current.</param>
public record ClientVersionVerdict(bool IsBlocked, bool IsUpdateRecommended, string? Message)
{
    public static ClientVersionVerdict Ok { get; } = new(false, false, null);
}

/// <summary>
/// Asks a server which client versions it accepts, before connecting to it.
/// </summary>
/// <remarks>
/// Runs against the plain HTTP endpoint rather than the hub because the clients worth warning are
/// exactly those a hub payload would break — the check has to complete before the SignalR
/// handshake and before VATSIM sign-in.
/// <para>
/// Every failure path returns <see cref="ClientVersionVerdict.Ok" />. An unreachable or older
/// server, a malformed response, or an unreadable version must not be the thing that stops someone
/// connecting: if the server is genuinely incompatible the connection attempt reports that on its
/// own, whereas a false block leaves the user with no way forward.
/// </para>
/// </remarks>
public sealed class ClientVersionGate
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly ILogger _log = AppLog.CreateLogger<ClientVersionGate>();

    public async Task<ClientVersionVerdict> EvaluateAsync(string serverUrl, string clientVersion, CancellationToken ct)
    {
        var requirements = await FetchAsync(serverUrl, ct);
        if (requirements is null)
        {
            return ClientVersionVerdict.Ok;
        }

        if (Yaat.Sim.ClientVersions.IsOlderThan(clientVersion, requirements.MinimumClientVersion))
        {
            _log.LogWarning(
                "Server {Url} requires client {Minimum}; this build is {Current} — refusing to connect",
                serverUrl,
                requirements.MinimumClientVersion,
                clientVersion
            );
            return new ClientVersionVerdict(
                IsBlocked: true,
                IsUpdateRecommended: false,
                $"This server requires YAAT {requirements.MinimumClientVersion} or newer — you're running {clientVersion}. Update, then reconnect."
            );
        }

        if (Yaat.Sim.ClientVersions.IsOlderThan(clientVersion, requirements.RecommendedClientVersion))
        {
            return new ClientVersionVerdict(
                IsBlocked: false,
                IsUpdateRecommended: true,
                $"This server recommends YAAT {requirements.RecommendedClientVersion} — you're running {clientVersion}."
            );
        }

        return ClientVersionVerdict.Ok;
    }

    private async Task<ClientRequirementsDto?> FetchAsync(string serverUrl, CancellationToken ct)
    {
        try
        {
            var url = $"{serverUrl.TrimEnd('/')}/api/client-requirements";
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            return await _http.GetFromJsonAsync(url, YaatHubJsonContext.Default.ClientRequirementsDto, cts.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A server that predates the endpoint answers with the /api catch-all's empty array,
            // which fails to deserialize here — indistinguishable from being unreachable, and
            // treated the same way: let the connection attempt speak for itself.
            _log.LogDebug(ex, "Could not read the client-version gate from {Url}", serverUrl);
            return null;
        }
    }
}

/// <summary>
/// The server's client-version gate. Mirrors the server record; append-only on both sides, since a
/// client that cannot read it cannot learn why it was refused.
/// </summary>
public record ClientRequirementsDto(string MinimumClientVersion, string RecommendedClientVersion);
