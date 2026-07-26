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

    private static readonly ILogger Log = SimLog.CreateLogger("ApproachGateDatabase");

    private static Dictionary<(string Airport, string Runway), double> _minIntercepts = [];

    private static bool _initialized;

    /// <summary>
    /// Builds the gate table from the current FAA CIFP cycle plus any <paramref name="additional"/> parse
    /// results — ARTCC-supplied procedure fragments, whose approaches would otherwise fall back to the
    /// <see cref="DefaultMinInterceptNm"/> default. <paramref name="cifpData"/> wins on a conflicting FAF.
    /// Reads <see cref="NavigationDatabase.Instance"/> internally, so initialize the nav DB first.
    /// </summary>
    public static void Initialize(CifpParseResult cifpData, IReadOnlyList<CifpParseResult> additional)
    {
        var navDb = NavigationDatabase.Instance;
        var result = new Dictionary<(string Airport, string Runway), double>();
        int computed = 0;
        int skipped = 0;

        var fafFixes = new Dictionary<(string Airport, string Runway), string>();
        foreach (var extra in additional)
        {
            foreach (var (key, fix) in extra.FafFixes)
            {
                fafFixes[key] = fix;
            }
        }

        foreach (var (key, fix) in cifpData.FafFixes)
        {
            fafFixes[key] = fix;
        }

        foreach (var ((airport, runway), fafFixName) in fafFixes)
        {
            // Resolve FAF fix position
            (double Lat, double Lon)? fafPos = navDb.GetFixPosition(fafFixName);

            if (fafPos is null && cifpData.TerminalWaypoints.TryGetValue(fafFixName, out var terminalPos))
            {
                fafPos = terminalPos;
            }

            if (fafPos is null)
            {
                foreach (var extra in additional)
                {
                    if (extra.TerminalWaypoints.TryGetValue(fafFixName, out var extraPos))
                    {
                        fafPos = extraPos;
                        break;
                    }
                }
            }

            if (fafPos is null)
            {
                skipped++;
                continue;
            }

            // Get runway threshold
            var runwayInfo = navDb.GetRunway(airport, runway) ?? navDb.GetRunway($"K{airport}", runway);
            if (runwayInfo is null)
            {
                skipped++;
                continue;
            }

            // Compute FAF → threshold distance
            double fafDist = GeoMath.DistanceNm(fafPos.Value.Lat, fafPos.Value.Lon, runwayInfo.ThresholdLatitude, runwayInfo.ThresholdLongitude);

            // Approach gate = max(FAF + 1nm, 5nm)
            double approachGate = Math.Max(fafDist + GatePaddingNm, MinGateFloorNm);

            // Min intercept = gate + 2nm
            double minIntercept = approachGate + InterceptPaddingNm;

            result[(NormalizeAirport(airport), runway)] = minIntercept;
            computed++;
        }

        _minIntercepts = result;
        _initialized = true;

        Log.LogInformation("Approach gate database: {Computed} runways computed, " + "{Skipped} skipped (missing data)", computed, skipped);
    }

    /// <summary>
    /// Returns the minimum intercept distance for the given airport
    /// and runway. Returns 7.0nm default if not found.
    /// </summary>
    public static double GetMinInterceptDistanceNm(string airportId, string runwayId)
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
        return airportId.StartsWith('K') ? airportId[1..] : airportId;
    }
}
