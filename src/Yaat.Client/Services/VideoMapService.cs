using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Sim;
using Yaat.Sim.Data;

namespace Yaat.Client.Services;

/// <summary>
/// Downloads, caches, and parses video map GeoJSON files from the
/// vNAS data API. Maps are cached to %LOCALAPPDATA%/yaat/cache/videomaps/.
/// The parsed in-memory cache carries a <see cref="CacheTtl"/> so a map updated on vNAS is
/// re-checked (via the disk HEAD/Last-Modified freshness logic) instead of being pinned for the
/// client process lifetime.
/// </summary>
public sealed class VideoMapService
{
    private const string DataApiBase = "https://data-api.vnas.vatsim.net/Files/VideoMaps";

    private static readonly string CacheDir = YaatPaths.Combine("cache", "videomaps");

    /// <summary>How long a parsed map is served from memory before the next load re-checks disk freshness.</summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(30);

    private readonly ILogger _log = AppLog.CreateLogger<VideoMapService>();

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly Dictionary<string, CachedVideoMap> _cache = [];

    /// <summary>
    /// Returns a cached parsed video map, or null if not yet loaded.
    /// </summary>
    public VideoMapData? GetCached(string mapId)
    {
        return _cache.GetValueOrDefault(mapId)?.Data;
    }

    /// <summary>
    /// Loads a batch of video maps for an ARTCC. Downloads any
    /// missing maps, parses them, and caches the results.
    /// </summary>
    public async Task<List<VideoMapData>> LoadMapsAsync(string artccId, IReadOnlyList<VideoMapInfoDto> maps)
    {
        var artccCacheDir = Path.Combine(CacheDir, artccId);
        Directory.CreateDirectory(artccCacheDir);

        var results = new List<VideoMapData>();
        var tasks = new List<Task>();

        foreach (var map in maps)
        {
            if (_cache.TryGetValue(map.Id, out var cached) && (DateTime.UtcNow - cached.FetchedUtc <= CacheTtl))
            {
                results.Add(cached.Data);
                continue;
            }

            var localMap = map;
            tasks.Add(
                Task.Run(async () =>
                {
                    var data = await LoadSingleMapAsync(artccId, localMap.Id, artccCacheDir);
                    if (data is not null)
                    {
                        lock (_cache)
                        {
                            _cache[localMap.Id] = new CachedVideoMap(data, DateTime.UtcNow);
                        }

                        lock (results)
                        {
                            results.Add(data);
                        }
                    }
                })
            );
        }

        await Task.WhenAll(tasks);
        return results;
    }

    private async Task<VideoMapData?> LoadSingleMapAsync(string artccId, string mapId, string artccCacheDir)
    {
        var cachePath = Path.Combine(artccCacheDir, $"{mapId}.geojson");
        var url = $"{DataApiBase}/{artccId}/{mapId}.geojson";

        var json = await HttpFileCache.GetOrRefreshAsync(_http, url, cachePath, HttpCacheFreshness.HeadLastModified, diskTtl: null, _log);
        if (json is null)
        {
            return null;
        }

        try
        {
            return VideoMapParser.Parse(mapId, json);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to parse video map {Id}", mapId);
            return null;
        }
    }

    /// <summary>
    /// Clears all cached parsed maps (does not delete disk cache).
    /// </summary>
    public void ClearMemoryCache()
    {
        _cache.Clear();
    }

    private sealed record CachedVideoMap(VideoMapData Data, DateTime FetchedUtc);
}
