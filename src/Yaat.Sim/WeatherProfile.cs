using System.Text.Json.Serialization;
using Yaat.Sim.Data;

namespace Yaat.Sim;

public class WindLayer
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    /// <summary>Altitude in feet MSL.</summary>
    [JsonPropertyName("altitude")]
    public double Altitude { get; set; }

    /// <summary>Wind from direction in degrees magnetic.</summary>
    [JsonPropertyName("direction")]
    public double Direction { get; set; }

    /// <summary>Wind speed in knots.</summary>
    [JsonPropertyName("speed")]
    public double Speed { get; set; }

    /// <summary>Gust speed in knots. Stored but not applied to physics.</summary>
    [JsonPropertyName("gusts")]
    public double? Gusts { get; set; }
}

public class WeatherProfile
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("artccId")]
    public string ArtccId { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("precipitation")]
    public string? Precipitation { get; set; }

    /// <summary>Wind layers sorted by altitude ascending on set.</summary>
    [JsonPropertyName("windLayers")]
    public List<WindLayer> WindLayers
    {
        get => _windLayers;
        set => _windLayers = [.. value.OrderBy(l => l.Altitude)];
    }

    [JsonPropertyName("metars")]
    public List<string> Metars { get; set; } = [];

    private List<WindLayer> _windLayers = [];
    private readonly Dictionary<string, MetarParser.ParsedMetar?> _metarCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Get parsed ceiling/visibility for an airport, with caching.
    /// Uses exact station match first, then IDW interpolation from nearby stations.
    /// </summary>
    public MetarParser.ParsedMetar? GetWeatherForAirport(string airportId, NavigationDatabase? navDb)
    {
        if (Metars.Count == 0)
        {
            return null;
        }

        if (_metarCache.TryGetValue(airportId, out var cached))
        {
            return cached;
        }

        var result = MetarInterpolator.GetWeatherForAirport(Metars, airportId, navDb);
        _metarCache[airportId] = result;
        return result;
    }
}
