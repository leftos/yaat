using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Pathfinding;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Phases.Ground;

/// <summary>
/// Follows a target aircraft on the ground over the taxi graph. On its first tick it plans how to join the lead's taxi
/// path (<see cref="FollowRoutePlanner.Plan"/>) and drives that follow route — its route to the merge node, then the lead's
/// path from there — through its own <see cref="GroundNavigator"/>, never the aircraft's assigned route. Short of a merge the
/// lead has not passed, it gives way there, short of the lead's track as a <c>GIVEWAY</c> stops, until the lead's tail is past
/// the merge and the follow gap is open. It keeps the nose-to-tail gap along the path (<see cref="FollowGap"/>) and stops at
/// every runway bar it has no crossing for. With no plan to join it holds in position:
/// planning again each tick while the lead is not yet on a taxiway (or there is no layout), for good with no taxi path to the
/// lead's path or the follower ahead of the lead. It never stops on a runway or inside its hold line: with no follow route there,
/// it first drives a clearing route off it (<see cref="ClearingRoute"/>). When the target is deleted or no longer on the ground,
/// the follow drops its route, drives clear of any runway hold line it is inside, brakes along its clearing route to rest, then
/// completes into a <see cref="HoldingInPositionPhase"/>. Requires PhaseContext.AircraftLookup to resolve the target.
/// </summary>
public sealed class FollowingPhase(string targetCallsign) : Phase
{
    private static readonly ILogger Log = SimLog.CreateLogger("FollowingPhase");

    /// <summary>
    /// Walking-pace floor the follower closes up at behind a crawling lead — matching the lead's speed verbatim would
    /// leave the follower at the close-follow band's edge. Behind a stopped lead the follower rides the brake curve alone.
    /// </summary>
    private const double FollowCloseUpSpeedKts = 5.0;

    /// <summary>
    /// How far (ft) beyond the stop gap the follower counts as at it. The brake curve is read a tick ahead, so the follower
    /// comes to rest up to a tick of its last speed short of the gap; without this window it would creep up the rest of
    /// the way and stop again.
    /// </summary>
    private const double FollowStopSettleFt = 1.0;

    /// <summary>The follow owns every runway-bar stop through its bar scan, so its navigator treats every hold-short as cleared.</summary>
    private static readonly Func<int, bool> AlwaysCleared = static _ => true;

    private GroundNavigator _nav = new();

    /// <summary>Whether the navigator has set up the follow route's current segment; false after a plan or a restore.</summary>
    private bool _navSetUp;

    /// <summary>The follow route: the route to the merge node, then the lead's path from it. Null until planned.</summary>
    private TaxiRoute? _followRoute;

    /// <summary>The index of the follow route's first segment on the lead's path (the segment out of the merge node).</summary>
    private int _mergeSegmentIndex;

    /// <summary>
    /// The follow route read as the plan it came from, for the along-path gap and the give-way stop: the merge node, the
    /// follower's route to it (its segment index kept at the follow route's by <see cref="SyncPathToMerge"/>), the lead's path
    /// edge into it, the lead's path from it, and the lead's track through it — the edge into the merge and the first out — that
    /// the give-way stop keeps clear of. Null with no route.
    /// </summary>
    private sealed record FollowView(
        int MergeNode,
        TaxiRoute PathToMerge,
        DirectionalEdge? LeadEdgeIntoMerge,
        IReadOnlyList<DirectionalEdge> LeadPathFromMerge,
        IReadOnlyList<DirectionalEdge> LeadTrackAtMerge
    );

    /// <summary>The plan view of the follow route (<see cref="FollowView"/>). Null with no route.</summary>
    private FollowView? _plan;

    /// <summary>Whether the follower is giving way short of the merge until the lead's tail clears it and the gap is open.</summary>
    private bool _givingWay;

    /// <summary>
    /// Set when the plan found no taxi path to the lead's path or the follower ahead of the lead: the follow holds in position
    /// for good, planning no more, until a new command replaces it. A <see cref="FollowRoutePlan.WaitForLead"/> plans again
    /// each tick instead. Snapshotted, so a restored follow holds where the live one held rather than planning again.
    /// </summary>
    private bool _unjoinable;

    /// <summary>
    /// The clearing route: with no follow route to drive while some part of the follower is on a runway or inside its hold line,
    /// the taxi route to the nearest hold-short bar of that runway ahead and on past it, driven until the tail and both wingtips
    /// are past that bar's hold line, then braked along to rest, so the follower never holds on a runway (AIM 4-3-21). Null when
    /// not clearing. Snapshotted.
    /// </summary>
    private TaxiRoute? _clearingRoute;

    /// <summary>The hold-short bar node the clearing route clears; null when not clearing. Snapshotted.</summary>
    private int? _clearingBarNodeId;

    /// <summary>
    /// Whether the follower is past its clearing bar's hold line and braking along the clearing route to rest. Not snapshotted:
    /// read again from the follower's position on the first tick after a restore.
    /// </summary>
    private bool _brakingClear;

    /// <summary>The rest of the clearing route's edges, refilled each tick traffic ahead on it is looked for; not state.</summary>
    private readonly List<DirectionalEdge> _clearingPathBuffer = [];

    /// <summary>
    /// The runways a clearing route has been tried for since the follow last installed a follow route, so a hold inside a hold
    /// line with no way out, and a clearing already driven, are not tried again every tick, while a follower inside a second,
    /// intersecting runway's hold line still clears that one. Snapshotted.
    /// </summary>
    private List<RunwayIdentifier> _clearingAttemptedRunways = [];

    /// <summary>How far (ft) past the hold line, beyond the aircraft's own length, a clearing route runs on.</summary>
    private const double ClearingBeyondBarFt = 50.0;

    /// <summary>How much nearer (ft) a runway's centreline an edge may end and still count as leading away from it.</summary>
    private const double AwayToleranceFt = 1.0;

    /// <summary>How far ahead (ft) of the aircraft its heading is probed to tell whether it takes it away from a runway.</summary>
    private const double HeadingProbeFt = 50.0;

    /// <summary>The speed (kt) below which another aircraft at a runway exit counts as standing there, occupying it.</summary>
    private const double AtRestKts = 1.0;

    /// <summary>The reason a hold in position was last logged for, so it is logged once. Logging only: not snapshotted.</summary>
    private string? _loggedHoldReason;

    /// <summary>Whether this follow has logged that its type has no wingspan (<see cref="NoteTailOnlyOnce"/>). Logging only: not snapshotted.</summary>
    private bool _loggedTailOnly;

    private const double HoldShortDetectionNm = 0.02; // ~120 ft
    private const double HoldShortAngleThreshold = 90.0;

    /// <summary>
    /// How far (ft) beyond <see cref="HoldShortDetectionNm"/> a bar may sit off the follower's line of travel and still be
    /// one it is rolling toward: a taxiway's half-width plus the slack of steering on the lead rather than a centerline.
    /// </summary>
    private const double BarCorridorHalfWidthFt = 50.0;

    /// <summary>How close (ft) to a bar's stop the hold is taken — the same window a taxiing aircraft takes a set-back stop in.</summary>
    private const double BarStopTakeFt = GroundNavigator.SetBackStopMarginFt + 1.0;

    private const double LogIntervalSeconds = 3.0;

    private readonly string _targetCallsign = targetCallsign;
    private double _timeSinceLastLog;

    /// <summary>
    /// The runway bar the follow is stopping at, held from the tick it is first seen until the hold is taken there, so it
    /// stays the stop once the bar is off the nose — an aircraft cutting the corner onto the taxiway passes abeam the bar
    /// node with its nose still short of the hold line. Null with no bar ahead.
    /// </summary>
    private int? _latchedBarNodeId;

    /// <summary>The bearing (deg true) from the latched bar back up the taxiway edge leading into it, read when it was latched.</summary>
    private double? _latchedBarApproachDeg;

    /// <summary>
    /// The straight taxi edge the moving follower was last found on, by its end nodes (<c>Nodes[0]</c>, <c>Nodes[1]</c>):
    /// where <see cref="RefreshCurrentTaxiway"/> looks first. Null before the first find and while off every taxiway.
    /// </summary>
    private (int NodeA, int NodeB)? _taxiEdgeNodeIds;

    public string TargetCallsign => _targetCallsign;

    private IReadOnlyList<RunwayIdentifier> _crossingClearedRunways = [];
    private bool _hasBeenOnClearedRunway;

    /// <summary>
    /// Runways whose pavement the follower has occupied during this follow and not yet exited clear of: their bars are passed
    /// on the way out, met moving away from the runway, since leaving a runway is not crossing it (AIM 2-3-5.a.1, 4-3-21.b); a
    /// bar of one met heading toward it still stops the follower (<see cref="IsExitingAwayFrom"/>). A runway is dropped once the
    /// follower's tail is farther from its centreline than any of its bars on that side (<see cref="TrackExitingRunways"/>), so a
    /// later crossing of the same runway stops at the bar again. Snapshotted.
    /// </summary>
    private List<RunwayIdentifier> _exitingRunways = [];

    /// <summary>
    /// Runways the crossing clearance that started this follow cleared — <c>FOLLOWG X; CROSS 1L 1R</c> releasing a
    /// follow armed at the 1L bar clears both, one clearance for the pair (7110.65 §3-7-2c). The follow does not stop
    /// at a bar protecting any of them (<see cref="CheckRunwayHoldShort"/>), except the follower's own departure bar.
    /// The clearance is used once: the first tick the aircraft is clear of every cleared runway's pavement after
    /// having been on one, it expires (<see cref="ExpireUsedCrossingClearance"/>). Empty for a follow no crossing
    /// started.
    /// </summary>
    public IReadOnlyList<RunwayIdentifier> CrossingClearedRunways
    {
        get => _crossingClearedRunways;
        init => _crossingClearedRunways = value;
    }

    public override string Name => $"Following {_targetCallsign}";

    public override void OnStart(PhaseContext ctx)
    {
        ctx.Aircraft.IsOnGround = true;
        _loggedTailOnly = false;
        Log.LogDebug("[Follow] {Callsign}: following {Target}", ctx.Aircraft.Callsign, _targetCallsign);
    }

    /// <summary>The follow route while one is planned, else null. Never the aircraft's assigned route.</summary>
    public TaxiRoute? FollowRoute => _followRoute;

    /// <summary>The index of the follow route's first segment on the lead's path; meaningful only with a <see cref="FollowRoute"/>.</summary>
    public int MergeSegmentIndex => _mergeSegmentIndex;

    /// <summary>Whether the follower is giving way short of the merge.</summary>
    public bool IsGivingWay => _givingWay;

    /// <summary>The clearing route the follower drives off a runway with no follow route, else null.</summary>
    public TaxiRoute? ClearingRoute => _clearingRoute;

    /// <summary>The runways a clearing route has been tried for since the follow last installed a follow route.</summary>
    public IReadOnlyList<RunwayIdentifier> ClearingAttemptedRunways => _clearingAttemptedRunways;

    /// <summary>Whether the follow holds in position for good, planning no more: no taxi path to the lead's path, or ahead of the lead.</summary>
    public bool IsUnjoinable => _unjoinable;

    /// <summary>
    /// The taxi route an aircraft is driving: a <c>FOLLOWG</c> follower's follow route, or its clearing route off a runway (it
    /// never has both) — null while it has neither, waiting for its lead or between plans, since the assigned route
    /// <c>FOLLOWG</c> leaves in place is not one it drives — else its assigned route. The ground conflict detector reads every
    /// aircraft's route through this.
    /// </summary>
    public static TaxiRoute? DrivenRouteOf(AircraftState aircraft) =>
        aircraft.Phases?.CurrentPhase is FollowingPhase follow ? follow.FollowRoute ?? follow.ClearingRoute : aircraft.Ground.AssignedTaxiRoute;

    public override bool OnTick(PhaseContext ctx)
    {
        if (ctx.Aircraft.Ground.IsImmobile)
        {
            TickHeld(ctx);
            return false;
        }

        ExpireUsedCrossingClearance(ctx);
        TrackExitingRunways(ctx);
        RefreshCurrentTaxiway(ctx);
        if (CheckRunwayHoldShort(ctx, out SpeedCap? barCap))
        {
            return true;
        }

        AircraftState? target = ctx.AircraftLookup?.Invoke(_targetCallsign);
        if ((target is null) || !target.IsOnGround)
        {
            return TickLeadGone(ctx, target, barCap);
        }

        if ((_clearingRoute is null) && DriveFollowRoute(ctx, target))
        {
            ApplySpeedCaps(ctx, target, barCap);
            return false;
        }

        if (DriveClearingRoute(ctx))
        {
            ApplyClearingCaps(ctx, barCap);
        }
        else
        {
            HoldInPosition(ctx, barCap);
        }

        return false;
    }

    /// <summary>
    /// The lead is deleted or no longer on the ground: the follow route is dropped, and a follower on a runway or inside its hold
    /// line first drives its clearing route off it and, once clear, brakes along that route to rest (<see cref="DriveClearingRoute"/>).
    /// With no clearing route it brakes to a stop where it is (<see cref="HoldInPosition"/>), and at rest (<see cref="IsAtRest"/>) a
    /// <see cref="HoldingInPositionPhase"/> takes over, so the aircraft stays in a state that accepts commands. True when the
    /// follow completes.
    /// </summary>
    private bool TickLeadGone(PhaseContext ctx, AircraftState? lead, SpeedCap? barCap)
    {
        if (_followRoute is not null)
        {
            DropFollowRoute();
        }

        if (DriveClearingRoute(ctx))
        {
            ApplyClearingCaps(ctx, barCap);
            return false;
        }

        HoldInPosition(ctx, barCap);
        if (!IsAtRest(ctx))
        {
            return false;
        }

        Log.LogDebug(
            "[Follow] {Callsign}: target {Target} {Reason}; stopped",
            ctx.Aircraft.Callsign,
            _targetCallsign,
            lead is null ? "not found" : "no longer on ground"
        );
        ctx.Aircraft.IndicatedAirspeed = 0;
        ctx.Targets.TargetSpeed = 0;
        ctx.Aircraft.Phases?.InsertAfterCurrent(new HoldingInPositionPhase());
        return true;
    }

    /// <summary>
    /// Whether the follower is within one sub-tick of taxi braking of rest, so clearing the residual is no more than physics' own
    /// ground snap would.
    /// </summary>
    private static bool IsAtRest(PhaseContext ctx) =>
        ctx.Aircraft.IndicatedAirspeed <= (CategoryPerformance.TaxiDecelRate(ctx.Category) / SimulationEngine.PhysicsSubTickRate);

    /// <summary>
    /// Folds <paramref name="cap"/> into the published speed and brake rate: the speed is the lowest cap, and the brake rate the
    /// highest any cap below the current ground speed needs (a null rate is the category taxi rate), so a lower cap braked at
    /// the taxi rate never hides a higher one — a runway bar's — that needs the max-effort rate to be met.
    /// </summary>
    private static void ApplyCap(PhaseContext ctx, SpeedCap? cap)
    {
        if (cap is not { } applied)
        {
            return;
        }

        ctx.Targets.TargetSpeed = Math.Min(ctx.Targets.TargetSpeed ?? 0.0, applied.Kts);
        double taxiRate = CategoryPerformance.TaxiDecelRate(ctx.Category);
        bool needsBraking = applied.Kts < ctx.Aircraft.GroundSpeed;
        if (needsBraking && ((applied.DecelRate ?? taxiRate) > (ctx.Targets.DesiredDecelRate ?? taxiRate)))
        {
            ctx.Targets.DesiredDecelRate = applied.DecelRate;
        }
    }

    /// <summary>
    /// Drives the clearing route, starting one when the follower has no follow route while inside a runway's hold line
    /// (<see cref="StartClearingRoute"/>). A follower past its bar's hold line brakes along the route to rest
    /// (<see cref="BrakeAlongClearedRoute"/>); then the clearing ends, and the follower is checked again, so one inside a second,
    /// intersecting runway's hold line clears that one too. Traffic stopping the follower short of the line leaves the route in
    /// place to wait on (<see cref="ApplyClearingCaps"/>). False while the follower holds in position: no clearing route, or it
    /// is clear and at rest, or the route ran out.
    /// </summary>
    private bool DriveClearingRoute(PhaseContext ctx)
    {
        if ((_clearingRoute is { } cleared) && IsPastClearingBar(ctx))
        {
            if (BrakeAlongClearedRoute(ctx, cleared))
            {
                return true;
            }

            Log.LogDebug(
                "[Follow] {Callsign}: tail and wingtips past the hold line at runway bar node {NodeId}; clear of that runway, "
                    + "{Speed:F1} kt on its clearing route",
                ctx.Aircraft.Callsign,
                _clearingBarNodeId,
                ctx.Aircraft.GroundSpeed
            );
            EndClearingRoute();
        }

        if ((_clearingRoute ?? StartClearingRoute(ctx)) is not { } clearing)
        {
            return false;
        }

        if (TickNavigator(ctx, clearing))
        {
            return true;
        }

        Log.LogWarning(
            "[Follow] {Callsign}: clearing route ran out at node {Node} before the tail passed the hold line at runway bar node {NodeId}; holding",
            ctx.Aircraft.Callsign,
            _nav.TargetNodeId,
            _clearingBarNodeId
        );
        EndClearingRoute();
        return false;
    }

    /// <summary>
    /// Installs the clearing route for the first runway the follower is inside the hold line of and has not tried one for since
    /// the last follow route (<see cref="RunwayToClear"/>). It clears only when some part of it is on the runway's pavement, or when
    /// its heading takes it away from the centreline: a follower off the pavement facing the runway gets no route toward or
    /// across it, but a warning, and holds. The route (<see cref="BuildClearingRoute"/>) runs to the nearest free hold-short bar
    /// of the runway ahead and on past it. The route installed, or null with none.
    /// </summary>
    private TaxiRoute? StartClearingRoute(PhaseContext ctx)
    {
        if ((ctx.GroundLayout is not { } layout) || (RunwayToClear(ctx.Aircraft, layout) is not { } runway))
        {
            return null;
        }

        _clearingAttemptedRunways = [.. _clearingAttemptedRunways, runway.Id];
        bool onPavement = RunwayOccupancy.IsOnPavement(ctx.Aircraft, runway);
        if (!onPavement && !IsHeadingAwayFrom(ctx.Aircraft, runway))
        {
            Log.LogWarning(
                "[Follow] {Callsign}: holding in position inside runway {Runway}'s hold line facing it: "
                    + "no clearing route toward or across the runway",
                ctx.Aircraft.Callsign,
                runway.Id
            );
            return null;
        }

        if (BuildClearingRoute(ctx, layout, runway, onPavement) is not { } clearing)
        {
            return null;
        }

        _clearingRoute = clearing.Route;
        _clearingBarNodeId = clearing.BarNodeId;
        _nav = new GroundNavigator();
        _navSetUp = false;
        Log.LogDebug(
            "[Follow] {Callsign}: no follow route inside runway {Runway}'s hold line; clearing it past bar node {NodeId}: {Route}",
            ctx.Aircraft.Callsign,
            runway.Id,
            clearing.BarNodeId,
            clearing.Route.ToSummary()
        );
        return clearing.Route;
    }

    /// <summary>The first runway the follower is inside the hold line of (nose, centre or tail) and has tried no clearing route for.</summary>
    private RunwayInfo? RunwayToClear(AircraftState aircraft, AirportGroundLayout layout)
    {
        LatLon nose = NoseOf(aircraft);
        LatLon tail = TailOf(aircraft);
        foreach (RunwayInfo runway in RunwayOccupancy.AirportRunways(layout.AirportId))
        {
            if (!AnyOverlaps(_clearingAttemptedRunways, runway.Id) && IsAnyPartInsideHoldLine(layout, runway, nose, aircraft.Position, tail))
            {
                return runway;
            }
        }

        return null;
    }

    /// <summary>Whether the aircraft's heading takes it farther from <paramref name="runway"/>'s centreline.</summary>
    private static bool IsHeadingAwayFrom(AircraftState aircraft, RunwayInfo runway)
    {
        LatLon ahead = GeoMath.ProjectPoint(aircraft.Position, aircraft.TrueHeading, HeadingProbeFt / GeoMath.FeetPerNm);
        return CentrelineFt(runway, ahead) > CentrelineFt(runway, aircraft.Position);
    }

    /// <summary>
    /// The clearing route's speed caps: the runway bar's braking curve, and the gap to the nearest aircraft ahead on the clearing
    /// route — the lead or any other — kept as the follow route keeps it (<see cref="GapSpeedKts"/>). Stopped by that aircraft,
    /// the follower waits on its route, and says once that it is not yet clear.
    /// </summary>
    private void ApplyClearingCaps(PhaseContext ctx, SpeedCap? barCap)
    {
        ApplyCap(ctx, barCap);
        if (ClearingTrafficAhead(ctx) is not { } ahead)
        {
            return;
        }

        string aheadType = ahead.Aircraft.AircraftType;
        AircraftCategory aheadCategory = AircraftCategorization.Categorize(aheadType);
        double stopGapFt = FollowGap.StopGapFt(aheadType, aheadCategory, ctx.Aircraft.AircraftType, ctx.Category);
        double bandFt = FollowGap.CloseFollowBandFt(aheadType, aheadCategory, ctx.Aircraft.AircraftType, ctx.Category);
        double gapKts = GapSpeedKts(ctx, ahead.Aircraft, ahead.GapFt, stopGapFt, bandFt);
        ApplyCap(ctx, new SpeedCap(gapKts, null));
        if ((gapKts <= 0.0) && !_brakingClear)
        {
            LogHoldOnce(ctx, $"{ahead.Aircraft.Callsign} ahead on the clearing route; not yet clear of the runway's hold line", LogLevel.Information);
        }
    }

    /// <summary>Another aircraft on the clearing route ahead of the follower, and the nose-to-tail gap (ft) to it along the route.</summary>
    private readonly record struct TrafficAhead(AircraftState Aircraft, double GapFt);

    /// <summary>The nearest aircraft on the ground ahead of the follower on the rest of its clearing route; null with none.</summary>
    private TrafficAhead? ClearingTrafficAhead(PhaseContext ctx)
    {
        if (
            (ctx.GroundLayout is not { } layout)
            || (_clearingRoute is not { IsComplete: false } route)
            || (ctx.ListAircraft?.Invoke() is not { } traffic)
        )
        {
            return null;
        }

        List<DirectionalEdge> path = FillClearingPath(route);
        double followerAlongFt = (path[0].DistanceNm - GeoMath.DistanceNm(ctx.Aircraft.Position, path[0].ToNode.Position)) * GeoMath.FeetPerNm;
        TrafficAhead? nearest = null;
        foreach (AircraftState other in traffic)
        {
            if ((GapAlongPathFt(ctx, layout, path, followerAlongFt, other) is { } gapFt) && ((nearest is null) || (gapFt < nearest.Value.GapFt)))
            {
                nearest = new TrafficAhead(other, gapFt);
            }
        }

        return nearest;
    }

    /// <summary>The edges of <paramref name="route"/> from its current segment on, refilled into the reused path buffer.</summary>
    private List<DirectionalEdge> FillClearingPath(TaxiRoute route)
    {
        _clearingPathBuffer.Clear();
        for (int i = route.CurrentSegmentIndex; i < route.Segments.Count; i++)
        {
            _clearingPathBuffer.Add(route.Segments[i].Edge);
        }

        return _clearingPathBuffer;
    }

    /// <summary>
    /// The nose-to-tail gap (ft) from the follower, <paramref name="followerAlongFt"/> along <paramref name="path"/>, to
    /// <paramref name="other"/> on it ahead of the follower; null for the follower itself, an aircraft off the ground, off the
    /// path or behind the follower.
    /// </summary>
    private static double? GapAlongPathFt(
        PhaseContext ctx,
        AirportGroundLayout layout,
        IReadOnlyList<DirectionalEdge> path,
        double followerAlongFt,
        AircraftState other
    )
    {
        if (ReferenceEquals(other, ctx.Aircraft) || !other.IsOnGround || (FollowRoutePlanner.LocateOnPath(layout, path, other) is not { } at))
        {
            return null;
        }

        double aheadFt = FollowRoutePlanner.PathOffsetFt(path, at) - followerAlongFt;
        return aheadFt <= 0.0
            ? null
            : aheadFt - (AircraftLength.ResolveFt(ctx.Aircraft.AircraftType) / 2.0) - (AircraftLength.ResolveFt(other.AircraftType) / 2.0);
    }

    /// <summary>
    /// The follower is clear of the runway it was clearing: the navigator keeps steering it along <paramref name="cleared"/> while
    /// physics brakes it to rest at the taxi rate, so the braking roll stays on the route's centreline, through any bend or fillet,
    /// and short of the route's end (7110.65 §3-10-9.b NOTE 1; AIM 4-3-21.b). True while it is still rolling on the route; false
    /// at rest, or when the route ran out.
    /// </summary>
    private bool BrakeAlongClearedRoute(PhaseContext ctx, TaxiRoute cleared)
    {
        _brakingClear = !IsAtRest(ctx) && TickNavigator(ctx, cleared);
        ctx.Targets.TargetSpeed = 0;
        ctx.Targets.DesiredDecelRate = null;
        return _brakingClear;
    }

    /// <summary>Forgets the clearing route and its navigator state, keeping the note that one was tried.</summary>
    private void EndClearingRoute()
    {
        _clearingRoute = null;
        _clearingBarNodeId = null;
        _brakingClear = false;
        _nav = new GroundNavigator();
        _navSetUp = false;
    }

    /// <summary>
    /// The runway the aircraft is inside the hold line of: its nose, centre or tail on the runway's pavement, or nearer the
    /// centreline than the nearest of the runway's hold-short bars on that point's side. Null when clear of every runway.
    /// </summary>
    public static RunwayInfo? RunwayInsideHoldLine(AircraftState aircraft, AirportGroundLayout layout)
    {
        LatLon[] points = [NoseOf(aircraft), aircraft.Position, TailOf(aircraft)];
        return RunwayOccupancy.AirportRunways(layout.AirportId).FirstOrDefault(runway => points.Any(p => IsInsideHoldLine(layout, runway, p)));
    }

    /// <summary>
    /// Whether <paramref name="point"/> is on <paramref name="runway"/>'s pavement or nearer its centreline than the nearest of
    /// the runway's hold-short bars on the point's side.
    /// </summary>
    public static bool IsInsideHoldLine(AirportGroundLayout layout, RunwayInfo runway, LatLon point)
    {
        if (RunwayOccupancy.IsWithinPavement(point, runway))
        {
            return true;
        }

        bool right = IsRightOfCentreline(runway, point);
        GroundNode? nearest = null;
        double nearestNm = double.MaxValue;
        foreach (GroundNode bar in RunwayBars(layout, runway))
        {
            double distanceNm = GeoMath.DistanceNm(bar.Position, point);
            if ((IsRightOfCentreline(runway, bar.Position) == right) && (distanceNm < nearestNm))
            {
                nearest = bar;
                nearestNm = distanceNm;
            }
        }

        return (nearest is not null) && (CentrelineFt(runway, point) < CentrelineFt(runway, nearest.Position));
    }

    /// <summary>A clearing route and the hold-short bar node it clears.</summary>
    private readonly record struct Clearing(TaxiRoute Route, int BarNodeId);

    /// <summary>
    /// The clearing route off <paramref name="runway"/>: from where the follower stands (the edge it is on into its start node, as
    /// <see cref="FollowRoutePlanner"/> leads a follow route in), the auto route to the nearest by taxi distance of the runway's
    /// bars ahead (<see cref="ClearingGoals"/>), then on past it (<see cref="ClearingRouteVia"/>). A route that enters another
    /// runway, or — for a follower off the pavement — leads back toward this one, is passed over for the next nearest bar. Null,
    /// logged as a warning, with no bar ahead or no acceptable route.
    /// </summary>
    private static Clearing? BuildClearingRoute(PhaseContext ctx, AirportGroundLayout layout, RunwayInfo runway, bool onPavement)
    {
        HashSet<int> goals = ClearingGoals(ctx, layout, runway, onPavement);
        if (goals.Count == 0)
        {
            Log.LogWarning(
                "[Follow] {Callsign}: holding in position inside runway {Runway}'s hold line: no hold-short bar of it ahead to clear to",
                ctx.Aircraft.Callsign,
                runway.Id
            );
            return null;
        }

        if (layout.FindTaxiStartNode(ctx.Aircraft.Position, ctx.Aircraft.TrueHeading) is { } start)
        {
            var taxiClass = FollowRoutePlanner.TaxiClass.Of(ctx.Aircraft);
            TaxiRouteSegment? leadIn = FollowRoutePlanner.LeadInSegment(start, ctx.Aircraft);
            while ((goals.Count > 0) && (taxiClass.FindRoute(layout, start.Id, goals) is { } toBar))
            {
                if (ClearingRouteVia(ctx.Aircraft, layout, runway, leadIn, toBar, onPavement) is { } route)
                {
                    return new Clearing(route, toBar.GoalNodeId);
                }

                goals.Remove(toBar.GoalNodeId);
            }
        }

        Log.LogWarning(
            "[Follow] {Callsign}: holding in position inside runway {Runway}'s hold line: no taxi route out past any of its bars ahead",
            ctx.Aircraft.Callsign,
            runway.Id
        );
        return null;
    }

    /// <summary>
    /// The bars of <paramref name="runway"/> the follower may clear to: those ahead — within 90° of its heading as seen from its
    /// tail while some part of it is on the pavement crossing the runway, else from its centre, or the bar it straddles — less any
    /// exit another aircraft stands in (<see cref="IsExitOccupied"/>), unless every one ahead is.
    /// </summary>
    private static HashSet<int> ClearingGoals(PhaseContext ctx, AirportGroundLayout layout, RunwayInfo runway, bool onPavement)
    {
        AircraftState aircraft = ctx.Aircraft;
        LatLon from = onPavement ? TailOf(aircraft) : aircraft.Position;
        List<GroundNode> ahead =
        [
            .. RunwayBars(layout, runway)
                .Where(n =>
                    (Math.Abs(GeoMath.SignedBearingDifference(aircraft.TrueHeading.Degrees, GeoMath.BearingTo(from, n.Position))) <= 90.0)
                    || (!onPavement && Straddles(aircraft, n))
                ),
        ];
        List<GroundNode> free = [.. ahead.Where(n => !IsExitOccupied(ctx, runway, n))];
        return [.. (free.Count > 0 ? free : ahead).Select(n => n.Id)];
    }

    /// <summary>
    /// Whether <paramref name="bar"/> lies under the aircraft: between its nose and tail, within a taxiway's half-width of its
    /// axis.
    /// </summary>
    private static bool Straddles(AircraftState aircraft, GroundNode bar)
    {
        double distFt = GeoMath.DistanceNm(aircraft.Position, bar.Position) * GeoMath.FeetPerNm;
        double offRad =
            GeoMath.SignedBearingDifference(aircraft.TrueHeading.Degrees, GeoMath.BearingTo(aircraft.Position, bar.Position)) * Math.PI / 180.0;
        double halfLengthFt = AircraftLength.ResolveFt(aircraft.AircraftType) / 2.0;
        return (Math.Abs(distFt * Math.Cos(offRad)) <= halfLengthFt) && (Math.Abs(distFt * Math.Sin(offRad)) <= BarCorridorHalfWidthFt);
    }

    /// <summary>
    /// Whether another aircraft at rest on the ground stands in the exit at <paramref name="bar"/>: within half its length of the
    /// bar node or of an edge out of it leading away from the runway. One still moving is not: the gap to it caps the clearing.
    /// </summary>
    private static bool IsExitOccupied(PhaseContext ctx, RunwayInfo runway, GroundNode bar)
    {
        if (ctx.ListAircraft?.Invoke() is not { } traffic)
        {
            return false;
        }

        double barFt = CentrelineFt(runway, bar.Position);
        List<LatLon> beyond = [.. bar.Edges.Select(e => e.OtherNode(bar).Position).Where(p => CentrelineFt(runway, p) > barFt)];
        return traffic.Any(other =>
            !ReferenceEquals(other, ctx.Aircraft) && other.IsOnGround && (other.GroundSpeed < AtRestKts) && StandsIn(other, bar.Position, beyond)
        );
    }

    private static bool StandsIn(AircraftState other, LatLon bar, List<LatLon> beyond)
    {
        double reachFt = AircraftLength.ResolveFt(other.AircraftType) / 2.0;
        return (GeoMath.DistanceNm(other.Position, bar) * GeoMath.FeetPerNm <= reachFt)
            || beyond.Any(far => GeoMath.DistanceToSegmentFt(other.Position, bar, far) <= reachFt);
    }

    /// <summary>
    /// The clearing route through <paramref name="toBar"/>: the lead-in edge, the auto route to the bar, then on past the bar
    /// (<see cref="BeyondTheBar"/>). Null with no edge on past the bar, or when the route enters another runway — its centreline or
    /// past one of its bars, which needs a clearance (7110.65 §3-10-9.b.2) — or, for a follower that started off the pavement,
    /// has a segment that brings it nearer this runway's centreline.
    /// </summary>
    private static TaxiRoute? ClearingRouteVia(
        AircraftState aircraft,
        AirportGroundLayout layout,
        RunwayInfo runway,
        TaxiRouteSegment? leadIn,
        GoalRoute toBar,
        bool onPavement
    )
    {
        List<TaxiRouteSegment> segments = leadIn is { } first ? [first, .. toBar.Route.Segments] : [.. toBar.Route.Segments];
        GroundNode bar = layout.Nodes[toBar.GoalNodeId];
        bool arrivesAtBar = (segments.Count > 0) && (segments[^1].ToNodeId == bar.Id);
        double arrivalDeg = arrivesAtBar ? GeoMath.BearingTo(segments[^1].Edge.FromNode.Position, bar.Position) : aircraft.TrueHeading.Degrees;
        double needFt = AircraftLength.ResolveFt(aircraft.AircraftType) + ClearingBeyondBarFt;
        List<TaxiRouteSegment> beyond = BeyondTheBar(runway, bar, arrivesAtBar ? segments[^1].FromNodeId : null, arrivalDeg, needFt);
        if (beyond.Count == 0)
        {
            return null;
        }

        segments.AddRange(beyond);
        return IsAcceptableClearing(runway, segments, onPavement) ? new TaxiRoute { Segments = segments, HoldShortPoints = [] } : null;
    }

    /// <summary>
    /// Whether a clearing route off <paramref name="runway"/> along <paramref name="segments"/> needs no clearance: no segment
    /// enters another runway, and, for a follower that started off the pavement, none brings it nearer this runway's centreline.
    /// </summary>
    private static bool IsAcceptableClearing(RunwayInfo runway, List<TaxiRouteSegment> segments, bool onPavement)
    {
        foreach (TaxiRouteSegment segment in segments)
        {
            if (EntersAnotherRunway(runway, segment.Edge) || (!onPavement && BringsNearer(runway, segment.Edge)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The route on past <paramref name="bar"/> until it reaches <paramref name="needFt"/> beyond it: at each node the straightest
    /// continuation of the way in that leads no nearer the runway, never onto another runway. It may turn onto the next taxiway;
    /// on that one it stops at the first junction rather than drive on through it, and it stops where the pavement dead-ends.
    /// </summary>
    private static List<TaxiRouteSegment> BeyondTheBar(RunwayInfo runway, GroundNode bar, int? cameFromId, double arrivalDeg, double needFt)
    {
        List<TaxiRouteSegment> beyond = [];
        GroundNode at = bar;
        double runFt = 0.0;
        while ((runFt < needFt) && (NextEdgeAway(runway, at, cameFromId, arrivalDeg) is { } next))
        {
            beyond.Add(ToSegment(next));
            runFt += next.DistanceNm * GeoMath.FeetPerNm;
            arrivalDeg = GeoMath.BearingTo(next.FromNode.Position, next.ToNode.Position);
            cameFromId = at.Id;
            at = next.ToNode;
            bool onNextTaxiway = !string.Equals(next.Edge.TaxiwayName, beyond[0].TaxiwayName, StringComparison.OrdinalIgnoreCase);
            if (onNextTaxiway && (at.Edges.Count > 2))
            {
                break;
            }
        }

        return beyond;
    }

    /// <summary>
    /// The straightest edge out of <paramref name="at"/> from <paramref name="arrivalDeg"/>, not back, leading no nearer the
    /// runway and onto no other runway.
    /// </summary>
    private static DirectionalEdge? NextEdgeAway(RunwayInfo runway, GroundNode at, int? cameFromId, double arrivalDeg)
    {
        double atFt = CentrelineFt(runway, at.Position);
        IGroundEdge? edge = at
            .Edges.Where(e =>
                !e.IsRunwayCenterline
                && (e.OtherNode(at).Id != cameFromId)
                && (CentrelineFt(runway, e.OtherNode(at).Position) >= (atFt - AwayToleranceFt))
                && !EntersAnotherRunway(runway, e.Directed(at, e.OtherNode(at)))
            )
            .MinBy(e => Math.Abs(GeoMath.SignedBearingDifference(arrivalDeg, GeoMath.BearingTo(at.Position, e.OtherNode(at).Position))));
        return edge?.Directed(at, edge.OtherNode(at));
    }

    /// <summary>
    /// Whether <paramref name="edge"/> runs onto another runway than <paramref name="runway"/>: into one of its bars, or along
    /// its centreline.
    /// </summary>
    private static bool EntersAnotherRunway(RunwayInfo runway, DirectionalEdge edge)
    {
        bool intoOtherBar = (edge.ToNode.Type == GroundNodeType.RunwayHoldShort) && (edge.ToNode.RunwayId is { } id) && !id.Overlaps(runway.Id);
        LatLon middle = new(
            (edge.FromNode.Position.Lat + edge.ToNode.Position.Lat) / 2.0,
            (edge.FromNode.Position.Lon + edge.ToNode.Position.Lon) / 2.0
        );
        return intoOtherBar || (edge.Edge.IsRunwayCenterline && !RunwayOccupancy.IsWithinPavement(middle, runway));
    }

    /// <summary>Whether <paramref name="edge"/> ends nearer <paramref name="runway"/>'s centreline than it starts.</summary>
    private static bool BringsNearer(RunwayInfo runway, DirectionalEdge edge) =>
        CentrelineFt(runway, edge.ToNode.Position) < (CentrelineFt(runway, edge.FromNode.Position) - AwayToleranceFt);

    /// <summary>
    /// Whether the follower is past the clearing bar's hold line (<see cref="IsClearPastBar"/>). True when the bar or its runway
    /// can no longer be found, so the clearing ends.
    /// </summary>
    private bool IsPastClearingBar(PhaseContext ctx)
    {
        if (
            (ctx.GroundLayout is not { } layout)
            || (_clearingBarNodeId is not { } nodeId)
            || !layout.Nodes.TryGetValue(nodeId, out GroundNode? bar)
            || (bar.RunwayId is not { } barRunway)
            || (RunwayNamed(layout, barRunway) is not { } runway)
        )
        {
            return true;
        }

        NoteTailOnlyOnce(ctx.Aircraft);
        return IsClearPastBar(ctx.Aircraft, runway, bar);
    }

    /// <summary>
    /// Whether <paramref name="aircraft"/> is past <paramref name="bar"/>'s hold line of <paramref name="runway"/>: its tail and
    /// both wingtips (<see cref="FillTrailingParts"/>) on the bar's side of the runway and farther from its centreline than the
    /// bar, so all of it is past the line (AIM 4-3-21.b), on an angled exit too.
    /// </summary>
    public static bool IsClearPastBar(AircraftState aircraft, RunwayInfo runway, GroundNode bar)
    {
        bool barRight = IsRightOfCentreline(runway, bar.Position);
        double barFt = CentrelineFt(runway, bar.Position);
        Span<LatLon> parts = stackalloc LatLon[MaxTrailingParts];
        int count = FillTrailingParts(aircraft, parts);
        for (int i = 0; i < count; i++)
        {
            if ((IsRightOfCentreline(runway, parts[i]) != barRight) || (CentrelineFt(runway, parts[i]) <= barFt))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The most parts <see cref="FillTrailingParts"/> fills: the tail and both wingtips.</summary>
    private const int MaxTrailingParts = 3;

    /// <summary>
    /// Fills <paramref name="parts"/> with the parts of the aircraft last to clear a hold line as it leaves a runway: its tail and
    /// both wingtips, the span from the FAA aircraft data; with no wingspan for the type, the tail alone. Returns how many it filled.
    /// </summary>
    private static int FillTrailingParts(AircraftState aircraft, Span<LatLon> parts)
    {
        parts[0] = TailOf(aircraft);
        if (WingspanFt(aircraft) is not { } spanFt)
        {
            return 1;
        }

        double halfSpanNm = spanFt / 2.0 / GeoMath.FeetPerNm;
        double headingDeg = aircraft.TrueHeading.Degrees;
        parts[1] = GeoMath.ProjectPoint(aircraft.Position, new TrueHeading((headingDeg + 90.0) % 360.0), halfSpanNm);
        parts[2] = GeoMath.ProjectPoint(aircraft.Position, new TrueHeading((headingDeg + 270.0) % 360.0), halfSpanNm);
        return MaxTrailingParts;
    }

    private static double? WingspanFt(AircraftState aircraft) => FaaAircraftDatabase.Get(aircraft.AircraftType)?.WingspanFt;

    /// <summary>
    /// Logs at Debug, once per follow, that the follower's type has no wingspan, so its hold lines are judged by its tail alone
    /// (<see cref="FillTrailingParts"/>). Logging only: not snapshotted.
    /// </summary>
    private void NoteTailOnlyOnce(AircraftState aircraft)
    {
        if (_loggedTailOnly || (WingspanFt(aircraft) is not null))
        {
            return;
        }

        _loggedTailOnly = true;
        Log.LogDebug(
            "[Follow] {Callsign}: no wingspan for {Type}; judging the hold line by its tail alone",
            aircraft.Callsign,
            aircraft.AircraftType
        );
    }

    private static LatLon NoseOf(AircraftState aircraft) =>
        GeoMath.ProjectPoint(aircraft.Position, aircraft.TrueHeading, AircraftLength.ResolveFt(aircraft.AircraftType) / 2.0 / GeoMath.FeetPerNm);

    private static LatLon TailOf(AircraftState aircraft) =>
        GeoMath.ProjectPoint(
            aircraft.Position,
            new TrueHeading((aircraft.TrueHeading.Degrees + 180.0) % 360.0),
            AircraftLength.ResolveFt(aircraft.AircraftType) / 2.0 / GeoMath.FeetPerNm
        );

    /// <summary>
    /// Each layout's runway hold-short bar nodes by runway (<see cref="RunwayBarIndex"/>), built on first use and held with the
    /// layout — a new layout instance starts a new index — so a tick's hold-line checks never scan every node of the layout.
    /// </summary>
    private static readonly ConditionalWeakTable<AirportGroundLayout, RunwayBarIndex> BarIndexes = [];

    /// <summary>A layout's runway hold-short bar nodes with a runway, and those of each runway asked for so far, by its identifier.</summary>
    private sealed class RunwayBarIndex(GroundNode[] bars)
    {
        private readonly ConcurrentDictionary<RunwayIdentifier, GroundNode[]> _byRunway = new();

        public static RunwayBarIndex For(AirportGroundLayout layout) =>
            new([.. layout.Nodes.Values.Where(n => (n.Type == GroundNodeType.RunwayHoldShort) && (n.RunwayId is not null))]);

        public GroundNode[] Of(RunwayInfo runway) =>
            _byRunway.TryGetValue(runway.Id, out GroundNode[]? known) ? known : _byRunway.GetOrAdd(runway.Id, Filter(runway.Id));

        private GroundNode[] Filter(RunwayIdentifier id) => [.. bars.Where(n => (n.RunwayId is { } barRunway) && barRunway.Overlaps(id))];
    }

    /// <summary>The runway hold-short bar nodes of <paramref name="runway"/>.</summary>
    private static GroundNode[] RunwayBars(AirportGroundLayout layout, RunwayInfo runway) =>
        BarIndexes.GetValue(layout, RunwayBarIndex.For).Of(runway);

    /// <summary>
    /// Plans the follow route when there is none and ticks the navigator over it. A route that runs out this tick is planned
    /// again and driven in the same tick, so a follower whose new plan joins the lead's path never stands a tick with no route
    /// publishing a stop. False while the follower holds in position: no plan, or the route ran out with none to replace it.
    /// </summary>
    private bool DriveFollowRoute(PhaseContext ctx, AircraftState lead)
    {
        if (!EnsurePlan(ctx, lead))
        {
            return false;
        }

        return TickFollowNavigator(ctx) || (EnsurePlan(ctx, lead) && TickFollowNavigator(ctx));
    }

    /// <summary>
    /// Ticks the navigator over the follow route, keeping the plan view's route to the merge at the route's segment; false with
    /// no route, or when the route ran out, which drops it for a new plan.
    /// </summary>
    private bool TickFollowNavigator(PhaseContext ctx)
    {
        if (_followRoute is not { } route)
        {
            return false;
        }

        if (TickNavigator(ctx, route))
        {
            SyncPathToMerge();
            return true;
        }

        Log.LogDebug(
            "[Follow] {Callsign}: reached the end of the follow route at node {Node}; planning again",
            ctx.Aircraft.Callsign,
            _nav.TargetNodeId
        );
        DropFollowRoute();
        return false;
    }

    /// <summary>A speed cap on the follow and the brake rate it needs published; null rate for the category taxi rate.</summary>
    private readonly record struct SpeedCap(double Kts, double? DecelRate);

    /// <summary>
    /// Stops the follower where it is: no plan to drive, or its route ran out. Physics brakes it at the taxi rate, or at the
    /// firm rate a runway bar ahead needs (<paramref name="barCap"/>).
    /// </summary>
    private static void HoldInPosition(PhaseContext ctx, SpeedCap? barCap)
    {
        ctx.Targets.TargetSpeed = 0;
        ctx.Targets.DesiredDecelRate = barCap?.DecelRate;
    }

    /// <summary>
    /// A HOLD or GIVEWAY on the follower: the navigator still ticks, as <see cref="TaxiingPhase"/>'s does while held, so its
    /// curve playback stays with the aircraft physics brakes to rest; the published speed is pinned to zero after it.
    /// </summary>
    private void TickHeld(PhaseContext ctx)
    {
        if ((_clearingRoute is { } clearing) && !TickNavigator(ctx, clearing))
        {
            EndClearingRoute();
        }
        else if ((_clearingRoute is null) && (_followRoute is not null))
        {
            TickFollowNavigator(ctx);
        }

        ctx.Targets.TargetSpeed = 0;
        ctx.Targets.DesiredDecelRate = null;
    }

    /// <summary>
    /// Plans the follow route when there is none (the first tick, a restored follow without one, a route that ran out). True
    /// with a route to drive; false while the follower holds in position — no layout, or a plan other than
    /// <see cref="FollowRoutePlan.Joinable"/> — logged once per reason.
    /// </summary>
    private bool EnsurePlan(PhaseContext ctx, AircraftState lead)
    {
        if (_followRoute is not null)
        {
            return true;
        }

        if (_unjoinable)
        {
            return false;
        }

        if (ctx.GroundLayout is not { } layout)
        {
            LogHoldOnce(ctx, "no ground layout", LogLevel.Information);
            return false;
        }

        FollowRoutePlan plan = FollowRoutePlanner.Plan(layout, ctx.Aircraft, lead);
        if (plan is not FollowRoutePlan.Joinable joinable)
        {
            HoldOnUnjoinedPlan(ctx, plan);
            return false;
        }

        if ((joinable.PathToMerge.Segments.Count + joinable.LeadPathFromMerge.Count) == 0)
        {
            LogHoldOnce(ctx, $"{_targetCallsign}'s path ends where the follower joins it", LogLevel.Information);
            return false;
        }

        InstallPlan(ctx, layout, joinable, lead);
        return true;
    }

    /// <summary>
    /// Holds for a plan that joins nothing, logged once: for good on a <see cref="FollowRoutePlan.FollowerAhead"/> or a
    /// <see cref="FollowRoutePlan.NoPath"/>, planning again each tick on a <see cref="FollowRoutePlan.WaitForLead"/>.
    /// </summary>
    private void HoldOnUnjoinedPlan(PhaseContext ctx, FollowRoutePlan plan)
    {
        (string reason, LogLevel level) = plan switch
        {
            FollowRoutePlan.WaitForLead => ($"waiting for {_targetCallsign} to taxi onto a taxiway", LogLevel.Debug),
            FollowRoutePlan.FollowerAhead => ($"ahead of {_targetCallsign} on its route", LogLevel.Information),
            _ => ($"no taxi route to {_targetCallsign}'s path", LogLevel.Information),
        };
        _unjoinable = plan is not FollowRoutePlan.WaitForLead;
        LogHoldOnce(ctx, reason, level);
    }

    /// <summary>
    /// Installs <paramref name="joinable"/> as the follow route, giving way short of the merge when the follower has a route to it
    /// and the merge lies ahead of the lead or the lead's tail is not yet past it.
    /// </summary>
    private void InstallPlan(PhaseContext ctx, AirportGroundLayout layout, FollowRoutePlan.Joinable joinable, AircraftState lead)
    {
        bool giveWay =
            (joinable.PathToMerge.Segments.Count > 0) && (joinable.MergeAheadOfLead || !LeadTailPastMerge(layout, joinable.LeadPathFromMerge, lead));
        TaxiRoute route = new()
        {
            Segments = [.. joinable.PathToMerge.Segments, .. joinable.LeadPathFromMerge.Select(ToSegment)],
            HoldShortPoints = [],
        };

        // A fresh navigator for every new route: one carried over would set the route up from the last route's leftovers (its
        // aimed line, fillet and target node), which a snapshot taken with no route does not hold, so a restore would diverge.
        _nav = new GroundNavigator();
        SetFollowRoute(route, joinable.PathToMerge.Segments.Count, joinable.LeadEdgeIntoMerge, giveWay);
        _loggedHoldReason = null;
        _clearingAttemptedRunways = [];
        Log.LogDebug(
            "[Follow] {Callsign}: following {Target} on the taxi graph: {Route}, joining its path at node {Merge} (segment {MergeIndex}), "
                + "giving way: {GiveWay}",
            ctx.Aircraft.Callsign,
            _targetCallsign,
            route.ToSummary(),
            joinable.MergeNode,
            _mergeSegmentIndex,
            giveWay
        );
    }

    private void LogHoldOnce(PhaseContext ctx, string reason, LogLevel level)
    {
        if (_loggedHoldReason == reason)
        {
            return;
        }

        _loggedHoldReason = reason;
        Log.Log(level, "[Follow] {Callsign}: holding in position, {Reason}", ctx.Aircraft.Callsign, reason);
    }

    private static TaxiRouteSegment ToSegment(DirectionalEdge edge) => new() { Edge = edge, TaxiwayName = edge.Edge.TaxiwayName ?? "" };

    /// <summary>Whether the lead's tail is past the start of <paramref name="leadPath"/>; false when the lead is not on it.</summary>
    private static bool LeadTailPastMerge(AirportGroundLayout layout, IReadOnlyList<DirectionalEdge> leadPath, AircraftState lead) =>
        (FollowRoutePlanner.LocateOnPath(layout, leadPath, lead) is { } at) && FollowRoutePlanner.LeadTailPastMerge(leadPath, at, lead.AircraftType);

    /// <summary>
    /// Installs <paramref name="route"/> as the follow route, its lead's path starting at segment <paramref name="mergeIndex"/>,
    /// and the plan view of it the gap and give-way read; the navigator sets the current segment up on its next tick.
    /// </summary>
    private void SetFollowRoute(TaxiRoute route, int mergeIndex, DirectionalEdge? leadEdgeIntoMerge, bool givingWay)
    {
        _followRoute = route;
        _mergeSegmentIndex = mergeIndex;
        _givingWay = givingWay;
        _navSetUp = false;
        int mergeNode = mergeIndex < route.Segments.Count ? route.Segments[mergeIndex].FromNodeId : route.Segments[^1].ToNodeId;
        TaxiRoute pathToMerge = new() { Segments = [.. route.Segments.Take(mergeIndex)], HoldShortPoints = [] };
        List<DirectionalEdge> leadPath = [.. route.Segments.Skip(mergeIndex).Select(s => s.Edge)];
        List<DirectionalEdge> track = leadEdgeIntoMerge is { } into ? [into, .. leadPath.Take(1)] : [.. leadPath.Take(1)];
        _plan = new FollowView(mergeNode, pathToMerge, leadEdgeIntoMerge, leadPath, track);
        SyncPathToMerge();
    }

    /// <summary>
    /// Keeps the plan view's route to the merge at the follow route's segment, so the gap and the give-way stop read the same
    /// route progress whichever is read first.
    /// </summary>
    private void SyncPathToMerge()
    {
        if ((_plan is { } plan) && (_followRoute is { } route))
        {
            plan.PathToMerge.CurrentSegmentIndex = Math.Min(route.CurrentSegmentIndex, _mergeSegmentIndex);
        }
    }

    /// <summary>Forgets the follow route and its navigator state; a snapshot with no route carries neither.</summary>
    private void DropFollowRoute()
    {
        _followRoute = null;
        _nav = new GroundNavigator();
        _mergeSegmentIndex = 0;
        _plan = null;
        _givingWay = false;
        _navSetUp = false;
    }

    /// <summary>
    /// Ticks the navigator over <paramref name="route"/> (the follow route or the clearing route), setting the current segment
    /// up first when it is not, and advances the route on each node arrival. False when the route ran out.
    /// </summary>
    private bool TickNavigator(PhaseContext ctx, TaxiRoute route)
    {
        _nav.MaxSpeedKts = CategoryPerformance.TaxiSpeed(ctx.Category);
        if (!_navSetUp)
        {
            _nav.RouteEndSpeedKts = 0;
            _nav.SetupSegment(route, ctx, AlwaysCleared);
            _navSetUp = true;
        }

        bool isLastSegment = (route.CurrentSegmentIndex + 1) >= route.Segments.Count;
        if (_nav.Tick(ctx, isLastSegment, AlwaysCleared) != NavigatorResult.ArrivedAtNode)
        {
            return true;
        }

        route.CurrentSegmentIndex = Math.Min(route.CurrentSegmentIndex + 1, route.Segments.Count);
        if (route.IsComplete)
        {
            return false;
        }

        _nav.SetupSegment(route, ctx, AlwaysCleared);
        return true;
    }

    /// <summary>
    /// Publishes the follow's speed: the lowest of the navigator's (taxi speed, corner and route-end limits), the gap to the
    /// lead, the give-way stop short of the merge and the runway bar's braking curve, with the highest brake rate any cap below
    /// the ground speed needs (<see cref="ApplyCap"/>).
    /// </summary>
    private void ApplySpeedCaps(PhaseContext ctx, AircraftState lead, SpeedCap? barCap)
    {
        AircraftCategory leadCategory = AircraftCategorization.Categorize(lead.AircraftType);
        double stopGapFt = FollowGap.StopGapFt(lead.AircraftType, leadCategory, ctx.Aircraft.AircraftType, ctx.Category);
        double bandFt = FollowGap.CloseFollowBandFt(lead.AircraftType, leadCategory, ctx.Aircraft.AircraftType, ctx.Category);
        PathPosition? leadAt = LocateLead(ctx, lead);
        double gapFt = GapFt(ctx, lead, leadAt);
        var gapCap = new SpeedCap(GapSpeedKts(ctx, lead, gapFt, stopGapFt, bandFt), null);
        SpeedCap? giveWayCap = GiveWayCap(ctx, lead, leadAt, gapFt, stopGapFt);

        ApplyCap(ctx, gapCap);
        ApplyCap(ctx, giveWayCap);
        ApplyCap(ctx, barCap);
        LogPeriodic(ctx, lead, gapFt, stopGapFt);
    }

    /// <summary>
    /// Where the lead stands on its path from the merge this tick (<see cref="FollowRoutePlanner.LocateOnPath"/>); null when
    /// not on it, or with no plan.
    /// </summary>
    private PathPosition? LocateLead(PhaseContext ctx, AircraftState lead) =>
        (ctx.GroundLayout is { } layout) && (_plan is { } plan) ? FollowRoutePlanner.LocateOnPath(layout, plan.LeadPathFromMerge, lead) : null;

    /// <summary>
    /// The nose-to-tail gap (ft) to the lead along the follow route (<see cref="FollowRoutePlanner.AlongPathGapFt"/>), the lead
    /// at <paramref name="leadAt"/> on its path from the merge, or the straight-line nose-to-tail distance
    /// (<see cref="NoseToTailFt"/>) when the lead or the follower is not on the path.
    /// </summary>
    private double GapFt(PhaseContext ctx, AircraftState lead, PathPosition? leadAt)
    {
        if ((ctx.GroundLayout is { } layout) && (_plan is { } plan))
        {
            double? alongFt = FollowRoutePlanner.AlongPathGapFt(
                FollowRoutePlanner.FollowerToMergeFt(layout, plan.PathToMerge, plan.LeadPathFromMerge, ctx.Aircraft),
                plan.LeadPathFromMerge,
                leadAt,
                ctx.Aircraft.AircraftType,
                lead.AircraftType
            );
            if (alongFt is { } gapFt)
            {
                return gapFt;
            }
        }

        return NoseToTailFt(ctx.Aircraft, lead);
    }

    /// <summary>
    /// The speed the gap allows: the lower of taxi speed and the brake curve <c>v = sqrt(2·a·d)</c> to the stop gap; inside the
    /// close-follow band behind a moving lead, no slower than the lead, with a walking-pace floor to close up; zero at the stop gap.
    /// </summary>
    private static double GapSpeedKts(PhaseContext ctx, AircraftState lead, double gapFt, double stopGapFt, double bandFt)
    {
        if (gapFt <= (stopGapFt + FollowStopSettleFt))
        {
            return 0.0;
        }

        // Brake curve to the stop gap rather than a flat close-up speed: it decays to 0 exactly at the stop gap, so the
        // follower rolls to a stop instead of holding a speed until the gap trips the branch above (bang-bang at the
        // boundary). It caps the follow beyond the close-follow band too: the band is narrower than a taxi-speed stop at the
        // taxi brake rate. The curve is read where this tick's travel leaves the follower: read where it stands, the target
        // trails it by a tick and the follower ends a couple of feet inside the stop gap.
        double thisTickFt = ctx.Aircraft.GroundSpeed * GroundStopBraking.FeetPerSecondPerKt * ctx.DeltaSeconds;
        double remainingToStopNm = Math.Max(0.0, gapFt - stopGapFt - thisTickFt) / GeoMath.FeetPerNm;
        double brakeCurveKts = Math.Sqrt(2.0 * CategoryPerformance.TaxiDecelRate(ctx.Category) * remainingToStopNm * 3600.0);
        double taxiKts = CategoryPerformance.TaxiSpeed(ctx.Category);
        double leadAwayKts = LeadTracksTowardFollower(ctx.Aircraft, lead) ? 0.0 : lead.GroundSpeed;
        return (gapFt <= bandFt) && (leadAwayKts > 0.0)
            ? Math.Max(leadAwayKts, Math.Min(FollowCloseUpSpeedKts, brakeCurveKts))
            : Math.Min(taxiKts, brakeCurveKts);
    }

    /// <summary>
    /// Whether the lead's track — its pushback track on a tug, else its heading — points back toward the follower: a lead
    /// backing or coming toward the follower opens no gap, so its speed is no speed to match.
    /// </summary>
    private static bool LeadTracksTowardFollower(AircraftState follower, AircraftState lead)
    {
        double trackDeg = lead.Ground.PushbackTrueHeading?.Degrees ?? lead.TrueHeading.Degrees;
        return Math.Abs(GeoMath.SignedBearingDifference(trackDeg, GeoMath.BearingTo(lead.Position, follower.Position))) < 90.0;
    }

    /// <summary>
    /// While giving way short of the merge, the braking curve onto the give-way stop
    /// (<see cref="GroundConflictDetector.GiveWayStop(AircraftState, TaxiRoute, IReadOnlyList{DirectionalEdge}, AircraftState, out string?)"/>,
    /// placed as a <c>GIVEWAY</c> places its stop): routine, firm when only the firm rate makes it, a firm stop where it is
    /// otherwise. Released — null from then on — once the follower is past the merge, or the lead's tail is past it with the
    /// gap at least the stop gap. Null too when the merge is beyond the give-way look-ahead.
    /// </summary>
    private SpeedCap? GiveWayCap(PhaseContext ctx, AircraftState lead, PathPosition? leadAt, double gapFt, double stopGapFt)
    {
        if (!_givingWay || (_plan is not { } plan))
        {
            return null;
        }

        if (IsGiveWayOver(leadAt, lead.AircraftType, gapFt, stopGapFt))
        {
            _givingWay = false;
            Log.LogDebug(
                "[Follow] {Callsign}: {Target}'s tail is past the merge at node {Merge} and the gap is {Gap:F0} ft; falling in behind",
                ctx.Aircraft.Callsign,
                _targetCallsign,
                plan.MergeNode,
                gapFt
            );
            return null;
        }

        return GroundConflictDetector.GiveWayStop(ctx.Aircraft, plan.PathToMerge, plan.LeadTrackAtMerge, lead, out _) is { } stop
            ? StopCap(ctx, stop.ToStopFt, GroundStopBraking.ChooseStopBraking(ctx, stop.ToStopFt))
            : null;
    }

    /// <summary>
    /// Whether the give-way is over: the follower is past the merge, or the lead — at <paramref name="leadAt"/> on its path from
    /// the merge — has its tail past the merge with the gap at least the stop gap.
    /// </summary>
    private bool IsGiveWayOver(PathPosition? leadAt, string leadType, double gapFt, double stopGapFt)
    {
        if ((_plan is not { } plan) || (_followRoute is not { } route))
        {
            return true;
        }

        bool followerPastMerge = route.CurrentSegmentIndex >= _mergeSegmentIndex;
        bool leadTailPast = (leadAt is { } at) && FollowRoutePlanner.LeadTailPastMerge(plan.LeadPathFromMerge, at, leadType);
        return followerPastMerge || (leadTailPast && (gapFt >= stopGapFt));
    }

    /// <summary>
    /// The cap onto a stop <paramref name="toStopFt"/> ahead under <paramref name="braking"/>: the braking curve at the taxi
    /// rate (no rate override) when that makes the stop, at the max-effort rate when only that does, else a firm stop where the
    /// follower is.
    /// </summary>
    private static SpeedCap StopCap(PhaseContext ctx, double toStopFt, GroundStopBraking.StopBraking braking)
    {
        double maxEffortRate = CategoryPerformance.ExpediteExitDecelRate(ctx.Category);
        return braking switch
        {
            GroundStopBraking.StopBraking.Routine => new SpeedCap(
                GroundStopBraking.StopCurveKts(ctx, toStopFt, CategoryPerformance.TaxiDecelRate(ctx.Category)),
                null
            ),
            GroundStopBraking.StopBraking.MaxEffort => new SpeedCap(GroundStopBraking.StopCurveKts(ctx, toStopFt, maxEffortRate), maxEffortRate),
            _ => new SpeedCap(0.0, maxEffortRate),
        };
    }

    /// <summary>
    /// The straight-line distance (ft) from the follower's nose — its centre plus half its length along its heading — to the
    /// lead's tail — its centre less half its length along its heading. Negative once the nose is past the tail: the tail
    /// lies behind the nose, measured along the line from the follower's centre to the lead's, so a follower parked facing
    /// away from its lead still reads the gap ahead of it.
    /// </summary>
    public static double NoseToTailFt(AircraftState follower, AircraftState lead)
    {
        double followerHalfNm = AircraftLength.ResolveFt(follower.AircraftType) / 2.0 / GeoMath.FeetPerNm;
        double leadHalfNm = AircraftLength.ResolveFt(lead.AircraftType) / 2.0 / GeoMath.FeetPerNm;
        LatLon nose = GeoMath.ProjectPoint(follower.Position, follower.TrueHeading, followerHalfNm);
        LatLon tail = GeoMath.ProjectPoint(lead.Position, new TrueHeading((lead.TrueHeading.Degrees + 180.0) % 360.0), leadHalfNm);
        double distFt = GeoMath.DistanceNm(nose, tail) * GeoMath.FeetPerNm;
        double towardLeadDeg = GeoMath.BearingTo(follower.Position, lead.Position);
        double offRad = GeoMath.SignedBearingDifference(towardLeadDeg, GeoMath.BearingTo(nose, tail)) * Math.PI / 180.0;
        return Math.Cos(offRad) >= 0.0 ? distFt : -distFt;
    }

    private void LogPeriodic(PhaseContext ctx, AircraftState lead, double gapFt, double stopGapFt)
    {
        _timeSinceLastLog += ctx.DeltaSeconds;
        if (_timeSinceLastLog < LogIntervalSeconds)
        {
            return;
        }

        _timeSinceLastLog = 0;
        Log.LogTrace(
            "[Follow] {Callsign}: nose {Gap:F0} ft from the tail of {Target} (stop gap {StopGap:F0} ft), segment {Seg}/{Segs}, gs={Gs:F1}kts, "
                + "targetGs={TGs:F1}kts, giving way: {GiveWay}",
            ctx.Aircraft.Callsign,
            gapFt,
            _targetCallsign,
            stopGapFt,
            _followRoute?.CurrentSegmentIndex,
            _followRoute?.Segments.Count,
            ctx.Aircraft.GroundSpeed,
            lead.GroundSpeed,
            _givingWay
        );
    }

    /// <summary>
    /// A firm stop at a runway bar publishes its own brake rate; clearing it on exit keeps that rate from carrying into the
    /// next phase, since <see cref="ControlTargets"/> persist across phases.
    /// </summary>
    public override void OnEnd(PhaseContext ctx, PhaseStatus endStatus) => ctx.Targets.DesiredDecelRate = null;

    public override CommandAcceptance CanAcceptCommand(CanonicalCommandType cmd)
    {
        return cmd switch
        {
            CanonicalCommandType.Taxi or CanonicalCommandType.TaxiAuto => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.HoldPosition => CommandAcceptance.Allowed,
            CanonicalCommandType.Resume => CommandAcceptance.Allowed,
            CanonicalCommandType.CrossRunway => CommandAcceptance.Allowed,
            CanonicalCommandType.FollowGround => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.Delete => CommandAcceptance.ClearsPhase,
            _ => CommandAcceptance.Rejected("aircraft is following another; only HOLD/RES, CROSS, a new FOLLOWG, or a new TAXI apply"),
        };
    }

    public override PhaseDto ToSnapshot() =>
        new FollowingPhaseDto
        {
            Status = (int)Status,
            ElapsedSeconds = ElapsedSeconds,
            Requirements = SnapshotRequirements(),
            TargetCallsign = _targetCallsign,
            TimeSinceLastLog = _timeSinceLastLog,
            CrossingClearedRunways = [.. _crossingClearedRunways.Select(static r => r.ToString())],
            HasBeenOnClearedRunway = _hasBeenOnClearedRunway,
            ExitingRunways = [.. _exitingRunways.Select(static r => r.ToString())],
            LatchedBarNodeId = _latchedBarNodeId,
            LatchedBarApproachDeg = _latchedBarApproachDeg,
            TaxiEdgeNodeA = _taxiEdgeNodeIds?.NodeA,
            TaxiEdgeNodeB = _taxiEdgeNodeIds?.NodeB,
            FollowRoute = _followRoute?.ToSnapshot(),
            MergeSegmentIndex = _mergeSegmentIndex,
            LeadEdgeIntoMerge = _plan?.LeadEdgeIntoMerge is { } intoMerge ? TaxiRoute.ToSegmentSnapshot(ToSegment(intoMerge)) : null,
            GivingWay = _givingWay,
            Unjoinable = _unjoinable,
            ClearingRoute = _clearingRoute?.ToSnapshot(),
            ClearingBarNodeId = _clearingBarNodeId,
            ClearingAttemptedRunways = _clearingAttemptedRunways.Count > 0 ? [.. _clearingAttemptedRunways.Select(static r => r.ToString())] : null,
            Navigator = (_followRoute ?? _clearingRoute) is null ? null : _nav.ToSnapshot(),
        };

    /// <summary>
    /// Restores a follow from <paramref name="dto"/>. Its follow route is rebuilt over <paramref name="groundLayout"/>; the
    /// navigator resumes the primitive it was playing at the next tick's segment set-up. A snapshot without a follow route —
    /// or one the layout no longer resolves — plans afresh on the next tick.
    /// </summary>
    public static FollowingPhase FromSnapshot(FollowingPhaseDto dto, AirportGroundLayout? groundLayout)
    {
        var phase = new FollowingPhase(dto.TargetCallsign)
        {
            _timeSinceLastLog = dto.TimeSinceLastLog,
            CrossingClearedRunways = [.. dto.CrossingClearedRunways.Select(RunwayIdentifier.Parse)],
            _hasBeenOnClearedRunway = dto.HasBeenOnClearedRunway,
            _exitingRunways = [.. dto.ExitingRunways.Select(RunwayIdentifier.Parse)],
            _latchedBarNodeId = dto.LatchedBarNodeId,
            _latchedBarApproachDeg = dto.LatchedBarApproachDeg,
            _taxiEdgeNodeIds = (dto.TaxiEdgeNodeA is { } nodeA) && (dto.TaxiEdgeNodeB is { } nodeB) ? (nodeA, nodeB) : null,
            _unjoinable = dto.Unjoinable,
            _clearingAttemptedRunways = [.. (dto.ClearingAttemptedRunways ?? []).Select(RunwayIdentifier.Parse)],
            Status = (PhaseStatus)dto.Status,
            ElapsedSeconds = dto.ElapsedSeconds,
        };
        phase.RestoreRequirements(dto.Requirements);
        if ((groundLayout is { } layout) && phase.RestoreRoute(dto, layout) && (dto.Navigator is { } navigator))
        {
            phase._nav = GroundNavigator.FromSnapshot(navigator);
        }

        return phase;
    }

    /// <summary>
    /// Restores the follow route, else the clearing route, from <paramref name="dto"/> over <paramref name="layout"/>; true with a
    /// route installed, which the navigator's snapshot then resumes. A snapshot with neither, or one the layout no longer
    /// resolves, leaves the follow to plan afresh.
    /// </summary>
    private bool RestoreRoute(FollowingPhaseDto dto, AirportGroundLayout layout)
    {
        if ((dto.FollowRoute is { } routeDto) && (TaxiRoute.FromSnapshot(routeDto, layout) is { Segments.Count: > 0 } route))
        {
            int mergeIndex = Math.Clamp(dto.MergeSegmentIndex, 0, route.Segments.Count);
            DirectionalEdge? intoMerge = dto.LeadEdgeIntoMerge is { } segment ? TaxiRoute.ResolveSegment(layout, segment)?.Edge : null;
            SetFollowRoute(route, mergeIndex, intoMerge, dto.GivingWay);
            return true;
        }

        if ((dto.ClearingRoute is { } clearingDto) && (TaxiRoute.FromSnapshot(clearingDto, layout) is { Segments.Count: > 0 } clearing))
        {
            _clearingRoute = clearing;
            _clearingBarNodeId = dto.ClearingBarNodeId;
            return true;
        }

        return false;
    }

    /// <summary>
    /// A runway bar the follower must stop at, with the reason it holds there, the bearing (deg true) from the bar back up
    /// the taxiway edge leading into it, and how far (ft) the nose still has to roll to the hold line — the line through the
    /// bar node square to that edge — measured along the edge; negative once the nose is past the line.
    /// </summary>
    private readonly record struct BarAhead(GroundNode Node, HoldShortReason Reason, double ApproachDeg, double ToStopFt);

    /// <summary>
    /// Brake for a runway bar ahead the follower must stop at, and hold there once stopped. Returns true when the hold
    /// was taken (<see cref="HoldingShortPhase"/> + a new <see cref="FollowingPhase"/> inserted, this phase completes);
    /// otherwise <paramref name="barCap"/> is the braking-curve cap onto the bar's stop, or null with no bar ahead, with the
    /// brake rate the stop needs: the taxi rate (no override) when that makes the hold line, the max-effort rate when only
    /// that does.
    /// </summary>
    private bool CheckRunwayHoldShort(PhaseContext ctx, out SpeedCap? barCap)
    {
        barCap = null;
        ctx.Targets.DesiredDecelRate = null;
        if (FindBarAhead(ctx) is not { } bar)
        {
            return false;
        }

        GroundStopBraking.StopBraking braking = GroundStopBraking.ChooseStopBraking(ctx, bar.ToStopFt);
        if (TryTakeHoldAtBar(ctx, bar, braking))
        {
            return true;
        }

        barCap = StopCap(ctx, bar.ToStopFt, braking);
        return false;
    }

    /// <summary>
    /// The runway bar the follower must stop at, or null: the latched bar while the follower is still closing on its hold
    /// line, otherwise the nearest bar ahead it is closing on, which is then latched. Looked for out to the follower's
    /// braking distance to the bar's stop (<see cref="BarLookAheadFt"/>), so the stop is braked for at the taxi brake rate
    /// rather than met inside it. Bars the follower is already on the runway of, and bars of a runway its crossing clearance
    /// covers, are passed — except its own departure bar, which no crossing clearance covers: that runway is left by LUAW/CTO.
    /// </summary>
    private BarAhead? FindBarAhead(PhaseContext ctx)
    {
        if (ctx.GroundLayout is null)
        {
            return null;
        }

        double halfLengthFt = AircraftLength.ResolveFt(ctx.Aircraft.AircraftType) / 2.0;
        double lookAheadFt = BarLookAheadFt(ctx, halfLengthFt);
        if (LatchedBar(ctx, ctx.GroundLayout, halfLengthFt, lookAheadFt) is { } latched)
        {
            return latched;
        }

        BarAhead? nearest = null;
        double nearestDistanceFt = double.MaxValue;
        foreach (GroundNode node in ctx.GroundLayout.Nodes.Values)
        {
            if (!IsBarAhead(ctx, node, lookAheadFt) || !MustStopAt(ctx, node))
            {
                continue;
            }

            double distanceFt = GeoMath.DistanceNm(ctx.Aircraft.Position, node.Position) * GeoMath.FeetPerNm;
            if (distanceFt >= nearestDistanceFt)
            {
                continue;
            }

            BarAhead candidate = ToBarAhead(ctx, node, ApproachBearingDeg(node, ctx.Aircraft.Position), halfLengthFt);
            if (IsClosingOnHoldLine(ctx, candidate, lookAheadFt))
            {
                nearest = candidate;
                nearestDistanceFt = distanceFt;
            }
        }

        if (nearest is { } bar)
        {
            _latchedBarNodeId = bar.Node.Id;
            _latchedBarApproachDeg = bar.ApproachDeg;
            Log.LogDebug(
                "[Follow] {Callsign}: runway bar node {NodeId} ({Runway}) ahead, nose {ToStop:F1} ft from its hold line at {Speed:F1} kt",
                ctx.Aircraft.Callsign,
                bar.Node.Id,
                bar.Node.RunwayId?.ToString() ?? "unknown",
                bar.ToStopFt,
                ctx.Aircraft.GroundSpeed
            );
        }

        return nearest;
    }

    /// <summary>
    /// The latched bar, while the follower must still stop there and is still closing on its hold line; otherwise the
    /// latch is released and null returned.
    /// </summary>
    private BarAhead? LatchedBar(PhaseContext ctx, AirportGroundLayout layout, double halfLengthFt, double lookAheadFt)
    {
        if ((_latchedBarNodeId is not { } nodeId) || (_latchedBarApproachDeg is not { } approachDeg))
        {
            return null;
        }

        if (layout.Nodes.TryGetValue(nodeId, out GroundNode? node) && MustStopAt(ctx, node))
        {
            BarAhead bar = ToBarAhead(ctx, node, approachDeg, halfLengthFt);
            if (IsClosingOnHoldLine(ctx, bar, lookAheadFt))
            {
                return bar;
            }
        }

        Log.LogDebug("[Follow] {Callsign}: runway bar node {NodeId} no longer ahead; released", ctx.Aircraft.Callsign, nodeId);
        ReleaseLatchedBar();
        return null;
    }

    private void ReleaseLatchedBar()
    {
        _latchedBarNodeId = null;
        _latchedBarApproachDeg = null;
    }

    /// <summary>
    /// Whether the follower must stop at this bar: not when it is already on that runway; always when the bar is its own
    /// departure bar; otherwise not when it is leaving a runway it occupied in this follow away from it, nor when its crossing
    /// clearance covers the runway.
    /// </summary>
    private bool MustStopAt(PhaseContext ctx, GroundNode node)
    {
        if (IsAlreadyOnThatRunway(ctx, node))
        {
            return false;
        }

        // The bar the clearing route leaves the runway by: stopping at it would hold the follower inside its hold line.
        if ((_clearingRoute is not null) && (_clearingBarNodeId == node.Id))
        {
            return false;
        }

        if (BarIsOwnDestination(ctx, node))
        {
            return true;
        }

        return !IsExitingAwayFrom(ctx, node) && !IsClearedToCross(node);
    }

    /// <summary><paramref name="node"/> as a bar ahead of the follower, its hold line square to <paramref name="approachDeg"/>.</summary>
    private static BarAhead ToBarAhead(PhaseContext ctx, GroundNode node, double approachDeg, double halfLengthFt)
    {
        HoldShortReason reason = BarIsOwnDestination(ctx, node) ? HoldShortReason.DestinationRunway : HoldShortReason.RunwayCrossing;
        LatLon nose = GeoMath.ProjectPoint(ctx.Aircraft.Position, ctx.Aircraft.TrueHeading, halfLengthFt / GeoMath.FeetPerNm);
        double noseFromBarFt = GeoMath.DistanceNm(node.Position, nose) * GeoMath.FeetPerNm;
        double offAxisRad = GeoMath.SignedBearingDifference(approachDeg, GeoMath.BearingTo(node.Position, nose)) * Math.PI / 180.0;
        return new BarAhead(node, reason, approachDeg, noseFromBarFt * Math.Cos(offAxisRad));
    }

    /// <summary>
    /// The bearing (deg true) from <paramref name="bar"/> back up the edge leading into it: of the bar node's edges, the
    /// one whose far end lies nearest in bearing to <paramref name="aircraftPosition"/>.
    /// </summary>
    private static double ApproachBearingDeg(GroundNode bar, LatLon aircraftPosition)
    {
        double towardAircraft = GeoMath.BearingTo(bar.Position, aircraftPosition);
        double approachDeg = towardAircraft;
        double nearestOffDeg = double.MaxValue;
        foreach (IGroundEdge edge in bar.Edges)
        {
            double edgeDeg = GeoMath.BearingTo(bar.Position, FarNode(edge, bar).Position);
            double offDeg = Math.Abs(GeoMath.SignedBearingDifference(towardAircraft, edgeDeg));
            if (offDeg < nearestOffDeg)
            {
                nearestOffDeg = offDeg;
                approachDeg = edgeDeg;
            }
        }

        return approachDeg;
    }

    private static GroundNode FarNode(IGroundEdge edge, GroundNode from) => edge.Nodes[0].Id == from.Id ? edge.Nodes[1] : edge.Nodes[0];

    /// <summary>
    /// Whether the follower is rolling into the bar's hold line: its heading carries it toward the line, and at that angle
    /// the nose meets the line within <paramref name="lookAheadFt"/> (or is already at or past it). A bar beside the
    /// follower's path, whose line it runs along rather than into, is not one it stops at.
    /// </summary>
    private static bool IsClosingOnHoldLine(PhaseContext ctx, BarAhead bar, double lookAheadFt)
    {
        double towardLineDeg = (bar.ApproachDeg + 180.0) % 360.0;
        double offLineRad = GeoMath.SignedBearingDifference(ctx.Aircraft.TrueHeading.Degrees, towardLineDeg) * Math.PI / 180.0;
        double closingCos = Math.Cos(offLineRad);
        return (closingCos > 0.0) && (bar.ToStopFt <= (lookAheadFt * closingCos));
    }

    /// <summary>
    /// How far ahead (ft) a bar is looked for: far enough to brake from the current speed at the category's taxi brake
    /// rate to a stop half a fuselage short of the bar, with one tick of travel on top, and never less than
    /// <see cref="HoldShortDetectionNm"/>.
    /// </summary>
    private static double BarLookAheadFt(PhaseContext ctx, double halfLengthFt)
    {
        double speedKts = ctx.Aircraft.GroundSpeed;
        double brakingFt = (speedKts * speedKts) / (2.0 * CategoryPerformance.TaxiDecelRate(ctx.Category)) * GroundStopBraking.FeetPerSecondPerKt;
        double oneTickFt = speedKts * GroundStopBraking.FeetPerSecondPerKt * ctx.DeltaSeconds;
        double stopFt = halfLengthFt + GroundNavigator.SetBackStopMarginFt + brakingFt + oneTickFt;
        return Math.Max(HoldShortDetectionNm * GeoMath.FeetPerNm, stopFt);
    }

    /// <summary>
    /// A runway hold-short bar within <paramref name="lookAheadFt"/> and off the nose. Inside
    /// <see cref="HoldShortDetectionNm"/> any bar within <see cref="HoldShortAngleThreshold"/> of the nose counts; beyond
    /// it, only a bar within <see cref="BarCorridorHalfWidthFt"/> of the line the follower is rolling along, so the longer
    /// look-ahead a fast follower needs does not stop it at a bar on a connector beside its taxiway.
    /// </summary>
    private static bool IsBarAhead(PhaseContext ctx, GroundNode node, double lookAheadFt)
    {
        if (node.Type != GroundNodeType.RunwayHoldShort)
        {
            return false;
        }

        double distFt = GeoMath.DistanceNm(ctx.Aircraft.Position, node.Position) * GeoMath.FeetPerNm;
        if (distFt > lookAheadFt)
        {
            return false;
        }

        double bearing = GeoMath.BearingTo(ctx.Aircraft.Position, node.Position);
        double offNoseDeg = Math.Abs(GeoMath.SignedBearingDifference(ctx.Aircraft.TrueHeading.Degrees, bearing));
        if (offNoseDeg > HoldShortAngleThreshold)
        {
            return false;
        }

        double offLineFt = distFt * Math.Sin(offNoseDeg * Math.PI / 180.0);
        return (distFt <= (HoldShortDetectionNm * GeoMath.FeetPerNm)) || (offLineFt <= BarCorridorHalfWidthFt);
    }

    /// <summary>
    /// Take the hold at <paramref name="bar"/> once the follower is at rest (<see cref="IsAtRest"/>) within
    /// <see cref="BarStopTakeFt"/> of the hold line
    /// (or past it). On the <see cref="GroundStopBraking.StopBraking.Backstop"/>, take it the tick before the nose would
    /// reach the line even braking at the max-effort rate, stopping dead where the follower is, and log that one-tick stop as a
    /// warning. Inserts <see cref="HoldingShortPhase"/> and a fresh follow behind it. Returns true when the hold was taken.
    /// </summary>
    private bool TryTakeHoldAtBar(PhaseContext ctx, BarAhead bar, GroundStopBraking.StopBraking braking)
    {
        bool settled = IsAtRest(ctx) && (bar.ToStopFt <= BarStopTakeFt);
        bool lastResort =
            (braking == GroundStopBraking.StopBraking.Backstop)
            && (bar.ToStopFt <= (GroundStopBraking.MaxEffortBrakingTravelThisTickFt(ctx) + BarStopTakeFt));
        if (!settled && !lastResort)
        {
            return false;
        }

        GroundNode node = bar.Node;
        if (lastResort)
        {
            Log.LogWarning(
                "[Follow] {Callsign}: stopped dead at runway node {NodeId} ({Runway}), reason={Reason}, nose {ToStop:F1} ft from the hold line "
                    + "at {Speed:F1} kt: no brake rate made the line",
                ctx.Aircraft.Callsign,
                node.Id,
                node.RunwayId?.ToString() ?? "unknown",
                bar.Reason,
                bar.ToStopFt,
                ctx.Aircraft.GroundSpeed
            );
        }
        else
        {
            Log.LogDebug(
                "[Follow] {Callsign}: holding short at runway node {NodeId} ({Runway}), reason={Reason}, nose {ToStop:F1} ft from the hold line, "
                    + "{Speed:F1} kt at rest",
                ctx.Aircraft.Callsign,
                node.Id,
                node.RunwayId?.ToString() ?? "unknown",
                bar.Reason,
                bar.ToStopFt,
                ctx.Aircraft.GroundSpeed
            );
        }
        ctx.Aircraft.IndicatedAirspeed = 0;
        ctx.Targets.TargetSpeed = 0;
        ctx.Targets.DesiredDecelRate = null;
        ctx.Aircraft.Ground.CurrentTaxiway = TaxiwayAtBar(node, ctx.Aircraft.Position);
        ReleaseLatchedBar();

        var holdShort = new HoldShortPoint
        {
            NodeId = node.Id,
            Reason = bar.Reason,
            TargetName = node.RunwayId?.ToString(),
        };

        var holdPhase = new HoldingShortPhase(holdShort);
        var resumeFollow = new FollowingPhase(_targetCallsign);
        ctx.Aircraft.Phases?.InsertAfterCurrent([holdPhase, resumeFollow]);
        return true;
    }

    /// <summary>
    /// The taxiway the follower holds on: the name of the bar node's taxiway edge that runs back toward the aircraft, or
    /// null when no edge there is named, so the hold-short report falls back to "taxiway" rather than naming a stale one.
    /// A follow has no route to read it from, and the hold-short report names it.
    /// </summary>
    private static string? TaxiwayAtBar(GroundNode bar, LatLon aircraftPosition)
    {
        double towardAircraft = GeoMath.BearingTo(bar.Position, aircraftPosition);
        string? nearest = null;
        double nearestOffDeg = double.MaxValue;
        foreach (IGroundEdge edge in bar.Edges)
        {
            if (edge.IsRunwayCenterline || string.IsNullOrEmpty(edge.TaxiwayName))
            {
                continue;
            }

            double offDeg = Math.Abs(GeoMath.SignedBearingDifference(towardAircraft, GeoMath.BearingTo(bar.Position, FarNode(edge, bar).Position)));
            if (offDeg < nearestOffDeg)
            {
                nearestOffDeg = offDeg;
                nearest = edge.TaxiwayName;
            }
        }

        return nearest;
    }

    /// <summary>
    /// Whether the aircraft is already on the pavement of the runway this bar protects. A follower being led
    /// off a runway it was staged on — down 1R and out via the F1 intersection — passes that runway's far-side
    /// bar on the way out, and stopping there would leave it holding short of the runway it is standing on.
    /// Leaving a runway is not crossing it; only a bar for a runway the aircraft is not yet on is a crossing.
    /// </summary>
    private static bool IsAlreadyOnThatRunway(PhaseContext ctx, GroundNode node)
    {
        if ((node.RunwayId is not { } barRunway) || ctx.GroundLayout is null)
        {
            return false;
        }

        foreach (RunwayInfo runway in RunwayOccupancy.AirportRunways(ctx.GroundLayout.AirportId))
        {
            if (runway.Id.Overlaps(barRunway) && RunwayOccupancy.IsOnPavement(ctx.Aircraft, runway))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A follow has no route to name the taxiway it is on, so a moving follower reads it from the straight taxiway edge it
    /// is rolling on — null off every taxiway or on an unnamed one — rather than carry a name from before, such as the
    /// taxiway a previous hold was on. The edge is looked for first where the follower last was
    /// (<see cref="_taxiEdgeNodeIds"/>), through <see cref="TaxiEdgeLocator.EdgeUnder"/>.
    /// </summary>
    private void RefreshCurrentTaxiway(PhaseContext ctx)
    {
        if ((ctx.GroundLayout is null) || (ctx.Aircraft.GroundSpeed <= 0.0))
        {
            return;
        }

        GroundEdge? onEdge = TaxiEdgeLocator.EdgeUnder(ctx.GroundLayout, ctx.Aircraft.Position, _taxiEdgeNodeIds);
        _taxiEdgeNodeIds = onEdge is null ? null : (onEdge.Nodes[0].Id, onEdge.Nodes[1].Id);
        ctx.Aircraft.Ground.CurrentTaxiway = onEdge?.TaxiwayName is { Length: > 0 } taxiway ? taxiway : null;
    }

    /// <summary>
    /// Whether the bar protects a runway the follower is exiting — one it occupied in this follow and has not exited clear of, or
    /// one whose hold line it is inside (<see cref="ExitingRunwayOf"/>) — and the follower meets it moving away from that runway:
    /// the route's segment out of the bar node does not lead closer to the runway's centreline. A bar off the remaining route is
    /// judged by the follower's heading instead, so the bar a clearing route is about to leave by is passed on the tick before
    /// that route starts.
    /// </summary>
    private bool IsExitingAwayFrom(PhaseContext ctx, GroundNode node)
    {
        if (ExitingRunwayOf(ctx, node) is not { } runway)
        {
            return false;
        }

        double barFt = CentrelineFt(runway, node.Position);
        TaxiRoute? route = _clearingRoute ?? _followRoute;
        if ((route is not null) && (RemainingSegmentFrom(route, node.Id) is { } outOfBar))
        {
            return CentrelineFt(runway, outOfBar.Edge.ToNode.Position) >= barFt;
        }

        LatLon ahead = GeoMath.ProjectPoint(ctx.Aircraft.Position, ctx.Aircraft.TrueHeading, HoldShortDetectionNm);
        return CentrelineFt(runway, ahead) >= CentrelineFt(runway, ctx.Aircraft.Position);
    }

    /// <summary>The first segment of <paramref name="route"/> from its current one on that leaves <paramref name="nodeId"/>; null with none.</summary>
    private static TaxiRouteSegment? RemainingSegmentFrom(TaxiRoute route, int nodeId)
    {
        for (int i = Math.Max(route.CurrentSegmentIndex, 0); i < route.Segments.Count; i++)
        {
            if (route.Segments[i].FromNodeId == nodeId)
            {
                return route.Segments[i];
            }
        }

        return null;
    }

    /// <summary>
    /// The runway <paramref name="node"/>'s bar protects when the follower is exiting it: it is remembered as occupied in this
    /// follow, or the follower's nose, centre or tail is inside its hold line. Null otherwise.
    /// </summary>
    private RunwayInfo? ExitingRunwayOf(PhaseContext ctx, GroundNode node)
    {
        if ((node.RunwayId is not { } barRunway) || (ctx.GroundLayout is not { } layout) || (RunwayNamed(layout, barRunway) is not { } runway))
        {
            return null;
        }

        foreach (RunwayIdentifier exiting in _exitingRunways)
        {
            if (barRunway.Overlaps(exiting))
            {
                return runway;
            }
        }

        return IsAnyPartInsideHoldLine(layout, runway, NoseOf(ctx.Aircraft), ctx.Aircraft.Position, TailOf(ctx.Aircraft)) ? runway : null;
    }

    /// <summary>The airport runway <paramref name="id"/> names (either end, or both); null when the layout's airport has none.</summary>
    private static RunwayInfo? RunwayNamed(AirportGroundLayout layout, RunwayIdentifier id)
    {
        foreach (RunwayInfo runway in RunwayOccupancy.AirportRunways(layout.AirportId))
        {
            if (runway.Id.Overlaps(id))
            {
                return runway;
            }
        }

        return null;
    }

    /// <summary>Whether the follower's nose, centre or tail is inside <paramref name="runway"/>'s hold line.</summary>
    private static bool IsAnyPartInsideHoldLine(AirportGroundLayout layout, RunwayInfo runway, LatLon nose, LatLon centre, LatLon tail) =>
        IsInsideHoldLine(layout, runway, nose) || IsInsideHoldLine(layout, runway, centre) || IsInsideHoldLine(layout, runway, tail);

    /// <summary>Whether any of <paramref name="runways"/> overlaps <paramref name="id"/>.</summary>
    private static bool AnyOverlaps(List<RunwayIdentifier> runways, RunwayIdentifier id)
    {
        foreach (RunwayIdentifier runway in runways)
        {
            if (runway.Overlaps(id))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>How far (ft) <paramref name="point"/> lies from <paramref name="runway"/>'s centreline, between its ends.</summary>
    private static double CentrelineFt(RunwayInfo runway, LatLon point) =>
        GeoMath.DistanceToSegmentFt(point, new LatLon(runway.Lat1, runway.Lon1), new LatLon(runway.Lat2, runway.Lon2));

    /// <summary>Which side of <paramref name="runway"/>'s centreline, end 1 to end 2, <paramref name="point"/> lies on: true to the right.</summary>
    private static bool IsRightOfCentreline(RunwayInfo runway, LatLon point)
    {
        var end1 = new LatLon(runway.Lat1, runway.Lon1);
        double alongDeg = GeoMath.BearingTo(end1, new LatLon(runway.Lat2, runway.Lon2));
        return GeoMath.SignedBearingDifference(alongDeg, GeoMath.BearingTo(end1, point)) > 0.0;
    }

    /// <summary>
    /// Remembers each runway whose pavement the follower is on, and forgets one it has exited clear of: off its pavement, with
    /// its tail and wingtips farther from the centreline than any of the runway's bars on their side (<see cref="IsClearOfBars"/>).
    /// </summary>
    private void TrackExitingRunways(PhaseContext ctx)
    {
        if (ctx.GroundLayout is not { } layout)
        {
            return;
        }

        foreach (RunwayInfo runway in RunwayOccupancy.AirportRunways(layout.AirportId))
        {
            bool remembered = AnyOverlaps(_exitingRunways, runway.Id);
            if (RunwayOccupancy.IsOnPavement(ctx.Aircraft, runway))
            {
                _exitingRunways = remembered ? _exitingRunways : [.. _exitingRunways, runway.Id];
            }
            else if (remembered && IsClearOfBars(ctx, layout, runway))
            {
                Log.LogDebug(
                    "[Follow] {Callsign}: tail and wingtips clear of runway {Runway}'s bars; they stop the follow again",
                    ctx.Aircraft.Callsign,
                    runway.Id
                );
                _exitingRunways = [.. _exitingRunways.Where(r => !r.Overlaps(runway.Id))];
            }
        }
    }

    /// <summary>
    /// Whether the follower's tail and both wingtips (<see cref="FillTrailingParts"/>) each lie farther from
    /// <paramref name="runway"/>'s centreline than the farthest of the runway's hold-short bars on that part's side: the whole
    /// aircraft is past every hold line on that side, whichever way it faces. A part with no bar of the runway on its side counts
    /// as clear.
    /// </summary>
    private bool IsClearOfBars(PhaseContext ctx, AirportGroundLayout layout, RunwayInfo runway)
    {
        NoteTailOnlyOnce(ctx.Aircraft);
        GroundNode[] bars = RunwayBars(layout, runway);
        Span<LatLon> parts = stackalloc LatLon[MaxTrailingParts];
        int count = FillTrailingParts(ctx.Aircraft, parts);
        for (int i = 0; i < count; i++)
        {
            if (CentrelineFt(runway, parts[i]) <= FarthestBarFt(runway, bars, IsRightOfCentreline(runway, parts[i])))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>How far (ft) from <paramref name="runway"/>'s centreline the farthest of <paramref name="bars"/> on the given side lies; 0 with none.</summary>
    private static double FarthestBarFt(RunwayInfo runway, GroundNode[] bars, bool right)
    {
        double farthestFt = 0.0;
        foreach (GroundNode bar in bars)
        {
            if (IsRightOfCentreline(runway, bar.Position) == right)
            {
                farthestFt = Math.Max(farthestFt, CentrelineFt(runway, bar.Position));
            }
        }

        return farthestFt;
    }

    /// <summary>Whether the bar protects a runway the crossing clearance that started this follow already cleared.</summary>
    private bool IsClearedToCross(GroundNode node) =>
        (node.RunwayId is { } barRunway) && _crossingClearedRunways.Any(cleared => barRunway.Overlaps(cleared));

    /// <summary>
    /// Spend the crossing clearance once it has been used: note when the aircraft is on the pavement of a cleared
    /// runway, and drop the clearance the first tick after that it is clear of all of them, so a bar of the same
    /// runway met later in the follow stops the aircraft again.
    /// </summary>
    private void ExpireUsedCrossingClearance(PhaseContext ctx)
    {
        if ((_crossingClearedRunways.Count == 0) || ctx.GroundLayout is null)
        {
            return;
        }

        bool onClearedRunway = RunwayOccupancy
            .AirportRunways(ctx.GroundLayout.AirportId)
            .Any(runway => _crossingClearedRunways.Any(cleared => runway.Id.Overlaps(cleared)) && RunwayOccupancy.IsOnPavement(ctx.Aircraft, runway));
        if (onClearedRunway)
        {
            _hasBeenOnClearedRunway = true;
            return;
        }

        if (_hasBeenOnClearedRunway)
        {
            Log.LogDebug("[Follow] {Callsign}: clear of the runways its crossing clearance covered; clearance spent", ctx.Aircraft.Callsign);
            _crossingClearedRunways = [];
            _hasBeenOnClearedRunway = false;
        }
    }

    /// <summary>Whether the crossing clearance has been used on a cleared runway and not yet spent (snapshotted).</summary>
    public bool HasBeenOnClearedRunway => _hasBeenOnClearedRunway;

    /// <summary>
    /// Whether this bar is the one the follower's own taxi clearance ends at. FOLLOWG does not erase the route
    /// the aircraft was cleared on, so a bar matching that route's destination runway is its departure bar, not
    /// a crossing. The distinction is load-bearing twice over: <see cref="HoldShortReason.DestinationRunway"/>
    /// makes <c>RES</c> refuse to release it onto the runway without a takeoff clearance, and it puts the
    /// aircraft at tier 0 in <see cref="RunwayDepartureQueue"/> — at the front of the line, where it is.
    /// </summary>
    private static bool BarIsOwnDestination(PhaseContext ctx, GroundNode node)
    {
        if (node.RunwayId is not { } barRunway)
        {
            return false;
        }

        HoldShortPoint? destination = ctx.Aircraft.Ground.AssignedTaxiRoute?.HoldShortPoints.FirstOrDefault(h =>
            h.Reason == HoldShortReason.DestinationRunway
        );
        return (destination?.TargetName is { } name) && barRunway.Overlaps(RunwayIdentifier.Parse(name));
    }
}
