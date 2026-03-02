using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Sim.Data;

namespace Yaat.Client.Services;

/// <summary>
/// Downloads, caches, and parses video map GeoJSON files from the
/// vNAS data API. Maps are cached to %LOCALAPPDATA%/yaat/cache/videomaps/.
/// </summary>
public sealed class VideoMapService
{
    private const string DataApiBase =
        "https://data-api.vnas.vatsim.net/Files/VideoMaps";

    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData),
        "yaat", "cache", "videomaps");

    private readonly ILogger _log =
        AppLog.CreateLogger<VideoMapService>();

    private readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    private readonly Dictionary<string, VideoMapData> _cache = [];

    /// <summary>
    /// Returns a cached parsed video map, or null if not yet loaded.
    /// </summary>
    public VideoMapData? GetCached(string mapId)
    {
        return _cache.GetValueOrDefault(mapId);
    }

    /// <summary>
    /// Loads a batch of video maps for an ARTCC. Downloads any
    /// missing maps, parses them, and caches the results.
    /// </summary>
    public async Task<List<VideoMapData>> LoadMapsAsync(
        string artccId, IReadOnlyList<VideoMapInfoDto> maps)
    {
        var artccCacheDir = Path.Combine(CacheDir, artccId);
        Directory.CreateDirectory(artccCacheDir);

        var results = new List<VideoMapData>();
        var tasks = new List<Task>();

        foreach (var map in maps)
        {
            if (_cache.TryGetValue(map.Id, out var cached))
            {
                results.Add(cached);
                continue;
            }

            var localMap = map;
            tasks.Add(Task.Run(async () =>
            {
                var data = await LoadSingleMapAsync(
                    artccId, localMap.Id, artccCacheDir);
                if (data is not null)
                {
                    lock (_cache)
                    {
                        _cache[localMap.Id] = data;
                    }

                    lock (results)
                    {
                        results.Add(data);
                    }
                }
            }));
        }

        await Task.WhenAll(tasks);
        return results;
    }

    private async Task<VideoMapData?> LoadSingleMapAsync(
        string artccId, string mapId, string artccCacheDir)
    {
        var cachePath = Path.Combine(artccCacheDir, $"{mapId}.geojson");

        // Try download first
        if (!File.Exists(cachePath))
        {
            try
            {
                var url = $"{DataApiBase}/{artccId}/{mapId}.geojson";
                var json = await _http.GetStringAsync(url);
                await File.WriteAllTextAsync(cachePath, json);
                _log.LogDebug("Downloaded video map {Id}", mapId);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "Failed to download video map {Id}", mapId);
            }
        }

        // Parse from cache
        if (!File.Exists(cachePath))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(cachePath);
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
}
