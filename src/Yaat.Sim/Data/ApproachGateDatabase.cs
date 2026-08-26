using Microsoft.Extensions.Logging;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;

namespace Yaat.Sim.Data;

/// <summary>
/// Precomputed FAF-to-threshold distances per (airport, runway), from which the FAA 7110.65 §5-9-1
/// approach gate follows:
///   approach gate = max(FAF_distance + 1nm, 5nm)
///   min intercept = approach gate + 2nm
///
/// The P/CG defines the gate's 5 nm floor against the <em>landing</em> threshold, and the phases that
/// consume this measure the aircraft's distance to the same point — but the table is built at startup
/// from CIFP, long before any airport map exists, so the stored FAF distance is to the pavement end.
/// Callers pass the runway end's threshold displacement and the gate is finished on the landing datum.
/// </summary>
public static class ApproachGateDatabase
{
    private const double DefaultMinInterceptNm = 7.0;
    private const double MinGateFloorNm = 5.0;
    private const double GatePaddingNm = 1.0;

    /// <summary>Vectors end this far outside the approach gate (§5-9-1.a); subtract it to recover the gate itself.</summary>
    public const double InterceptPaddingNm = 2.0;

    private static readonly ILogger Log = SimLog.CreateLogger("ApproachGateDatabase");

    private static Dictionary<(string Airport, string Runway), double> _fafDistancesToPavementNm = [];

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

            // FAF → pavement threshold. The displacement is added back at read time.
            double fafDist = GeoMath.DistanceNm(fafPos.Value.Lat, fafPos.Value.Lon, runwayInfo.ThresholdLatitude, runwayInfo.ThresholdLongitude);

            result[(NormalizeAirport(airport), runway)] = fafDist;
            computed++;
        }

        _fafDistancesToPavementNm = result;
        _initialized = true;

        Log.LogInformation("Approach gate database: {Computed} runways computed, " + "{Skipped} skipped (missing data)", computed, skipped);
    }

    /// <summary>
    /// Minimum legal intercept distance (nm) from the runway's <em>landing</em> threshold, for the
    /// runway end whose threshold is displaced <paramref name="thresholdDisplacementNm"/>. Pass 0 when
    /// no airport map is available; the runway then reads as undisplaced. Returns the 7.0 nm default
    /// when the runway has no FAF in the loaded procedures.
    /// </summary>
    public static double GetMinInterceptDistanceNm(string airportId, string runwayId, double thresholdDisplacementNm)
    {
        if (!_initialized)
        {
            return DefaultMinInterceptNm;
        }

        string normalized = NormalizeAirport(airportId);

        if (!_fafDistancesToPavementNm.TryGetValue((normalized, runwayId), out double fafDistToPavementNm))
        {
            return DefaultMinInterceptNm;
        }

        // The FAF is out on the approach side, so a threshold displaced downfield is that much further
        // from it. P/CG "approach gate": 1 nm outside the FAF, and never closer than 5 nm to the
        // landing threshold — both measured on the landing datum.
        double fafDistNm = fafDistToPavementNm + thresholdDisplacementNm;
        double approachGate = Math.Max(fafDistNm + GatePaddingNm, MinGateFloorNm);
        return approachGate + InterceptPaddingNm;
    }

    private static string NormalizeAirport(string airportId)
    {
        return airportId.StartsWith('K') ? airportId[1..] : airportId;
    }
}
