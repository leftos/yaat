using Microsoft.Extensions.Logging;
using Yaat.Sim.Data;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Phases.Pattern;

public sealed partial class VfrFollowPhase
{
    // ─── Turn-out to the downwind heading ───

    /// <summary>
    /// Starts a turn-out to the downwind heading when the lead is on base or final to a runway and the follower is on that
    /// runway's pattern side within <see cref="TurnOutRangeNm"/> of its threshold (<see cref="TurnOutCircuitFor"/>), and the
    /// follower is level with or ahead of the lead (its shortest path to the threshold, <see cref="ShortestPathToThresholdNm"/>,
    /// no longer than the lead's remaining path), its hold alongside the lead has stalled (<paramref name="stalledHold"/>), or
    /// a base break-off asked for it (<see cref="RequestTurnOut"/>). The follower cannot fall behind a same-speed lead, and
    /// continuing to the final ahead of it would be cutting in (AIM 4-3-4).
    /// </summary>
    private bool TryStartTurnOut(PhaseContext ctx, AircraftState lead, bool stalledHold)
    {
        bool requested = _turnOutRequested;
        _turnOutRequested = false;
        if (TurnOutCircuitFor(ctx, lead, requested) is not { } circuit)
        {
            if (requested)
            {
                Log.LogDebug(
                    "[VfrFollow] {Callsign}: no turn-out, {Lead} is no longer on base or final in range",
                    ctx.Aircraft.Callsign,
                    TargetCallsign
                );
            }

            return false;
        }

        string? reason =
            requested ? "base break-off"
            : stalledHold ? "stalled alongside at the offset cap"
            : IsLevelOrAhead(ctx, lead, circuit) ? "level with or ahead"
            : null;
        if (reason is null)
        {
            return false;
        }

        StartTurnOut(ctx, circuit, reason);
        return true;
    }

    /// <summary>
    /// The circuit a turn-out rejoins: the runway the lead is flying its base or final to while it is airborne on either (on the
    /// final in <see cref="FinalApproachPhase"/> or by geometry, <see cref="AirborneFollowHelper.IsOnFinalByGeometry"/>), in
    /// <see cref="TurnOutDirection"/> traffic, at its pattern altitude; null when the lead is on neither, or the follower is not
    /// on that circuit's pattern side, or (unless a base break-off <paramref name="requested"/> the turn-out) not within
    /// <see cref="TurnOutRangeNm"/> of the threshold.
    /// </summary>
    private FollowPatternReturn? TurnOutCircuitFor(PhaseContext ctx, AircraftState lead, bool requested)
    {
        if (lead.IsOnGround || (lead.Phases is not { AssignedRunway: { } runway } leadPhases) || !IsOnBaseOrFinalLeg(lead, runway))
        {
            return null;
        }

        FollowPatternReturn circuit = OwnReturnFor(runway) ?? NewTurnOutCircuit(ctx, runway, leadPhases);
        var threshold = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude);
        bool inRange = requested || (GeoMath.DistanceNm(ctx.Aircraft.Position, threshold) <= TurnOutRangeNm);
        return (inRange && IsOnPatternSide(ctx.Aircraft, runway, circuit.Direction)) ? circuit with { FromBase = false } : null;
    }

    /// <summary>
    /// A turn-out circuit to <paramref name="runway"/> this pursuit did not come from: in <see cref="TurnOutDirection"/> traffic,
    /// at the follower's pattern altitude for it (<see cref="ResolvePatternAltitudeFt"/>).
    /// </summary>
    private static FollowPatternReturn NewTurnOutCircuit(PhaseContext ctx, RunwayInfo runway, PhaseList leadPhases) =>
        new(runway, TurnOutDirection(runway, leadPhases), ResolvePatternAltitudeFt(ctx.Aircraft, runway, ctx.GroundLayout), FromBase: false);

    /// <summary>
    /// True when <paramref name="lead"/> flies its base, or its final (in <see cref="FinalApproachPhase"/> or by geometry), to
    /// <paramref name="runway"/>, its own assigned runway.
    /// </summary>
    private static bool IsOnBaseOrFinalLeg(AircraftState lead, RunwayInfo runway) =>
        (lead.Phases?.CurrentPhase is BasePhase or FinalApproachPhase) || AirborneFollowHelper.IsOnFinalByGeometry(lead, runway);

    /// <summary>The traffic direction of a turn-out circuit this pursuit did not come from: the lead's, else the runway's default.</summary>
    private static PatternDirection TurnOutDirection(RunwayInfo runway, PhaseList leadPhases) =>
        leadPhases.TrafficDirection ?? PatternGeometry.InferDefaultPatternDirection(runway) ?? PatternDirection.Left;

    /// <summary>
    /// True when the follower's shortest path to <paramref name="circuit"/>'s threshold is no longer than the lead's remaining
    /// path (<see cref="AirborneFollowHelper.LeadRemainingPathNm"/>).
    /// </summary>
    private static bool IsLevelOrAhead(PhaseContext ctx, AircraftState lead, FollowPatternReturn circuit) =>
        FollowerPathToThresholdNm(ctx.Aircraft, circuit) <= AirborneFollowHelper.LeadRemainingPathNm(lead, circuit.Runway);

    /// <summary>
    /// <paramref name="aircraft"/>'s shortest path to <paramref name="circuit"/>'s threshold (<see cref="ShortestPathToThresholdNm"/>)
    /// from its present position and heading, at its own turn radius.
    /// </summary>
    internal static double FollowerPathToThresholdNm(AircraftState aircraft, FollowPatternReturn circuit)
    {
        FinalFramePosition frame = FinalFrameOf(aircraft.Position, aircraft.TrueHeading, circuit.Runway, circuit.Direction);
        AircraftCategory category = AircraftCategorization.Categorize(aircraft.AircraftType);
        return ShortestPathToThresholdNm(frame, BasePhase.TurnRadiusNm(aircraft.GroundSpeed, category));
    }

    /// <summary>
    /// Begins the turn-out: level at the circuit's pattern altitude, climbing back to it from a base descent (an assigned
    /// altitude stands), the spacing excursion dropped, the turn toward the downwind heading, and the one call.
    /// </summary>
    private void StartTurnOut(PhaseContext ctx, FollowPatternReturn circuit, string reason)
    {
        var turnOut = new FollowTurnOut(circuit, ctx.Aircraft.Position);
        _turnOut = turnOut;
        _widen.Active = false;
        _widen.Side = 0;
        _parallelHold = null;
        if (ctx.Targets.AssignedAltitude is null)
        {
            ctx.Targets.TargetAltitude = circuit.PatternAltitudeFt;
            ctx.Targets.DesiredVerticalRate = null;
        }

        Log.LogDebug(
            "[VfrFollow] {Callsign}: turning out to the {Rwy} downwind heading behind {Lead} ({Reason})",
            ctx.Aircraft.Callsign,
            circuit.Runway.Designator,
            TargetCallsign,
            reason
        );
        SteerTurnOut(ctx, turnOut);
        ctx.Targets.TargetSpeed = AircraftPerformance.ApproachSpeed(ctx.AircraftType, ctx.Category);
        Pilot.PilotResponder.RouteByTunedPosition(
            ctx.Aircraft,
            Pilot.PilotReportRouting.From(ctx),
            Pilot.PilotResponder.BuildTurningDownwindForSpacing(ctx.Aircraft, TargetCallsign)
        );
    }

    /// <summary>
    /// One tick of the turn-out. A lead now landing another runway ends the follow with the instructor told
    /// (<see cref="AirborneFollowHelper.TryEndFollowLeadChangedRunway"/>). A lead that otherwise lands, goes around or leaves the
    /// final ends it too: the follower is already on a pattern-side downwind, so it re-enters the circuit as a downwind with its
    /// clearance, and says nothing. A lead lost from sight ends it the same way, with the loss handled as any follow's.
    /// </summary>
    private bool TickTurnOut(PhaseContext ctx, AircraftState? lead, FollowTurnOut turnOut)
    {
        if (AirborneFollowHelper.TryEndFollowLeadChangedRunway(ctx.Aircraft, lead))
        {
            ReturnToPattern(ctx, turnOut.Circuit);
            return true;
        }

        if ((lead is not null) && !IsOnBaseOrFinalTo(lead, turnOut.Circuit.Runway))
        {
            Log.LogDebug(
                "[VfrFollow] {Callsign}: {Lead} is no longer on base or final to {Rwy}, ending the turn-out",
                ctx.Aircraft.Callsign,
                TargetCallsign,
                turnOut.Circuit.Runway.Designator
            );
            AirborneFollowHelper.ClearFollowState(ctx.Aircraft);
            ReturnToPattern(ctx, turnOut.Circuit);
            return true;
        }

        if (AirborneFollowHelper.CheckLeadLifecycle(ctx))
        {
            ReturnToPattern(ctx, turnOut.Circuit);
            return true;
        }

        // CheckLeadLifecycle ends the follow when the lead is not found, so a null here ends it the same way.
        if (lead is null)
        {
            AirborneFollowHelper.ClearFollowState(ctx.Aircraft);
            ReturnToPattern(ctx, turnOut.Circuit);
            return true;
        }

        return TickTurnOutLeg(ctx, lead, turnOut);
    }

    /// <summary>
    /// True while <paramref name="lead"/> is airborne on its base, its final (<see cref="IsOnBaseOrFinalLeg"/>) or its landing to
    /// <paramref name="runway"/>.
    /// </summary>
    private static bool IsOnBaseOrFinalTo(AircraftState lead, RunwayInfo runway) =>
        !lead.IsOnGround
        && (lead.Phases?.AssignedRunway is { } leadRunway)
        && AirborneFollowHelper.IsSameRunway(leadRunway, runway)
        && ((lead.Phases.CurrentPhase is LandingPhase) || IsOnBaseOrFinalLeg(lead, runway));

    /// <summary>
    /// Flies the turned-out downwind: turns base behind the lead once the reversal is done, the exit holds
    /// (<see cref="ShouldExitTurnOut"/>, judged by the base projection, <see cref="BaseFollowSpacing.ProjectedBaseGapNm"/>, so
    /// the base it installs keeps) and the base crosses no parallel runway's final; past the distance limit
    /// (<see cref="TurnOutPastLimit"/>) holds the heading for the controller; otherwise keeps the offset band at the speed floor.
    /// </summary>
    private bool TickTurnOutLeg(PhaseContext ctx, AircraftState lead, FollowTurnOut turnOut)
    {
        RunwayInfo runway = turnOut.Circuit.Runway;
        var threshold = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude);
        FinalFramePosition frame = FinalFrameOf(ctx.Aircraft.Position, ctx.Aircraft.TrueHeading, runway, turnOut.Circuit.Direction);
        bool reversed = ctx.Aircraft.TrueHeading.AbsAngleTo(runway.TrueHeading.ToReciprocal()) <= TurnOutCorrectionDeg;
        bool leadPassedAbeam = AirborneFollowHelper.AlongFinalNm(lead.Position, runway) < frame.AlongNm;
        if (
            reversed
            && ShouldExitTurnOut(
                leadPassedAbeam,
                BaseFollowSpacing.ProjectedBaseGapNm(ctx, lead, runway),
                AirborneFollowHelper.PatternSpacingNm(lead)
            )
            && !CapturePathCrossesParallelFinal(
                ctx.Aircraft.Position,
                GeoMath.ProjectPoint(threshold, runway.TrueHeading.ToReciprocal(), frame.AlongNm),
                runway
            )
        )
        {
            TurnBaseBehindLead(ctx, turnOut, frame);
            return true;
        }

        double startAlongNm = AirborneFollowHelper.AlongFinalNm(turnOut.StartPoint, runway);
        if (TurnOutPastLimit(frame.AlongNm, startAlongNm))
        {
            HoldForBaseTurn(ctx, turnOut, frame);
            return true;
        }

        SteerTurnOut(ctx, turnOut);
        ctx.Targets.TargetSpeed = AircraftPerformance.ApproachSpeed(ctx.AircraftType, ctx.Category);
        return false;
    }

    /// <summary>
    /// Ends the turn-out with a base turn in the pattern direction from where the follower is: the base, final and landing on
    /// the circuit's runway, the base's final-turn distance the follower's present distance out, still following, with its
    /// clearance carried. The circuit is flown at a satisfied deferred pattern altitude (<see cref="ClaimSatisfiedDeferral"/>),
    /// else an altitude the controller assigned, else the pattern altitude the turn-out captured.
    /// </summary>
    private void TurnBaseBehindLead(PhaseContext ctx, FollowTurnOut turnOut, FinalFramePosition frame)
    {
        FollowPatternReturn circuit = turnOut.Circuit;
        double patternAltitudeFt = ClaimSatisfiedDeferral(ctx, circuit.Runway) ?? ctx.Targets.AssignedAltitude ?? circuit.PatternAltitudeFt;
        List<Phase> legs = PatternBuilder.BuildCircuit(
            new PatternCircuitRequest
            {
                Runway = circuit.Runway,
                Category = ctx.Category,
                AircraftType = ctx.Aircraft.AircraftType,
                WindSpeedKt = ctx.Aircraft.WindSpeedKts,
                Direction = circuit.Direction,
                EntryLeg = PatternEntryLeg.Base,
                TouchAndGo = false,
                FinalDistanceNm = frame.AlongNm,
                PatternSizeNm = null,
                AltitudeOverrideFt = patternAltitudeFt,
                AirportRunways = NavigationDatabase.Instance.GetRunways(circuit.Runway.AirportId),
                AuthoredRunway = (ctx.GroundLayout ?? ctx.Aircraft.Ground.Layout)?.FindRunway(circuit.Runway.Designator),
                ActiveRunways = ctx.ActiveRunways,
                Commanded = PatternCommanded.Of(ctx.Aircraft),
            }
        );
        Log.LogDebug(
            "[VfrFollow] {Callsign}: {Lead} has passed, turning base to {Rwy} {Along:F2} nm out",
            ctx.Aircraft.Callsign,
            TargetCallsign,
            circuit.Runway.Designator,
            frame.AlongNm
        );
        InstallJoinedCircuit(ctx, circuit.Runway, circuit.Direction, legs, keepFollowing: true);
    }

    /// <summary>
    /// The turn-out ran out of room before the lead passed and left space behind it: the follow ends into an extended downwind
    /// from where the follower is, holding the heading for the controller's base turn (B's no-room rule), with an RPO note and
    /// no second call.
    /// </summary>
    private void HoldForBaseTurn(PhaseContext ctx, FollowTurnOut turnOut, FinalFramePosition frame)
    {
        Log.LogDebug(
            "[VfrFollow] {Callsign}: no room to turn base behind {Lead}, {Along:F2} nm out; holding the downwind heading",
            ctx.Aircraft.Callsign,
            TargetCallsign,
            frame.AlongNm
        );
        ctx.Aircraft.PendingWarnings.Add(
            $"{ctx.Aircraft.Callsign} turned out behind {TargetCallsign} with no room to turn base behind it, "
                + "holding the downwind heading, awaiting a base turn"
        );
        InstallExtendedDownwind(
            ctx,
            turnOut.Circuit.Runway,
            turnOut.Circuit.Direction,
            ctx.Targets.AssignedAltitude ?? turnOut.Circuit.PatternAltitudeFt
        );
    }

    /// <summary>
    /// Steers the turn-out. Until the heading is within <see cref="TurnOutCorrectionDeg"/> of the downwind heading, the reversal
    /// turn toward it, the way that first moves away from the final (<see cref="ReversalTurn"/>). Then the downwind heading,
    /// corrected <see cref="TurnOutCorrectionDeg"/> outward below the offset band's floor (the excursion's offset cap,
    /// <see cref="ExcursionLimitsFor"/>) or inward above its ceiling (<see cref="TurnOutOffsetCorrection"/>), unless the
    /// correction would carry the follower toward a parallel runway's final.
    /// </summary>
    private static void SteerTurnOut(PhaseContext ctx, FollowTurnOut turnOut)
    {
        RunwayInfo runway = turnOut.Circuit.Runway;
        PatternDirection direction = turnOut.Circuit.Direction;
        TrueHeading downwind = runway.TrueHeading.ToReciprocal();
        FinalFramePosition frame = FinalFrameOf(ctx.Aircraft.Position, ctx.Aircraft.TrueHeading, runway, direction);
        if (ctx.Aircraft.TrueHeading.AbsAngleTo(downwind) > TurnOutCorrectionDeg)
        {
            ctx.Targets.TargetTrueHeading = downwind;
            ctx.Targets.PreferredTurnDirection = ReversalTurn(frame.HeadingDeg, direction);
            return;
        }

        ctx.Targets.PreferredTurnDirection = null;
        double floorNm = ExcursionLimitsFor(ctx).OffsetCapNm;
        int correction = TurnOutOffsetCorrection(frame.PatternSideNm, floorNm, floorNm + TurnOutBandWidthNm);
        if ((correction != 0) && CorrectionMeetsParallelFinal(ctx.Aircraft.Position, runway, direction, correction, frame.PatternSideNm, floorNm))
        {
            correction = 0;
        }

        ctx.Targets.TargetTrueHeading = TurnOutHeading(downwind, direction, correction);
    }

    /// <summary>
    /// True when an offset correction (+1 outward, -1 inward) would carry the follower toward a parallel runway's extended
    /// centerline within its distance to the band edge plus <see cref="AirborneFollowHelper.TrailParallelFinalMarginNm"/>.
    /// </summary>
    internal static bool CorrectionMeetsParallelFinal(
        LatLon position,
        RunwayInfo runway,
        PatternDirection direction,
        int correction,
        double patternSideNm,
        double floorNm
    )
    {
        int sign = direction == PatternDirection.Right ? 1 : -1;
        // Outward from the downwind heading is to its left in right traffic, to its right in left traffic.
        int side = -sign * correction;
        double toBandEdgeNm = correction > 0 ? floorNm - patternSideNm : patternSideNm - (floorNm + TurnOutBandWidthNm);
        double reachNm = toBandEdgeNm + AirborneFollowHelper.TrailParallelFinalMarginNm;
        return ExcursionMeetsParallelFinal(position, runway.TrueHeading.ToReciprocal(), side, runway, reachNm);
    }

    /// <summary>
    /// The turn toward the downwind heading that first moves the follower away from the final centerline: against the pattern
    /// direction from a heading on the base side of the downwind heading (from base, the 90° turn back), with it otherwise
    /// (from a heading parallel to the final, the 180° turn outward).
    /// </summary>
    internal static TurnDirection ReversalTurn(double headingDeg, PatternDirection direction)
    {
        bool againstPattern = headingDeg < 0.0;
        bool right = (direction == PatternDirection.Right) != againstPattern;
        return right ? TurnDirection.Right : TurnDirection.Left;
    }

    /// <summary>
    /// The offset correction for a follower <paramref name="patternSideNm"/> from the final centerline: +1 (outward) below
    /// <paramref name="floorNm"/>, -1 (inward) above <paramref name="ceilingNm"/>, 0 within the band.
    /// </summary>
    internal static int TurnOutOffsetCorrection(double patternSideNm, double floorNm, double ceilingNm)
    {
        if (patternSideNm < floorNm)
        {
            return 1;
        }

        return patternSideNm > ceilingNm ? -1 : 0;
    }

    /// <summary>The downwind heading turned <see cref="TurnOutCorrectionDeg"/> outward (+1), inward (-1) or not at all (0).</summary>
    internal static TrueHeading TurnOutHeading(TrueHeading downwind, PatternDirection direction, int correction)
    {
        int sign = direction == PatternDirection.Right ? 1 : -1;
        return downwind + (-sign * correction * TurnOutCorrectionDeg);
    }

    /// <summary>
    /// The turn-out's exit: the lead has passed abeam and the gap a base turned now would roll out with
    /// (<see cref="BaseFollowSpacing.ProjectedBaseGapNm"/>) is at least the pattern spacing <paramref name="requiredNm"/> plus
    /// <see cref="TurnOutExitMarginNm"/>.
    /// </summary>
    internal static bool ShouldExitTurnOut(bool leadPassedAbeam, double exitGapNm, double requiredNm) =>
        leadPassedAbeam && (exitGapNm >= (requiredNm + TurnOutExitMarginNm));

    /// <summary>
    /// True when the turned-out follower is <see cref="TurnOutMaxExtensionNm"/> along the final past its turn-out point, or
    /// <see cref="TurnOutMaxAlongFinalNm"/> out from the threshold.
    /// </summary>
    internal static bool TurnOutPastLimit(double alongNm, double startAlongNm) =>
        (alongNm - startAlongNm >= TurnOutMaxExtensionNm) || (alongNm >= TurnOutMaxAlongFinalNm);

    /// <summary>
    /// <paramref name="position"/> and <paramref name="heading"/> in <paramref name="runway"/>'s final frame for a
    /// <paramref name="direction"/> circuit.
    /// </summary>
    internal static FinalFramePosition FinalFrameOf(LatLon position, TrueHeading heading, RunwayInfo runway, PatternDirection direction)
    {
        var threshold = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude);
        double sign = direction == PatternDirection.Right ? 1.0 : -1.0;
        double alongNm = AirborneFollowHelper.AlongFinalNm(position, runway);
        double patternSideNm = sign * GeoMath.SignedCrossTrackDistanceNm(position, threshold, runway.TrueHeading);
        // Compass bearings grow clockwise; the frame's heading grows toward the pattern side, which is clockwise of the
        // downwind heading in left traffic and counter-clockwise of it in right traffic.
        double offDownwindDeg = ((((runway.TrueHeading.ToReciprocal().Degrees - heading.Degrees) % 360.0) + 540.0) % 360.0) - 180.0;
        double headingDeg = sign * offDownwindDeg;
        return new FinalFramePosition(alongNm, patternSideNm, headingDeg <= -180.0 ? headingDeg + 360.0 : headingDeg);
    }

    /// <summary>
    /// The follower's shortest path (nm) to the threshold, turning to the final in the pattern direction at
    /// <paramref name="turnRadiusNm"/>:
    /// <list type="bullet">
    /// <item><description>from a heading between the base heading and the heading straight away from the centerline, the turn
    /// in the pattern direction onto the base heading, the base to the final-turn point and the quarter-circle final turn
    /// (from the downwind heading, the base-now path);</description></item>
    /// <item><description>from a heading between the base heading and the final heading, the straight leg to the final-turn
    /// point on its own intercept, the turn onto the final and the final; an intercept at or past the threshold, straight
    /// there;</description></item>
    /// <item><description>from a heading in toward the threshold and away from or parallel to the centerline, straight
    /// there.</description></item>
    /// </list>
    /// </summary>
    internal static double ShortestPathToThresholdNm(FinalFramePosition frame, double turnRadiusNm)
    {
        double x = frame.AlongNm;
        double y = frame.PatternSideNm;
        double r = turnRadiusNm;
        if (frame.HeadingDeg is >= -90.0 and <= 90.0)
        {
            double headingRad = frame.HeadingDeg * Math.PI / 180.0;
            double turnRad = headingRad + (Math.PI / 2.0);
            // The turn onto the base heading ends at the circle's point abeam its centre, one radius outward along the final.
            double endX = x + (r * (1.0 + Math.Sin(headingRad)));
            double endY = y - (r * Math.Cos(headingRad));
            return (r * turnRad) + BaseAndFinalNm(endX, endY, r);
        }

        return frame.HeadingDeg < -90.0 ? InterceptPathNm(x, y, frame.HeadingDeg, r) : Math.Sqrt((x * x) + (y * y));
    }

    /// <summary>
    /// A base flown from (<paramref name="x"/>, <paramref name="y"/>) to the final-turn point, the quarter-circle turn and the
    /// final.
    /// </summary>
    private static double BaseAndFinalNm(double x, double y, double r) => Math.Max(0.0, y - r) + (Math.PI / 2.0 * r) + Math.Max(0.0, x - r);

    /// <summary>
    /// A leg flown on <paramref name="headingDeg"/> (between the base heading and the final heading) to the turn onto the final,
    /// the turn and the final; straight to the threshold when the leg meets the centerline at or past it.
    /// </summary>
    private static double InterceptPathNm(double x, double y, double headingDeg, double r)
    {
        double interceptRad = (headingDeg + 180.0) * Math.PI / 180.0;
        double interceptAlongNm = x - (y / Math.Tan(interceptRad));
        if (interceptAlongNm <= 0.0)
        {
            return Math.Sqrt((x * x) + (y * y));
        }

        double legNm = y / Math.Sin(interceptRad);
        double turnLeadNm = r * Math.Tan(interceptRad / 2.0);
        return Math.Max(0.0, legNm - turnLeadNm) + (r * interceptRad) + Math.Max(0.0, interceptAlongNm - turnLeadNm);
    }
}
