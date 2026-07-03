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
/// back to the on-disk copy. Callers memoize the parsed layout per process, so the GET runs about
/// once per airport per run.
///
/// Use <see cref="GetGeoJsonAsync"/> for the raw text or <see cref="GetLayoutAsync"/>
/// for a parsed <see cref="AirportGroundLayout"/>.
/// </summary>
public sealed class AirportLayoutDownloader : IDisposable
{
    private const string TrainingApiBase = "https://data-api.vnas.vatsim.net/api/training/airports";

    private static readonly ILogger Log = SimLog.CreateLogger<AirportLayoutDownloader>();

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
        var cachePath = GetCachePath(faaCode);
        Directory.CreateDirectory(_cacheDir);

        await EnsureFreshAsync(faaCode, cachePath, cancellationToken);

        if (!File.Exists(cachePath))
        {
            return null;
        }

        return await File.ReadAllTextAsync(cachePath, cancellationToken);
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

    /// <summary>
    /// Unconditionally GETs the current map and overwrites the cache when the content changed.
    /// The origin 405s HEAD and sends no Last-Modified/ETag, so a conditional probe cannot detect
    /// updates — a full GET is the only reliable refresh. Failures are logged and swallowed so a
    /// transient outage (or 404) falls back to whatever is already on disk.
    /// </summary>
    private async Task EnsureFreshAsync(string faaCode, string cachePath, CancellationToken cancellationToken)
    {
        var url = $"{TrainingApiBase}/{faaCode}/map";

        try
        {
            using var getResp = await _http.GetAsync(url, cancellationToken);
            if (getResp.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                Log.LogInformation("No airport layout available from vNAS for {Url} (HTTP 404)", url);
                return;
            }

            getResp.EnsureSuccessStatusCode();
            var json = await getResp.Content.ReadAsStringAsync(cancellationToken);

            if (File.Exists(cachePath) && string.Equals(await File.ReadAllTextAsync(cachePath, cancellationToken), json, StringComparison.Ordinal))
            {
                return;
            }

            await File.WriteAllTextAsync(cachePath, json, cancellationToken);
            Log.LogDebug("Refreshed cached airport layout for {AirportId}", faaCode);
        }
        catch (HttpRequestException ex)
        {
            Log.LogWarning(ex, "Failed to refresh airport layout for {AirportId}; using cached copy if present", faaCode);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            Log.LogWarning(ex, "Timed out refreshing airport layout for {AirportId}; using cached copy if present", faaCode);
        }
    }
}
