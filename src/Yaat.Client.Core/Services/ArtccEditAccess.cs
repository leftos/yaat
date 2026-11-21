using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;

namespace Yaat.Client.Services;

/// <summary>
/// The ARTCCs the signed-in user may edit scenarios for. Online, the server's answer (<see cref="ServerConnection.GetMyEditableArtccsAsync"/>)
/// is cached with the stored session in <see cref="VatsimAuthClient"/>, so it survives a restart; offline, that cached list
/// stands in, else the Settings ARTCC. A UI filter only: vNAS enforces on upload.
/// </summary>
/// <param name="auth">The session store the list is cached in.</param>
/// <param name="settingsArtcc">The Settings ARTCC (<c>UserPreferences.ArtccId</c>), read on each offline answer.</param>
public sealed class ArtccEditAccess(VatsimAuthClient auth, Func<string?> settingsArtcc)
{
    private static readonly ILogger Log = AppLog.CreateLogger("ArtccEditAccess");

    private readonly VatsimAuthClient _auth = auth;
    private readonly Func<string?> _settingsArtcc = settingsArtcc;

    /// <summary>
    /// Asks the server through <paramref name="fetchFromHub"/> (normally <see cref="ServerConnection.GetMyEditableArtccsAsync"/>)
    /// and returns its answer, caching it with the session for <paramref name="serverUrl"/> only while the CID signed in before
    /// the call is still the one signed in. When the call fails (not connected, a server without the call, or a connection
    /// dropped mid-call) returns <see cref="GetOffline"/> instead; a cancelled <paramref name="ct"/> propagates.
    /// </summary>
    public async Task<IReadOnlyList<string>> RefreshAsync(
        string serverUrl,
        Func<CancellationToken, Task<List<string>>> fetchFromHub,
        CancellationToken ct
    )
    {
        string? cid = _auth.GetIdentity(serverUrl)?.Cid;
        List<string> artccs;
        try
        {
            artccs = await fetchFromHub(ct);
        }
        catch (Exception ex) when (IsConnectionFailure(ex, ct))
        {
            Log.LogWarning(ex, "Editable ARTCCs unavailable from the server; using the cached list or the Settings ARTCC");
            return GetOffline(serverUrl);
        }

        if (cid is not null)
        {
            _auth.SetEditableArtccs(serverUrl, cid, artccs);
        }

        return artccs;
    }

    /// <summary>The list last cached for <paramref name="serverUrl"/>'s session, else the Settings ARTCC, else none.</summary>
    public IReadOnlyList<string> GetOffline(string serverUrl)
    {
        if (_auth.GetEditableArtccs(serverUrl) is { } cached)
        {
            return cached;
        }

        string? artcc = _settingsArtcc();
        return string.IsNullOrWhiteSpace(artcc) ? [] : [artcc.Trim().ToUpperInvariant()];
    }

    // A failed hub call rather than the caller giving up: the SignalR client fails a pending call with a cancellation when
    // its connection closes, so a cancellation counts as a failure unless the caller's own token asked for it.
    private static bool IsConnectionFailure(Exception ex, CancellationToken ct) =>
        ex is HubException or InvalidOperationException or IOException || ((ex is OperationCanceledException) && !ct.IsCancellationRequested);
}
