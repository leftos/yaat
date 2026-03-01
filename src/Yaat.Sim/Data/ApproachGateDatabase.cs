using Microsoft.Extensions.Logging;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;

namespace Yaat.Sim.Data;

/// <summary>
/// Precomputed minimum intercept distances per (airport, runway)
/// based on FAA 7110.65 §5-9-1 approach gate concept:
///   approach gate = max(FAF_distance + 1nm, 5nm)
///   min intercept = approach gate + 2nm
/// </summary>
public static class ApproachGateDatabase
{
    private const double DefaultMinInterceptNm = 7.0;
    private const double MinGateFloorNm = 5.0;
    private const double GatePaddingNm = 1.0;
    private const double InterceptPaddingNm = 2.0;

    private static Dictionary<(string Airport, string Runway), double>
        _minIntercepts = [];

    private static bool _initialized;

    public static void Initialize(
        CifpParseResult cifpData,
        IFixLookup fixLookup,
        IRunwayLookup runwayLookup,
        ILogger? logger = null)
    {
        var result = new Dictionary<(string Airport, string Runway), double>();
        int computed = 0;
        int skipped = 0;

        foreach (var ((airport, runway), fafFixName) in cifpData.FafFixes)
        {
            // Resolve FAF fix position
            (double Lat, double Lon)? fafPos =
                fixLookup.GetFixPosition(fafFixName);

            if (fafPos is null
                && cifpData.TerminalWaypoints.TryGetValue(
                    fafFixName, out var terminalPos))
            {
                fafPos = terminalPos;
            }

            if (fafPos is null)
            {
                skipped++;
                continue;
            }

            // Get runway threshold
            var runwayInfo = runwayLookup.GetRunway(airport, runway)
                ?? runwayLookup.GetRunway($"K{airport}", runway);
            if (runwayInfo is null)
            {
                skipped++;
                continue;
            }

            // Compute FAF → threshold distance
            double fafDist = GeoMath.DistanceNm(
                fafPos.Value.Lat, fafPos.Value.Lon,
                runwayInfo.ThresholdLatitude,
                runwayInfo.ThresholdLongitude);

            // Approach gate = max(FAF + 1nm, 5nm)
            double approachGate = Math.Max(
                fafDist + GatePaddingNm, MinGateFloorNm);

            // Min intercept = gate + 2nm
            double minIntercept = approachGate + InterceptPaddingNm;

            result[(NormalizeAirport(airport), runway)] = minIntercept;
            computed++;
        }

        _minIntercepts = result;
        _initialized = true;

        logger?.LogInformation(
            "Approach gate database: {Computed} runways computed, "
            + "{Skipped} skipped (missing data)",
            computed, skipped);
    }

    /// <summary>
    /// Returns the minimum intercept distance for the given airport
    /// and runway. Returns 7.0nm default if not found.
    /// </summary>
    public static double GetMinInterceptDistanceNm(
        string airportId, string runwayId)
    {
        if (!_initialized)
        {
            return DefaultMinInterceptNm;
        }

        string normalized = NormalizeAirport(airportId);

        if (_minIntercepts.TryGetValue((normalized, runwayId), out double dist))
        {
            return dist;
        }

        return DefaultMinInterceptNm;
    }

    private static string NormalizeAirport(string airportId)
    {
        return airportId.StartsWith('K')
            ? airportId[1..]
            : airportId;
    }
}
