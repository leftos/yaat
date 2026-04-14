using System.Text.Json;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;

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

    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "yaat",
        "cache",
        "artcc"
    );

    private readonly ILogger _log = AppLog.CreateLogger<ArtccAirportResolver>();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly Dictionary<string, IReadOnlyList<string>> _airportCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _jsonCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the list of underlying airport ICAO IDs for the given ARTCC.
    /// Results are cached in-memory and on disk with conditional HTTP freshness.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetAirportIdsAsync(string artccId)
    {
        if (_airportCache.TryGetValue(artccId, out var cached))
        {
            return cached;
        }

        var json = await GetArtccJsonAsync(artccId);
        if (json is null)
        {
            return [];
        }

        var airports = ExtractUnderlyingAirports(json);
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
        var json = await GetArtccJsonAsync(artccId);
        if (json is null)
        {
            return null;
        }

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("facility", out var facility))
        {
            return null;
        }

        return SearchFacilityForTowerCabVideoMapId(facility, airportId);
    }

    private async Task<string?> GetArtccJsonAsync(string artccId)
    {
        if (_jsonCache.TryGetValue(artccId, out var cached))
        {
            return cached;
        }

        Directory.CreateDirectory(CacheDir);
        var cachePath = Path.Combine(CacheDir, $"{artccId}.json");
        var url = $"{DataApiBase}/{artccId}";

        try
        {
            if (File.Exists(cachePath))
            {
                var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(cachePath);
                if (age < CacheTtl)
                {
                    _log.LogDebug("ARTCC config {Artcc} is fresh (age {AgeMin:F0} min)", artccId, age.TotalMinutes);
                    var diskJson = await File.ReadAllTextAsync(cachePath);
                    _jsonCache[artccId] = diskJson;
                    return diskJson;
                }

                _log.LogDebug("ARTCC config {Artcc} is stale (age {AgeHr:F1} h), re-downloading", artccId, age.TotalHours);
            }

            var json = await _http.GetStringAsync(url);
            await File.WriteAllTextAsync(cachePath, json);
            _jsonCache[artccId] = json;
            _log.LogDebug("Downloaded ARTCC config {Artcc}", artccId);
            return json;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to fetch ARTCC config for {Artcc}", artccId);

            // Fall back to disk cache
            if (File.Exists(cachePath))
            {
                var diskJson = await File.ReadAllTextAsync(cachePath);
                _jsonCache[artccId] = diskJson;
                return diskJson;
            }

            return null;
        }
    }

    private static List<string> ExtractUnderlyingAirports(string json)
    {
        var airports = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Navigate: starsConfiguration.areas[].underlyingAirports[]
        if (!root.TryGetProperty("starsConfiguration", out var starsCfg))
        {
            return [];
        }

        if (!starsCfg.TryGetProperty("areas", out var areas) || areas.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        foreach (var area in areas.EnumerateArray())
        {
            if (!area.TryGetProperty("underlyingAirports", out var uaArr) || uaArr.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var ap in uaArr.EnumerateArray())
            {
                var id = ap.GetString();
                if (!string.IsNullOrWhiteSpace(id))
                {
                    airports.Add(id);
                }
            }
        }

        return [.. airports.Order()];
    }

    private static string? SearchFacilityForTowerCabVideoMapId(JsonElement facility, string airportId)
    {
        if (facility.TryGetProperty("id", out var idProp))
        {
            var id = idProp.GetString();
            if (string.Equals(id, airportId, StringComparison.OrdinalIgnoreCase))
            {
                if (facility.TryGetProperty("towerCabConfiguration", out var tcc) && tcc.TryGetProperty("videoMapId", out var vmId))
                {
                    return vmId.GetString();
                }
            }
        }

        if (facility.TryGetProperty("childFacilities", out var children) && children.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in children.EnumerateArray())
            {
                var result = SearchFacilityForTowerCabVideoMapId(child, airportId);
                if (result is not null)
                {
                    return result;
                }
            }
        }

        return null;
    }
}
