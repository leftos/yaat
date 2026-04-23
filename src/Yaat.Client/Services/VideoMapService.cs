using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Sim;
using Yaat.Sim.Data;

namespace Yaat.Client.Services;

/// <summary>
/// Downloads, caches, and parses video map GeoJSON files from the
/// vNAS data API. Maps are cached to %LOCALAPPDATA%/yaat/cache/videomaps/.
/// </summary>
public sealed class VideoMapService
{
    private const string DataApiBase = "https://data-api.vnas.vatsim.net/Files/VideoMaps";

    private static readonly string CacheDir = YaatPaths.Combine("cache", "videomaps");

    private readonly ILogger _log = AppLog.CreateLogger<VideoMapService>();

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

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
    public async Task<List<VideoMapData>> LoadMapsAsync(string artccId, IReadOnlyList<VideoMapInfoDto> maps)
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
            tasks.Add(
                Task.Run(async () =>
                {
                    var data = await LoadSingleMapAsync(artccId, localMap.Id, artccCacheDir);
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

        // Freshness check: the vNAS data-api (Cloudflare origin) returns 200 OK on HEAD requests
        // regardless of If-Modified-Since, so we compare Last-Modified against the cached file's
        // mtime ourselves instead of relying on 304 responses.
        try
        {
            if (File.Exists(cachePath))
            {
                var fileInfo = new FileInfo(cachePath);
                using var headReq = new HttpRequestMessage(HttpMethod.Head, url);
                using var headResp = await _http.SendAsync(headReq);

                if (headResp.IsSuccessStatusCode)
                {
                    var serverLastModified = headResp.Content.Headers.LastModified?.UtcDateTime;
                    if ((serverLastModified is { } sm) && (sm <= fileInfo.LastWriteTimeUtc))
                    {
                        _log.LogDebug("Video map {Id} is up to date", mapId);
                    }
                    else
                    {
                        _log.LogDebug("Video map {Id} has been updated, re-downloading", mapId);
                        await DownloadAsync(url, cachePath, serverLastModified);
                    }
                }
                else
                {
                    _log.LogWarning("HEAD request for video map {Id} returned {Status}; using cached copy", mapId, headResp.StatusCode);
                }
            }
            else
            {
                _log.LogDebug("Downloading video map {Id}", mapId);
                await DownloadAsync(url, cachePath, serverLastModified: null);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to download/check video map {Id}", mapId);
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
    /// Downloads the map body and stamps the local file's mtime with the server's Last-Modified
    /// (when available) so subsequent freshness checks remain consistent.
    /// </summary>
    private async Task DownloadAsync(string url, string cachePath, DateTime? serverLastModified)
    {
        using var getResp = await _http.GetAsync(url);
        getResp.EnsureSuccessStatusCode();
        var json = await getResp.Content.ReadAsStringAsync();
        await File.WriteAllTextAsync(cachePath, json);

        var stamp = serverLastModified ?? getResp.Content.Headers.LastModified?.UtcDateTime;
        if (stamp is { } s)
        {
            File.SetLastWriteTimeUtc(cachePath, s);
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
