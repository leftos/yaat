using Yaat.Sim.Data.Airport;

namespace Yaat.Sim.Tests.Helpers;

/// <summary>
/// Computes time and cumulative-turn budgets for a taxi (origin → destination)
/// pair by inspecting the optimal A* route the simulator would have chosen.
///
/// The budgets are deliberately generous on the first pass: a real spin or
/// stall blows past them by 5-10×, so the budgets catch the bug class without
/// flaking on legitimate slow corners. If a regression slips through, tighten
/// the multipliers — don't replace this with hand-tuned per-pair numbers.
///
/// Formula (lifted from <c>docs/plans/we-ve-had-various-bugs-keen-waterfall.md</c>):
/// <code>
/// optimalDistFt   = sum of segment.DistanceNm × FeetPerNm
/// optimalTurnDeg  = sum of (|arr - dep| within each segment) + (|dep[i+1] - arr[i]| at each join)
/// optimalTimeSec  = sum_segments(distance / min(nominalKts, segMaxSafeSpeed)) -- arc-aware
/// timeBudgetSec   = optimalTimeSec × TimeFudgeMultiplier + cornerCount × SecondsPerCorner
/// turnBudgetDeg   = optimalTurnDeg × TurnFudgeMultiplier + TurnSlackDeg
/// </code>
///
/// <para>The arc-aware time floor is the key piece: jets traversing a tight
/// fillet arc at a GA-sized ramp are capped at the arc's safe speed
/// (potentially 5-10 kts) regardless of nominal taxi speed (jet 30 kts).
/// A flat distance/nominalKts budget under-allows these routes by 2-3×.
/// Per-segment integration matches how <c>TaxiPathfinder.EstimateTime</c>
/// scores Fastest routes — same math, applied to the chosen route.
/// </para>
///
/// <para>Intra-segment turn matters because arc edges (fillets at junctions
/// and curved taxiways) sweep heading along their length — a single arc
/// segment can rotate the aircraft 90° with no segment join involved. A
/// deriver that summed only join turns would under-budget such routes by 4-5×.
/// </para>
/// </summary>
internal static class TaxiBudgetDeriver
{
    public const double TimeFudgeMultiplier = 1.5;
    public const double SecondsPerCorner = 4.0;

    /// <summary>
    /// Multiplier on optimal cumulative-turn. Calibrated empirically against
    /// observed taxi behavior on OAK and SFO: optimal turn covers segment
    /// geometry (straight joins + arc sweeps), but the navigator adds
    /// per-corner slow-turn synthesis, pure-pursuit overshoot/correction, and
    /// fillet-arc tangent micro-flicks that inflate the observed turn by
    /// 1.0-1.3× over optimal on clean routes. A multiplier of 2.5 keeps a
    /// healthy 30-65% safety margin on healthy routes while still flagging
    /// anything that orbits — a typical spin produces 3-10× the natural turn.
    /// </summary>
    public const double TurnFudgeMultiplier = 2.5;
    public const double TurnSlackDeg = 60.0;
    public const double CornerAngleDeg = 30.0;
    private const double FtPerSecondsPerKt = GeoMath.FeetPerNm / 3600.0;

    public sealed record TaxiBudget(
        double OptimalDistFt,
        double OptimalTurnDeg,
        int CornerCount,
        double OptimalTimeSec,
        double TimeBudgetSec,
        double TurnBudgetDeg,
        TaxiRoute PlannedRoute
    );

    public static TaxiBudget Derive(AirportGroundLayout layout, int fromNodeId, int toNodeId, AircraftCategory category)
    {
        var route =
            TaxiPathfinder.FindRoute(layout, fromNodeId, toNodeId)
            ?? throw new InvalidOperationException($"TaxiBudgetDeriver: no A* route from node {fromNodeId} to {toNodeId} in {layout.AirportId}");

        double nominalKts = CategoryPerformance.TaxiSpeed(category);
        double turnRateDegSec = CategoryPerformance.GroundTurnRate(category);

        double optimalDistFt = 0;
        double optimalTurnDeg = 0;
        double optimalTimeSec = 0;
        int cornerCount = 0;
        double? prevArrivalBrg = null;

        foreach (var seg in route.Segments)
        {
            double distFt = seg.Edge.DistanceNm * GeoMath.FeetPerNm;
            optimalDistFt += distFt;

            // Per-segment arc-aware speed: straight segments use nominal,
            // arcs are capped by their geometry (tight fillet → low speed).
            double segMaxSafeKts = seg.Edge.Edge.MaxSafeSpeedKts(turnRateDegSec);
            double effKts = Math.Max(1.0, Math.Min(nominalKts, segMaxSafeKts));
            optimalTimeSec += distFt / (effKts * FtPerSecondsPerKt);

            double dep = seg.Edge.DepartureBearing;
            double arr = seg.Edge.ArrivalBearing;
            double intra = Math.Abs((((arr - dep) + 540.0) % 360.0) - 180.0);
            optimalTurnDeg += intra;

            if (prevArrivalBrg is { } prev)
            {
                double join = Math.Abs((((dep - prev) + 540.0) % 360.0) - 180.0);
                optimalTurnDeg += join;
                if (join > CornerAngleDeg)
                {
                    cornerCount++;
                }
            }

            prevArrivalBrg = arr;
        }

        double timeBudgetSec = (optimalTimeSec * TimeFudgeMultiplier) + (cornerCount * SecondsPerCorner);
        double turnBudgetDeg = (optimalTurnDeg * TurnFudgeMultiplier) + TurnSlackDeg;

        return new TaxiBudget(optimalDistFt, optimalTurnDeg, cornerCount, optimalTimeSec, timeBudgetSec, turnBudgetDeg, route);
    }
}
