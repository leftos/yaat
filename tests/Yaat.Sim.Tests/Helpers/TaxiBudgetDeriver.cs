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
/// timeBudgetSec   = optimalTimeSec × TimeFudgeMultiplier + cornerCount × SecondsPerCorner + StartupOverheadSec
/// turnBudgetDeg   = optimalTurnDeg + segCount × PerSegmentTurnOverheadDeg + TurnSlackDeg
/// </code>
///
/// <para>The arc-aware time floor is the key piece: jets traversing a tight
/// fillet arc at a GA-sized ramp are capped at the arc's safe speed
/// (potentially 5-10 kts) regardless of nominal taxi speed (jet 30 kts).
/// A flat distance/nominalKts budget under-allows these routes by 2-3×.
/// Per-segment integration matches how the router's Fastest preference
/// scores routes — same math, applied to the chosen route.
/// </para>
///
/// <para>Intra-segment turn matters because arc edges (fillets at junctions
/// and curved taxiways) sweep heading along their length — a single arc
/// segment can rotate the aircraft 90° with no segment join involved. A
/// deriver that summed only join turns would under-budget such routes by 4-5×.
/// </para>
///
/// <para>Turn budget uses a per-segment overhead, not a flat multiplier on
/// optimal turn. Empirically, the navigator's pure-pursuit tracking
/// accumulates ~15-25° of micro-correction per segment regardless of segment
/// geometry — a 110-segment route picks up 2000° of "noise" turn even when
/// the optimal route geometry only requires 300°. Multiplier-only budgets
/// underflow long clean routes by 6-8×. Per-segment overhead scales with
/// route length and cleanly separates calibration noise from genuine spins
/// (which produce 100°+/segment of accumulated turn).
/// </para>
/// </summary>
internal static class TaxiBudgetDeriver
{
    // Generous headroom over the optimal traversal so the budget catches spins/orbits (5-10× over)
    // without flaking on legitimately slow taxi. Ground turns are v/R-coupled and capped at the
    // realistic GroundTurnRate ceiling (jet 12 / TP 16 / piston 20 °/s), and tight fillets are held at
    // their curvature speed (a jet creeps a sharp ramp fillet at ~5 kt), so real taxi runs meaningfully
    // slower than a full-nominal-speed optimum — especially through corner-dense ramp routes. The
    // optimalTimeSec floor is arc-aware (per-segment MaxSafeSpeedKts) but still assumes nominal speed on
    // straights, so the corner approach/exit slowdown is absorbed by these overheads. The orbit
    // invariant (ThrowOnOrbit, 360° net turn on one segment) remains the real spin guard.
    public const double TimeFudgeMultiplier = 2.0;
    public const double SecondsPerCorner = 10.0;

    /// <summary>
    /// Constant startup overhead added to every time budget. Covers the
    /// parking-exit acceleration from gs=0 plus any initial slow-turn
    /// synthesis the navigator emits before settling into nominal taxi speed.
    /// Without this, very short routes (~300-500ft) under-budget because the
    /// distance/speed formula assumes the aircraft is already at TaxiSpeed.
    /// </summary>
    public const double StartupOverheadSec = 15.0;

    /// <summary>
    /// Per-segment cumulative-turn overhead added to the budget. Empirically
    /// calibrated against observed grid runs: healthy taxi accumulates
    /// 15-25°/segment of pure-pursuit micro-correction beyond the segment
    /// geometry's required turn. Setting this to 30° gives a clean safety
    /// margin on healthy routes while still flagging spins (which produce
    /// 100°+/segment of turn relative to segments actually traversed).
    /// </summary>
    public const double PerSegmentTurnOverheadDeg = 30.0;
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
            TaxiPathfinder.FindRoute(layout, fromNodeId, toNodeId, AircraftCategory.Jet)
            ?? throw new InvalidOperationException($"TaxiBudgetDeriver: no A* route from node {fromNodeId} to {toNodeId} in {layout.AirportId}");

        double nominalKts = CategoryPerformance.TaxiSpeed(category);

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
            double segMaxSafeKts = seg.Edge.Edge.MaxSafeSpeedKts(category);
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

        double timeBudgetSec = (optimalTimeSec * TimeFudgeMultiplier) + (cornerCount * SecondsPerCorner) + StartupOverheadSec;
        double turnBudgetDeg = optimalTurnDeg + (route.Segments.Count * PerSegmentTurnOverheadDeg) + TurnSlackDeg;

        return new TaxiBudget(optimalDistFt, optimalTurnDeg, cornerCount, optimalTimeSec, timeBudgetSec, turnBudgetDeg, route);
    }
}
