using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Data.Airport;

/// <summary>
/// Downloads ATCTrainer-format airport ground layout GeoJSON from the vNAS data API
/// (<c>https://data-api.vnas.vatsim.net/api/training/airports/{FAA}/map</c>) and caches
/// it on disk under <c>%LOCALAPPDATA%/yaat/cache/airports/</c>.
///
/// The origin (Cloudflare) hard-405s HEAD and sends no Last-Modified/ETag on GET (and ignores
/// If-Modified-Since), so there is no cheap conditional freshness probe. Each resolve GETs the
/// current map and overwrites the cache only when the content changed; a network failure falls
/// back to the on-disk copy. Callers memoize the parsed layout with their own TTL (the server's
/// AirportGroundDataService uses 30 minutes), so the GET recurs per TTL cycle, not once per run —
/// which is why confirmed 404s are negative-cached here for <see cref="NotFoundTtl"/>.
///
/// Use <see cref="GetGeoJsonAsync"/> for the raw text or <see cref="GetLayoutAsync"/>
/// for a parsed <see cref="AirportGroundLayout"/>.
/// </summary>
public sealed class AirportLayoutDownloader : IDisposable
{
    private const string TrainingApiBase = "https://data-api.vnas.vatsim.net/api/training/airports";

    /// <summary>
    /// How long a confirmed HTTP 404 suppresses re-fetching an airport's map. Many airports have no
    /// vNAS map at all (they 404 forever); without this, every caller-side refresh cycle re-hits the
    /// API — on the server that's a blocking network call reachable from the tick loop. Only a
    /// genuine 404 latches; network failures always retry.
    /// </summary>
    private static readonly TimeSpan NotFoundTtl = TimeSpan.FromHours(6);

    private static readonly ILogger Log = SimLog.CreateLogger<AirportLayoutDownloader>();

    private readonly ConcurrentDictionary<string, DateTime> _notFoundAtUtc = new(StringComparer.OrdinalIgnoreCase);
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string _cacheDir;

    public AirportLayoutDownloader()
        : this(http: null, cacheDir: null) { }

    public AirportLayoutDownloader(HttpClient? http, string? cacheDir)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _ownsHttp = http is null;
        _cacheDir = cacheDir ?? YaatPaths.Combine("cache", "airports");
    }

    /// <summary>
    /// Directory where airport GeoJSONs are cached. Exposed so callers can surface it
    /// in diagnostics or wire it into tools that read the cache directly.
    /// </summary>
    public string CacheDir => _cacheDir;

    /// <summary>
    /// Returns the cache path for <paramref name="airportId"/> regardless of whether the
    /// file exists yet. Always uses the FAA code (leading K stripped).
    /// </summary>
    public string GetCachePath(string airportId)
    {
        return Path.Combine(_cacheDir, ToFaaCode(airportId) + ".geojson");
    }

    /// <summary>
    /// Returns the GeoJSON text for <paramref name="airportId"/>, fetching from the
    /// API and refreshing the cache as needed. Returns null when the API has no map
    /// for this airport (404) or the request fails.
    /// </summary>
    public async Task<string?> GetGeoJsonAsync(string airportId, CancellationToken cancellationToken = default)
    {
        var faaCode = ToFaaCode(airportId);

        if (_notFoundAtUtc.TryGetValue(faaCode, out var notFoundAt))
        {
            if (DateTime.UtcNow - notFoundAt < NotFoundTtl)
            {
                Log.LogDebug("Airport {AirportId} known to have no vNAS map (404 negative cache); skipping fetch", faaCode);
                return null;
            }

            _notFoundAtUtc.TryRemove(faaCode, out _);
        }

        var cachePath = GetCachePath(faaCode);
        var url = $"{TrainingApiBase}/{faaCode}/map";

        var result = await HttpFileCache.GetOrRefreshAsync(
            _http,
            url,
            cachePath,
            HttpCacheFreshness.AlwaysRefetch,
            diskTtl: null,
            Log,
            cancellationToken
        );

        if (result.NotFound && result.Content is null)
        {
            _notFoundAtUtc[faaCode] = DateTime.UtcNow;
        }
        else if (result.Content is not null)
        {
            _notFoundAtUtc.TryRemove(faaCode, out _);
        }

        return result.Content;
    }

    /// <summary>
    /// Returns a parsed <see cref="AirportGroundLayout"/> for <paramref name="airportId"/>,
    /// fetching and caching the GeoJSON as needed. Returns null when the API has no
    /// map for this airport or the request fails.
    /// </summary>
    public async Task<AirportGroundLayout?> GetLayoutAsync(string airportId, CancellationToken cancellationToken = default)
    {
        var faaCode = ToFaaCode(airportId);
        var geoJson = await GetGeoJsonAsync(faaCode, cancellationToken);
        if (geoJson is null)
        {
            return null;
        }

        try
        {
            return GeoJsonParser.Parse(faaCode.ToLowerInvariant(), geoJson, faaCode);
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "Failed to parse cached airport GeoJSON for {AirportId}", faaCode);
            return null;
        }
    }

    /// <summary>
    /// Strips a leading 'K' from a 4-letter ICAO code so it matches the FAA-code keying
    /// used by the vNAS training-airports API.
    /// </summary>
    public static string ToFaaCode(string airportId)
    {
        if (airportId.Length == 4 && char.ToUpperInvariant(airportId[0]) == 'K')
        {
            return airportId[1..].ToUpperInvariant();
        }

        return airportId.ToUpperInvariant();
    }

    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }
}
