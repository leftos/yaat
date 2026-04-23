using Microsoft.Extensions.Logging;
using Yaat.Sim.Phases.Ground;

namespace Yaat.Sim.Phases.Tower;

/// <summary>
/// Classification of the lineup maneuver chosen by <see cref="LineUpGeometry.Compute"/>.
/// </summary>
public enum LineUpPathKind
{
    /// <summary>Geometry is unusable; the phase should fault and leave the aircraft stopped.</summary>
    Fault,

    /// <summary>
    /// Standard closed-form aligned lineup: straight nose-out (optional) →
    /// fillet arc onto centerline → rollout straight. Used when the aircraft
    /// can reach centerline from its current heading without consuming an
    /// unreasonable fraction of the remaining runway.
    /// </summary>
    Aligned,

    /// <summary>
    /// Pivot fallback: slow turn to perpendicular-toward-centerline → straight
    /// across until on centerline (minus turn radius) → slow turn to runway
    /// heading → rollout. Used when the aircraft's heading is too shallow
    /// relative to the runway for a straight path to fit (e.g. UAL859 at SFO
    /// 01R in issue #142, where straight would waste ~1860 ft of runway).
    /// </summary>
    Pivot,
}

/// <summary>
/// Immutable plan for a lineup maneuver. Produced by
/// <see cref="LineUpGeometry.Compute"/> from the aircraft pose and runway,
/// consumed by <see cref="LineUpPhase"/> as a state-machine driver. Each
/// path kind (<see cref="LineUpPathKind.Aligned"/>, <see cref="LineUpPathKind.Pivot"/>)
/// populates its own subset of fields; the unused fields carry default
/// values.
/// </summary>
public sealed record LineUpPathPlan
{
    public required LineUpPathKind Kind { get; init; }
    public string? FaultReason { get; init; }

    public required AircraftCategory Category { get; init; }
    public required double RunwayHeadingDeg { get; init; }
    public required double TurnAngleDeg { get; init; }

    /// <summary>
    /// Target speed for the straight/arc segments of the Aligned path. The
    /// Pivot path uses <see cref="PathPrimitiveSlowTurn.MaxSpeedKts"/> for
    /// its turns and <see cref="CategoryPerformance.TaxiCornerSpeed"/> for
    /// its perpendicular straight.
    /// </summary>
    public required double ArcSpeedKts { get; init; }

    /// <summary>True if the aircraft is already aligned with the runway heading (short-circuit path).</summary>
    public bool IsAlreadyAligned { get; init; }

    // ---- Aligned path: straight nose-out ----

    public double NoseOutFromLat { get; init; }
    public double NoseOutFromLon { get; init; }
    public double NoseOutToLat { get; init; }
    public double NoseOutToLon { get; init; }
    public double NoseOutLengthFt { get; init; }
    public double NoseOutBearingDeg { get; init; }

    // ---- Aligned path: arc ----

    public LineUpArcPlayback? InitialArcState { get; init; }
    public double ArcExitLat { get; init; }
    public double ArcExitLon { get; init; }

    // ---- Pivot path ----

    /// <summary>First pivot turn: rotates from aircraft heading to perpendicular-toward-centerline.</summary>
    public PathPrimitiveSlowTurn? PivotTurn1 { get; init; }

    /// <summary>Start of the perpendicular straight (= PivotTurn1 exit position).</summary>
    public double PivotStraightFromLat { get; init; }
    public double PivotStraightFromLon { get; init; }
    public double PivotStraightToLat { get; init; }
    public double PivotStraightToLon { get; init; }

    /// <summary>Length of the perpendicular straight in feet. Non-negative in a valid pivot plan.</summary>
    public double PivotStraightLengthFt { get; init; }
    public double PivotStraightBearingDeg { get; init; }

    /// <summary>Second pivot turn: rotates from perpendicular to runway heading, ending on centerline.</summary>
    public PathPrimitiveSlowTurn? PivotTurn2 { get; init; }

    // ---- Rollout (both paths) ----

    public double RolloutFromLat { get; init; }
    public double RolloutFromLon { get; init; }
    public double RolloutToLat { get; init; }
    public double RolloutToLon { get; init; }
    public double RolloutLengthFt { get; init; }

    public string Provenance { get; init; } = "";
}

/// <summary>
/// Pure geometry for lineup-onto-runway planning. Decides between a
/// closed-form aligned arc and a pivot-perpendicular-then-rotate fallback
/// based on how much along-runway distance a straight path would waste vs
/// the remaining runway length.
///
/// <para>
/// The module is a pure function of (runway, aircraft pose, category) — no
/// airport ground layout required. Fault conditions are returned as a plan
/// with <see cref="LineUpPathKind.Fault"/>; the caller is responsible for
/// stopping the aircraft and logging the reason.
/// </para>
/// </summary>
public static class LineUpGeometry
{
    private static readonly ILogger Log = SimLog.CreateLogger("LineUpGeometry");

    /// <summary>Below this turn-angle magnitude (deg) the aircraft is already aligned.</summary>
    public const double AlignedMaxTurnDeg = 5.0;

    /// <summary>Above this turn-angle magnitude (deg) the geometry is rejected.</summary>
    public const double MaxTurnDeg = 150.0;

    /// <summary>Rollout length past the arc exit / pivot-turn-2 exit, in feet.</summary>
    public const double RolloutLengthFt = 80.0;

    /// <summary>Safety factor for arc rotation-rate headroom (matches legacy LineUpPlanBuilder).</summary>
    public const double HeadroomSafetyFactor = 0.85;

    /// <summary>
    /// Threshold for straight-path waste as a fraction of remaining runway.
    /// Above this, the aligned path is rejected in favour of the pivot
    /// fallback. Per issue #142 analysis: UAL859 at SFO 01R wastes ~22.7%
    /// of remaining runway on a shallow-angle straight; a normal 90° hold-
    /// short approach wastes ~2% or less. 20% is the midpoint.
    /// </summary>
    public const double WasteFractionThreshold = 0.20;

    /// <summary>
    /// Compute the lineup plan for an aircraft given its pose and the
    /// assigned runway. The returned plan is either <see cref="LineUpPathKind.Aligned"/>,
    /// <see cref="LineUpPathKind.Pivot"/>, or <see cref="LineUpPathKind.Fault"/>
    /// (with a populated <see cref="LineUpPathPlan.FaultReason"/>).
    /// </summary>
    public static LineUpPathPlan Compute(RunwayInfo runway, double acLat, double acLon, TrueHeading acHeading, AircraftCategory category)
    {
        double rwyHdgDeg = runway.TrueHeading.Degrees;
        double acHdgDeg = acHeading.Degrees;

        // Signed turn: runway heading - aircraft heading, normalised to (-180°, 180°].
        // Positive = right turn, negative = left turn.
        double dthetaDeg = (((rwyHdgDeg - acHdgDeg) + 540.0) % 360.0) - 180.0;
        double turnMagnitudeDeg = Math.Abs(dthetaDeg);

        double lineUpRadiusFt = CategoryPerformance.LineUpTurnRadiusFt(category);
        double arcSpeedKts = ComputeArcSpeedKts(category, lineUpRadiusFt);

        // Already-aligned short-circuit: straight rollout only.
        if (turnMagnitudeDeg < AlignedMaxTurnDeg)
        {
            return BuildAlignedAlreadyAlignedPlan(runway, acLat, acLon, acHeading, category, dthetaDeg, arcSpeedKts);
        }

        if (turnMagnitudeDeg > MaxTurnDeg)
        {
            return Fault(category, rwyHdgDeg, dthetaDeg, arcSpeedKts, $"turn magnitude {turnMagnitudeDeg:F1}° exceeds max {MaxTurnDeg:F1}°");
        }

        double signedCrossNm = GeoMath.SignedCrossTrackDistanceNm(acLat, acLon, runway.ThresholdLatitude, runway.ThresholdLongitude, runway.TrueHeading);
        double crossFt = Math.Abs(signedCrossNm) * GeoMath.FeetPerNm;

        // Convergence check: aircraft must be moving toward centerline. The
        // rate of change of signed cross-track = v · sin(dHdg), where dHdg =
        // acHdg - rwyHdg. For convergence we need signedCross and sin(dHdg)
        // to have opposite signs.
        double dHdgDeg = acHdgDeg - rwyHdgDeg;
        double sinDHdg = Math.Sin(dHdgDeg * Math.PI / 180.0);
        if (signedCrossNm * sinDHdg >= -1e-12)
        {
            return Fault(
                category,
                rwyHdgDeg,
                dthetaDeg,
                arcSpeedKts,
                $"aircraft not converging to centerline (cross={signedCrossNm * GeoMath.FeetPerNm:F1}ft, dHdg={dHdgDeg:F1}°)"
            );
        }

        double noseWheelRadiusFt = CategoryPerformance.NoseWheelTurnRadiusFt(category);

        // Minimum cross-track for either path to fit: the pivot needs at
        // least one radius of cross-track so PivotTurn2 can end ON the
        // centerline. Less than that and the geometry collapses regardless
        // of which path we pick.
        if (crossFt < noseWheelRadiusFt)
        {
            return Fault(
                category,
                rwyHdgDeg,
                dthetaDeg,
                arcSpeedKts,
                $"|crossTrack|={crossFt:F1}ft < nose-wheel radius {noseWheelRadiusFt:F1}ft (pivot geometry collapses)"
            );
        }

        double wasteStraightFt = ComputeWasteStraightFt(crossFt, dHdgDeg);
        double alongFromThreshFt = GeoMath.AlongTrackDistanceNm(acLat, acLon, runway.ThresholdLatitude, runway.ThresholdLongitude, runway.TrueHeading) * GeoMath.FeetPerNm;
        double remainingRunwayFt = runway.LengthFt - alongFromThreshFt;
        if (remainingRunwayFt <= 0)
        {
            return Fault(
                category,
                rwyHdgDeg,
                dthetaDeg,
                arcSpeedKts,
                $"aircraft past runway end (along={alongFromThreshFt:F1}ft, length={runway.LengthFt:F1}ft)"
            );
        }

        // Decide: pivot when straight wastes too much runway OR when the
        // aligned arc radius cannot fit.
        bool mustPivotByRadius = crossFt < lineUpRadiusFt;
        bool mustPivotByWaste = wasteStraightFt > WasteFractionThreshold * remainingRunwayFt;

        if (mustPivotByRadius || mustPivotByWaste)
        {
            Log.LogDebug(
                "[LineUpGeometry] pivot chosen: cross={Cross:F0}ft dHdg={DHdg:F1}° wasteStraight={Waste:F0}ft remaining={Remain:F0}ft (radius-fail={RadFail}, waste-fail={WasteFail})",
                crossFt,
                dHdgDeg,
                wasteStraightFt,
                remainingRunwayFt,
                mustPivotByRadius,
                mustPivotByWaste
            );
            return BuildPivotPlan(runway, acLat, acLon, acHeading, category, dthetaDeg, arcSpeedKts, signedCrossNm, noseWheelRadiusFt);
        }

        Log.LogDebug(
            "[LineUpGeometry] aligned chosen: cross={Cross:F0}ft dHdg={DHdg:F1}° wasteStraight={Waste:F0}ft remaining={Remain:F0}ft",
            crossFt,
            dHdgDeg,
            wasteStraightFt,
            remainingRunwayFt
        );
        return BuildAlignedPlan(runway, acLat, acLon, acHeading, category, dthetaDeg, arcSpeedKts, signedCrossNm, lineUpRadiusFt);
    }

    // ---- Pure math helpers (public for unit testing) ----

    /// <summary>
    /// Along-runway distance (feet) that a straight-line lineup would consume
    /// before intercepting the runway centerline. Derived from the geometry
    /// of a constant-heading approach: with cross-track distance
    /// <paramref name="crossTrackFt"/> and heading delta
    /// <paramref name="dHdgDeg"/> relative to the runway, the along-runway
    /// component of the aircraft's path to centerline is
    /// <c>|crossTrack| / tan(|dHdg|)</c>.
    /// </summary>
    public static double ComputeWasteStraightFt(double crossTrackFt, double dHdgDeg)
    {
        double mag = Math.Abs(dHdgDeg);
        if (mag < 1e-9)
        {
            return double.PositiveInfinity;
        }
        return Math.Abs(crossTrackFt) / Math.Tan(mag * Math.PI / 180.0);
    }

    /// <summary>
    /// Conservative upper bound on along-runway distance consumed by the
    /// pivot path. A 90° pivot of radius r contributes ≤ r along-runway
    /// (sin 90° = 1 in the worst orientation); two pivots of radius r
    /// bound the pivot path at ≤ 2r. The perpendicular straight contributes
    /// zero along-runway by construction.
    /// </summary>
    public static double ComputeWastePivotFt(double noseWheelRadiusFt) => 2.0 * noseWheelRadiusFt;

    /// <summary>
    /// Target cruise speed through the aligned-path arc, chosen so that the
    /// tangent rotation rate <c>v/r</c> stays at <see cref="HeadroomSafetyFactor"/>
    /// of the category's <see cref="CategoryPerformance.GroundTurnRate"/>. Capped
    /// at <see cref="CategoryPerformance.TaxiCornerSpeed"/>.
    /// </summary>
    public static double ComputeArcSpeedKts(AircraftCategory cat, double radiusFt)
    {
        double turnRateRadPerSec = CategoryPerformance.GroundTurnRate(cat) * Math.PI / 180.0;
        double authoritySpeedKts = turnRateRadPerSec * HeadroomSafetyFactor * radiusFt * 3600.0 / GeoMath.FeetPerNm;
        double cornerCapKts = CategoryPerformance.TaxiCornerSpeed(cat);
        return Math.Min(authoritySpeedKts, cornerCapKts);
    }

    // ---- Plan builders ----

    private static LineUpPathPlan BuildAlignedAlreadyAlignedPlan(
        RunwayInfo runway,
        double acLat,
        double acLon,
        TrueHeading acHeading,
        AircraftCategory category,
        double dthetaDeg,
        double arcSpeedKts
    )
    {
        var (stopLat, stopLon) = GeoMath.ProjectPoint(acLat, acLon, runway.TrueHeading, RolloutLengthFt / GeoMath.FeetPerNm);
        return new LineUpPathPlan
        {
            Kind = LineUpPathKind.Aligned,
            Category = category,
            RunwayHeadingDeg = runway.TrueHeading.Degrees,
            TurnAngleDeg = dthetaDeg,
            ArcSpeedKts = arcSpeedKts,
            IsAlreadyAligned = true,
            NoseOutFromLat = acLat,
            NoseOutFromLon = acLon,
            NoseOutToLat = acLat,
            NoseOutToLon = acLon,
            NoseOutLengthFt = 0.0,
            NoseOutBearingDeg = acHeading.Degrees,
            InitialArcState = null,
            ArcExitLat = acLat,
            ArcExitLon = acLon,
            RolloutFromLat = acLat,
            RolloutFromLon = acLon,
            RolloutToLat = stopLat,
            RolloutToLon = stopLon,
            RolloutLengthFt = RolloutLengthFt,
            Provenance = "aligned-already",
        };
    }

    private static LineUpPathPlan BuildAlignedPlan(
        RunwayInfo runway,
        double acLat,
        double acLon,
        TrueHeading acHeading,
        AircraftCategory category,
        double dthetaDeg,
        double arcSpeedKts,
        double signedCrossNm,
        double radiusFt
    )
    {
        double rwyHdgDeg = runway.TrueHeading.Degrees;
        double acHdgDeg = acHeading.Degrees;
        double turnMagnitudeDeg = Math.Abs(dthetaDeg);
        bool rightTurn = dthetaDeg > 0;

        // Distance along aircraft heading to reach centerline intercept.
        double dHdgRad = (acHdgDeg - rwyHdgDeg) * Math.PI / 180.0;
        double sinDHdg = Math.Sin(dHdgRad);
        double distToCornerNm = -signedCrossNm / sinDHdg;

        // Tangent offset from the corner for a radius-r arc between the two
        // tangent lines meeting at angle turnMagnitudeDeg.
        double halfTurnRad = (turnMagnitudeDeg / 2.0) * Math.PI / 180.0;
        double tangentDistFt = radiusFt * Math.Tan(halfTurnRad);
        double tangentDistNm = tangentDistFt / GeoMath.FeetPerNm;

        double noseOutNm = distToCornerNm - tangentDistNm;
        if (noseOutNm < -1e-9)
        {
            return Fault(
                category,
                rwyHdgDeg,
                dthetaDeg,
                arcSpeedKts,
                $"aircraft past arc entry (distToCorner={distToCornerNm * GeoMath.FeetPerNm:F1}ft < tangent={tangentDistFt:F1}ft)"
            );
        }
        double noseOutNmClamped = Math.Max(0.0, noseOutNm);

        var (cornerLat, cornerLon) = GeoMath.ProjectPoint(acLat, acLon, acHeading, distToCornerNm);
        var (entryLat, entryLon) = GeoMath.ProjectPoint(acLat, acLon, acHeading, noseOutNmClamped);
        var (exitLat, exitLon) = GeoMath.ProjectPoint(cornerLat, cornerLon, runway.TrueHeading, tangentDistNm);

        double perpHdgDeg = ((acHdgDeg + (rightTurn ? 90.0 : -90.0)) + 360.0) % 360.0;
        var (centerLat, centerLon) = GeoMath.ProjectPoint(entryLat, entryLon, new TrueHeading(perpHdgDeg), radiusFt / GeoMath.FeetPerNm);
        double initialBearingFromCenterDeg = ((perpHdgDeg + 180.0) % 360.0 + 360.0) % 360.0;

        var arcState = new LineUpArcPlayback
        {
            CenterLat = centerLat,
            CenterLon = centerLon,
            RadiusFt = radiusFt,
            CurrentBearingFromCenterDeg = initialBearingFromCenterDeg,
            RemainingSweepDeg = turnMagnitudeDeg,
            RightTurn = rightTurn,
        };

        var (stopLat, stopLon) = GeoMath.ProjectPoint(exitLat, exitLon, runway.TrueHeading, RolloutLengthFt / GeoMath.FeetPerNm);

        return new LineUpPathPlan
        {
            Kind = LineUpPathKind.Aligned,
            Category = category,
            RunwayHeadingDeg = rwyHdgDeg,
            TurnAngleDeg = dthetaDeg,
            ArcSpeedKts = arcSpeedKts,
            IsAlreadyAligned = false,
            NoseOutFromLat = acLat,
            NoseOutFromLon = acLon,
            NoseOutToLat = entryLat,
            NoseOutToLon = entryLon,
            NoseOutLengthFt = noseOutNmClamped * GeoMath.FeetPerNm,
            NoseOutBearingDeg = acHdgDeg,
            InitialArcState = arcState,
            ArcExitLat = exitLat,
            ArcExitLon = exitLon,
            RolloutFromLat = exitLat,
            RolloutFromLon = exitLon,
            RolloutToLat = stopLat,
            RolloutToLon = stopLon,
            RolloutLengthFt = RolloutLengthFt,
            Provenance = "aligned",
        };
    }

    private static LineUpPathPlan BuildPivotPlan(
        RunwayInfo runway,
        double acLat,
        double acLon,
        TrueHeading acHeading,
        AircraftCategory category,
        double dthetaDeg,
        double arcSpeedKts,
        double signedCrossNm,
        double noseWheelRadiusFt
    )
    {
        double rwyHdgDeg = runway.TrueHeading.Degrees;
        double acHdgDeg = acHeading.Degrees;

        // Perpendicular-toward-centerline bearing:
        //   signedCross > 0 (aircraft right of runway) → perp = rwyHdg - 90°
        //   signedCross < 0 (aircraft left of runway)  → perp = rwyHdg + 90°
        bool aircraftOnLeft = signedCrossNm < 0;
        double perpTowardDeg = ((rwyHdgDeg + (aircraftOnLeft ? 90.0 : -90.0)) + 360.0) % 360.0;

        // First turn: aircraft heading → perpendicular-toward-centerline.
        var pivotTurn1 = PathPrimitiveBuilder.SlowTurn(
            fromLat: acLat,
            fromLon: acLon,
            fromHdgDeg: acHdgDeg,
            toHdgDeg: perpTowardDeg,
            radiusFt: noseWheelRadiusFt,
            maxSpeedKts: CategoryPerformance.SlowTurnSpeedKts,
            toNodeId: -1
        );

        // Compute end position of PivotTurn1.
        var (turn1ExitLat, turn1ExitLon) = SlowTurnExitPosition(pivotTurn1);

        // Cross-track of PivotTurn1 exit.
        double exit1SignedCrossNm = GeoMath.SignedCrossTrackDistanceNm(
            turn1ExitLat,
            turn1ExitLon,
            runway.ThresholdLatitude,
            runway.ThresholdLongitude,
            runway.TrueHeading
        );

        // PivotTurn2 entry must be noseWheelRadius ft off-centerline on the
        // same side as the aircraft, so its 90° arc ends ON centerline.
        double entry2SignedCrossFtTarget = aircraftOnLeft ? -noseWheelRadiusFt : +noseWheelRadiusFt;
        double exit1SignedCrossFt = exit1SignedCrossNm * GeoMath.FeetPerNm;

        // Straight segment goes from Turn1 exit along perpToward until the
        // aircraft's cross-track equals the Turn2-entry target.
        double straightLengthFt = aircraftOnLeft ? entry2SignedCrossFtTarget - exit1SignedCrossFt : exit1SignedCrossFt - entry2SignedCrossFtTarget;
        if (straightLengthFt < -1e-6)
        {
            return Fault(
                category,
                rwyHdgDeg,
                dthetaDeg,
                arcSpeedKts,
                $"pivot straight length would be negative ({straightLengthFt:F1}ft) — PivotTurn1 already overshoots Turn2 entry"
            );
        }
        double straightLengthFtClamped = Math.Max(0.0, straightLengthFt);

        var (turn2EntryLat, turn2EntryLon) = GeoMath.ProjectPoint(
            turn1ExitLat,
            turn1ExitLon,
            new TrueHeading(perpTowardDeg),
            straightLengthFtClamped / GeoMath.FeetPerNm
        );

        // Second turn: perpendicular → runway heading, ending on centerline.
        var pivotTurn2 = PathPrimitiveBuilder.SlowTurn(
            fromLat: turn2EntryLat,
            fromLon: turn2EntryLon,
            fromHdgDeg: perpTowardDeg,
            toHdgDeg: rwyHdgDeg,
            radiusFt: noseWheelRadiusFt,
            maxSpeedKts: CategoryPerformance.SlowTurnSpeedKts,
            toNodeId: -2
        );

        var (turn2ExitLat, turn2ExitLon) = SlowTurnExitPosition(pivotTurn2);

        var (stopLat, stopLon) = GeoMath.ProjectPoint(turn2ExitLat, turn2ExitLon, runway.TrueHeading, RolloutLengthFt / GeoMath.FeetPerNm);

        return new LineUpPathPlan
        {
            Kind = LineUpPathKind.Pivot,
            Category = category,
            RunwayHeadingDeg = rwyHdgDeg,
            TurnAngleDeg = dthetaDeg,
            ArcSpeedKts = arcSpeedKts,
            IsAlreadyAligned = false,
            PivotTurn1 = pivotTurn1,
            PivotStraightFromLat = turn1ExitLat,
            PivotStraightFromLon = turn1ExitLon,
            PivotStraightToLat = turn2EntryLat,
            PivotStraightToLon = turn2EntryLon,
            PivotStraightLengthFt = straightLengthFtClamped,
            PivotStraightBearingDeg = perpTowardDeg,
            PivotTurn2 = pivotTurn2,
            RolloutFromLat = turn2ExitLat,
            RolloutFromLon = turn2ExitLon,
            RolloutToLat = stopLat,
            RolloutToLon = stopLon,
            RolloutLengthFt = RolloutLengthFt,
            Provenance = "pivot",
        };
    }

    /// <summary>
    /// Compute the exit lat/lon of a <see cref="PathPrimitiveSlowTurn"/> by
    /// projecting from the arc centre along the final bearing-from-centre.
    /// </summary>
    private static (double Lat, double Lon) SlowTurnExitPosition(PathPrimitiveSlowTurn turn)
    {
        double signedSweep = turn.RightTurn ? turn.SweepDeg : -turn.SweepDeg;
        double finalBearingDeg = ((turn.StartBearingFromCenterDeg + signedSweep) % 360.0 + 360.0) % 360.0;
        return GeoMath.ProjectPoint(turn.CenterLat, turn.CenterLon, new TrueHeading(finalBearingDeg), turn.RadiusNm);
    }

    private static LineUpPathPlan Fault(AircraftCategory category, double rwyHdgDeg, double turnAngleDeg, double arcSpeedKts, string reason) =>
        new()
        {
            Kind = LineUpPathKind.Fault,
            FaultReason = reason,
            Category = category,
            RunwayHeadingDeg = rwyHdgDeg,
            TurnAngleDeg = turnAngleDeg,
            ArcSpeedKts = arcSpeedKts,
            Provenance = "fault",
        };
}
