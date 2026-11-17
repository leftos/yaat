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
        lock (_cache)
        {
            return _cache.GetValueOrDefault(mapId)?.Data;
        }
    }

    /// <summary>Puts <paramref name="data"/> in the in-memory cache as a fresh load would, with no download. Test seam.</summary>
    internal void CacheForTesting(VideoMapData data)
    {
        lock (_cache)
        {
            _cache[data.MapId] = new CachedVideoMap(data, DateTime.UtcNow);
        }
    }

    /// <summary>The cached map for <paramref name="mapId"/> when it is still within <see cref="CacheTtl"/>, else null.</summary>
    private VideoMapData? GetFresh(string mapId)
    {
        lock (_cache)
        {
            return (_cache.TryGetValue(mapId, out CachedVideoMap? cached) && ((DateTime.UtcNow - cached.FetchedUtc) <= CacheTtl))
                ? cached.Data
                : null;
        }
    }

    /// <summary>
    /// Loads a batch of video maps for an ARTCC. Downloads any
    /// missing maps, parses them, and caches the results.
    /// </summary>
    public async Task<List<VideoMapData>> LoadMapsAsync(string artccId, IReadOnlyList<VideoMapInfoDto> maps)
    {
        string artccCacheDir = Path.Combine(CacheDir, artccId);
        Directory.CreateDirectory(artccCacheDir);

        var results = new List<VideoMapData>();
        var tasks = new List<Task>();

        foreach (VideoMapInfoDto map in maps)
        {
            if (GetFresh(map.Id) is { } fresh)
            {
                results.Add(fresh);
                continue;
            }

            VideoMapInfoDto localMap = map;
            tasks.Add(
                Task.Run(async () =>
                {
                    VideoMapData? data = await LoadSingleMapAsync(artccId, localMap.Id, artccCacheDir);
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
        string cachePath = Path.Combine(artccCacheDir, $"{mapId}.geojson");
        string url = $"{DataApiBase}/{artccId}/{mapId}.geojson";

        string? json = (
            await HttpFileCache.GetOrRefreshAsync(_http, url, cachePath, HttpCacheFreshness.HeadLastModified, diskTtl: null, _log)
        ).Content;
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
        lock (_cache)
        {
            _cache.Clear();
        }
    }

    private sealed record CachedVideoMap(VideoMapData Data, DateTime FetchedUtc);
}
