using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Pathfinding;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using static Yaat.Sim.Data.Airport.RunwayHoldBars;

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
/// completes into a <see cref="HoldingInPositionPhase"/>; on a taxiway with an assigned route left it hands over to a
/// <see cref="TaxiingPhase"/> on the rest of that route instead, and with an assigned route it cannot reach it says once that it
/// has lost the traffic and is holding. A clearing route that runs out before the tail is past the hold line leaves the follower
/// holding short of the runway ahead, or holding in position with none, once said (<see cref="SayHoldingShortNotClear"/>).
/// Requires PhaseContext.AircraftLookup to resolve the target.
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
    /// Set when a lead re-route lost the follow route off a runway: the follower keeps the route and its navigator, braking to
    /// rest along it at the taxi rate, so the stop stays on the centreline, and drops the route once at rest
    /// (<see cref="BrakeAlongLostRoute"/>). Snapshotted.
    /// </summary>
    private bool _brakingLostRoute;

    /// <summary>
    /// Set when a clearing route ran out before the tail was past the hold line: the pilot has said once that it holds short of
    /// the runway ahead and is not clear of the one behind (<see cref="SayHoldingShortNotClear"/>), and says no lost-traffic
    /// call for that hold either. Snapshotted.
    /// </summary>
    private bool _saidHeldShortNotClear;

    /// <summary>
    /// Set when the pilot has said once that the lead was lost with no way back onto an assigned route, holding in position and
    /// asking for taxi instructions (<see cref="SayLostTrafficHolding"/>). Snapshotted.
    /// </summary>
    private bool _saidLostTraffic;

    /// <summary>
    /// Set when the pilot has said once that it cannot follow (<see cref="SayUnableToFollowOnce"/>): no taxi route onto its
    /// traffic's path, a re-plan after a lead re-route that finds it ahead of the lead, or a hold standing on the lead's route
    /// ahead of it (<see cref="HoldOnUnjoinedPlan"/>, <see cref="HoldOnEmptyPlan"/>). Snapshotted. Silences the lost-traffic
    /// call: a follower that cannot follow says that once and nothing more, while one unjoinable for another reason (a route
    /// lost to a backward start, or a first plan ahead of its lead) has said nothing and still reports losing the traffic.
    /// </summary>
    private bool _saidUnableNoRoute;

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

    /// <summary>
    /// The least distance (ft) past the bar, beyond the aircraft's own length, a clearing route runs on (<see cref="ClearingNeedFt"/>).
    /// </summary>
    private const double ClearingBeyondBarFt = 50.0;

    /// <summary>How much nearer (ft) a runway's centreline an edge may end and still count as leading away from it.</summary>
    private const double AwayToleranceFt = 1.0;

    /// <summary>How far ahead (ft) of the aircraft its heading is probed to tell whether it takes it away from a runway.</summary>
    private const double HeadingProbeFt = 50.0;

    /// <summary>The speed (kt) below which another aircraft at a runway exit counts as standing there, occupying it.</summary>
    private const double AtRestKts = 1.0;

    /// <summary>The reason a hold in position was last logged for, so it is logged once. Logging only: not snapshotted.</summary>
    private string? _loggedHoldReason;

    /// <summary>
    /// Whether this follow has logged that its type has no wingspan (<see cref="NoteTailOnlyOnce"/>). Logging only: not snapshotted.
    /// </summary>
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
    /// Whether the follower waits for its lead at each cleared bar judged so far in the phase's current whole second
    /// (<see cref="_leadWaitSecond"/>), by bar node id (<see cref="WaitsForLeadAt"/>): each judgement plans the follow afresh, so
    /// a verdict holds for the rest of that second. Snapshotted, so a restore mid-second keeps the verdicts the run held.
    /// </summary>
    private Dictionary<int, bool> _leadWaitVerdicts = [];

    /// <summary>The whole second of <see cref="Phase.ElapsedSeconds"/> <see cref="_leadWaitVerdicts"/> were judged in.</summary>
    private int _leadWaitSecond;

    /// <summary>
    /// The straight taxi edge the moving follower was last found on, by its end nodes (<c>Nodes[0]</c>, <c>Nodes[1]</c>):
    /// where <see cref="RefreshCurrentTaxiway"/> looks first. Null before the first find and while off every taxiway.
    /// </summary>
    private (int NodeA, int NodeB)? _taxiEdgeNodeIds;

    /// <summary>
    /// The lead's departure bar (<see cref="LeadDepartureBar"/>), read from the lead each tick it is on the ground
    /// (<see cref="RefreshLeadDepartureBar"/>) and kept once it has departed. Null until one is seen. Snapshotted.
    /// </summary>
    private LeadDepartureBar? _leadDepartureBar;

    /// <summary>
    /// Whether this follow has seen its lead departing (<see cref="NoteLeadSeenDeparting"/>): a lead deleted after that still
    /// counts as departed, so the follower taxis on to its departure bar. Snapshotted.
    /// </summary>
    private bool _leadSeenDeparting;

    public string TargetCallsign => _targetCallsign;

    /// <summary>
    /// Whether this follow has seen its lead departing; a follow inserted behind a hold the follow took, or rebuilt by a crossing
    /// clearance there, carries it over.
    /// </summary>
    public bool LeadSeenDeparting
    {
        get => _leadSeenDeparting;
        init => _leadSeenDeparting = value;
    }

    /// <summary>
    /// The runway bar the lead departs from: its hold-short node, and the runway end it departs on as a pilot names it ("30"),
    /// never the bar's combined runway id. A follower with no departure runway of its own whose flight plan departs this
    /// airport holds at that bar as a departure hold for that end (<see cref="DepartureEndAt"/>): it is no runway assignment.
    /// </summary>
    public sealed record LeadDepartureBar(int NodeId, string RunwayEnd);

    /// <summary>
    /// The lead's departure bar this follow knows of; a follow inserted behind a hold the follow took carries it over. Null
    /// until one is seen.
    /// </summary>
    public LeadDepartureBar? LeadDeparture
    {
        get => _leadDepartureBar;
        init => _leadDepartureBar = value;
    }

    private IReadOnlyList<RunwayIdentifier> _crossingClearedRunways = [];
    private List<RunwayIdentifier> _beenOnClearedRunways = [];

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
    /// A <c>CROSS</c> while following adds runways ahead (<see cref="ClearToCross"/>). Each runway's clearance is used once:
    /// the first tick the aircraft is clear of that runway's pavement after having been on it, that runway expires
    /// (<see cref="ExpireUsedCrossingClearance"/>). Empty for a follow no crossing started.
    /// </summary>
    public IReadOnlyList<RunwayIdentifier> CrossingClearedRunways
    {
        get => _crossingClearedRunways;
        init => _crossingClearedRunways = value;
    }

    /// <summary>
    /// Pre-clears <paramref name="runway"/>, a runway on the follower's route ahead: a <c>CROSS</c> issued while it follows, or
    /// while it holds at a bar of another runway, joins the crossing clearance (<see cref="CrossingClearedRunways"/>) so the
    /// follow does not stop at its bars. A runway the clearance already covers is not added twice.
    /// </summary>
    public void ClearToCross(RunwayIdentifier runway)
    {
        if (!_crossingClearedRunways.Any(cleared => cleared.Overlaps(runway)))
        {
            _crossingClearedRunways = [.. _crossingClearedRunways, runway];
        }
    }

    public override string Name => $"Following {_targetCallsign}";

    /// <summary>The speed the navigator asks for now, before the ground speed limit (<see cref="GroundNavigator.RequestedSpeedKts"/>).</summary>
    internal double? NavRequestedSpeedKts => _nav.RequestedSpeedKts;

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

    /// <summary>The cached wait-for-the-lead verdicts at cleared bars, by bar node id, for the current whole second of the phase.</summary>
    public IReadOnlyDictionary<int, bool> LeadWaitVerdicts => _leadWaitVerdicts;

    /// <summary>Whether the follower is braking to rest along a follow route a lead re-route lost, which it drops once at rest.</summary>
    public bool IsBrakingLostRoute => _brakingLostRoute;

    /// <summary>
    /// How many times this follow has run the follow route planner since it was created or restored. Diagnostic only: not snapshotted.
    /// </summary>
    internal int PlanCount { get; private set; }

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
        if (_brokenOff)
        {
            return TickBrokenOff(ctx);
        }

        if (ctx.Aircraft.Ground.IsImmobile)
        {
            TickHeld(ctx);
            return false;
        }

        ExpireUsedCrossingClearance(ctx);
        TrackExitingRunways(ctx);
        RefreshCurrentTaxiway(ctx);
        RefreshLeadDepartureBar(ctx);
        if (CheckRunwayHoldShort(ctx, out SpeedCap? barCap))
        {
            return true;
        }

        if (BrakeAlongLostRoute(ctx, barCap))
        {
            return false;
        }

        AircraftState? target = ctx.AircraftLookup?.Invoke(_targetCallsign);
        if ((target is null) || !target.IsOnGround || IsRollingForTakeoffFromKnownBar(ctx, target))
        {
            return TickLeadGone(ctx, target, barCap);
        }

        if (((_clearingRoute is null) || ResumeFollowFromBrakingRoll(ctx, target)) && DriveFollowRoute(ctx, target))
        {
            ApplySpeedCaps(ctx, target, barCap);
            return false;
        }

        TickWithoutFollowRoute(ctx, barCap);
        return false;
    }

    /// <summary>
    /// A tick with no follow route to drive: a route a lead re-route lost this tick is braked along to rest
    /// (<see cref="BrakeAlongLostRoute"/>); otherwise the follower drives its clearing route off a runway, or holds in position.
    /// </summary>
    private void TickWithoutFollowRoute(PhaseContext ctx, SpeedCap? barCap)
    {
        if (BrakeAlongLostRoute(ctx, barCap))
        {
            return;
        }

        if (DriveClearingRoute(ctx))
        {
            ApplyClearingCaps(ctx, barCap);
        }
        else
        {
            HoldInPosition(ctx, barCap);
        }
    }

    /// <summary>
    /// While a lead re-route has lost the follow route (<see cref="_brakingLostRoute"/>), ticks the navigator over that route with
    /// its speed pinned to zero, so physics brakes the follower to rest at the taxi rate — or the firm rate a runway bar ahead needs
    /// (<paramref name="barCap"/>) — on the route's centreline through any bend or fillet, rather than rolling on along its last
    /// heading. A bar ahead of a runway its crossing clearance covers, which <see cref="FindBarAhead"/> passes, is never one it
    /// comes to rest inside: it stops short at the firm rate when only that makes the hold line, and carries on across when not
    /// even that does (<see cref="CarriesOnAcross"/>), handing over to its clearing route on the runway's pavement
    /// (<see cref="HandOverToClearingRoute"/>). A route end inside the stop at the taxi rate is braked onto at the firm rate
    /// (<see cref="LostRouteEndDecelRate"/>); a route that runs out while the follower still rolls is kept, the follower holding
    /// the last segment's heading at the firm rate to rest (<see cref="StepLostRoute"/>). At rest the route is dropped, and so
    /// is a route that runs out while carrying on across. True while still braking along it, or once handed over.
    /// </summary>
    private bool BrakeAlongLostRoute(PhaseContext ctx, SpeedCap? barCap)
    {
        if (!_brakingLostRoute)
        {
            return false;
        }

        if ((_followRoute is { } route) && !IsAtRest(ctx) && StepBrakingAlongLostRoute(ctx, route, barCap))
        {
            return true;
        }

        if (IsAtRest(ctx))
        {
            Log.LogDebug("[Follow] {Callsign}: at rest on the follow route it lost; dropping it", ctx.Aircraft.Callsign);
        }
        else
        {
            Log.LogDebug("[Follow] {Callsign}: follow route it lost ran out while carrying on across", ctx.Aircraft.Callsign);
        }

        if (_followRoute is { IsComplete: true })
        {
            ctx.Targets.TargetTrueHeading = null;
        }

        DropFollowRoute();
        return false;
    }

    /// <summary>
    /// A step braking along the lost route: hands over to its clearing route when the follower is on a runway, else ticks the
    /// navigator over the route and brakes to rest at its end, or short of a crossed bar. False once the route has run out with
    /// nothing to stop for, when the caller drops it; true while it is still braking along the route, or once handed over.
    /// </summary>
    private bool StepBrakingAlongLostRoute(PhaseContext ctx, TaxiRoute route, SpeedCap? barCap)
    {
        if (CrossedRunwayUnder(ctx) is { } runway)
        {
            HandOverToClearingRoute(ctx, runway, barCap);
            return true;
        }

        CrossedStop? crossed = CrossedBarAhead(ctx) is { } bar ? new CrossedStop(bar, GroundStopBraking.ChooseStopBraking(ctx, bar.ToStopFt)) : null;
        bool carryOn = CarriesOnAcross(ctx, crossed);
        if (!StepLostRoute(ctx, route) && carryOn)
        {
            return false;
        }

        if (!carryOn)
        {
            AircraftState? lead = ctx.AircraftLookup?.Invoke(_targetCallsign);
            BrakeToRestShortOf(ctx, crossed, barCap);
            ApplyCap(ctx, LostRouteEndCap(ctx, route));
            ApplyCap(ctx, LostRouteLeadGapCap(ctx, lead));
            ApplyCap(ctx, LostRouteHeadOnCap(ctx, route, lead));
        }

        return true;
    }

    /// <summary>
    /// The firm stop short of the first edge of the lost route that runs head-on against <paramref name="lead"/>'s driven route
    /// (<see cref="DrivenRouteOf"/>) from its current segment on, when that edge lies inside the follower's stop at the taxi rate
    /// (<see cref="LostRouteEndDecelRate"/>): a follower a re-route left ahead of its lead brakes as hard as it may short of the
    /// lead's path rather than rolling into it. Null when no such edge lies ahead, or the taxi rate stops it short.
    /// </summary>
    private static SpeedCap? LostRouteHeadOnCap(PhaseContext ctx, TaxiRoute route, AircraftState? lead)
    {
        if (route.IsComplete || (lead is null) || (DrivenRouteOf(lead) is not { } leadRoute))
        {
            return null;
        }

        HashSet<(int, int)> againstLead =
        [
            .. leadRoute.Segments.Skip(Math.Max(0, leadRoute.CurrentSegmentIndex)).Select(s => (s.ToNodeId, s.FromNodeId)),
        ];
        int headOn = route.Segments.FindIndex(Math.Max(0, route.CurrentSegmentIndex), s => againstLead.Contains((s.FromNodeId, s.ToNodeId)));
        if (headOn < 0)
        {
            return null;
        }

        double toEdgeFt = route.RemainingDistanceFt(ctx.Aircraft.Position, headOn);
        return LostRouteEndDecelRate(ctx.Category, ctx.Aircraft.GroundSpeed, toEdgeFt) is { } rate ? new SpeedCap(0.0, rate) : null;
    }

    /// <summary>
    /// The brake rate for the lead (<see cref="LostRouteLeadGapDecelRate"/>) folded into the rest <see cref="BrakeToRestShortOf"/>
    /// already pins; null when the taxi rate will do.
    /// </summary>
    private static SpeedCap? LostRouteLeadGapCap(PhaseContext ctx, AircraftState? lead) =>
        LostRouteLeadGapDecelRate(ctx.Aircraft, ctx.Category, lead) is { } rate ? new SpeedCap(0.0, rate) : null;

    /// <summary>
    /// The brake rate (kt/s) a <paramref name="follower"/> of <paramref name="category"/> braking to rest along a lost route
    /// needs for <paramref name="lead"/>, the follow route no longer leading to it: the max-effort rate
    /// (<see cref="CategoryPerformance.ExpediteExitDecelRate"/>) when braking at the taxi rate would end inside the stop gap
    /// (<see cref="FollowGap.StopGapFt"/>) behind the lead's tail, read on the straight-line nose-to-tail gap
    /// (<see cref="NoseToTailFt"/>); else null, braking at the taxi rate. Null too with no lead, a lead off the ground, or a lead
    /// whose tail is not ahead of the follower (<see cref="IsTailAhead"/>): a lead abeam or behind is nothing to brake for.
    /// </summary>
    public static double? LostRouteLeadGapDecelRate(AircraftState follower, AircraftCategory category, AircraftState? lead)
    {
        if ((lead is null) || !lead.IsOnGround || !IsTailAhead(follower, lead))
        {
            return null;
        }

        AircraftCategory leadCategory = AircraftCategorization.Categorize(lead.AircraftType);
        double stopGapFt = FollowGap.StopGapFt(lead.AircraftType, leadCategory, follower.AircraftType, category);
        return LostRouteEndDecelRate(category, follower.GroundSpeed, NoseToTailFt(follower, lead) - stopGapFt);
    }

    /// <summary>
    /// Whether <paramref name="lead"/>'s tail lies within 90° either side of <paramref name="follower"/>'s heading, seen from
    /// the follower's centre.
    /// </summary>
    private static bool IsTailAhead(AircraftState follower, AircraftState lead) =>
        Math.Abs(GeoMath.SignedBearingDifference(follower.TrueHeading.Degrees, GeoMath.BearingTo(follower.Position, TailOf(lead)))) < 90.0;

    /// <summary>
    /// Ticks the navigator over the lost route, as a held follow's route is ticked (no lead, its end a stop), and true while it
    /// has route left. A route that runs out is kept, not dropped: from then on the navigator is no longer ticked, and the
    /// follower holds the last segment's heading (its arrival bearing) while it brakes to rest.
    /// </summary>
    private bool StepLostRoute(PhaseContext ctx, TaxiRoute route)
    {
        if (!route.IsComplete)
        {
            NavStep step = StepNavigator(ctx, route, isFollowRoute: true, routeEndIsStop: true);
            if (step == NavStep.Arrived)
            {
                SetUpNavSegment(ctx, route, isFollowRoute: true);
            }

            SyncPathToMerge();
            if (step != NavStep.RanOut)
            {
                return true;
            }

            Log.LogDebug(
                "[Follow] {Callsign}: follow route it lost ran out at node {Node} while moving; holding its heading to rest",
                ctx.Aircraft.Callsign,
                _nav.TargetNodeId
            );
        }

        ctx.Targets.TargetTrueHeading = new TrueHeading(route.Segments[^1].Edge.ArrivalBearing);
        return false;
    }

    /// <summary>
    /// The firm stop onto the lost route's end when it lies inside the follower's stop at the taxi rate
    /// (<see cref="LostRouteEndDecelRate"/>), at once once the route has run out; null when the taxi rate stops it short.
    /// </summary>
    private static SpeedCap? LostRouteEndCap(PhaseContext ctx, TaxiRoute route)
    {
        double toEndFt = route.IsComplete ? 0.0 : route.RemainingDistanceFt(ctx.Aircraft.Position, route.Segments.Count);
        return LostRouteEndDecelRate(ctx.Category, ctx.Aircraft.GroundSpeed, toEndFt) is { } rate ? new SpeedCap(0.0, rate) : null;
    }

    /// <summary>
    /// The brake rate (kt/s) onto a lost route's end <paramref name="toEndFt"/> ahead of a follower of
    /// <paramref name="category"/> at <paramref name="groundSpeedKts"/>: the firm rate
    /// (<see cref="CategoryPerformance.ExpediteExitDecelRate"/>) when the end lies inside its stopping distance at the taxi rate,
    /// else null, braking at the taxi rate.
    /// </summary>
    public static double? LostRouteEndDecelRate(AircraftCategory category, double groundSpeedKts, double toEndFt) =>
        GroundStopBraking.StoppingDistanceFt(groundSpeedKts, CategoryPerformance.TaxiDecelRate(category)) > toEndFt
            ? CategoryPerformance.ExpediteExitDecelRate(category)
            : null;

    /// <summary>A crossed bar ahead on the lost route and the braking that stops the follower short of its hold line.</summary>
    private readonly record struct CrossedStop(BarAhead Bar, GroundStopBraking.StopBraking Braking);

    /// <summary>
    /// Pins the speed to zero, braking at the taxi rate, or at the firm rate a runway bar ahead needs (<paramref name="barCap"/>)
    /// or the crossed bar ahead does to stop short of its hold line (<paramref name="crossed"/>, <see cref="StopCap"/>).
    /// </summary>
    private static void BrakeToRestShortOf(PhaseContext ctx, CrossedStop? crossed, SpeedCap? barCap)
    {
        ctx.Targets.TargetSpeed = 0;
        ctx.Targets.DesiredDecelRate = barCap?.DecelRate;
        if (crossed is { } stop)
        {
            ApplyCap(ctx, StopCap(ctx, stop.Bar.ToStopFt, stop.Braking));
        }
    }

    /// <summary>
    /// Whether a follower braking along its lost route carries on across a runway its crossing clearance covers, under that
    /// clearance, rather than come to rest inside its hold line: not even the firm rate stops it short of the crossed bar ahead
    /// on the route (<paramref name="crossed"/>), or its nose is already inside that runway's hold line
    /// (<see cref="CrossedRunwayAtNose"/>).
    /// </summary>
    private bool CarriesOnAcross(PhaseContext ctx, CrossedStop? crossed) =>
        crossed is { } stop ? (stop.Braking == GroundStopBraking.StopBraking.Backstop) : (CrossedRunwayAtNose(ctx) is not null);

    /// <summary>
    /// The nearest runway bar ahead on the rest of the lost route the follower is closing on whose runway its crossing clearance
    /// covers — one <see cref="FindBarAhead"/> passes — looked for out to the same braking distance; null with none. A bar off the
    /// route, past where it turns off, is not one it crosses.
    /// </summary>
    private BarAhead? CrossedBarAhead(PhaseContext ctx)
    {
        if ((ctx.GroundLayout is not { } layout) || (_followRoute is not { } route))
        {
            return null;
        }

        double halfLengthFt = AircraftLength.ResolveFt(ctx.Aircraft.AircraftType) / 2.0;
        double lookAheadFt = BarLookAheadFt(ctx, halfLengthFt);
        BarAhead? nearest = null;
        IEnumerable<GroundNode> crossedAhead = layout.Nodes.Values.Where(node => IsBarAhead(ctx, node, lookAheadFt) && IsCrossedBar(ctx, node));
        foreach (GroundNode node in BarsOnRestOfRoute(route, crossedAhead))
        {
            if (
                (ReachedBarAhead(ctx, node, halfLengthFt) is { } candidate)
                && IsClosingOnHoldLine(ctx, candidate, lookAheadFt)
                && ((nearest is null) || (candidate.ToStopFt < nearest.Value.ToStopFt))
            )
            {
                nearest = candidate;
            }
        }

        return nearest;
    }

    /// <summary>
    /// The bars of <paramref name="bars"/> a follower braking along <paramref name="route"/> would cross: those at an end of one of
    /// the route's segments from its current one on (<see cref="IsOnRestOfRoute"/>). A bar ahead of the follower past where the
    /// route turns off is not one it crosses.
    /// </summary>
    public static IEnumerable<GroundNode> BarsOnRestOfRoute(TaxiRoute route, IEnumerable<GroundNode> bars) =>
        bars.Where(bar => IsOnRestOfRoute(route, bar.Id));

    /// <summary>Whether node <paramref name="nodeId"/> is an end of one of <paramref name="route"/>'s segments from its current one on.</summary>
    private static bool IsOnRestOfRoute(TaxiRoute route, int nodeId)
    {
        for (int i = route.CurrentSegmentIndex; i < route.Segments.Count; i++)
        {
            if ((route.Segments[i].FromNodeId == nodeId) || (route.Segments[i].ToNodeId == nodeId))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether the follower passes <paramref name="node"/>'s bar because its crossing clearance covers the runway.</summary>
    private bool IsCrossedBar(PhaseContext ctx, GroundNode node) => !MustStopAt(ctx, node) && IsClearedToCross(node);

    /// <summary>
    /// A runway the crossing clearance covers whose hold line the follower's nose is inside, short of its pavement; null with none.
    /// </summary>
    private RunwayInfo? CrossedRunwayAtNose(PhaseContext ctx)
    {
        if (ctx.GroundLayout is not { } layout)
        {
            return null;
        }

        LatLon nose = NoseOf(ctx.Aircraft);
        return RunwayOccupancy
            .AirportRunways(layout.AirportId)
            .FirstOrDefault(r =>
                IsRunwayCrossingCleared(r)
                && IsInsideHoldLine(layout, r, nose, ctx.Aircraft.Position)
                && !RunwayOccupancy.IsOnPavement(ctx.Aircraft, r)
            );
    }

    /// <summary>A runway the crossing clearance covers whose pavement some part of the follower is on; null with none.</summary>
    private RunwayInfo? CrossedRunwayUnder(PhaseContext ctx) =>
        ctx.GroundLayout is not { } layout
            ? null
            : RunwayOccupancy
                .AirportRunways(layout.AirportId)
                .FirstOrDefault(r => IsRunwayCrossingCleared(r) && RunwayOccupancy.IsOnPavement(ctx.Aircraft, r));

    /// <summary>Whether the crossing clearance that started this follow covers <paramref name="runway"/>.</summary>
    private bool IsRunwayCrossingCleared(RunwayInfo runway) => _crossingClearedRunways.Any(cleared => runway.Id.Overlaps(cleared));

    /// <summary>
    /// A follower carrying on across <paramref name="runway"/> along its lost route is on the runway's pavement: the lost route is
    /// dropped and its clearing route takes over this tick, as for a route lost on the runway (<see cref="LoseFollowRoute"/>).
    /// </summary>
    private void HandOverToClearingRoute(PhaseContext ctx, RunwayInfo runway, SpeedCap? barCap)
    {
        Log.LogDebug(
            "[Follow] {Callsign}: on runway {Runway} carrying on across it along the follow route it lost; clearing it",
            ctx.Aircraft.Callsign,
            runway.Id
        );
        DropFollowRoute();
        TickWithoutFollowRoute(ctx, barCap);
    }

    /// <summary>
    /// The lead is deleted or no longer on the ground, or rolling for takeoff from its known departure bar
    /// (<see cref="IsRollingForTakeoffFromKnownBar"/>). A follower whose lead departed, with no route of its own left, that takes
    /// the lead's departure bar as its own (<see cref="TakesLeadDepartureBar"/>) taxis on to that bar
    /// (<see cref="TaxiOnToLeadDepartureBar"/>), where its bar scan stops it as a departure hold. Otherwise the follow route is
    /// dropped, and a follower on a runway or inside its hold line first drives its clearing route off it and, once clear, brakes
    /// along that route to rest (<see cref="DriveClearingRoute"/>). On a taxiway with an assigned route left, a
    /// <see cref="TaxiingPhase"/> takes over on the rest of it (<see cref="ResumeAssignedRoute"/>). Otherwise it brakes to a stop
    /// where it is (<see cref="HoldInPosition"/>), and at rest (<see cref="IsAtRest"/>) a <see cref="HoldingInPositionPhase"/>
    /// takes over, so the aircraft stays in a state that accepts commands. True when the follow completes.
    /// </summary>
    private bool _brokenOff;

    /// <summary>
    /// Whether a <c>HOLD</c> has broken off this follow (<see cref="BreakOff"/>): it no longer follows, only brakes to rest
    /// (snapshotted).
    /// </summary>
    public bool IsBrokenOff => _brokenOff;

    /// <summary>
    /// Breaks off the follow for a <c>HOLD</c>: from the next tick the follower only brakes to rest at its taxi brake rate, the
    /// way a follow whose lead is gone stops (<see cref="TickBrokenOff"/>), and at rest a <see cref="HoldingInPositionPhase"/>
    /// takes over.
    /// </summary>
    public void BreakOff() => _brokenOff = true;

    /// <summary>
    /// A broken-off follow: the navigator ticks as it does under a hold (<see cref="TickHeld"/>) while it brakes to a stop where
    /// it is (<see cref="HoldInPosition"/>); at rest (<see cref="IsAtRest"/>) a <see cref="HoldingInPositionPhase"/> takes over.
    /// True when the follow completes.
    /// </summary>
    private bool TickBrokenOff(PhaseContext ctx)
    {
        TickHeld(ctx);
        HoldInPosition(ctx, null);
        if (!IsAtRest(ctx))
        {
            return false;
        }

        string? tailInside = TailInsideRunwayEnd(ctx);
        Log.LogDebug(
            "[Follow] {Callsign}: follow of {Target} broken off; stopped, tail inside {Runway}",
            ctx.Aircraft.Callsign,
            _targetCallsign,
            tailInside ?? "no runway"
        );
        ctx.Aircraft.IndicatedAirspeed = 0;
        ctx.Targets.TargetSpeed = 0;
        ctx.Aircraft.Phases?.InsertAfterCurrent(
            HoldingInPositionPhase.AfterFollowBreakOff(heldShortOfRunway: null, tailInside, PilotResponder.SoloPositionsGround)
        );
        return true;
    }

    /// <summary>
    /// The end nearest the follower (<see cref="RunwayCrossingEnd.Nearest"/>) of a runway it has been on (<see cref="ExitingRunways"/>)
    /// whose far holding position marking its tail and wingtips have not cleared (<see cref="IsClearPastBar"/>, against that runway's
    /// bar nearest it on its side): the runway a broken-off follow comes to rest not clear of (AIM 2-3-5.a.1.(a), 4-3-21.b). Null
    /// when it is clear of every one, or with no layout.
    /// </summary>
    internal string? TailInsideRunwayEnd(PhaseContext ctx)
    {
        if (ctx.GroundLayout is not { } layout)
        {
            return null;
        }

        foreach (RunwayInfo runway in RunwayOccupancy.AirportRunways(layout.AirportId))
        {
            if (
                AnyOverlaps(_exitingRunways, runway.Id)
                && (NearestSameSideBar(layout, runway, ctx.Aircraft.Position) is { } bar)
                && !IsClearPastBar(ctx.Aircraft, runway, bar)
            )
            {
                return RunwayCrossingEnd.Nearest(ctx.Aircraft, runway.Id.ToString(), layout);
            }
        }

        return null;
    }

    /// <summary>The runways whose pavement the follower has been on and is not yet clear of the bars of (snapshotted).</summary>
    public IReadOnlyList<RunwayIdentifier> ExitingRunways => _exitingRunways;

    /// <summary>
    /// The crossing a <c>HOLD</c> hands the follower to when any part of it is inside the holding position markings of a runway
    /// the follow is cleared across, or still crossing (<see cref="ExitingRunways"/>): a <see cref="CrossingRunwayPhase"/> over
    /// the rest of the follow route to the first bar of that runway ahead on the far side, so the hold stops it there and a
    /// release finishes the crossing tail-clear. Null when it is inside no such runway's markings, has no follow route, or the
    /// route shows no bar of the runway behind it and one ahead on the far side.
    /// </summary>
    public CrossingRunwayPhase? CrossingToFinish(AircraftState aircraft, AirportGroundLayout layout)
    {
        if (_followRoute is not { IsComplete: false } route)
        {
            return null;
        }

        foreach (RunwayInfo runway in RunwayOccupancy.AirportRunways(layout.AirportId))
        {
            bool cleared = _crossingClearedRunways.Any(r => r.Overlaps(runway.Id)) || AnyOverlaps(_exitingRunways, runway.Id);
            GroundNode[] bars = RunwayBars(layout, runway);
            if (cleared && IsInsideRunwayMarkings(aircraft, bars, runway) && (CrossingAlongRoute(route, bars, runway) is { } crossing))
            {
                return crossing;
            }
        }

        return null;
    }

    /// <summary>
    /// The crossing of <paramref name="runway"/> along <paramref name="route"/> from its current segment: the entry side is the side
    /// of the last of the runway's <paramref name="bars"/> the route passed, the far bar the first one ahead on the other side.
    /// </summary>
    private static CrossingRunwayPhase? CrossingAlongRoute(TaxiRoute route, GroundNode[] bars, RunwayInfo runway)
    {
        int at = route.CurrentSegmentIndex;
        GroundNode? entry = null;
        for (int i = at; (i >= 0) && (entry is null); i--)
        {
            entry = Array.Find(bars, b => b.Id == route.Segments[i].FromNodeId);
        }

        if (entry is null)
        {
            return null;
        }

        bool entryRight = IsRightOfCentreline(runway, entry.Position);
        int farIndex = route.Segments.FindIndex(
            at,
            s => Array.Find(bars, b => b.Id == s.ToNodeId) is { } bar && (IsRightOfCentreline(runway, bar.Position) != entryRight)
        );
        if (farIndex < 0)
        {
            return null;
        }

        int farBarId = route.Segments[farIndex].ToNodeId;
        var path = new TaxiRoute
        {
            Segments = [.. route.Segments.Skip(at).Select(static s => new TaxiRouteSegment { Edge = s.Edge, TaxiwayName = s.TaxiwayName })],
            HoldShortPoints =
            [
                new HoldShortPoint
                {
                    NodeId = farBarId,
                    Reason = HoldShortReason.RunwayCrossing,
                    TargetName = runway.Id.ToString(),
                    IsCleared = true,
                },
            ],
        };
        return CrossingRunwayPhase.OverOwnPath(path.Segments[0].FromNodeId, farBarId, runway.Id.ToString(), path, false);
    }

    private bool TickLeadGone(PhaseContext ctx, AircraftState? lead, SpeedCap? barCap)
    {
        NoteLeadSeenDeparting(lead);
        if (_leadSeenDeparting && TaxiOnToLeadDepartureBar(ctx, lead, barCap))
        {
            return false;
        }

        if (_followRoute is not null)
        {
            DropFollowRoute();
        }

        if (DriveClearingRoute(ctx))
        {
            ApplyClearingCaps(ctx, barCap);
            return false;
        }

        if (ResumeAssignedRoute(ctx, lead))
        {
            return true;
        }

        SayLostTrafficHolding(ctx);
        HoldInPosition(ctx, barCap);
        if (!IsAtRest(ctx))
        {
            return false;
        }

        Log.LogDebug("[Follow] {Callsign}: target {Target} {Reason}; stopped", ctx.Aircraft.Callsign, _targetCallsign, LeadGoneReason(lead));
        ctx.Aircraft.IndicatedAirspeed = 0;
        ctx.Targets.TargetSpeed = 0;
        ctx.Aircraft.Phases?.InsertAfterCurrent(new HoldingInPositionPhase());
        return true;
    }

    /// <summary>What the lead-gone logs say became of the lead: not found, departing (still on the ground, rolling), or airborne.</summary>
    private static string LeadGoneReason(AircraftState? lead) =>
        lead is null ? "not found"
        : lead.IsOnGround ? "departing"
        : "no longer on ground";

    /// <summary>
    /// Records that the lead was seen departing (<see cref="_leadSeenDeparting"/>): it is present, has left the ground or is
    /// rolling for takeoff (the caller's lead-gone test), and its departure bar is known. A lead later deleted still counts as
    /// departed; one deleted on the ground never sets it.
    /// </summary>
    private void NoteLeadSeenDeparting(AircraftState? lead)
    {
        if ((lead is not null) && (_leadDepartureBar is not null))
        {
            _leadSeenDeparting = true;
        }
    }

    /// <summary>
    /// The lead has departed: a follower with no route of its own left, not clearing or braking along a lost route, that takes the
    /// lead's departure bar as its own (<see cref="CanTaxiOnToLeadBar"/>), drives on to the bar along the lead's path
    /// (<see cref="EnsureRouteToBar"/>), stopping at every bar on the way it has no crossing for and at that bar
    /// (<see cref="CheckRunwayHoldShort"/>). True while it drives on; false when it does not, or has no way to the bar, which
    /// leaves the lead-gone stop to the caller.
    /// </summary>
    private bool TaxiOnToLeadDepartureBar(PhaseContext ctx, AircraftState? lead, SpeedCap? barCap)
    {
        if (!CanTaxiOnToLeadBar(ctx, out AirportGroundLayout? layout, out GroundNode? bar) || !EnsureRouteToBar(ctx, layout, bar, lead))
        {
            return false;
        }

        if (!TickFollowNavigator(ctx, null))
        {
            return false;
        }

        ApplyCap(ctx, RouteEndStopCap(ctx));
        ApplyCap(ctx, barCap);
        return true;
    }

    /// <summary>
    /// Whether the follower may taxi on to the lead's departure bar: it drives no other route (<see cref="IsDrivingAnotherRoute"/>),
    /// the bar is known and on the layout, and the follower takes it as its own (<see cref="TakesLeadDepartureBar"/>).
    /// </summary>
    private bool CanTaxiOnToLeadBar(PhaseContext ctx, [NotNullWhen(true)] out AirportGroundLayout? layout, [NotNullWhen(true)] out GroundNode? bar)
    {
        layout = ctx.GroundLayout;
        bar = null;
        if (IsDrivingAnotherRoute(ctx) || (layout is null) || (_leadDepartureBar is not { } latch))
        {
            return false;
        }

        return layout.Nodes.TryGetValue(latch.NodeId, out bar) && TakesLeadDepartureBar(ctx, latch.NodeId);
    }

    /// <summary>Whether the follower is clearing a runway, braking along a lost route, or has an assigned route of its own left.</summary>
    private bool IsDrivingAnotherRoute(PhaseContext ctx) =>
        _brakingLostRoute || (_clearingRoute is not null) || (ctx.Aircraft.Ground.AssignedTaxiRoute is { IsComplete: false });

    /// <summary>
    /// Keeps a follow route whose rest still reaches <paramref name="bar"/>; otherwise installs, as the follow route driven from
    /// its first segment by a fresh navigator, the way onto the lead's path to the bar (<see cref="AlongLeadPathTo"/>), else the
    /// auto route from where the follower stands (<see cref="AutoRouteTo"/>). False with neither.
    /// </summary>
    private bool EnsureRouteToBar(PhaseContext ctx, AirportGroundLayout layout, GroundNode bar, AircraftState? lead)
    {
        if ((_followRoute is { IsComplete: false } route) && IsOnRestOfRoute(route, bar.Id))
        {
            return true;
        }

        if ((AlongLeadPathTo(ctx, layout, bar, lead) ?? AutoRouteTo(ctx, layout, bar)) is not { } toBar)
        {
            return false;
        }

        Log.LogDebug(
            "[Follow] {Callsign}: {Target} departed; taxiing on to its departure bar at node {Node}: {Route}",
            ctx.Aircraft.Callsign,
            _targetCallsign,
            bar.Id,
            toBar.ToSummary()
        );
        _nav = new GroundNavigator();
        SetFollowRoute(toBar, toBar.Segments.Count, null, givingWay: false);
        return true;
    }

    /// <summary>
    /// The lead's path to <paramref name="bar"/> from where the follower stands: the lead's driven route (<see cref="DrivenRouteOf"/>)
    /// up to the bar, joined at its nearest node the follower reaches without turning about (<see cref="ReanchoredRoute"/>). Null
    /// with no lead, no route of the lead's that reaches the bar, or no way onto it.
    /// </summary>
    private TaxiRoute? AlongLeadPathTo(PhaseContext ctx, AirportGroundLayout layout, GroundNode bar, AircraftState? lead)
    {
        if ((lead is null) || (DrivenRouteOf(lead) is not { } leadRoute))
        {
            return null;
        }

        List<TaxiRouteSegment> segments = [.. leadRoute.Segments];
        int barIndex = segments.FindIndex(s => s.ToNodeId == bar.Id);
        if (barIndex < 0)
        {
            return null;
        }

        var leadPath = new TaxiRoute { Segments = segments[..(barIndex + 1)], HoldShortPoints = [] };
        return FollowRoutePlanner.RouteOnto(layout, ctx.Aircraft, ReanchorGoals(leadPath), GroundTraffic(ctx)) is { } onto
            ? ReanchoredRoute(leadPath, onto)
            : null;
    }

    /// <summary>
    /// The auto route from where the follower stands to <paramref name="bar"/> (<see cref="FollowRoutePlanner.RouteOnto"/>), for a
    /// follower with no lead's path to take, under the runway guards a resumed assigned route has (<see cref="ResumeAssignedRoute"/>):
    /// never for an unjoinable follow, nor from inside a runway's hold line, nor along a way that crosses a runway hold line or
    /// enters a runway short of the bar (<see cref="RefuseRunwayEntry"/>). Null otherwise.
    /// </summary>
    private TaxiRoute? AutoRouteTo(PhaseContext ctx, AirportGroundLayout layout, GroundNode bar)
    {
        if (
            _unjoinable
            || (RunwayInsideHoldLine(ctx.Aircraft, layout) is not null)
            || (
                FollowRoutePlanner.RouteOnto(layout, ctx.Aircraft, new HashSet<int> { bar.Id }, GroundTraffic(ctx))
                is not { Segments.Count: > 0 } onto
            )
        )
        {
            LogHoldOnce(ctx, $"{_targetCallsign} departed; no taxi route on to its departure bar at node {bar.Id}", LogLevel.Information);
            return null;
        }

        if (RefuseRunwayEntry(ctx, layout, onto, $"{_targetCallsign} departed; the auto route on to its departure bar"))
        {
            return null;
        }

        var toBar = new TaxiRoute { Segments = onto.Segments, HoldShortPoints = [] };
        Log.LogInformation(
            "[Follow] {Callsign}: {Target} departed and its path is gone; taking the taxi route to its departure bar at node {Node}: {Route}",
            ctx.Aircraft.Callsign,
            _targetCallsign,
            bar.Id,
            toBar.ToSummary()
        );
        return toBar;
    }

    /// <summary>
    /// The lead lost with the follower on a taxiway — inside no runway's hold line, and with no runway cleared since its last
    /// follow route — and with an assigned route not yet driven to its end: a <see cref="TaxiingPhase"/> takes over on the rest
    /// of that route, re-anchored where the follower stands (<see cref="ReanchoredRoute"/>), so the route's own hold-shorts and
    /// crossings stop it. An unjoinable follow never resumes on its own. Nor does one whose way back onto the route crosses a
    /// runway hold line or enters a runway, which that route holds no stop for (<see cref="RunwayEntries"/>): it becomes
    /// unjoinable and the pilot says once that it has lost the traffic and wants taxi instructions
    /// (<see cref="SayLostTrafficHolding"/>). True when handed over; false with no route left, or none that reaches it, when the
    /// follower holds.
    /// </summary>
    private bool ResumeAssignedRoute(PhaseContext ctx, AircraftState? lead)
    {
        if (
            _unjoinable
            || (ctx.GroundLayout is not { } layout)
            || (ctx.Aircraft.Ground.AssignedTaxiRoute is not { IsComplete: false } assigned)
            || (_clearingAttemptedRunways.Count > 0)
            || (RunwayInsideHoldLine(ctx.Aircraft, layout) is not null)
            || (FollowRoutePlanner.RouteOnto(layout, ctx.Aircraft, ReanchorGoals(assigned), GroundTraffic(ctx)) is not { } onto)
        )
        {
            return false;
        }

        if (RefuseLeadInAcrossRunway(ctx, layout, onto) || (ReanchoredRoute(assigned, onto) is not { } resumed))
        {
            return false;
        }

        Log.LogDebug(
            "[Follow] {Callsign}: target {Target} {Reason}; resuming its assigned route: {Route}",
            ctx.Aircraft.Callsign,
            _targetCallsign,
            LeadGoneReason(lead),
            resumed.ToSummary()
        );
        ctx.Aircraft.Ground.AssignedTaxiRoute = resumed;
        ctx.Aircraft.Phases?.InsertAfterCurrent(new TaxiingPhase());
        return true;
    }

    /// <summary>The nodes of <paramref name="assigned"/>'s remaining segments: where a follower's way back onto it may join it.</summary>
    private static HashSet<int> ReanchorGoals(TaxiRoute assigned)
    {
        List<TaxiRouteSegment> remaining = [.. assigned.Segments.Skip(assigned.CurrentSegmentIndex)];
        return [.. remaining.Select(s => s.FromNodeId), remaining[^1].ToNodeId];
    }

    /// <summary>
    /// Refuses a way back onto the assigned route that crosses a runway hold line or enters a runway, since the assigned route
    /// holds no stop for it: the wrapper around <see cref="RefuseRunwayEntry"/> that logs the refusal in the assigned-route
    /// wording. True when refused.
    /// </summary>
    internal bool RefuseLeadInAcrossRunway(PhaseContext ctx, AirportGroundLayout layout, FollowRoutePlanner.RouteFromHere onto) =>
        RefuseRunwayEntry(ctx, layout, onto, $"{_targetCallsign} lost; the way back onto its assigned route");

    /// <summary>
    /// Refuses <paramref name="onto"/> when it crosses a runway hold line or enters a runway (<see cref="RunwayEntries"/>). The
    /// first refusal makes the follow unjoinable, logs it as <paramref name="way"/> entering a runway, and has the pilot say once
    /// that it lost the traffic and asks for taxi instructions (<see cref="SayLostTrafficHolding"/>): the follow has a route it
    /// cannot reach, not a plan that joins nothing (<see cref="SayUnableNoRouteToFollow"/>). True when refused.
    /// </summary>
    private bool RefuseRunwayEntry(PhaseContext ctx, AirportGroundLayout layout, FollowRoutePlanner.RouteFromHere onto, string way)
    {
        if (RunwayEntries(layout, onto, StartPose.Of(ctx.Aircraft)) is not { Count: > 0 } entries)
        {
            return false;
        }

        if (!_unjoinable)
        {
            _unjoinable = true;
            LogHoldOnce(ctx, $"{way} enters a runway at node {entries[0]}", LogLevel.Information);
            SayLostTrafficHolding(ctx);
        }

        return true;
    }

    /// <summary>
    /// Where <paramref name="onto"/> enters a runway, less its goal node (arriving at the assigned route is no crossing, and the
    /// assigned route holds its own stop there): the hold-short bars a taxi route's implicit runway hold-shorts mark
    /// (<see cref="HoldShortAnnotator.AddImplicitRunwayHoldShorts"/>), then the nodes it reaches along a runway centreline edge or
    /// on a runway's pavement (<see cref="RunwayNodesEntered"/>), so a layout with no bar, or a way back starting on one, is
    /// caught too. Bars first.
    /// </summary>
    internal static List<int> RunwayEntries(AirportGroundLayout layout, FollowRoutePlanner.RouteFromHere onto, StartPose? startPose)
    {
        List<HoldShortPoint> bars = [];
        HoldShortAnnotator.AddImplicitRunwayHoldShorts(layout, onto.Segments, bars, startPose);
        List<int> entries = [.. bars.Where(b => b.NodeId != onto.GoalNodeId).Select(b => b.NodeId)];
        if ((StartBarCrossed(layout, onto.Segments) is { } startBar) && !entries.Contains(startBar))
        {
            entries.Insert(0, startBar);
        }

        entries.AddRange(RunwayNodesEntered(layout, onto).Where(id => !entries.Contains(id)));
        return entries;
    }

    /// <summary>
    /// The nodes of <paramref name="onto"/>, less its goal, that it reaches along a runway centreline edge or that lie on a
    /// runway's pavement.
    /// </summary>
    private static IEnumerable<int> RunwayNodesEntered(AirportGroundLayout layout, FollowRoutePlanner.RouteFromHere onto)
    {
        List<RunwayInfo> runways = [.. RunwayOccupancy.AirportRunways(layout.AirportId)];
        return onto
            .Segments.Where(s =>
                (s.ToNodeId != onto.GoalNodeId)
                && (s.Edge.Edge.IsRunwayCenterline || runways.Any(r => RunwayOccupancy.IsWithinPavement(s.Edge.ToNode.Position, r)))
            )
            .Select(s => s.ToNodeId);
    }

    /// <summary>
    /// The runway bar <paramref name="segments"/> start at when they go on to another bar of the same runway: the annotator reads
    /// such a start as an aircraft already mid-crossing, but a follower taking a way back stands short of every hold line, so
    /// from that bar it crosses the runway. Null otherwise.
    /// </summary>
    private static int? StartBarCrossed(AirportGroundLayout layout, List<TaxiRouteSegment> segments)
    {
        if ((segments.Count == 0) || (RunwayOfBar(layout, segments[0].FromNodeId) is not { } runway))
        {
            return null;
        }

        return segments.Any(s => (RunwayOfBar(layout, s.ToNodeId) is { } other) && other.Overlaps(runway)) ? segments[0].FromNodeId : null;
    }

    /// <summary>The runway node <paramref name="nodeId"/> is a hold-short bar of; null for any other node.</summary>
    private static RunwayIdentifier? RunwayOfBar(AirportGroundLayout layout, int nodeId) =>
        (layout.Nodes.TryGetValue(nodeId, out GroundNode? node) && (node.Type == GroundNodeType.RunwayHoldShort)) ? node.RunwayId : null;

    /// <summary>
    /// The rest of <paramref name="assigned"/> from where the follower stands. <c>FOLLOWG</c> leaves the assigned route as it
    /// was, so its current segment is where the taxi was given, not where the follower is: the route from where it stands onto
    /// the nearest node of the route's remaining segments (<paramref name="onto"/>, from
    /// <see cref="FollowRoutePlanner.RouteOnto"/>, never turning it about), then the assigned route on from that node, with the
    /// assigned route's hold-short points. Null with nothing to drive.
    /// </summary>
    private static TaxiRoute? ReanchoredRoute(TaxiRoute assigned, FollowRoutePlanner.RouteFromHere onto)
    {
        List<TaxiRouteSegment> remaining = [.. assigned.Segments.Skip(assigned.CurrentSegmentIndex)];
        int rejoinAt = remaining.FindIndex(s => s.FromNodeId == onto.GoalNodeId);
        List<TaxiRouteSegment> onward = rejoinAt < 0 ? [] : remaining[rejoinAt..];
        if ((onto.Segments.Count + onward.Count) == 0)
        {
            return null;
        }

        int? pullFrom = assigned.SpotLineUpPullFromSegment - assigned.CurrentSegmentIndex - rejoinAt + onto.Segments.Count;
        return new TaxiRoute
        {
            Segments = [.. onto.Segments, .. onward],
            HoldShortPoints = assigned.HoldShortPoints,
            Warnings = assigned.Warnings,
            ImpliedLanes = assigned.ImpliedLanes,
            ClearanceTaxiways = assigned.ClearanceTaxiways,
            DestinationParking = assigned.DestinationParking,
            DestinationSpot = assigned.DestinationSpot,
            SpotLineUpPullFromSegment = ((rejoinAt >= 0) && (pullFrom >= onto.Segments.Count)) ? pullFrom : null,
        };
    }

    /// <summary>
    /// Whether the follower is within one sub-tick of taxi braking of rest, so clearing the residual is no more than physics' own
    /// ground snap would.
    /// </summary>
    internal static bool IsAtRest(PhaseContext ctx) =>
        ctx.Aircraft.IndicatedAirspeed <= (CategoryPerformance.TaxiDecelRate(ctx.Category) / SimulationEngine.PhysicsSubTickRate);

    /// <summary>
    /// Folds <paramref name="cap"/> into the published speed and brake rate: the speed is the lowest cap, and the brake rate the
    /// highest any cap below the current ground speed needs (a null rate is the category taxi rate), so a lower cap braked at
    /// the taxi rate never hides a higher one — a runway bar's — that needs the max-effort rate to be met. A null target is no
    /// stop: physics clears the target once the aircraft reaches it, and the navigator publishes none on the sub-tick it arrives
    /// at a node, so a null target reads as the speed the aircraft holds, which is what physics does with it. The target the cap
    /// lowers — an explicit <see cref="ControlTargets.TargetSpeed"/>, or the held speed a null one reads as — is first capped by
    /// the ground conflict detector's <see cref="AircraftGroundOps.SpeedLimit"/>, so a cap never publishes a speed above the
    /// detector's limit.
    /// </summary>
    private static void ApplyCap(PhaseContext ctx, SpeedCap? cap)
    {
        if (cap is not { } applied)
        {
            return;
        }

        double publishedKts = ctx.Targets.TargetSpeed ?? ctx.Aircraft.IndicatedAirspeed;
        if (ctx.Aircraft.Ground.SpeedLimit is { } limitKts)
        {
            publishedKts = Math.Min(publishedKts, limitKts);
        }

        ctx.Targets.TargetSpeed = Math.Min(publishedKts, applied.Kts);
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
        SayHoldingShortNotClear(ctx, clearing);
        EndClearingRoute();
        return false;
    }

    /// <summary>
    /// The pilot says once, on the transition into the hold a clearing route ran out short of (<see cref="DriveClearingRoute"/>),
    /// that it is holding short of the runway its way on enters and not clear of the one behind, or, with no such runway, that it
    /// is holding in position and not clear of the one behind: a transmission to a solo student on ground or tower, else the
    /// RPO's terminal (<see cref="PilotResponder.BuildHoldingShortNotClear"/>,
    /// <see cref="PilotResponder.BuildHoldingPositionNotClear"/>). Each runway is named by the end nearest the follower, as
    /// the hold-short texts name one. Nothing when the cleared runway cannot be read, or once said — and a follow that has said
    /// this says no lost-traffic call for that hold (<see cref="SayLostTrafficHolding"/>): one line per hold, the runway one,
    /// which is the one safety turns on.
    /// </summary>
    private void SayHoldingShortNotClear(PhaseContext ctx, TaxiRoute clearing)
    {
        if (_saidHeldShortNotClear || (ctx.GroundLayout is not { } layout) || (BarRunwayAt(layout, _clearingBarNodeId) is not { } cleared))
        {
            return;
        }

        _saidHeldShortNotClear = true;
        string clearedEnd = RunwayCrossingEnd.Nearest(ctx.Aircraft, cleared.ToString(), layout);
        PilotResponder.RouteSoloOrRpoTransmission(
            ctx.Aircraft,
            ctx.SoloTrainingMode,
            ctx.RpoShowPilotSpeech,
            ctx.StudentPositionType,
            RunwayAheadAtTheHold(layout, cleared, clearing.Segments[^1]) is { } next
                ? PilotResponder.BuildHoldingShortNotClear(ctx.Aircraft, RunwayCrossingEnd.Nearest(ctx.Aircraft, next.ToString(), layout), clearedEnd)
                : PilotResponder.BuildHoldingPositionNotClear(ctx.Aircraft, clearedEnd),
            PilotResponder.SoloPositionsGround
        );
    }

    /// <summary>The runway identifier of the hold-short bar at node <paramref name="barNodeId"/>; null when it is no bar.</summary>
    private static RunwayIdentifier? BarRunwayAt(AirportGroundLayout layout, int? barNodeId) =>
        (barNodeId is { } nodeId) ? RunwayOfBar(layout, nodeId) : null;

    /// <summary>
    /// The runway the follower's way on from the end of <paramref name="last"/> enters, which <paramref name="clearedId"/>'s
    /// clearing route ran out short of: the runway of the straightest edge out of the route's last node that is not back along
    /// the way in, leads no nearer <paramref name="clearedId"/>'s centreline, and <see cref="EntersAnotherRunway"/> refuses — the
    /// candidates <see cref="NextEdgeAway"/> passes over, kept instead. Null when the route ends with no such edge, or the
    /// cleared runway is unknown.
    /// </summary>
    internal static RunwayIdentifier? RunwayAheadAtTheHold(AirportGroundLayout layout, RunwayIdentifier clearedId, TaxiRouteSegment last)
    {
        if ((RunwayNamed(layout, clearedId) is not { } cleared) || !layout.Nodes.TryGetValue(last.ToNodeId, out GroundNode? at))
        {
            return null;
        }

        double atFt = CentrelineFt(cleared, at.Position);
        double arrivalDeg = GeoMath.BearingTo(last.Edge.FromNode.Position, last.Edge.ToNode.Position);
        IGroundEdge? refused = at
            .Edges.Where(e =>
                !e.IsRunwayCenterline
                && (e.OtherNode(at).Id != last.FromNodeId)
                && (CentrelineFt(cleared, e.OtherNode(at).Position) >= (atFt - AwayToleranceFt))
                && EntersAnotherRunway(cleared, e.Directed(at, e.OtherNode(at)))
            )
            .MinBy(e => Math.Abs(GeoMath.SignedBearingDifference(arrivalDeg, GeoMath.BearingTo(at.Position, e.OtherNode(at).Position))));
        return refused?.OtherNode(at).RunwayId;
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
        // A new route is a way to move again, so the held-short latch ends with its hold: only that hold's call is silenced
        // (<see cref="SayLostTrafficHolding"/>).
        _saidHeldShortNotClear = false;
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

    /// <summary>
    /// A follower braking to rest along its clearing route — still rolling, its tail and wingtips past the bar's hold line, which
    /// is read from where it stands (<see cref="IsPastClearingBar"/>), so a restored follow does the same — plans the follow
    /// again each tick, unless it is unjoinable. A <see cref="FollowRoutePlan.Joinable"/> plan with a route to drive ends the
    /// clearing and is installed, so the follower takes up following in the same tick without stopping first; any other plan
    /// leaves it braking, to plan again at rest as before. True when the plan was installed.
    /// </summary>
    private bool ResumeFollowFromBrakingRoll(PhaseContext ctx, AircraftState lead)
    {
        if (
            _unjoinable
            || IsAtRest(ctx)
            || (ctx.GroundLayout is not { } layout)
            || !IsPastClearingBar(ctx)
            || (RunwayInsideHoldLine(ctx.Aircraft, layout) is not null)
        )
        {
            return false;
        }

        PlanCount++;
        if (
            (FollowRoutePlanner.Plan(layout, ctx.Aircraft, lead, GroundTraffic(ctx)) is not FollowRoutePlan.Joinable joinable)
            || HoldsWhereItStands(joinable)
        )
        {
            return false;
        }

        Log.LogDebug(
            "[Follow] {Callsign}: {Target} can be followed again while braking past runway bar node {NodeId}; following it",
            ctx.Aircraft.Callsign,
            _targetCallsign,
            _clearingBarNodeId
        );
        EndClearingRoute();
        InstallPlan(ctx, layout, joinable, lead, 0);
        return true;
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
    /// The runway the aircraft is inside the hold line of: its nose, centre or tail on the runway's pavement, or past the marking
    /// of the nearest of the runway's hold-short bars on that point's side, judged with the aircraft's position as its main gear
    /// (<see cref="RunwayHoldBars.IsInsideBarLine"/>). Null when clear of every runway.
    /// </summary>
    public static RunwayInfo? RunwayInsideHoldLine(AircraftState aircraft, AirportGroundLayout layout)
    {
        LatLon[] points = [NoseOf(aircraft), aircraft.Position, TailOf(aircraft)];
        return RunwayOccupancy
            .AirportRunways(layout.AirportId)
            .FirstOrDefault(runway => points.Any(p => IsInsideHoldLine(layout, runway, p, aircraft.Position)));
    }

    /// <summary>
    /// Whether <paramref name="point"/>, a part of an aircraft whose main gear is at <paramref name="gearPosition"/>, is on
    /// <paramref name="runway"/>'s pavement or past the marking of the nearest of the runway's hold-short bars on the point's side.
    /// </summary>
    public static bool IsInsideHoldLine(AirportGroundLayout layout, RunwayInfo runway, LatLon point, LatLon gearPosition) =>
        RunwayOccupancy.IsWithinPavement(point, runway) || IsInsideBarLine(layout, runway, point, gearPosition);

    /// <summary>
    /// Whether a bare <paramref name="point"/>, with no aircraft pose, is inside <paramref name="runway"/>'s hold line
    /// (<see cref="IsInsideHoldLine(AirportGroundLayout, RunwayInfo, LatLon, LatLon)"/>), judged as an aircraft standing there:
    /// the point is its own main-gear position.
    /// </summary>
    public static bool IsInsideHoldLine(AirportGroundLayout layout, RunwayInfo runway, LatLon point) =>
        IsInsideHoldLine(layout, runway, point, point);

    /// <summary>A clearing route and the hold-short bar node it clears.</summary>
    private readonly record struct Clearing(TaxiRoute Route, int BarNodeId);

    /// <summary>
    /// The clearing route off <paramref name="runway"/>: from where the follower stands (the fillet arc or edge it is on into its
    /// start node, picked from its own trail and led in as a follow route's is by <see cref="FollowRoutePlanner.StartOf"/>), the
    /// auto route to the nearest by taxi distance of the runway's
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

        if (FollowRoutePlanner.StartOf(layout, ctx.Aircraft, requireAhead: false, GroundTraffic(ctx)) is { } start)
        {
            var taxiClass = FollowRoutePlanner.TaxiClass.Of(ctx.Aircraft);
            TaxiRouteSegment? leadIn = start.LeadIn;
            while ((goals.Count > 0) && (taxiClass.FindRoute(layout, start.Node.Id, NearestGoalRequest.To(goals)) is { } toBar))
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
    /// Every aircraft on the ground the phase's world holds, as the follow route's start pick reads them
    /// (<see cref="AirportGroundLayout.FindTaxiStartNode"/>), listed only when the planner asks; empty when the context has no
    /// world.
    /// </summary>
    private static Func<IReadOnlyList<NeighbourCandidate>> GroundTraffic(PhaseContext ctx) =>
        () => NeighbourCandidate.OnGround(ctx.ListAircraft?.Invoke());

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
        double needFt = ClearingNeedFt(aircraft.AircraftType);
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
    /// How far (ft) past the bar a clearing route for <paramref name="aircraftType"/> runs: the aircraft's length, then the
    /// longer of <see cref="ClearingBeyondBarFt"/> and its stopping distance from taxi speed at the taxi brake rate
    /// (<see cref="GroundStopBraking.StoppingDistanceFt"/>), so a follower clearing at taxi speed has its tail past the hold line
    /// and room on the route to brake to rest.
    /// </summary>
    private static double ClearingNeedFt(string aircraftType)
    {
        AircraftCategory category = AircraftCategorization.Categorize(aircraftType);
        double stopFt = GroundStopBraking.StoppingDistanceFt(CategoryPerformance.TaxiSpeed(category), CategoryPerformance.TaxiDecelRate(category));
        return AircraftLength.ResolveFt(aircraftType) + Math.Max(ClearingBeyondBarFt, stopFt);
    }

    /// <summary>
    /// The route on past <paramref name="bar"/> until it reaches <paramref name="needFt"/> beyond it: at each node the straightest
    /// continuation of the way in that leads no nearer the runway, never onto another runway, on through any junction short of
    /// that length. It stops where the pavement dead-ends.
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
    /// Whether the follower is past the clearing bar's hold line (<see cref="IsClearPastBar"/>). False when the bar or its runway
    /// can no longer be found: a hold line it cannot read is one it is not known to be past, so it drives on along its clearing
    /// route rather than brake or follow.
    /// </summary>
    private bool IsPastClearingBar(PhaseContext ctx)
    {
        NoteTailOnlyOnce(ctx.Aircraft);
        return IsPastClearingBar(ctx.GroundLayout, _clearingBarNodeId, ctx.Aircraft);
    }

    /// <summary>
    /// Whether <paramref name="aircraft"/> is past the hold line of the runway bar <paramref name="barNodeId"/> of
    /// <paramref name="layout"/> (<see cref="IsClearPastBar"/>); false when the bar or its runway cannot be resolved.
    /// </summary>
    public static bool IsPastClearingBar(AirportGroundLayout? layout, int? barNodeId, AircraftState aircraft)
    {
        if (
            (layout is null)
            || (barNodeId is not { } nodeId)
            || !layout.Nodes.TryGetValue(nodeId, out GroundNode? bar)
            || (bar.RunwayId is not { } barRunway)
            || (RunwayNamed(layout, barRunway) is not { } runway)
        )
        {
            return false;
        }

        return IsClearPastBar(aircraft, runway, bar);
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

    /// <summary>
    /// How far (ft) the part of <paramref name="aircraft"/> last to clear the hold line of the runway bar <paramref name="barNodeId"/>
    /// of <paramref name="layout"/> is short of that line, with the parts and distances <see cref="IsClearPastBar"/> judges by: the
    /// bar's distance from the runway's centreline less the part's, a part across the centreline from the bar counting its
    /// distance as negative. Negative when the tail and both wingtips are all past the line (<see cref="IsClearPastBar"/> holds);
    /// null when the bar or its runway cannot be resolved.
    /// </summary>
    public static double? ShortOfBarFt(AirportGroundLayout? layout, int barNodeId, AircraftState aircraft)
    {
        if (
            (layout is null)
            || !layout.Nodes.TryGetValue(barNodeId, out GroundNode? bar)
            || (bar.RunwayId is not { } barRunway)
            || (RunwayNamed(layout, barRunway) is not { } runway)
        )
        {
            return null;
        }

        bool barRight = IsRightOfCentreline(runway, bar.Position);
        double barFt = CentrelineFt(runway, bar.Position);
        Span<LatLon> parts = stackalloc LatLon[MaxTrailingParts];
        int count = FillTrailingParts(aircraft, parts);
        double shortFt = double.NegativeInfinity;
        for (int i = 0; i < count; i++)
        {
            shortFt = Math.Max(shortFt, barFt - BarSideFt(runway, barRight, parts[i]));
        }

        return shortFt;
    }

    /// <summary>
    /// How far (ft) <paramref name="point"/> lies from <paramref name="runway"/>'s centreline on the side
    /// <paramref name="barRight"/> names, negative on the other side.
    /// </summary>
    private static double BarSideFt(RunwayInfo runway, bool barRight, LatLon point) =>
        (IsRightOfCentreline(runway, point) == barRight) ? CentrelineFt(runway, point) : -CentrelineFt(runway, point);

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
    /// Plans the follow route when there is none and ticks the navigator over it. A route that runs out this tick is planned
    /// again and driven in the same tick, so a follower whose new plan joins the lead's path never stands a tick with no route
    /// publishing a stop. False while the follower holds in position: no plan, or the route ran out with none to replace it.
    /// </summary>
    private bool DriveFollowRoute(PhaseContext ctx, AircraftState lead)
    {
        if (!EnsurePlan(ctx, lead) || !FollowLeadReroute(ctx, lead))
        {
            return false;
        }

        return TickFollowNavigator(ctx, lead) || (EnsurePlan(ctx, lead) && TickFollowNavigator(ctx, lead));
    }

    /// <summary>
    /// Plans again the tick the lead's route no longer matches the lead path the follow route was planned from
    /// (<see cref="FollowRoutePlanner.LeadRouteMatchesPath"/>): a new <c>TAXI</c> to the lead, or a lead that is itself a follower
    /// planning a new follow route. A new plan that drives differently from the follower's segment on is adopted, spliced in at
    /// that segment when it runs through it, and replaces any give-way the old plan latched with its own. One that joins nothing
    /// — no taxi path to the lead's new path, or the follower now ahead of the lead on it — loses the route
    /// (<see cref="LoseFollowRoute"/>): the follower stops and holds in position for good, logging it once. An empty plan
    /// (<see cref="HoldsWhereItStands"/>) loses it too, but the follower plans again each tick once stopped. False when the route was lost.
    /// </summary>
    private bool FollowLeadReroute(PhaseContext ctx, AircraftState lead)
    {
        if (
            (ctx.GroundLayout is not { } layout)
            || (_plan is not { } plan)
            || (_followRoute is not { IsComplete: false })
            || FollowRoutePlanner.LeadRouteMatchesPath(layout, ctx.Aircraft, lead, plan.MergeNode, plan.LeadPathFromMerge)
        )
        {
            return true;
        }

        PlanCount++;
        FollowRoutePlan replanned = FollowRoutePlanner.Replan(layout, ctx.Aircraft, lead, GroundTraffic(ctx));
        if (replanned is FollowRoutePlan.WaitForLead)
        {
            return true;
        }

        if (replanned is not FollowRoutePlan.Joinable joinable)
        {
            LoseFollowRoute(ctx, layout);
            HoldOnUnjoinedPlan(ctx, replanned, isReplan: true);
            return false;
        }

        if (HoldsWhereItStands(joinable))
        {
            LoseFollowRoute(ctx, layout);
            HoldOnEmptyPlan(ctx, joinable);
            return false;
        }

        return AdoptIfDifferent(ctx, lead, joinable, "was re-routed", keepOnBackwardStart: false);
    }

    /// <summary>
    /// The follow route is lost to a lead re-route. A follower with some part on a runway or inside its hold line drops it, so
    /// its clearing route takes over this tick (<see cref="DriveClearingRoute"/>); any other keeps it and brakes to rest along it
    /// (<see cref="BrakeAlongLostRoute"/>), dropping it there.
    /// </summary>
    private void LoseFollowRoute(PhaseContext ctx, AirportGroundLayout layout)
    {
        if (RunwayToClear(ctx.Aircraft, layout) is not null)
        {
            DropFollowRoute();
            return;
        }

        _brakingLostRoute = true;
    }

    /// <summary>
    /// Whether <paramref name="joinable"/> leaves the follower nothing to drive yet: no route to the merge, the merge being where
    /// the follower stands, and either no lead path from it (the lead's path ends there) or the merge ahead of the lead, so
    /// driving on would put the follower ahead of it. Never installed: the follower holds and plans again
    /// (<see cref="HoldOnEmptyPlan"/>).
    /// </summary>
    private static bool HoldsWhereItStands(FollowRoutePlan.Joinable joinable) =>
        (joinable.PathToMerge.Segments.Count == 0) && ((joinable.LeadPathFromMerge.Count == 0) || joinable.MergeAheadOfLead);

    /// <summary>
    /// Holds for a plan with nothing to drive yet (<see cref="HoldsWhereItStands"/>), logged once; the follow plans again each tick.
    /// A follower standing on the lead's route ahead of it also says it cannot follow and asks for taxi instructions
    /// (<see cref="SayUnableToFollowOnce"/>) the first time it holds so.
    /// </summary>
    private void HoldOnEmptyPlan(PhaseContext ctx, FollowRoutePlan.Joinable joinable)
    {
        if (joinable.LeadPathFromMerge.Count == 0)
        {
            LogHoldOnce(ctx, $"{_targetCallsign}'s path ends where the follower joins it", LogLevel.Information);
            return;
        }

        LogHoldOnce(ctx, $"standing on {_targetCallsign}'s route ahead of it; waiting for it to pass", LogLevel.Information);
        SayUnableToFollowOnce(ctx);
    }

    /// <summary>
    /// Adopts <paramref name="joinable"/> when it drives differently from the follow route's current segment on: its route from
    /// that segment, or its merge, differs (<see cref="DrivesAlike"/>). A new route that runs through that segment is spliced in
    /// from it (<see cref="InstallPlan"/>); one that does not is installed from its start, when that start lies ahead of the
    /// follower (<see cref="FollowRoutePlanner.LiesAhead"/>) and its first edge leaves within 90° of the heading
    /// (<see cref="FollowRoutePlanner.DepartsAhead"/>). One that starts behind it, or leaves back across its heading, would turn
    /// it about or swerve it across a junction. A re-plan at a node arrival (<paramref name="keepOnBackwardStart"/>) keeps the
    /// route the follower is driving, which still follows the lead; after a lead re-route the route is lost instead
    /// (<see cref="LoseFollowRoute"/>), the follower held for good. Nothing with no follow route left to drive, or no layout.
    /// False when the route was lost.
    /// </summary>
    private bool AdoptIfDifferent(PhaseContext ctx, AircraftState lead, FollowRoutePlan.Joinable joinable, string why, bool keepOnBackwardStart)
    {
        if ((_followRoute is not { IsComplete: false } route) || (ctx.GroundLayout is not { } layout))
        {
            return true;
        }

        List<TaxiRouteSegment> segments = FollowSegments(joinable);
        TaxiRouteSegment current = route.Segments[route.CurrentSegmentIndex];
        int at = segments.FindIndex(s => (s.FromNodeId == current.FromNodeId) && (s.ToNodeId == current.ToNodeId));
        if ((at >= 0) && DrivesAlike(route, segments, at, joinable.PathToMerge.Segments.Count))
        {
            return true;
        }

        if ((at < 0) && !StartsAlongHeading(ctx.Aircraft, segments[0]))
        {
            return OnBackwardStart(ctx, layout, segments, why, keepOnBackwardStart);
        }

        Log.LogDebug(
            "[Follow] {Callsign}: {Target} {Why}; following the new plan {How}",
            ctx.Aircraft.Callsign,
            _targetCallsign,
            why,
            (at >= 0) ? $"from segment {at}" : "from its start"
        );
        InstallPlan(ctx, layout, joinable, lead, Math.Max(at, 0));
        return true;
    }

    /// <summary>
    /// A plan whose route starts behind the follower, or leaves back across its heading: at a node arrival
    /// (<paramref name="keepOnBackwardStart"/>) the follower keeps the route it is driving, which still follows the lead; after
    /// a lead re-route it loses the route instead (<see cref="LoseFollowRoute"/>), held for good. True when the route is kept.
    /// </summary>
    private bool OnBackwardStart(PhaseContext ctx, AirportGroundLayout layout, List<TaxiRouteSegment> segments, string why, bool keepOnBackwardStart)
    {
        if (keepOnBackwardStart)
        {
            Log.LogDebug(
                "[Follow] {Callsign}: {Target} {Why}, but the new plan leaves node {Node} back across the heading; keeping the route",
                ctx.Aircraft.Callsign,
                _targetCallsign,
                why,
                segments[0].FromNodeId
            );
            return true;
        }

        LoseFollowRoute(ctx, layout);
        _unjoinable = true;
        LogHoldOnce(
            ctx,
            $"route lost: {_targetCallsign}'s new path starts behind the follower, or back across its heading, at node {segments[0].FromNodeId}",
            LogLevel.Information
        );
        return false;
    }

    /// <summary>
    /// Whether a route starting on <paramref name="first"/> takes the follower on along its heading: its start node lies ahead
    /// (<see cref="FollowRoutePlanner.LiesAhead"/>) and its first edge leaves within 90° of the heading
    /// (<see cref="FollowRoutePlanner.DepartsAhead"/>).
    /// </summary>
    private static bool StartsAlongHeading(AircraftState aircraft, TaxiRouteSegment first) =>
        FollowRoutePlanner.LiesAhead(aircraft, first.Edge.FromNode) && FollowRoutePlanner.DepartsAhead(aircraft, first.Edge);

    /// <summary>
    /// Ticks the navigator over the follow route, keeping the plan view's route to the merge at the route's segment; false with
    /// no route, or when the route ran out, which drops it for a new plan. With a <paramref name="lead"/>, each node arrival plans
    /// again (<see cref="ReplanAtNode"/>) before the next segment is set up, and the route's end is no stop while the lead is
    /// moving (<see cref="IsMoving"/>): the follower drives on to it and plans again there rather than braking toward it. Behind
    /// a lead at rest the end is a stop (<see cref="RouteEndCap"/>). A held follower (no <paramref name="lead"/>) does not plan.
    /// </summary>
    private bool TickFollowNavigator(PhaseContext ctx, AircraftState? lead)
    {
        if (_followRoute is not { } route)
        {
            return false;
        }

        NavStep step = StepNavigator(ctx, route, isFollowRoute: true, routeEndIsStop: (lead is null) || RouteEndIsStop(lead));
        if (step != NavStep.RanOut)
        {
            if (step == NavStep.Arrived)
            {
                if ((lead is not null) && (ctx.GroundLayout is { } layout))
                {
                    ReplanAtNode(ctx, layout, lead);
                }

                TaxiRoute driven = _followRoute ?? route;
                SetUpNavSegment(ctx, driven, isFollowRoute: true);
            }

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
        else if (_followRoute is { IsComplete: true })
        {
            // A lost route that ran out while rolling (StepLostRoute): nothing left to steer along, so the hold drops it.
            ctx.Targets.TargetTrueHeading = null;
            DropFollowRoute();
        }
        else if ((_clearingRoute is null) && (_followRoute is not null))
        {
            TickFollowNavigator(ctx, null);
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

        PlanCount++;
        FollowRoutePlan plan = FollowRoutePlanner.Plan(layout, ctx.Aircraft, lead, GroundTraffic(ctx));
        if (plan is not FollowRoutePlan.Joinable joinable)
        {
            HoldOnUnjoinedPlan(ctx, plan, isReplan: false);
            return false;
        }

        if (HoldsWhereItStands(joinable))
        {
            HoldOnEmptyPlan(ctx, joinable);
            return false;
        }

        InstallPlan(ctx, layout, joinable, lead, 0);
        return true;
    }

    /// <summary>
    /// Plans again on a node arrival, before the next segment of the follow route is set up, and adopts the new plan when it
    /// drives differently from here on (<see cref="AdoptIfDifferent"/>): a lead whose path grew past the planned end, or changed.
    /// A plan other than <see cref="FollowRoutePlan.Joinable"/> keeps the route the follower is driving: a lead momentarily on no
    /// straight edge keeps the last plan, and a route the lead lost is caught by <see cref="FollowLeadReroute"/>. A new route
    /// that starts behind the follower, or leaves back across its heading, keeps the route it is driving at a node arrival too —
    /// only a lead re-route loses it (<see cref="AdoptIfDifferent"/>).
    /// </summary>
    private void ReplanAtNode(PhaseContext ctx, AirportGroundLayout layout, AircraftState lead)
    {
        if (_followRoute is not { IsComplete: false })
        {
            return;
        }

        PlanCount++;
        if (
            (FollowRoutePlanner.Replan(layout, ctx.Aircraft, lead, GroundTraffic(ctx)) is FollowRoutePlan.Joinable joinable)
            && !HoldsWhereItStands(joinable)
        )
        {
            AdoptIfDifferent(ctx, lead, joinable, "has a new path", keepOnBackwardStart: true);
        }
    }

    /// <summary>
    /// Whether <paramref name="segments"/>, from <paramref name="at"/>, are the follow route's segments from its current one,
    /// edge for edge, with the merge (<paramref name="mergeIndex"/> in <paramref name="segments"/>) as far ahead.
    /// </summary>
    private bool DrivesAlike(TaxiRoute route, List<TaxiRouteSegment> segments, int at, int mergeIndex)
    {
        int from = route.CurrentSegmentIndex;
        if ((segments.Count - at) != (route.Segments.Count - from))
        {
            return false;
        }

        if (Math.Max(0, mergeIndex - at) != Math.Max(0, _mergeSegmentIndex - from))
        {
            return false;
        }

        for (int i = 0; i < segments.Count - at; i++)
        {
            TaxiRouteSegment planned = segments[at + i];
            TaxiRouteSegment driven = route.Segments[from + i];
            if ((planned.FromNodeId != driven.FromNodeId) || (planned.ToNodeId != driven.ToNodeId))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A follow route's segments for <paramref name="joinable"/>: its route to the merge, then the lead's path from it.</summary>
    private static List<TaxiRouteSegment> FollowSegments(FollowRoutePlan.Joinable joinable) =>
        [.. joinable.PathToMerge.Segments, .. joinable.LeadPathFromMerge.Select(ToSegment)];

    /// <summary>
    /// Holds for a plan that joins nothing, logged once: for good on a <see cref="FollowRoutePlan.FollowerAhead"/> or a
    /// <see cref="FollowRoutePlan.NoPath"/>, planning again each tick on a <see cref="FollowRoutePlan.WaitForLead"/>.
    /// <c>FOLLOWG</c> refuses a follow with no path to the lead or ahead of it, as it arms one, so either met at runtime — on the
    /// first plan of an armed follow's release, or a re-plan after the lead was re-routed — is a route lost since. The first
    /// <see cref="FollowRoutePlan.NoPath"/> that makes the follow unjoinable, and the first <see cref="FollowRoutePlan.FollowerAhead"/>
    /// of a re-plan (<paramref name="isReplan"/>, after the lead was re-routed), also has the pilot say it cannot follow and ask for
    /// taxi instructions (<see cref="SayUnableToFollowOnce"/>). A first plan's <see cref="FollowRoutePlan.FollowerAhead"/> holds
    /// silently here; a first plan that finds the follower standing on the lead's route ahead is a joinable plan with nothing to
    /// drive, held by <see cref="HoldOnEmptyPlan"/>, which does say it.
    /// </summary>
    private void HoldOnUnjoinedPlan(PhaseContext ctx, FollowRoutePlan plan, bool isReplan)
    {
        (string reason, LogLevel level) = plan switch
        {
            FollowRoutePlan.WaitForLead => ($"waiting for {_targetCallsign} to taxi onto a taxiway", LogLevel.Debug),
            FollowRoutePlan.FollowerAhead => ($"route lost: ahead of {_targetCallsign} on its route", LogLevel.Information),
            _ => ($"route lost: no taxi route to {_targetCallsign}'s path", LogLevel.Information),
        };
        bool saysUnable = !_unjoinable && ((plan is FollowRoutePlan.NoPath) || (isReplan && (plan is FollowRoutePlan.FollowerAhead)));
        _unjoinable = plan is not FollowRoutePlan.WaitForLead;
        LogHoldOnce(ctx, reason, level);
        if (saysUnable)
        {
            SayUnableToFollowOnce(ctx);
        }
    }

    /// <summary>
    /// The pilot says it cannot follow and asks for taxi instructions (<see cref="SayUnableNoRouteToFollow"/>), once per follow:
    /// the latch (<see cref="_saidUnableNoRoute"/>) is snapshotted, so a restore says nothing again, and a follower that has said
    /// it says no lost-traffic call either.
    /// </summary>
    private void SayUnableToFollowOnce(PhaseContext ctx)
    {
        if (_saidUnableNoRoute)
        {
            return;
        }

        _saidUnableNoRoute = true;
        SayUnableNoRouteToFollow(ctx);
    }

    /// <summary>
    /// The lead is lost with no legal way back onto an assigned route and nothing left to clear: the pilot says once that it has
    /// lost the traffic, is holding in position and wants taxi instructions — a transmission to a solo student on ground or
    /// tower, else the RPO's terminal (<see cref="PilotResponder.BuildLostTrafficHolding"/>). A follower with no assigned
    /// route left says nothing (it holds silently), one that has already said it cannot follow (<see cref="_saidUnableNoRoute"/>)
    /// or has said the runway call for this hold (<see cref="_saidHeldShortNotClear"/>) says nothing either, and neither does one
    /// that has already said this.
    /// </summary>
    private void SayLostTrafficHolding(PhaseContext ctx)
    {
        if (_saidLostTraffic || _saidUnableNoRoute || _saidHeldShortNotClear || (ctx.Aircraft.Ground.AssignedTaxiRoute is not { IsComplete: false }))
        {
            return;
        }

        _saidLostTraffic = true;
        PilotResponder.RouteSoloOrRpoTransmission(
            ctx.Aircraft,
            ctx.SoloTrainingMode,
            ctx.RpoShowPilotSpeech,
            ctx.StudentPositionType,
            PilotResponder.BuildLostTrafficHolding(ctx.Aircraft, _targetCallsign),
            PilotResponder.SoloPositionsGround
        );
    }

    /// <summary>
    /// The pilot says it cannot follow, with no taxi route onto its traffic's path, and asks for taxi instructions: to a solo
    /// student on ground or tower, else in the RPO's terminal naming the lead.
    /// </summary>
    private void SayUnableNoRouteToFollow(PhaseContext ctx) =>
        PilotResponder.RouteSoloOrRpoTransmission(
            ctx.Aircraft,
            ctx.SoloTrainingMode,
            ctx.RpoShowPilotSpeech,
            ctx.StudentPositionType,
            PilotResponder.BuildUnableNoRouteToFollow(ctx.Aircraft, _targetCallsign),
            PilotResponder.SoloPositionsGround
        );

    /// <summary>
    /// Installs <paramref name="joinable"/> as the follow route, driven from segment <paramref name="startIndex"/>, giving way
    /// short of the merge when the follower still has route to drive to it and the merge lies ahead of the lead or the lead's tail
    /// is not yet past it. Driven from its first segment (<paramref name="startIndex"/> 0) the route gets a fresh navigator: a first
    /// plan, and also the usual splice at a node arrival or a lead re-route, whose new route starts on the follower's own segment.
    /// Spliced in at a later segment, the navigator carries on. Either way the segment is set up again on the next step, since
    /// <see cref="SetFollowRoute"/> marks it not set up.
    /// </summary>
    private void InstallPlan(PhaseContext ctx, AirportGroundLayout layout, FollowRoutePlan.Joinable joinable, AircraftState lead, int startIndex)
    {
        int mergeIndex = joinable.PathToMerge.Segments.Count;
        bool giveWay = (mergeIndex > startIndex) && GivesWayAtMerge(layout, joinable, lead);
        TaxiRoute route = new()
        {
            Segments = FollowSegments(joinable),
            HoldShortPoints = [],
            CurrentSegmentIndex = startIndex,
        };

        // A fresh navigator for every route driven from its first segment, splices that start on the follower's own segment
        // included: one carried over would set the route up from the last route's leftovers (its aimed line, fillet and target
        // node), which a snapshot taken with no route does not hold, so a restore would diverge. A route spliced in at a later
        // segment keeps the navigator, which the snapshot carries with it.
        if (startIndex == 0)
        {
            _nav = new GroundNavigator();
        }

        SetFollowRoute(route, mergeIndex, joinable.LeadEdgeIntoMerge, giveWay);
        _loggedHoldReason = null;
        _clearingAttemptedRunways = [];
        // A re-installed route is a way to move again, so the held-short latch ends with its hold: only that hold's call is
        // silenced (<see cref="SayLostTrafficHolding"/>).
        _saidHeldShortNotClear = false;
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

    /// <summary>
    /// Whether a follower on <paramref name="joinable"/> is to give way to <paramref name="lead"/> short of the merge: the merge
    /// lies ahead of the lead, or the lead's tail is not yet past it.
    /// </summary>
    private static bool GivesWayAtMerge(AirportGroundLayout layout, FollowRoutePlan.Joinable joinable, AircraftState lead) =>
        joinable.MergeAheadOfLead || !LeadTailPastMerge(layout, joinable.LeadPathFromMerge, lead);

    /// <summary>
    /// A runway bar a follower is cleared across: <paramref name="NearBar"/>, the bar it stands at or rolls up to, and the
    /// runways its crossing clearance covers (<paramref name="ClearedRunways"/>, the near bar's own runway included or not).
    /// </summary>
    public sealed record BarCrossing(GroundNode NearBar, IReadOnlyCollection<RunwayIdentifier> ClearedRunways)
    {
        /// <summary>Whether the clearance covers the runway <paramref name="bar"/> protects: the near bar's runway or a cleared one.</summary>
        public bool Covers(GroundNode bar) =>
            (bar.RunwayId is { } barRunway)
            && (((NearBar.RunwayId is { } nearRunway) && barRunway.Overlaps(nearRunway)) || ClearedRunways.Any(barRunway.Overlaps));
    }

    /// <summary>
    /// Whether the follower in <paramref name="ctx"/>, cleared across <paramref name="crossing"/>'s near bar with a follow of
    /// <paramref name="lead"/>, keeps holding there until the lead is ahead of it on their shared route. It does while the
    /// follow's plan (<see cref="FollowRoutePlanner.Plan"/>) has its merge ahead of the lead with the follower standing at it, or
    /// has route to drive to the merge and the follower would give way there (<see cref="GivesWayAtMerge"/>), and after that
    /// until the gap to the lead is at least the stop gap, the release a give-way at the merge waits for
    /// (<see cref="IsGiveWayOver"/>). Only behind a lead driving a route: one whose path is only guessed from where it stands
    /// and heads is given way to at the merge, past the bar. Never when the merge lies beyond the near bar's runway and the
    /// follower fits between that runway's far hold line and its give-way stop (<see cref="RoomBeyondFarBarFt"/>): it crosses
    /// and waits there instead.
    /// </summary>
    public static bool WaitsAtBarForLead(PhaseContext ctx, AirportGroundLayout layout, AircraftState lead, BarCrossing crossing)
    {
        if (
            (DrivenRouteOf(lead) is not { IsComplete: false })
            || (FollowRoutePlanner.Plan(layout, ctx.Aircraft, lead, GroundTraffic(ctx)) is not FollowRoutePlan.Joinable joinable)
        )
        {
            return false;
        }

        if (joinable.PathToMerge.Segments.Count == 0)
        {
            return joinable.MergeAheadOfLead;
        }

        if ((RoomBeyondFarBarFt(ctx.Aircraft, joinable, lead, crossing) is { } roomFt) && (roomFt >= 0.0))
        {
            return false;
        }

        if (GivesWayAtMerge(layout, joinable, lead))
        {
            return true;
        }

        AircraftCategory leadCategory = AircraftCategorization.Categorize(lead.AircraftType);
        double stopGapFt = FollowGap.StopGapFt(lead.AircraftType, leadCategory, ctx.Aircraft.AircraftType, ctx.Category);
        double? gapFt = FollowRoutePlanner.AlongPathGapFt(
            FollowRoutePlanner.FollowerToMergeFt(layout, joinable, ctx.Aircraft),
            joinable.LeadPathFromMerge,
            FollowRoutePlanner.LocateOnPath(layout, joinable.LeadPathFromMerge, lead),
            ctx.Aircraft.AircraftType,
            lead.AircraftType
        );
        return (gapFt ?? NoseToTailFt(ctx.Aircraft, lead)) < stopGapFt;
    }

    /// <summary>
    /// Whether a wait at a bar for <paramref name="lead"/> (<see cref="WaitsAtBarForLead"/>) is a real wait for the traffic to pass,
    /// which the pilot reports: the follow's merge lies ahead of the lead, or the lead, past it, is not moving away from it (below
    /// <see cref="LeadMovingKts"/>). The short wait for a lead moving away past the merge to get its tail clear of it is not.
    /// True when the follow joins nothing, so a wait is never left unreported on a plan that changed under it.
    /// </summary>
    public static bool IsWaitForTrafficToPass(PhaseContext ctx, AirportGroundLayout layout, AircraftState lead) =>
        (FollowRoutePlanner.Plan(layout, ctx.Aircraft, lead, GroundTraffic(ctx)) is not FollowRoutePlan.Joinable joinable)
        || joinable.MergeAheadOfLead
        || !IsMoving(lead);

    /// <summary>
    /// The room (ft) a follower at <paramref name="follower"/>'s position on <paramref name="joinable"/>'s route to the merge has to
    /// wait for <paramref name="lead"/> beyond the runway of <paramref name="crossing"/>'s near bar: how far its tail, with its
    /// centre at its stop short of the merge, lies past the farthest runway bar the route reaches before that stop — negative
    /// when it would stop with its tail short of it, or inside a runway's pair of bars. The stop is the give-way stop
    /// (<see cref="GroundConflictDetector.GiveWayStop(AircraftState, TaxiRoute, IReadOnlyList{DirectionalEdge}, AircraftState, out string?)"/>),
    /// or the hold line of a bar of a runway the clearance does not cover where the route reaches one first: the follower holds
    /// short of it. A give-way stop beyond the look-ahead leaves room without bound (positive infinity): the follower crosses
    /// and gives way at the merge when it gets there. Null when the route reaches no other bar of the near bar's runway before
    /// the stop, so the merge is not beyond its far hold line.
    /// </summary>
    public static double? RoomBeyondFarBarFt(AircraftState follower, FollowRoutePlan.Joinable joinable, AircraftState lead, BarCrossing crossing)
    {
        TaxiRoute toMerge = joinable.PathToMerge;
        List<DirectionalEdge> track = TrackAtMerge(joinable.LeadEdgeIntoMerge, joinable.LeadPathFromMerge);
        if ((crossing.NearBar.RunwayId is not { } nearRunway) || toMerge.IsComplete || (track.Count == 0))
        {
            return null;
        }

        double halfLengthFt = AircraftLength.ResolveFt(follower.AircraftType) / 2.0;
        double giveWayFt = GroundConflictDetector.GiveWayStop(follower, toMerge, track, lead, out _) is { } stop
            ? stop.ToStopFt
            : double.PositiveInfinity;
        BarsBeforeStop bars = ScanBarsBeforeStop(follower, toMerge, crossing, nearRunway, giveWayFt);
        if (!bars.CrossesNearRunway)
        {
            return null;
        }

        return bars.StopsInsideARunway ? -halfLengthFt : bars.StopFt - halfLengthFt - bars.LastBarFt;
    }

    /// <summary>
    /// What a follower's route to the merge reaches before its stop (<see cref="ScanBarsBeforeStop"/>): the stop (ft along the
    /// route), the farthest bar before it, whether one of those is a bar of the near bar's runway, and whether the stop lies
    /// between a runway's two bars.
    /// </summary>
    private readonly record struct BarsBeforeStop(double StopFt, double LastBarFt, bool CrossesNearRunway, bool StopsInsideARunway);

    /// <summary>
    /// Walks <paramref name="toMerge"/> from <paramref name="follower"/>'s position to the stop, <paramref name="giveWayFt"/> along
    /// it or, where it reaches first a bar of a runway <paramref name="crossing"/> does not cover, half the follower's length short
    /// of that bar. Each bar passed opens or closes its runway — <paramref name="nearRunway"/>, the near bar's, opens at the start —
    /// so a runway left open at the stop is one the stop lies inside.
    /// </summary>
    private static BarsBeforeStop ScanBarsBeforeStop(
        AircraftState follower,
        TaxiRoute toMerge,
        BarCrossing crossing,
        RunwayIdentifier nearRunway,
        double giveWayFt
    )
    {
        double halfLengthFt = AircraftLength.ResolveFt(follower.AircraftType) / 2.0;
        List<RunwayIdentifier> open = [nearRunway];
        bool crossesNearRunway = false;
        double stopFt = giveWayFt;
        double lastBarFt = 0.0;
        for (int i = Math.Max(0, toMerge.CurrentSegmentIndex); i < toMerge.Segments.Count; i++)
        {
            GroundNode node = toMerge.Segments[i].Edge.ToNode;
            double alongFt = toMerge.RemainingDistanceFt(follower.Position, i + 1);
            if (alongFt > stopFt)
            {
                break;
            }

            if ((node.Type != GroundNodeType.RunwayHoldShort) || (node.Id == crossing.NearBar.Id) || (node.RunwayId is not { } barRunway))
            {
                continue;
            }

            if (!crossing.Covers(node))
            {
                stopFt = Math.Min(stopFt, alongFt - halfLengthFt);
                break;
            }

            crossesNearRunway |= barRunway.Overlaps(nearRunway);
            lastBarFt = Math.Max(lastBarFt, alongFt);
            if (open.FindIndex(barRunway.Overlaps) is var at and >= 0)
            {
                open.RemoveAt(at);
            }
            else
            {
                open.Add(barRunway);
            }
        }

        return new BarsBeforeStop(stopFt, lastBarFt, crossesNearRunway, open.Count > 0);
    }

    /// <summary>The lead's path edges into and out of the merge, the track a give-way at the merge keeps clear of.</summary>
    private static List<DirectionalEdge> TrackAtMerge(DirectionalEdge? leadEdgeIntoMerge, IReadOnlyList<DirectionalEdge> leadPathFromMerge) =>
        leadEdgeIntoMerge is { } into ? [into, .. leadPathFromMerge.Take(1)] : [.. leadPathFromMerge.Take(1)];

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
        List<DirectionalEdge> track = TrackAtMerge(leadEdgeIntoMerge, leadPath);
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
        _brakingLostRoute = false;
    }

    /// <summary>
    /// Ticks the navigator over <paramref name="route"/> (the clearing route, whose end is a stop), setting the current segment
    /// up first when it is not, and advances the route on each node arrival. False when the route ran out.
    /// </summary>
    private bool TickNavigator(PhaseContext ctx, TaxiRoute route)
    {
        NavStep step = StepNavigator(ctx, route, isFollowRoute: false, routeEndIsStop: true);
        if (step == NavStep.Arrived)
        {
            SetUpNavSegment(ctx, route, isFollowRoute: false);
        }

        return step != NavStep.RanOut;
    }

    /// <summary>What one navigator tick over a route did: drove on along its segment, arrived at the segment's end node, or ran out.</summary>
    private enum NavStep
    {
        Driving,
        Arrived,
        RanOut,
    }

    /// <summary>
    /// Ticks the navigator over <paramref name="route"/>, setting the current segment up first when it is not, and advances the
    /// route's segment on a node arrival without setting the next one up (<see cref="SetUpNavSegment"/>), so the caller may plan
    /// again first. With <paramref name="routeEndIsStop"/> false, read each tick, the last segment is driven as a pass-through.
    /// </summary>
    private NavStep StepNavigator(PhaseContext ctx, TaxiRoute route, bool isFollowRoute, bool routeEndIsStop)
    {
        _nav.MaxSpeedKts = CategoryPerformance.TaxiSpeed(ctx.Category);
        if (!_navSetUp)
        {
            SetUpNavSegment(ctx, route, isFollowRoute);
        }

        bool isLastSegment = (route.CurrentSegmentIndex + 1) >= route.Segments.Count;
        if (_nav.Tick(ctx, isLastSegment && routeEndIsStop, AlwaysCleared) != NavigatorResult.ArrivedAtNode)
        {
            return NavStep.Driving;
        }

        route.CurrentSegmentIndex = Math.Min(route.CurrentSegmentIndex + 1, route.Segments.Count);
        return route.IsComplete ? NavStep.RanOut : NavStep.Arrived;
    }

    /// <summary>
    /// Sets the navigator up on <paramref name="route"/>'s current segment. A clearing route's end is a stop the navigator brakes
    /// to (0 kt); a follow route's is not, since whether it is a stop turns on the lead each tick (<see cref="RouteEndCap"/>), and
    /// a set-up segment carries no per-tick state a restored follow could read differently.
    /// </summary>
    private void SetUpNavSegment(PhaseContext ctx, TaxiRoute route, bool isFollowRoute)
    {
        _nav.RouteEndSpeedKts = isFollowRoute ? _nav.MaxSpeedKts : 0;
        _nav.SetupSegment(route, ctx, AlwaysCleared);
        _navSetUp = true;
    }

    /// <summary>
    /// Whether the follow route's end is a stop: unless the lead is moving with no route of its own left (its path ends at the far
    /// end of the edge it is on), so it is driving on past the planned end, where the follower plans again. A lead with a route
    /// stops at that route's end, which is the follow route's end too; a new route for it is planned the tick it is given
    /// (<see cref="FollowLeadReroute"/>).
    /// </summary>
    private static bool RouteEndIsStop(AircraftState lead) => !IsMoving(lead) || (FollowRoutePlanner.RemainingRoute(lead).Count > 0);

    /// <summary>The ground speed (kt) at or above which a lead counts as rolling on past the follow route's end.</summary>
    private const double LeadMovingKts = 1.0;

    /// <summary>Whether <paramref name="aircraft"/> is rolling: at <see cref="LeadMovingKts"/> or faster over the ground.</summary>
    private static bool IsMoving(AircraftState aircraft) => aircraft.GroundSpeed >= LeadMovingKts;

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
        ApplyCap(ctx, RouteEndCap(ctx, lead));
        ApplyCap(ctx, barCap);
        LogPeriodic(ctx, lead, gapFt, stopGapFt);
    }

    /// <summary>
    /// Behind a lead at rest, the braking curve at the taxi rate onto the follow route's end, read along the route; null while the
    /// lead is moving, when the follower drives on to the end and plans again there (<see cref="TickFollowNavigator"/>).
    /// </summary>
    private SpeedCap? RouteEndCap(PhaseContext ctx, AircraftState lead) => RouteEndIsStop(lead) ? RouteEndStopCap(ctx) : null;

    /// <summary>The braking curve at the taxi rate onto the follow route's end, read along the route; null with no route left.</summary>
    private SpeedCap? RouteEndStopCap(PhaseContext ctx)
    {
        if (_followRoute is not { IsComplete: false } route)
        {
            return null;
        }

        double toEndFt = route.RemainingDistanceFt(ctx.Aircraft.Position, route.Segments.Count);
        return new SpeedCap(GroundStopBraking.StopCurveKts(ctx, toEndFt, CategoryPerformance.TaxiDecelRate(ctx.Category)), null);
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
    /// backing or coming toward the follower opens no gap, so its speed is no speed to match, and the ground conflict detector
    /// limits it against its follower as in any other pair.
    /// </summary>
    internal static bool LeadTracksTowardFollower(AircraftState follower, AircraftState lead) =>
        TaxiEdgeTrail.TracksWithin90Of(lead, GeoMath.BearingTo(lead.Position, follower.Position));

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
        LatLon nose = NoseOf(follower);
        LatLon tail = TailOf(lead);
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
            BeenOnClearedRunways = [.. _beenOnClearedRunways.Select(static r => r.ToString())],
            ExitingRunways = [.. _exitingRunways.Select(static r => r.ToString())],
            BrokenOff = _brokenOff,
            LatchedBarNodeId = _latchedBarNodeId,
            LatchedBarApproachDeg = _latchedBarApproachDeg,
            LeadDepartureBarNodeId = _leadDepartureBar?.NodeId,
            LeadDepartureRunwayEnd = _leadDepartureBar?.RunwayEnd,
            LeadSeenDeparting = _leadSeenDeparting,
            LeadWaitVerdicts = (_leadWaitVerdicts.Count > 0) ? new Dictionary<int, bool>(_leadWaitVerdicts) : null,
            LeadWaitSecond = (_leadWaitVerdicts.Count > 0) ? _leadWaitSecond : null,
            TaxiEdgeNodeA = _taxiEdgeNodeIds?.NodeA,
            TaxiEdgeNodeB = _taxiEdgeNodeIds?.NodeB,
            FollowRoute = _followRoute?.ToSnapshot(),
            MergeSegmentIndex = _mergeSegmentIndex,
            LeadEdgeIntoMerge = _plan?.LeadEdgeIntoMerge is { } intoMerge ? TaxiRoute.ToSegmentSnapshot(ToSegment(intoMerge)) : null,
            GivingWay = _givingWay,
            Unjoinable = _unjoinable,
            BrakingLostRoute = _brakingLostRoute ? true : null,
            SaidHeldShortNotClear = _saidHeldShortNotClear ? true : null,
            SaidLostTraffic = _saidLostTraffic ? true : null,
            SaidUnableNoRoute = _saidUnableNoRoute ? true : null,
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
            _beenOnClearedRunways = [.. (dto.BeenOnClearedRunways ?? []).Select(RunwayIdentifier.Parse)],
            _exitingRunways = [.. dto.ExitingRunways.Select(RunwayIdentifier.Parse)],
            _brokenOff = dto.BrokenOff,
            _latchedBarNodeId = dto.LatchedBarNodeId,
            _latchedBarApproachDeg = dto.LatchedBarApproachDeg,
            _leadDepartureBar =
                (dto.LeadDepartureBarNodeId is { } leadBarNodeId) && (dto.LeadDepartureRunwayEnd is { } leadRunwayEnd)
                    ? new LeadDepartureBar(leadBarNodeId, leadRunwayEnd)
                    : null,
            _leadSeenDeparting = dto.LeadSeenDeparting,
            _leadWaitVerdicts = dto.LeadWaitVerdicts is { } verdicts ? new Dictionary<int, bool>(verdicts) : [],
            _leadWaitSecond = dto.LeadWaitSecond ?? 0,
            _taxiEdgeNodeIds = (dto.TaxiEdgeNodeA is { } nodeA) && (dto.TaxiEdgeNodeB is { } nodeB) ? (nodeA, nodeB) : null,
            _unjoinable = dto.Unjoinable,
            _brakingLostRoute = dto.BrakingLostRoute ?? false,
            _saidHeldShortNotClear = dto.SaidHeldShortNotClear ?? false,
            _saidLostTraffic = dto.SaidLostTraffic ?? false,
            _saidUnableNoRoute = dto.SaidUnableNoRoute ?? false,
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
    private readonly record struct BarAhead(GroundNode Node, HoldShortReason Reason, string? TargetName, double ApproachDeg, double ToStopFt);

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

            if ((ReachedBarAhead(ctx, node, halfLengthFt) is { } candidate) && IsClosingOnHoldLine(ctx, candidate, lookAheadFt))
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
    /// Whether the follower must stop at this bar: not when it is already on that runway; always when the bar is a departure
    /// bar for it (<see cref="DepartureEndAt"/>), which no crossing clearance covers; otherwise not when it is leaving a runway it
    /// occupied in this follow away from it, nor when its crossing clearance covers the runway — unless it is to wait at that bar
    /// for its lead (<see cref="WaitsForLeadAt"/>).
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

        if (DepartureEndAt(ctx, node) is not null)
        {
            return true;
        }

        return !IsExitingAwayFrom(ctx, node) && (!IsClearedToCross(node) || WaitsForLeadAt(ctx, node));
    }

    /// <summary>
    /// Whether the follower, cleared across the runway <paramref name="bar"/> protects, is to wait at it for its lead as a hold at
    /// that bar would (<see cref="WaitsAtBarForLead"/>): the merge lies ahead of the lead beyond the bar with no room to wait
    /// past the runway. Judged once per bar per whole second of the phase (<see cref="_leadWaitVerdicts"/>), as a hold waiting
    /// at the bar is, so the wait/go decision lags the traffic by under a second.
    /// </summary>
    private bool WaitsForLeadAt(PhaseContext ctx, GroundNode bar)
    {
        int second = (int)Math.Floor(ElapsedSeconds);
        if (second != _leadWaitSecond)
        {
            _leadWaitVerdicts.Clear();
            _leadWaitSecond = second;
        }

        if (!_leadWaitVerdicts.TryGetValue(bar.Id, out bool waits))
        {
            waits =
                (ctx.GroundLayout is { } layout)
                && (ctx.AircraftLookup?.Invoke(_targetCallsign) is { IsOnGround: true } lead)
                && WaitsAtBarForLead(ctx, layout, lead, new BarCrossing(bar, _crossingClearedRunways));
            _leadWaitVerdicts[bar.Id] = waits;
        }

        return waits;
    }

    /// <summary>
    /// <paramref name="node"/> as a bar ahead of the follower, its hold line square to <paramref name="approachDeg"/>: a departure
    /// hold for the runway end it departs on there (<see cref="DepartureEndAt"/>), else a crossing of the bar's runway.
    /// </summary>
    private BarAhead ToBarAhead(PhaseContext ctx, GroundNode node, double approachDeg, double halfLengthFt)
    {
        string? departureEnd = DepartureEndAt(ctx, node);
        HoldShortReason reason = departureEnd is null ? HoldShortReason.RunwayCrossing : HoldShortReason.DestinationRunway;
        LatLon nose = GeoMath.ProjectPoint(ctx.Aircraft.Position, ctx.Aircraft.TrueHeading, halfLengthFt / GeoMath.FeetPerNm);
        double noseFromBarFt = GeoMath.DistanceNm(node.Position, nose) * GeoMath.FeetPerNm;
        double offAxisRad = GeoMath.SignedBearingDifference(approachDeg, GeoMath.BearingTo(node.Position, nose)) * Math.PI / 180.0;
        return new BarAhead(node, reason, departureEnd ?? node.RunwayId?.ToString(), approachDeg, noseFromBarFt * Math.Cos(offAxisRad));
    }

    /// <summary>
    /// <paramref name="node"/> as a bar ahead of the follower (<see cref="ToBarAhead"/>), or null when the follower does not reach
    /// it. With a follow route still to run, a bar counts only when it is on the rest of that route (<see cref="IsOnRestOfRoute"/>):
    /// a bar of another taxiway beside the trail, however it is approached, is not one the follower reaches. With no follow route,
    /// or a finished one kept while the follower brakes to rest past its end, a bar counts when the edge it is approached by from
    /// the follower — of the bar's edges, the one whose far end lies nearest in bearing to the follower — is a straight edge of the
    /// bar's own taxiway (<see cref="RunwayHoldBars.BarTaxiwayEdges"/>); a bar on a fillet tangent that the follower reaches only
    /// through the arc from a taxiway it is passing is not ahead of it. The hold line is square to the bar's taxiway, never to an
    /// arc (<see cref="ApproachBearingDeg"/>).
    /// </summary>
    private BarAhead? ReachedBarAhead(PhaseContext ctx, GroundNode node, double halfLengthFt)
    {
        List<GroundEdge> taxiwayEdges = RunwayHoldBars.BarTaxiwayEdges(node);
        LatLon position = ctx.Aircraft.Position;
        bool reached =
            (_followRoute is { IsComplete: false } route)
                ? IsOnRestOfRoute(route, node.Id)
                : ((NearestInBearing(node, node.Edges, position) is GroundEdge approach) && taxiwayEdges.Contains(approach));
        if (!reached)
        {
            return null;
        }

        return ToBarAhead(ctx, node, ApproachBearingDeg(node, taxiwayEdges, position), halfLengthFt);
    }

    /// <summary>
    /// Of <paramref name="edges"/> at <paramref name="bar"/>, the one whose far end lies nearest in bearing to
    /// <paramref name="position"/>; null with none.
    /// </summary>
    private static IGroundEdge? NearestInBearing(GroundNode bar, IEnumerable<IGroundEdge> edges, LatLon position)
    {
        double towardPosition = GeoMath.BearingTo(bar.Position, position);
        return edges.MinBy(edge =>
            Math.Abs(GeoMath.SignedBearingDifference(towardPosition, GeoMath.BearingTo(bar.Position, FarNode(edge, bar).Position)))
        );
    }

    /// <summary>
    /// The bearing (deg true) from <paramref name="bar"/> back up the edge leading into it: of the bar's straight edges of its own
    /// taxiway (<paramref name="taxiwayEdges"/>), the one whose far end lies nearest in bearing to <paramref name="aircraftPosition"/>;
    /// the bearing to the aircraft when the bar has none.
    /// </summary>
    private static double ApproachBearingDeg(GroundNode bar, List<GroundEdge> taxiwayEdges, LatLon aircraftPosition) =>
        NearestInBearing(bar, taxiwayEdges, aircraftPosition) is { } edge
            ? GeoMath.BearingTo(bar.Position, FarNode(edge, bar).Position)
            : GeoMath.BearingTo(bar.Position, aircraftPosition);

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
            TargetName = bar.TargetName,
        };

        var holdPhase = new HoldingShortPhase(holdShort);
        if (!IsClearedToCross(node) || (bar.Reason != HoldShortReason.RunwayCrossing))
        {
            // Held at a bar its clearance does not cover, the follow keeps the runways pre-cleared beyond it, never one it has used.
            ctx.Aircraft.Phases?.InsertAfterCurrent([
                holdPhase,
                new FollowingPhase(_targetCallsign)
                {
                    CrossingClearedRunways = PreClearedRunways,
                    LeadDeparture = _leadDepartureBar,
                    LeadSeenDeparting = _leadSeenDeparting,
                },
            ]);
            return true;
        }

        // Cleared across, stopped only to wait for the lead: the hold keeps the clearance armed and lets the follow, which keeps
        // crossing the cleared runways, go once the lead is ahead.
        holdPhase.SatisfyClearance(ClearanceType.RunwayCrossing);
        var resumeFollow = new FollowingPhase(_targetCallsign)
        {
            CrossingClearedRunways = _crossingClearedRunways,
            BeenOnClearedRunways = _beenOnClearedRunways,
            LeadDeparture = _leadDepartureBar,
            LeadSeenDeparting = _leadSeenDeparting,
        };
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

    /// <summary>
    /// The first segment of <paramref name="route"/> from its current one on that leaves <paramref name="nodeId"/>; null with none.
    /// </summary>
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

    /// <summary>
    /// Whether the follower's nose, centre or tail is inside <paramref name="runway"/>'s hold line, each part judged with the
    /// centre as the main gear.
    /// </summary>
    private static bool IsAnyPartInsideHoldLine(AirportGroundLayout layout, RunwayInfo runway, LatLon nose, LatLon centre, LatLon tail) =>
        IsInsideHoldLine(layout, runway, nose, centre)
        || IsInsideHoldLine(layout, runway, centre, centre)
        || IsInsideHoldLine(layout, runway, tail, centre);

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
        return IsClearOfRunwayBars(ctx.Aircraft, layout, runway);
    }

    /// <summary>
    /// Whether <paramref name="aircraft"/>'s tail and both wingtips each lie farther from <paramref name="runway"/>'s centreline
    /// than the farthest of the runway's bars on that part's side (<see cref="IsClearOfBars"/>), judged without the follow's state.
    /// </summary>
    public static bool IsClearOfRunwayBars(AircraftState aircraft, AirportGroundLayout layout, RunwayInfo runway)
    {
        GroundNode[] bars = RunwayBars(layout, runway);
        Span<LatLon> parts = stackalloc LatLon[MaxTrailingParts];
        int count = FillTrailingParts(aircraft, parts);
        for (int i = 0; i < count; i++)
        {
            if (CentrelineFt(runway, parts[i]) <= FarthestBarFt(runway, bars, IsRightOfCentreline(runway, parts[i])))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether any part of <paramref name="aircraft"/> — its position, tail or wingtips — lies inside <paramref name="runway"/>'s
    /// holding position markings: no farther from the centreline than the farthest bar on its side.
    /// </summary>
    private static bool IsInsideRunwayMarkings(AircraftState aircraft, GroundNode[] bars, RunwayInfo runway)
    {
        Span<LatLon> parts = stackalloc LatLon[MaxTrailingParts + 1];
        int count = FillTrailingParts(aircraft, parts);
        parts[count++] = aircraft.Position;
        for (int i = 0; i < count; i++)
        {
            if (CentrelineFt(runway, parts[i]) <= FarthestBarFt(runway, bars, IsRightOfCentreline(runway, parts[i])))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// How far (ft) from <paramref name="runway"/>'s centreline the farthest of <paramref name="bars"/> on the given side lies; 0 with none.
    /// </summary>
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
    /// Spend each runway's crossing clearance once it has been used, on its own: note each cleared runway whose pavement the
    /// aircraft is on, and drop a runway from the clearance the first tick after that it is clear of that runway's pavement, so a
    /// bar of the same runway met later in the follow stops the aircraft again. The other cleared runways stay cleared.
    /// </summary>
    private void ExpireUsedCrossingClearance(PhaseContext ctx)
    {
        if ((_crossingClearedRunways.Count == 0) || ctx.GroundLayout is null)
        {
            return;
        }

        IReadOnlyList<RunwayInfo> runways = RunwayOccupancy.AirportRunways(ctx.GroundLayout.AirportId);
        List<RunwayIdentifier> spent = [];
        foreach (RunwayIdentifier cleared in _crossingClearedRunways)
        {
            bool onIt = runways.Any(runway => runway.Id.Overlaps(cleared) && RunwayOccupancy.IsOnPavement(ctx.Aircraft, runway));
            bool beenOn = _beenOnClearedRunways.Any(been => been.Overlaps(cleared));
            if (onIt && !beenOn)
            {
                _beenOnClearedRunways.Add(cleared);
            }
            else if (!onIt && beenOn)
            {
                spent.Add(cleared);
            }
        }

        foreach (RunwayIdentifier runway in spent)
        {
            Log.LogDebug("[Follow] {Callsign}: clear of runway {Runway} its crossing clearance covered; spent", ctx.Aircraft.Callsign, runway);
            _crossingClearedRunways = [.. _crossingClearedRunways.Where(cleared => !cleared.Overlaps(runway))];
            _beenOnClearedRunways.RemoveAll(been => been.Overlaps(runway));
        }
    }

    /// <summary>
    /// The cleared runways whose pavement the follower has been on and not yet left clear of (snapshotted): each is spent the
    /// first tick it is clear of it (<see cref="ExpireUsedCrossingClearance"/>). A follow a crossing hands over to starts with the
    /// runway that crossing crossed.
    /// </summary>
    public IReadOnlyList<RunwayIdentifier> BeenOnClearedRunways
    {
        get => _beenOnClearedRunways;
        init => _beenOnClearedRunways = [.. value];
    }

    /// <summary>The cleared runways not yet used (<see cref="BeenOnClearedRunways"/>): the pre-clearances a later follow takes on.</summary>
    public IReadOnlyList<RunwayIdentifier> PreClearedRunways =>
        [.. _crossingClearedRunways.Where(cleared => !_beenOnClearedRunways.Any(been => been.Overlaps(cleared)))];

    /// <summary>
    /// The runway end the follower departs on from this bar, when the bar is a departure bar for it rather than a crossing; else
    /// null. A follower with a departure runway of its own departs from that runway's bars only
    /// (<see cref="OwnDepartureEndAt"/>); one with none takes the lead's departure bar as its own
    /// (<see cref="TakesLeadDepartureBar"/>), for the end the lead departs on. The distinction is load-bearing twice over:
    /// <see cref="HoldShortReason.DestinationRunway"/> makes <c>RES</c> refuse to release it onto the runway without a takeoff
    /// clearance, and it puts the aircraft at tier 0 in <see cref="RunwayDepartureQueue"/> — at the front of the line, where it is.
    /// </summary>
    private string? DepartureEndAt(PhaseContext ctx, GroundNode node)
    {
        if (node.RunwayId is not { } barRunway)
        {
            return null;
        }

        if (HasOwnDepartureRunway(ctx))
        {
            return OwnDepartureEndAt(ctx, barRunway);
        }

        return TakesLeadDepartureBar(ctx, node.Id) ? _leadDepartureBar?.RunwayEnd : null;
    }

    /// <summary>
    /// The end of the follower's own departure runway that <paramref name="barRunway"/>'s bar protects: the destination runway
    /// of the taxi route it was cleared on (<c>FOLLOWG</c> does not erase it), else a runway assigned with <c>RWY</c>
    /// (<see cref="OwnAssignedRunway"/>); null when the bar protects neither.
    /// </summary>
    private static string? OwnDepartureEndAt(PhaseContext ctx, RunwayIdentifier barRunway)
    {
        if ((OwnRouteDepartureEnd(ctx) is { } routeEnd) && barRunway.Overlaps(RunwayIdentifier.Parse(routeEnd)))
        {
            return routeEnd;
        }

        return (OwnAssignedRunway(ctx) is { } assigned) && barRunway.Overlaps(assigned.Id) ? assigned.Designator : null;
    }

    /// <summary>Whether the follower has a departure runway of its own: its route's destination runway, or one assigned with RWY.</summary>
    private static bool HasOwnDepartureRunway(PhaseContext ctx) => (OwnRouteDepartureEnd(ctx) is not null) || (OwnAssignedRunway(ctx) is not null);

    /// <summary>The destination runway end of the taxi route the follower was cleared on; null with none.</summary>
    private static string? OwnRouteDepartureEnd(PhaseContext ctx) =>
        ctx.Aircraft.Ground.AssignedTaxiRoute?.HoldShortPoints.FirstOrDefault(h => h.Reason == HoldShortReason.DestinationRunway)?.TargetName;

    /// <summary>
    /// The runway assigned to the follower (<see cref="PhaseList.AssignedRunway"/>) when its flight plan departs this airport, so
    /// it is a departure runway; an arrival's landing runway is not one. Null otherwise.
    /// </summary>
    private static RunwayInfo? OwnAssignedRunway(PhaseContext ctx) =>
        (ctx.Aircraft.Phases?.AssignedRunway is { } assigned) && DepartsFromThisAirport(ctx) ? assigned : null;

    /// <summary>
    /// Whether the follower's flight plan departs the airport whose ground layout it is on (<see cref="IsThisAirport"/>). A plan
    /// naming no departure airport (a VFR local with nothing filed) departs here unless its destination is this airport, an
    /// arrival; one naming another departure airport does not.
    /// </summary>
    private static bool DepartsFromThisAirport(PhaseContext ctx)
    {
        if (ctx.GroundLayout is not { } layout)
        {
            return false;
        }

        AircraftFlightPlan plan = ctx.Aircraft.FlightPlan;
        return string.IsNullOrEmpty(plan.Departure) ? !IsThisAirport(plan.Destination, layout) : IsThisAirport(plan.Departure, layout);
    }

    /// <summary>
    /// Whether <paramref name="airportId"/> names <paramref name="layout"/>'s airport, ICAO and FAA ids alike, non-CONUS ones
    /// ("PHNL"/"HNL") included through the navigation database (<see cref="NavigationDatabase.AirportIdsMatchResolved"/>); with
    /// no database loaded, by the K-prefix match alone (<see cref="NavigationDatabase.AirportIdsMatch"/>).
    /// </summary>
    private static bool IsThisAirport(string airportId, AirportGroundLayout layout) =>
        NavigationDatabase.InstanceOrNull is { } navDb
            ? navDb.AirportIdsMatchResolved(airportId, layout.AirportId)
            : NavigationDatabase.AirportIdsMatch(airportId, layout.AirportId);

    /// <summary>
    /// Whether the follower takes node <paramref name="nodeId"/> as its departure bar because it is the lead's
    /// (<see cref="_leadDepartureBar"/>, matched by node, so another bar of that runway on the way stays a crossing): only with no
    /// departure runway of its own and a flight plan departing this airport. The hold reports the lead's runway end and assigns
    /// no runway; the runway is assigned by the line-up or takeoff clearance.
    /// </summary>
    private bool TakesLeadDepartureBar(PhaseContext ctx, int nodeId) =>
        (_leadDepartureBar is { } latch) && (latch.NodeId == nodeId) && !HasOwnDepartureRunway(ctx) && DepartsFromThisAirport(ctx);

    /// <summary>
    /// Reads the lead's departure bar again while the lead is on the ground (<see cref="LiveDepartureBarOf"/>), keeping the one
    /// last read when none is seen now — a lead that has lined up or departed no longer shows it — and dropping it when the lead
    /// crosses there or holds there for another reason than departing (<see cref="IsCrossingOrHeldThereNotDeparting"/>).
    /// </summary>
    private void RefreshLeadDepartureBar(PhaseContext ctx)
    {
        if (ctx.AircraftLookup?.Invoke(_targetCallsign) is not { IsOnGround: true } lead)
        {
            return;
        }

        if (LiveDepartureBarOf(ctx, lead) is { } seen)
        {
            if ((_leadDepartureBar is not { } latch) || (latch.NodeId != seen.NodeId) || (latch.RunwayEnd != seen.RunwayEnd))
            {
                SetLeadDepartureBar(ctx, new LeadDepartureBar(seen.NodeId, seen.RunwayEnd));
            }
        }
        else if (IsCrossingOrHeldThereNotDeparting(lead, _leadDepartureBar))
        {
            SetLeadDepartureBar(ctx, null);
        }
    }

    private void SetLeadDepartureBar(PhaseContext ctx, LeadDepartureBar? bar)
    {
        Log.LogDebug(
            "[Follow] {Callsign}: {Target}'s departure bar {Bar}",
            ctx.Aircraft.Callsign,
            _targetCallsign,
            bar is null ? "dropped" : $"node {bar.NodeId} for runway {bar.RunwayEnd}"
        );
        _leadDepartureBar = bar;
    }

    /// <summary>
    /// Whether <paramref name="lead"/> is crossing the runway from <paramref name="bar"/>'s node (<see cref="CrossingRunwayPhase"/>),
    /// or holds there for another reason than departing: a lead crossing keeps today's crossing behaviour for its follower.
    /// </summary>
    private static bool IsCrossingOrHeldThereNotDeparting(AircraftState lead, LeadDepartureBar? bar) =>
        (bar is not null)
        && (
            lead.Phases?.CurrentPhase switch
            {
                HoldingShortPhase { HoldShort: { } held } => (held.NodeId == bar.NodeId) && (held.Reason != HoldShortReason.DestinationRunway),
                CrossingRunwayPhase crossing => crossing.ApproachNodeId == bar.NodeId,
                _ => false,
            }
        );

    /// <summary>
    /// The departure bar <paramref name="lead"/> shows now, as its node and runway end: the bar it holds at as a departure hold;
    /// else its taxi route's destination bar; else, while it lines up or rolls for takeoff on its assigned runway, the bar the
    /// follow route ends at when that bar protects the runway. Null with none.
    /// </summary>
    private (int NodeId, string RunwayEnd)? LiveDepartureBarOf(PhaseContext ctx, AircraftState lead)
    {
        if (lead.Phases?.CurrentPhase is HoldingShortPhase { HoldShort: { Reason: HoldShortReason.DestinationRunway, TargetName: { } heldEnd } held })
        {
            return (held.NodeId, heldEnd);
        }

        HoldShortPoint? destination = lead.Ground.AssignedTaxiRoute?.HoldShortPoints.FirstOrDefault(h =>
            h.Reason == HoldShortReason.DestinationRunway
        );
        if (destination?.TargetName is { } routeEnd)
        {
            return (destination.NodeId, routeEnd);
        }

        return LinedUpDepartureBar(ctx, lead);
    }

    /// <summary>
    /// For a lead lining up, lined up or rolling for takeoff on its assigned runway, the bar the follow route ends at when that
    /// bar protects the runway (<see cref="ProtectsRunway"/>): the bar the lead left for it. Null otherwise.
    /// </summary>
    private (int NodeId, string RunwayEnd)? LinedUpDepartureBar(PhaseContext ctx, AircraftState lead)
    {
        if (
            (lead.Phases?.CurrentPhase is not (LineUpPhase or LinedUpAndWaitingPhase or TakeoffPhase))
            || (lead.Phases.AssignedRunway is not { } runway)
        )
        {
            return null;
        }

        return (FollowRouteEndNode(ctx) is { } end) && ProtectsRunway(end, runway) ? (end.Id, runway.Designator) : null;
    }

    /// <summary>The node the follow route ends at; null with no follow route or no ground layout.</summary>
    private GroundNode? FollowRouteEndNode(PhaseContext ctx) =>
        (_followRoute is { Segments.Count: > 0 } route)
        && (ctx.GroundLayout is { } layout)
        && layout.Nodes.TryGetValue(route.Segments[^1].ToNodeId, out GroundNode? end)
            ? end
            : null;

    /// <summary>Whether <paramref name="node"/> is a runway hold-short bar of <paramref name="runway"/>.</summary>
    private static bool ProtectsRunway(GroundNode node, RunwayInfo runway) =>
        (node.Type == GroundNodeType.RunwayHoldShort) && (node.RunwayId is { } nodeRunway) && nodeRunway.Overlaps(runway.Id);

    /// <summary>
    /// Whether <paramref name="lead"/> is rolling for takeoff (<see cref="TakeoffPhase"/>) from a departure bar the follower takes
    /// as its own (<see cref="TakesLeadDepartureBar"/>): it has left the ground by departing as far as the follow is concerned
    /// (<see cref="TickLeadGone"/>). A follower with its own departure runway keeps following until the lead lifts off.
    /// </summary>
    private bool IsRollingForTakeoffFromKnownBar(PhaseContext ctx, AircraftState lead) =>
        (_leadDepartureBar is { } latch) && (lead.Phases?.CurrentPhase is TakeoffPhase) && TakesLeadDepartureBar(ctx, latch.NodeId);
}
