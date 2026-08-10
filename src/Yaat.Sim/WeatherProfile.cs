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

    /// <summary>
    /// Half-spread of direction variability in degrees: the direction wanders within
    /// ±this value around <see cref="Direction"/>. A METAR "21015KT 180V240" authors
    /// mean 210 with half-spread 30. Null = no authored variability.
    /// </summary>
    [JsonPropertyName("directionVariabilityDeg")]
    public double? DirectionVariabilityDeg { get; set; }

    /// <summary>
    /// True for a VRB wind: no prevailing direction — the wind wanders the full circle.
    /// <see cref="Direction"/> still holds the (weak) mean the wander is seeded around.
    /// Null/false = normal directional wind.
    /// </summary>
    [JsonPropertyName("variable")]
    public bool? Variable { get; set; }
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
    private List<(string AirportId, LatLon Position)>? _stationIndex;

    /// <summary>
    /// Pre-computed interpolated METAR values populated during weather timeline transitions.
    /// When set, <see cref="GetWeatherForAirport"/> returns these instead of parsing METAR text.
    /// </summary>
    [JsonIgnore]
    public Dictionary<string, MetarParser.ParsedMetar>? ParsedMetarOverrides { get; set; }

    /// <summary>
    /// Get parsed ceiling/visibility for an airport, with caching.
    /// Checks interpolated overrides first, then exact station match, then IDW interpolation.
    /// </summary>
    public MetarParser.ParsedMetar? GetWeatherForAirport(string airportId)
    {
        if (ParsedMetarOverrides is not null && ParsedMetarOverrides.TryGetValue(airportId, out var over))
        {
            return over;
        }

        if (Metars.Count == 0)
        {
            return null;
        }

        if (_metarCache.TryGetValue(airportId, out var cached))
        {
            return cached;
        }

        var result = MetarInterpolator.GetWeatherForAirport(Metars, airportId);
        _metarCache[airportId] = result;
        return result;
    }

    /// <summary>
    /// Weather describing the air mass at a geographic position: the METAR of the
    /// nearest reporting station within <paramref name="maxRangeNm"/>. Returns the
    /// station's airport id (FAA-lookup form) alongside the parsed METAR so callers
    /// can resolve the station elevation as the AGL→MSL datum for its cloud bases.
    /// Returns null when no station is within range — the local air mass is unknown
    /// and callers should fall back to their no-weather behavior.
    /// </summary>
    public (MetarParser.ParsedMetar Metar, string StationAirportId)? GetWeatherNearPosition(LatLon position, double maxRangeNm)
    {
        _stationIndex ??= BuildStationIndex();

        string? nearestId = null;
        double nearestDist = maxRangeNm;
        foreach (var (airportId, stationPos) in _stationIndex)
        {
            double dist = GeoMath.DistanceNm(position, stationPos);
            if (dist <= nearestDist)
            {
                nearestDist = dist;
                nearestId = airportId;
            }
        }

        // Interpolated overrides can carry stations that have no METAR text in
        // Metars (weather timeline transitions replace parsed values wholesale),
        // so their keys are candidates too. Resolved per call, not cached —
        // ParsedMetarOverrides mutates at runtime while the Metars index does not.
        if (ParsedMetarOverrides is not null)
        {
            foreach (var stationKey in ParsedMetarOverrides.Keys)
            {
                var resolved = ResolveStationAirport(stationKey);
                if (resolved is not { } station)
                {
                    continue;
                }

                double dist = GeoMath.DistanceNm(position, station.Position);
                if (dist <= nearestDist)
                {
                    nearestDist = dist;
                    nearestId = station.AirportId;
                }
            }
        }

        if (nearestId is null)
        {
            return null;
        }

        var metar = GetWeatherForAirport(nearestId);
        return metar is null ? null : (metar, nearestId);
    }

    private double? _surfaceElevationFt;
    private bool _surfaceElevationResolved;

    /// <summary>
    /// Ground elevation (ft MSL) the wind-variability altitude taper is referenced to.
    /// Resolved from the profile's first nav-database-known METAR station — real profiles
    /// often author their lowest wind layer well above the field (or, for live weather, at
    /// 0 MSL regardless of terrain), so the layer altitude is not a usable ground datum:
    /// it would disable variability entirely at high-elevation airports and shift the
    /// taper band at airports whose lowest layer is aloft. Falls back to the lowest wind
    /// layer's altitude when no station resolves. Cached per profile; timeline collapse
    /// builds a fresh profile every second, so the cache follows the weather.
    /// </summary>
    public double SurfaceElevationFt()
    {
        if (_surfaceElevationResolved)
        {
            return _surfaceElevationFt ?? (_windLayers.Count > 0 ? _windLayers[0].Altitude : 0);
        }

        _surfaceElevationResolved = true;
        var navDb = NavigationDatabase.Instance;
        foreach (var metarStr in Metars)
        {
            var parsed = MetarParser.Parse(metarStr);
            if (parsed is null)
            {
                continue;
            }

            double? elevation = navDb.GetAirportElevation(parsed.StationId);
            if (elevation is null && parsed.StationId.Length == 4 && (parsed.StationId[0] is 'K' or 'k'))
            {
                elevation = navDb.GetAirportElevation(parsed.StationId[1..]);
            }

            if (elevation is { } resolved)
            {
                _surfaceElevationFt = resolved;
                return resolved;
            }
        }

        return _windLayers.Count > 0 ? _windLayers[0].Altitude : 0;
    }

    private List<(string AirportId, LatLon Position)> BuildStationIndex()
    {
        var index = new List<(string, LatLon)>();
        foreach (var metarStr in Metars)
        {
            var parsed = MetarParser.Parse(metarStr);
            if (parsed is null)
            {
                continue;
            }

            if (ResolveStationAirport(parsed.StationId) is { } station)
            {
                index.Add(station);
            }
        }
        return index;
    }

    /// <summary>
    /// Resolves a METAR station id to a nav-database airport position, trying the
    /// id as-is and then with the ICAO 'K' prefix stripped (FAA-lookup form).
    /// Returns the ORIGINAL id — <see cref="GetWeatherForAirport"/> needs it to hit
    /// <see cref="ParsedMetarOverrides"/> (keyed by station id) and exact METAR
    /// matches, and <c>NavigationDatabase.GetAirportElevation</c> accepts either
    /// form. Null if the station is not in the nav database.
    /// </summary>
    private static (string AirportId, LatLon Position)? ResolveStationAirport(string stationId)
    {
        var navDb = NavigationDatabase.Instance;
        var pos = navDb.GetFixPosition(stationId);
        if (pos is null && stationId.Length == 4 && (stationId[0] is 'K' or 'k'))
        {
            pos = navDb.GetFixPosition(stationId[1..]);
        }
        if (pos is null)
        {
            return null;
        }

        return (stationId, new LatLon(pos.Value.Lat, pos.Value.Lon));
    }
}
