using Microsoft.Extensions.Logging;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Phases.Ground;

/// <summary>
/// The near-line gate at a cleared runway crossing: an aircraft cleared across a runway does not enter it while traffic beyond
/// the far holding-position marking leaves it too little room to clear the runway (AIM 4-3-18.b, AIM 4-3-21.b), and crosses by
/// itself once the room opens. <see cref="TaxiingPhase"/> brakes onto the near bar for it while the aircraft has not committed
/// (<see cref="IsCommitted"/>); <see cref="HoldingShortPhase"/> keeps a cleared aircraft at the bar for it (<see cref="HoldsAtBar"/>).
/// </summary>
internal static class RunwayCrossingFarSideGate
{
    private static readonly ILogger Log = SimLog.CreateLogger("RunwayCrossingFarSideGate");

    /// <summary>
    /// Room (ft) on top of <see cref="RunwayTailClearance.MarginFt"/> an aircraft at rest at the near line waits for before it
    /// resumes, so traffic creeping at the edge of the room does not start and stop it by turns.
    /// </summary>
    internal const double ResumeMarginFt = 10.0;

    /// <summary>Distance (ft) from the path across and beyond within which another aircraft counts as on it.</summary>
    private const double PathLateralFt = 40.0;

    /// <summary>Largest heading difference (deg) from the path at which another aircraft counts as aligned with it.</summary>
    private const double AlignedDeg = 45.0;

    /// <summary>The gentlest deceleration (kt/s) the gate projects a stop from (owner ruling).</summary>
    private const double MinDecelKtsPerSec = 0.5;

    /// <summary>About half the longest airliner's length (ft), so the path is searched far enough to find a long leader's tail.</summary>
    private const double LongestHalfLengthFt = 140.0;

    /// <summary>Another aircraft on the path across and beyond: where along it (ft from the near bar), the leg's bearing, its length.</summary>
    private readonly record struct OnPath(AircraftState Aircraft, double AlongFt, double LegBearingDeg, double LengthFt);

    /// <summary>
    /// The aircraft judging its gate, how far ahead (s) it projects the others' stops, and whether a leader's own near-line gate
    /// counts toward the stop projected for it (<see cref="KnownStopAheadFt"/>): only at the top of a judgement, so a leader's
    /// gate is judged without its own leaders' gates and the judgement never recurses further.
    /// </summary>
    private readonly record struct Judge(PhaseContext Ctx, double LookAheadSeconds, bool JudgeLeadersGates);

    /// <summary>
    /// Whether an aircraft short of a cleared crossing bar, <paramref name="toStopFt"/> from its painted stop, has committed to
    /// the crossing. Rolling, it has once it can no longer stop with its nose at or short of the near holding-position marking
    /// even at the firm rate (<see cref="GroundStopBraking.StopBraking.Backstop"/>). At rest
    /// (<see cref="TaxiingPhase.StoppedGroundSpeedKts"/>) it can always stay where it is, so it has only when its nose is already
    /// over the marking through <paramref name="nearBar"/> square to the way across, <paramref name="acrossDeg"/>
    /// (<see cref="CrossingRunwayPhase.NosePastMarkingFt"/>), however the along-route measure to its stop reads.
    /// </summary>
    internal static bool IsCommitted(PhaseContext ctx, double toStopFt, LatLon nearBar, double acrossDeg) =>
        ctx.Aircraft.GroundSpeed < TaxiingPhase.StoppedGroundSpeedKts
            ? CrossingRunwayPhase.NosePastMarkingFt(ctx.Aircraft, nearBar, acrossDeg) > 0.0
            : GroundStopBraking.ChooseStopBraking(ctx, toStopFt) == GroundStopBraking.StopBraking.Backstop;

    /// <summary>
    /// Whether a <see cref="HoldingShortPhase"/> at <paramref name="holdShort"/>, now cleared, waits: the crossing queued after it
    /// starts from this bar and its far side is blocked (<see cref="IsBlocked"/>), judged as a resume from rest. The path beyond the
    /// far bar is the aircraft's taxi route after it, when the route reaches it. Covers the release of every hold at a crossing bar
    /// — the resume a stop at the bar queued and the crossings a CROSS at the bar queues.
    /// </summary>
    internal static bool HoldsAtBar(PhaseContext ctx, HoldShortPoint holdShort)
    {
        if (NextCrossingFrom(ctx.Aircraft, holdShort.NodeId) is not { } crossing)
        {
            return false;
        }

        TaxiRoute? route = ctx.Aircraft.Ground.AssignedTaxiRoute;
        int beyondStart = route is null ? 0 : SegmentAfterNode(route, crossing.TargetNodeId);
        return IsBlocked(ctx, (holdShort.NodeId, crossing.TargetNodeId), (route, beyondStart), resuming: true, judgeLeadersGates: true);
    }

    /// <summary>
    /// Whether the far side of the crossing between <paramref name="bars"/> (the near and the far bar node) leaves the aircraft
    /// too little room to clear the runway: some aircraft on its path, at the stop point projected for it, has its tail less than
    /// the aircraft's own length, the pair's stop gap (<see cref="GroundConflictDetector.StopGapFt"/>) and
    /// <see cref="RunwayTailClearance.MarginFt"/> — and <see cref="ResumeMarginFt"/> when <paramref name="resuming"/> from rest —
    /// past the far holding-position marking. The path is the straight line across between the bars, then <paramref name="beyond"/>'s route
    /// from its segment <c>Start</c> on, extended straight where it runs out. An aircraft between the bars counts only when it is
    /// aligned with the way across, so runway traffic is not traffic ahead. With <paramref name="judgeLeadersGates"/>, a leader
    /// held at a cleared crossing bar ahead of it by its own gate is projected to stop there (<see cref="KnownStopAheadFt"/>).
    /// </summary>
    internal static bool IsBlocked(
        PhaseContext ctx,
        (int Near, int Far) bars,
        (TaxiRoute? Route, int Start) beyond,
        bool resuming,
        bool judgeLeadersGates
    )
    {
        if (
            (ctx.GroundLayout is not { } layout)
            || (ctx.ListAircraft is not { } listAircraft)
            || !layout.Nodes.TryGetValue(bars.Near, out GroundNode? near)
            || !layout.Nodes.TryGetValue(bars.Far, out GroundNode? far)
        )
        {
            return false;
        }

        double ownLengthFt = AircraftLength.ResolveFt(ctx.Aircraft.AircraftType);
        double crossFt = GeoMath.DistanceNm(near.Position, far.Position) * GeoMath.FeetPerNm;
        double lookAheadSeconds =
            (crossFt + ownLengthFt) / (CategoryPerformance.RunwayCrossingSpeed(ctx.Category) * GroundStopBraking.FeetPerSecondPerKt);
        double extentFt =
            ownLengthFt + GroundConflictDetector.DefaultStopDistanceFt + RunwayTailClearance.MarginFt + ResumeMarginFt + LongestHalfLengthFt;
        List<LatLon> path = FarSidePath(layout, near.Position, far.Position, beyond, extentFt);
        double reachFt = Math.Max(crossFt, extentFt) + PathLateralFt;
        List<OnPath> onPath = AircraftOnPath(ctx.Aircraft, listAircraft(), path, crossFt, (far.Position, reachFt));
        double marginFt = RunwayTailClearance.MarginFt + (resuming ? ResumeMarginFt : 0.0);
        var judge = new Judge(ctx, lookAheadSeconds, judgeLeadersGates);
        foreach (OnPath other in onPath)
        {
            if (
                (ProjectedStopAlongFt(judge, other, onPath) is { } stopAlongFt)
                && LeavesTooLittleRoom(ctx.Aircraft, ownLengthFt, other, stopAlongFt - crossFt, marginFt)
            )
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The pilot's "holding short of runway 1L, traffic ahead" (<see cref="PilotResponder.BuildHoldingShortTrafficAhead"/>) for the
    /// crossing bar <paramref name="runwayTarget"/> names, for a combined id the end a pilot names it by
    /// (<see cref="RunwayCrossingEnd.Spoken"/>: the active end, else the nearest), routed to a student on ground or as an RPO's pilot speech or warning. A bar that
    /// names no runway makes no call.
    /// </summary>
    internal static void SayHoldingShortForTraffic(PhaseContext ctx, string? runwayTarget)
    {
        if (runwayTarget is null)
        {
            Log.LogDebug("[FarSideGate] {Callsign}: holding short for traffic at a bar that names no runway, no call", ctx.Aircraft.Callsign);
            return;
        }

        string runway = runwayTarget.Contains('/')
            ? RunwayCrossingEnd.Spoken(ctx.Aircraft, runwayTarget, ctx.GroundLayout, ctx.ActiveRunways)
            : HoldShortTarget.Describe(runwayTarget);
        PilotResponder.RouteSoloOrRpoTransmission(
            ctx.Aircraft,
            ctx.SoloTrainingMode,
            ctx.RpoShowPilotSpeech,
            ctx.StudentPositionType,
            PilotResponder.BuildHoldingShortTrafficAhead(ctx.Aircraft, runway),
            PilotResponder.SoloPositionsGround
        );
    }

    /// <summary>The crossing queued right after the current phase when it starts from <paramref name="nearBarNodeId"/>; else null.</summary>
    private static CrossingRunwayPhase? NextCrossingFrom(AircraftState aircraft, int nearBarNodeId) =>
        (aircraft.Phases is { } phases)
        && (phases.CurrentIndex + 1 < phases.Phases.Count)
        && (phases.Phases[phases.CurrentIndex + 1] is CrossingRunwayPhase crossing)
        && (crossing.ApproachNodeId == nearBarNodeId)
            ? crossing
            : null;

    /// <summary>
    /// The index of the segment after the one <paramref name="route"/> reaches <paramref name="nodeId"/> by, from the current
    /// segment on; the segment count (no path beyond) when the route does not reach it.
    /// </summary>
    private static int SegmentAfterNode(TaxiRoute route, int nodeId)
    {
        for (int i = Math.Max(0, route.CurrentSegmentIndex); i < route.Segments.Count; i++)
        {
            if (route.Segments[i].ToNodeId == nodeId)
            {
                return i + 1;
            }
        }

        return route.Segments.Count;
    }

    /// <summary>
    /// The aircraft other than <paramref name="self"/> on <paramref name="path"/> (<see cref="OnPathOf"/>), each first ruled out
    /// by one distance check against <paramref name="reach"/>, the far bar and the farthest any part of the path lies from it.
    /// </summary>
    private static List<OnPath> AircraftOnPath(
        AircraftState self,
        IReadOnlyList<AircraftState> all,
        List<LatLon> path,
        double crossFt,
        (LatLon FarBar, double Ft) reach
    )
    {
        List<OnPath> onPath = [];
        foreach (AircraftState other in all)
        {
            if (
                !ReferenceEquals(other, self)
                && other.IsOnGround
                && (GeoMath.DistanceNm(reach.FarBar, other.Position) * GeoMath.FeetPerNm <= reach.Ft)
                && (OnPathOf(other, path, crossFt) is { } found)
            )
            {
                onPath.Add(found);
            }
        }

        return onPath;
    }

    /// <summary>
    /// <paramref name="other"/> on <paramref name="path"/>: ahead of the near bar and within <see cref="PathLateralFt"/> of a leg
    /// (<see cref="ProjectOntoPath"/>), and, between the bars, aligned with the way across. Null otherwise.
    /// </summary>
    private static OnPath? OnPathOf(AircraftState other, List<LatLon> path, double crossFt)
    {
        if (
            (ProjectOntoPath(path, other.Position) is not { AlongFt: > 0.0 } at)
            || ((at.AlongFt < crossFt) && !IsAlignedWith(other, at.LegBearingDeg))
        )
        {
            return null;
        }

        return new OnPath(other, at.AlongFt, at.LegBearingDeg, AircraftLength.ResolveFt(other.AircraftType));
    }

    /// <summary>
    /// Where (ft along the path) <paramref name="other"/>'s centre is projected to stop. At rest
    /// (<see cref="GroundConflictDetector.HeldStationarySpeedKts"/>), where it is. Otherwise its travel to the stop projected for
    /// it (<see cref="ProjectedStopTravelFt"/>), or with none its travel over the look-ahead, taken along the path by the cosine
    /// of its heading off the leg — negative for an aircraft heading back toward the crossing. An aircraft aligned with the path
    /// and moving away (<see cref="IsAlignedWith"/>) stops no farther than behind the nearest aircraft at rest ahead of it
    /// (<see cref="QueueStopAlongFt"/>), and with no stop of its own and none such ahead it leaves room: null.
    /// </summary>
    private static double? ProjectedStopAlongFt(Judge judge, OnPath other, List<OnPath> onPath)
    {
        AircraftState aircraft = other.Aircraft;
        if (aircraft.GroundSpeed < GroundConflictDetector.HeldStationarySpeedKts)
        {
            return other.AlongFt;
        }

        bool movingAway = IsAlignedWith(aircraft, other.LegBearingDeg);
        double? queueStopFt = movingAway ? QueueStopAlongFt(other, onPath) : null;
        double? travelFt = ProjectedStopTravelFt(judge, aircraft);
        if ((travelFt is null) && movingAway)
        {
            return queueStopFt;
        }

        double offLegRad = GeoMath.SignedBearingDifference(other.LegBearingDeg, aircraft.TrueHeading.Degrees) * Math.PI / 180.0;
        double travelOrDriftFt = travelFt ?? (aircraft.GroundSpeed * GroundStopBraking.FeetPerSecondPerKt * judge.LookAheadSeconds);
        double stopAlongFt = other.AlongFt + (travelOrDriftFt * Math.Cos(offLegRad));
        return queueStopFt is { } capFt ? Math.Min(stopAlongFt, capFt) : stopAlongFt;
    }

    /// <summary>
    /// The farthest (ft along the path) <paramref name="other"/>'s centre can stop behind the nearest aircraft at rest
    /// (<see cref="GroundConflictDetector.HeldStationarySpeedKts"/>) ahead of it on the path: that aircraft's tail, less the
    /// pair's stop gap (<see cref="GroundConflictDetector.StopGapFt"/>) and <paramref name="other"/>'s half-length. Null with
    /// none ahead.
    /// </summary>
    private static double? QueueStopAlongFt(OnPath other, List<OnPath> onPath)
    {
        OnPath? nearest = null;
        foreach (OnPath ahead in onPath)
        {
            if (
                (ahead.AlongFt > other.AlongFt)
                && (ahead.Aircraft.GroundSpeed < GroundConflictDetector.HeldStationarySpeedKts)
                && ((nearest is null) || (ahead.AlongFt < nearest.Value.AlongFt))
            )
            {
                nearest = ahead;
            }
        }

        return nearest is { } stopped
            ? stopped.AlongFt - (stopped.LengthFt / 2.0) - GroundConflictDetector.StopGapFt(other.LengthFt, stopped.LengthFt) - (other.LengthFt / 2.0)
            : null;
    }

    /// <summary>
    /// Whether <paramref name="other"/>, its centre projected to stop <paramref name="stopCentrePastFarFt"/> past the far marking,
    /// leaves less than the room the aircraft needs beyond it: its own length, the pair's stop gap
    /// (<see cref="GroundConflictDetector.StopGapFt"/>) and <paramref name="marginFt"/>.
    /// </summary>
    private static bool LeavesTooLittleRoom(AircraftState self, double ownLengthFt, OnPath other, double stopCentrePastFarFt, double marginFt)
    {
        double tailPastFarFt = stopCentrePastFarFt - (other.LengthFt / 2.0);
        double neededFt = ownLengthFt + GroundConflictDetector.StopGapFt(ownLengthFt, other.LengthFt) + marginFt;
        if (tailPastFarFt >= neededFt)
        {
            return false;
        }

        if (Log.IsEnabled(LogLevel.Debug))
        {
            Log.LogDebug(
                "[FarSideGate] {Callsign}: far side blocked by {Other}: its tail stops {Tail:F0} ft past the far marking, {Needed:F0} ft needed",
                self.Callsign,
                other.Aircraft.Callsign,
                tailPastFarFt,
                neededFt
            );
        }

        return true;
    }

    /// <summary>
    /// The path across and beyond: the two bars, then the end node of each segment of <paramref name="beyond"/>'s route from its
    /// segment <c>Start</c> on until <paramref name="extentFt"/> past the far bar, extended straight along its last leg when the
    /// segments run out first.
    /// </summary>
    private static List<LatLon> FarSidePath(
        AirportGroundLayout layout,
        LatLon nearBar,
        LatLon farBar,
        (TaxiRoute? Route, int Start) beyond,
        double extentFt
    )
    {
        List<LatLon> path = [nearBar, farBar];
        double beyondFt = 0.0;
        TaxiRoute? route = beyond.Route;
        for (int i = beyond.Start; (route is not null) && (i < route.Segments.Count) && (beyondFt < extentFt); i++)
        {
            if (!layout.Nodes.TryGetValue(route.Segments[i].ToNodeId, out GroundNode? node))
            {
                break;
            }

            beyondFt += GeoMath.DistanceNm(path[^1], node.Position) * GeoMath.FeetPerNm;
            path.Add(node.Position);
        }

        if (beyondFt < extentFt)
        {
            var lastLegHeading = new TrueHeading(GeoMath.BearingTo(path[^2], path[^1]));
            path.Add(GeoMath.ProjectPoint(path[^1], lastLegHeading, (extentFt - beyondFt) / GeoMath.FeetPerNm));
        }

        return path;
    }

    /// <summary>
    /// Where <paramref name="point"/> lies along <paramref name="path"/> (ft from its start) and the bearing of the leg it lies on,
    /// taking the leg it is nearest; null when it is more than <see cref="PathLateralFt"/> from every leg.
    /// </summary>
    private static (double AlongFt, double LegBearingDeg)? ProjectOntoPath(List<LatLon> path, LatLon point)
    {
        (double AlongFt, double LegBearingDeg, double LateralFt)? best = null;
        double legStartFt = 0.0;
        for (int i = 1; i < path.Count; i++)
        {
            double legFt = GeoMath.DistanceNm(path[i - 1], path[i]) * GeoMath.FeetPerNm;
            double legDeg = GeoMath.BearingTo(path[i - 1], path[i]);
            double toPointFt = GeoMath.DistanceNm(path[i - 1], point) * GeoMath.FeetPerNm;
            double offRad = GeoMath.SignedBearingDifference(legDeg, GeoMath.BearingTo(path[i - 1], point)) * Math.PI / 180.0;
            double x = toPointFt * Math.Cos(offRad);
            double y = toPointFt * Math.Sin(offRad);
            double alongFt = Math.Clamp(x, 0.0, legFt);
            double lateralFt = Math.Sqrt(((x - alongFt) * (x - alongFt)) + (y * y));
            if ((lateralFt <= PathLateralFt) && ((best is null) || (lateralFt < best.Value.LateralFt)))
            {
                best = (legStartFt + alongFt, legDeg, lateralFt);
            }

            legStartFt += legFt;
        }

        return best is { } found ? (found.AlongFt, found.LegBearingDeg) : null;
    }

    private static bool IsAlignedWith(AircraftState other, double bearingDeg) =>
        Math.Abs(GeoMath.SignedBearingDifference(bearingDeg, other.TrueHeading.Degrees)) <= AlignedDeg;

    /// <summary>
    /// How far (ft) <paramref name="other"/> travels before the stop projected for it, in order: zero when it is below
    /// <see cref="GroundConflictDetector.HeldStationarySpeedKts"/>; the nearer of its known stop on its own route
    /// (<see cref="KnownStopAheadFt"/>) when it reaches it within the judge's look-ahead at its speed, and its braking distance
    /// at the rate it is braking at (<see cref="BrakingRateKtsPerSec"/>) when it stops within that time. Null when neither
    /// projects a stop.
    /// </summary>
    private static double? ProjectedStopTravelFt(Judge judge, AircraftState other)
    {
        double speedKts = other.GroundSpeed;
        if (speedKts < GroundConflictDetector.HeldStationarySpeedKts)
        {
            return 0.0;
        }

        double reachFt = speedKts * GroundStopBraking.FeetPerSecondPerKt * judge.LookAheadSeconds;
        double? travelFt = (KnownStopAheadFt(judge, other) is { } stopFt) && (stopFt <= reachFt) ? stopFt : null;
        if ((BrakingRateKtsPerSec(other) is { } decelKtsPerSec) && (speedKts / decelKtsPerSec <= judge.LookAheadSeconds))
        {
            double brakingFt = GroundStopBraking.StoppingDistanceFt(speedKts, decelKtsPerSec);
            travelFt = Math.Min(travelFt ?? brakingFt, brakingFt);
        }

        return travelFt;
    }

    /// <summary>
    /// The deceleration (kt/s) <paramref name="other"/> is braking to a stop at — its published brake rate, else its category's
    /// taxi rate — when the speed it is held to (<see cref="HeldToKts"/>) is under
    /// <see cref="GroundConflictDetector.HeldStationarySpeedKts"/> and that rate is at least <see cref="MinDecelKtsPerSec"/>;
    /// null otherwise, so an aircraft slowing to a fillet's corner speed is not taken for one stopping.
    /// </summary>
    private static double? BrakingRateKtsPerSec(AircraftState other)
    {
        if (HeldToKts(other) is not < GroundConflictDetector.HeldStationarySpeedKts)
        {
            return null;
        }

        double rate = other.Targets.DesiredDecelRate ?? CategoryPerformance.TaxiDecelRate(AircraftCategorization.Categorize(other.AircraftType));
        return rate >= MinDecelKtsPerSec ? rate : null;
    }

    /// <summary>
    /// The speed (kt) <paramref name="other"/> is held to: the lower of its speed target and its ground-conflict speed limit
    /// (<see cref="AircraftGroundOps.SpeedLimit"/>), so a leader the detector is braking is seen braking; null with neither.
    /// </summary>
    private static double? HeldToKts(AircraftState other) =>
        (other.Targets.TargetSpeed, other.Ground.SpeedLimit) switch
        {
            ({ } targetKts, { } limitKts) => Math.Min(targetKts, limitKts),
            (var targetKts, var limitKts) => targetKts ?? limitKts,
        };

    /// <summary>
    /// How far (ft) <paramref name="other"/> is from the nearest stop its own clearance sets ahead of it, as its taxi brakes for
    /// them (<see cref="TaxiingPhase"/>'s held speed): a GIVEWAY's give-way point, or the painted stop of the first bar on its
    /// taxi route it stops at (<see cref="FirstStopBarAhead"/>), else the route's end, whichever is nearer. Null with none of them.
    /// </summary>
    private static double? KnownStopAheadFt(Judge judge, AircraftState other)
    {
        PhaseContext ctx = judge.Ctx;
        // A give-way given on the landing rollout sets no stop until the aircraft is off its landing runway and taxiing.
        double? giveWayFt =
            (
                (!other.HasGiveWayPendingOnLandingRunway)
                && (other.Ground.Hold is { Kind: HoldKind.GiveWay, YieldTarget: { } yieldTarget })
                && (ctx.AircraftLookup?.Invoke(yieldTarget) is { } target)
                && (GroundConflictDetector.GiveWayStop(other, target, out _) is { } giveWay)
            )
                ? giveWay.ToStopFt
                : null;
        double? routeStopFt = RouteStopAheadFt(judge, other);
        return (giveWayFt, routeStopFt) switch
        {
            ({ } giveWayStopFt, { } routeFt) => Math.Min(giveWayStopFt, routeFt),
            _ => giveWayFt ?? routeStopFt,
        };
    }

    /// <summary>
    /// How far (ft) <paramref name="other"/> is from the painted stop of the first bar on its taxi route it stops at
    /// (<see cref="FirstStopBarAhead"/>), else from the route's end. Null without a route left or a layout.
    /// </summary>
    private static double? RouteStopAheadFt(Judge judge, AircraftState other)
    {
        if (
            (other.Ground.AssignedTaxiRoute is not { IsComplete: false } route) || ((other.Ground.Layout ?? judge.Ctx.GroundLayout) is not { } layout)
        )
        {
            return null;
        }

        return FirstStopBarAhead(judge, other, route, layout) is { } bar
            ? TaxiingPhase.AlongRouteDistanceToHoldShortFt(layout, route, other.Position, bar)
            : RemainingRouteFt(route, other.Position);
    }

    /// <summary>
    /// The first bar on <paramref name="route"/> <paramref name="other"/> stops at. When the judge judges leaders' gates, the
    /// first its own taxi brakes for (<see cref="TaxiingPhase.FirstStoppingBarAhead"/>, judged without its own leaders' gates):
    /// a cleared crossing bar its near-line gate holds it at counts, so a leader rolling toward a line it will be held at is not
    /// projected to roll on. Otherwise the first uncleared bar.
    /// </summary>
    private static HoldShortPoint? FirstStopBarAhead(Judge judge, AircraftState other, TaxiRoute route, AirportGroundLayout layout) =>
        judge.JudgeLeadersGates
            ? TaxiingPhase.FirstStoppingBarAhead(LeaderContext(judge.Ctx, other, layout), route, layout, judgeLeadersGates: false)?.Bar
            : TaxiingPhase.FirstUnclearedBarAhead(route)?.Bar;

    /// <summary><paramref name="leader"/>'s own phase context for judging its gate, over the world <paramref name="ctx"/> sees.</summary>
    private static PhaseContext LeaderContext(PhaseContext ctx, AircraftState leader, AirportGroundLayout layout) =>
        new()
        {
            ActiveRunways = ctx.ActiveRunways,
            Aircraft = leader,
            Targets = leader.Targets,
            Category = AircraftCategorization.Categorize(leader.AircraftType),
            DeltaSeconds = ctx.DeltaSeconds,
            Logger = ctx.Logger,
            GroundLayout = layout,
            ListAircraft = ctx.ListAircraft,
            AircraftLookup = ctx.AircraftLookup,
        };

    /// <summary>How far (ft) along <paramref name="route"/> from <paramref name="position"/> its last node is.</summary>
    private static double RemainingRouteFt(TaxiRoute route, LatLon position)
    {
        int first = Math.Max(0, route.CurrentSegmentIndex);
        double remainingFt = 0.0;
        for (int i = first; i < route.Segments.Count; i++)
        {
            TaxiRouteSegment segment = route.Segments[i];
            remainingFt +=
                i == first
                    ? GeoMath.DistanceNm(position, segment.Edge.ToNode.Position) * GeoMath.FeetPerNm
                    : segment.Edge.DistanceNm * GeoMath.FeetPerNm;
        }

        return remainingFt;
    }
}
