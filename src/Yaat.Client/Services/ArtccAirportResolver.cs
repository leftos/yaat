using System.Text.Json;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Sim;
using Yaat.Sim.Data;

namespace Yaat.Client.Services;

/// <summary>
/// Fetches ARTCC configuration from vNAS data API, caches to disk, and uses a
/// TTL-based freshness check to avoid redundant downloads. (The data-api
/// /api/artccs endpoint does not support HEAD or Last-Modified.)
/// Provides airport ID extraction and tower cab video map ID lookup.
/// </summary>
public sealed class ArtccAirportResolver
{
    private const string DataApiBase = "https://data-api.vnas.vatsim.net/api/artccs";

    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(6);

    private static readonly string CacheDir = YaatPaths.Combine("cache", "artcc");

    private readonly ILogger _log = AppLog.CreateLogger<ArtccAirportResolver>();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly Dictionary<string, IReadOnlyList<string>> _airportCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _jsonCache = new(StringComparer.OrdinalIgnoreCase);

    private static string CachePathFor(string artccId) => Path.Combine(CacheDir, $"{artccId}.json");

    /// <summary>
    /// The in-memory caches are valid only while the on-disk copy is within <see cref="CacheTtl"/>;
    /// gating on the disk file's mtime keeps them honoring the same TTL as the disk cache instead of
    /// pinning a config for the client process lifetime.
    /// </summary>
    private static bool IsDiskFresh(string cachePath) => File.Exists(cachePath) && (DateTime.UtcNow - File.GetLastWriteTimeUtc(cachePath) < CacheTtl);

    /// <summary>
    /// Returns the list of underlying airport ICAO IDs for the given ARTCC.
    /// Results are cached in-memory and on disk with conditional HTTP freshness.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetAirportIdsAsync(string artccId)
    {
        if (_airportCache.TryGetValue(artccId, out IReadOnlyList<string>? cached) && IsDiskFresh(CachePathFor(artccId)))
        {
            return cached;
        }

        string? json = await GetArtccJsonAsync(artccId);
        if (json is null)
        {
            return [];
        }

        List<string> airports = ExtractUnderlyingAirports(json);
        _airportCache[artccId] = airports;
        _log.LogInformation("Resolved {Count} airports for {Artcc}", airports.Count, artccId);
        return airports;
    }

    /// <summary>
    /// Finds the tower cab video map ID for a specific airport facility within an ARTCC config.
    /// Returns null if not found.
    /// </summary>
    public async Task<string?> GetTowerCabVideoMapIdAsync(string artccId, string airportId)
    {
        string? json = await GetArtccJsonAsync(artccId);
        if (json is null)
        {
            return null;
        }

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("facility", out JsonElement facility))
        {
            return null;
        }

        return SearchFacilityForTowerCabVideoMapId(facility, airportId);
    }

    private async Task<string?> GetArtccJsonAsync(string artccId)
    {
        string cachePath = CachePathFor(artccId);
        if (_jsonCache.TryGetValue(artccId, out string? cached) && IsDiskFresh(cachePath))
        {
            return cached;
        }

        string url = $"{DataApiBase}/{artccId}";
        string? json = (await HttpFileCache.GetOrRefreshAsync(_http, url, cachePath, HttpCacheFreshness.AlwaysRefetch, CacheTtl, _log)).Content;
        if (json is not null)
        {
            _jsonCache[artccId] = json;
        }

        return json;
    }

    /// <summary>
    /// Collects every underlying airport ID in an ARTCC config, from every STARS facility in it.
    /// </summary>
    /// <remarks>
    /// A STARS config belongs to a facility, not to the ARTCC document — each TRACON carries its own
    /// under <c>facility.childFacilities[].starsConfiguration.areas[].underlyingAirports[]</c>, nested
    /// arbitrarily deep. Airports repeat heavily across areas and facilities, so results are deduplicated.
    /// IDs are FAA-style (<c>OAK</c>) for domestic airports; <see cref="LiveWeatherService"/> prefixes
    /// three-letter IDs with K before querying aviationweather.gov.
    /// </remarks>
    public static List<string> ExtractUnderlyingAirports(string json)
    {
        var airports = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("facility", out JsonElement facility))
        {
            CollectUnderlyingAirports(facility, airports);
        }

        return [.. airports.Order()];
    }

    private static void CollectUnderlyingAirports(JsonElement facility, HashSet<string> airports)
    {
        if (
            facility.TryGetProperty("starsConfiguration", out JsonElement starsCfg)
            && starsCfg.ValueKind == JsonValueKind.Object
            && starsCfg.TryGetProperty("areas", out JsonElement areas)
            && areas.ValueKind == JsonValueKind.Array
        )
        {
            foreach (JsonElement area in areas.EnumerateArray())
            {
                if (!area.TryGetProperty("underlyingAirports", out JsonElement uaArr) || uaArr.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (JsonElement ap in uaArr.EnumerateArray())
                {
                    string? id = ap.GetString();
                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        airports.Add(id);
                    }
                }
            }
        }

        if (facility.TryGetProperty("childFacilities", out JsonElement children) && children.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement child in children.EnumerateArray())
            {
                CollectUnderlyingAirports(child, airports);
            }
        }
    }

    private static string? SearchFacilityForTowerCabVideoMapId(JsonElement facility, string airportId)
    {
        if (facility.TryGetProperty("id", out JsonElement idProp))
        {
            string? id = idProp.GetString();
            if (string.Equals(id, airportId, StringComparison.OrdinalIgnoreCase))
            {
                if (facility.TryGetProperty("towerCabConfiguration", out JsonElement tcc) && tcc.TryGetProperty("videoMapId", out JsonElement vmId))
                {
                    return vmId.GetString();
                }
            }
        }

        if (facility.TryGetProperty("childFacilities", out JsonElement children) && children.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement child in children.EnumerateArray())
            {
                string? result = SearchFacilityForTowerCabVideoMapId(child, airportId);
                if (result is not null)
                {
                    return result;
                }
            }
        }

        return null;
    }
}
