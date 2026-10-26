using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Data.Airport;

/// <summary>
/// Downloads the tower-cab videomap GeoJSON a vNAS ARTCC configuration publishes for an airport
/// (<c>{base}/{artccId}/{videoMapId}.geojson</c>) and caches it on disk under
/// <c>%LOCALAPPDATA%/yaat/cache/towercab-maps/{artccId}/{videoMapId}.geojson</c>. One download path
/// for every consumer of a tower-cab map, so the server's layout build and the client's ground view
/// read the same file.
///
/// The maps are served with <c>Last-Modified</c>, so the refresh is a conditional HEAD
/// (<see cref="HttpCacheFreshness.HeadLastModified"/>); a network failure falls back to the on-disk
/// copy. With an empty base URL — the vNAS config fetch failed, so no URL is known — no request is
/// made: the cached copy is served when one exists, otherwise there is no map.
/// </summary>
public sealed class TowerCabMapDownloader : IDisposable
{
    private static readonly ILogger Log = SimLog.CreateLogger<TowerCabMapDownloader>();

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string _cacheRoot;

    public TowerCabMapDownloader(HttpClient? http, string? cacheRoot)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _ownsHttp = http is null;
        _cacheRoot = cacheRoot ?? YaatPaths.Combine("cache", "towercab-maps");
    }

    /// <summary>
    /// Returns the cache path for <paramref name="artccId"/>'s <paramref name="videoMapId"/>, whether or
    /// not the file exists yet.
    /// </summary>
    public string GetCachePath(string artccId, string videoMapId) => Path.Combine(_cacheRoot, artccId, $"{videoMapId}.geojson");

    /// <summary>
    /// Fetches the tower-cab map, refreshing the cache when vNAS serves a newer copy. A network failure
    /// returns the cached copy with <see cref="HttpCacheResult.RefreshFailed"/> set; an empty
    /// <paramref name="baseUrl"/> skips the network entirely and serves the cached copy, or null content
    /// when nothing is cached.
    /// </summary>
    public async Task<HttpCacheResult> FetchAsync(string baseUrl, string artccId, string videoMapId, CancellationToken cancellationToken)
    {
        string cachePath = GetCachePath(artccId, videoMapId);

        if (string.IsNullOrEmpty(baseUrl))
        {
            string? cached = File.Exists(cachePath) ? await File.ReadAllTextAsync(cachePath, cancellationToken) : null;
            Log.LogWarning(
                "No vNAS video map base URL; {CacheState} for tower cab map {ArtccId}/{VideoMapId}",
                cached is null ? "nothing is cached" : "serving the cached copy",
                artccId,
                videoMapId
            );

            return new HttpCacheResult(cached, NotFound: false, RefreshFailed: true);
        }

        string url = $"{baseUrl.TrimEnd('/')}/{artccId}/{videoMapId}.geojson";
        return await HttpFileCache.GetOrRefreshAsync(
            _http,
            url,
            cachePath,
            HttpCacheFreshness.HeadLastModified,
            diskTtl: null,
            Log,
            cancellationToken
        );
    }

    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }
}
