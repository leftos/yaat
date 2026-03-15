using Yaat.Sim.Data;

namespace Yaat.Sim;

public static class MetarInterpolator
{
    private const double MaxInterpolationRangeNm = 50.0;

    /// <summary>
    /// Get ceiling/visibility for an airport. First checks for exact station match,
    /// then falls back to distance-weighted interpolation from nearby stations.
    /// </summary>
    public static MetarParser.ParsedMetar? GetWeatherForAirport(IEnumerable<string> metars, string airportId)
    {
        var metarList = metars as IReadOnlyList<string> ?? metars.ToList();

        // Exact match first
        var exact = MetarParser.FindStation(metarList, airportId);
        if (exact is not null)
        {
            return exact;
        }

        var navDb = NavigationDatabase.Instance;

        // Resolve airport position
        var airportPos = navDb.GetFixPosition(airportId);
        if (airportPos is null)
        {
            return null;
        }

        double aptLat = airportPos.Value.Lat;
        double aptLon = airportPos.Value.Lon;

        // Parse all METARs and find nearby stations
        var nearby = new List<(MetarParser.ParsedMetar Metar, double DistNm)>();

        foreach (var metarStr in metarList)
        {
            var parsed = MetarParser.Parse(metarStr);
            if (parsed is null)
            {
                continue;
            }

            // Resolve station position (strip K prefix for FAA lookup)
            var stationPos = navDb.GetFixPosition(parsed.StationId);
            if (stationPos is null && parsed.StationId.Length == 4 && parsed.StationId[0] == 'K')
            {
                stationPos = navDb.GetFixPosition(parsed.StationId[1..]);
            }

            if (stationPos is null)
            {
                continue;
            }

            double dist = GeoMath.DistanceNm(aptLat, aptLon, stationPos.Value.Lat, stationPos.Value.Lon);
            if (dist <= MaxInterpolationRangeNm)
            {
                nearby.Add((parsed, dist));
            }
        }

        if (nearby.Count == 0)
        {
            return null;
        }

        // Single station: use directly
        if (nearby.Count == 1)
        {
            return nearby[0].Metar;
        }

        // Interpolate: min ceiling (conservative), IDW-weighted visibility
        return Interpolate(nearby, airportId);
    }

    private static MetarParser.ParsedMetar Interpolate(List<(MetarParser.ParsedMetar Metar, double DistNm)> stations, string airportId)
    {
        // Ceiling: use minimum from all nearby stations (conservative)
        int? minCeiling = null;
        foreach (var (metar, _) in stations)
        {
            if (metar.CeilingFeetAgl is { } ceil)
            {
                minCeiling = minCeiling is null ? ceil : Math.Min(minCeiling.Value, ceil);
            }
        }

        // Visibility: inverse-distance-weighted average
        double? weightedVis = null;
        double totalWeight = 0;
        double visSum = 0;

        foreach (var (metar, dist) in stations)
        {
            if (metar.VisibilityStatuteMiles is not { } vis)
            {
                continue;
            }

            // Avoid division by zero: treat very close stations as distance 0.1nm
            double weight = 1.0 / Math.Max(dist, 0.1);
            totalWeight += weight;
            visSum += vis * weight;
        }

        if (totalWeight > 0)
        {
            weightedVis = visSum / totalWeight;
        }

        string icao = MetarParser.ToIcao(airportId);
        return new MetarParser.ParsedMetar(icao, minCeiling, weightedVis);
    }
}
