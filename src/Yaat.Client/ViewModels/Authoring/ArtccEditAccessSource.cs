using Yaat.Client.Services;

namespace Yaat.Client.ViewModels.Authoring;

/// <summary>
/// The ARTCCs the signed-in user may edit, through <see cref="ArtccEditAccess"/>: asked of the server while connected
/// (and cached with the session), else the cached list, else the Settings ARTCC, else none.
/// </summary>
/// <param name="access">The access list and its cache.</param>
/// <param name="isConnected">Whether the hub connection is up now.</param>
/// <param name="serverUrl">The server whose session the list belongs to: the connected one, else the last used.</param>
/// <param name="fetchFromHub">Asks the hub for the list (normally <see cref="ServerConnection.GetMyEditableArtccsAsync"/>).</param>
public sealed class ArtccEditAccessSource(
    ArtccEditAccess access,
    Func<bool> isConnected,
    Func<string> serverUrl,
    Func<CancellationToken, Task<List<string>>> fetchFromHub
) : IArtccAccessSource
{
    private readonly ArtccEditAccess _access = access ?? throw new ArgumentNullException(nameof(access));
    private readonly Func<bool> _isConnected = isConnected ?? throw new ArgumentNullException(nameof(isConnected));
    private readonly Func<string> _serverUrl = serverUrl ?? throw new ArgumentNullException(nameof(serverUrl));
    private readonly Func<CancellationToken, Task<List<string>>> _fetchFromHub =
        fetchFromHub ?? throw new ArgumentNullException(nameof(fetchFromHub));

    /// <inheritdoc/>
    public Task<IReadOnlyList<string>> GetEditableArtccsAsync(CancellationToken ct) =>
        _isConnected() ? _access.RefreshAsync(_serverUrl(), _fetchFromHub, ct) : Task.FromResult(_access.GetOffline(_serverUrl()));
}
