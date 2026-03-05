using System.Text.Json;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;

namespace Yaat.Client.Services;

/// <summary>
/// Fetches ARTCC configuration from vNAS data API and extracts the unique
/// underlying airport IDs from STARS areas.
/// </summary>
public sealed class ArtccAirportResolver
{
    private const string DataApiBase = "https://data-api.vnas.vatsim.net/api/artccs";

    private readonly ILogger _log = AppLog.CreateLogger<ArtccAirportResolver>();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly Dictionary<string, IReadOnlyList<string>> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the list of underlying airport ICAO IDs for the given ARTCC.
    /// Results are cached in-memory per ARTCC.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetAirportIdsAsync(string artccId)
    {
        if (_cache.TryGetValue(artccId, out var cached))
        {
            return cached;
        }

        try
        {
            var url = $"{DataApiBase}/{artccId}";
            var json = await _http.GetStringAsync(url);
            var airports = ExtractUnderlyingAirports(json);
            _cache[artccId] = airports;
            _log.LogInformation("Resolved {Count} airports for {Artcc}", airports.Count, artccId);
            return airports;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to fetch ARTCC config for {Artcc}", artccId);
            return [];
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
}
