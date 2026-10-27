using System.Text.Json;
using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Soak;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// The SFO staging and assertions the <see cref="FollowGroundAtRunwayBarTests"/> classes share.
/// </summary>
public abstract class FollowGroundAtRunwayBarTestBase(ITestOutputHelper output)
{
    protected ITestOutputHelper Output { get; } = output;

    private protected const string Type = "B738";
    private protected const string Follower = "FOL1";
    private protected const string Leader = "LED1";
    private protected const string TaxiToOneRightBar = "TAXI A F1 F RWY 28L CROSS 1L HS 1R";
    private protected const string TaxiToOneLeftBar = "TAXI A F1 F RWY 28L";
    private protected const int StageBudgetSeconds = 600;
    private protected const int HeldSeconds = 20;
    private protected const int CrossBudgetSeconds = 180;
    private protected const int DepartureBarBudgetSeconds = 600;
    private protected const int SnapshotCompareSeconds = 60;

    /// <summary>
    /// Captures the engine, restores it into a second SFO engine, and ticks both side by side: the restored follower
    /// must be in the same phase at the same position every second.
    /// </summary>
    private protected void AssertRestoredGoesOnAsTheOriginal(SfoGround ground, AircraftState follower)
    {
        StateSnapshotDto snapshot = ground.Engine.CaptureSnapshot();
        SfoGround restoredGround = Assert.IsType<SfoGround>(SfoGroundHarness.Build(Output, autoCrossMode: AutoCrossRunwayMode.Off));
        restoredGround.Engine.RestoreFromSnapshot(snapshot);
        AircraftState restored = Assert.IsType<AircraftState>(restoredGround.Engine.World.GetSnapshot().FirstOrDefault(a => a.Callsign == Follower));

        for (int second = 1; second <= SnapshotCompareSeconds; second++)
        {
            ground.Engine.TickOneSecond();
            restoredGround.Engine.TickOneSecond();
            Assert.Equal(follower.Phases?.CurrentPhase?.Name, restored.Phases?.CurrentPhase?.Name);
            Assert.Equal(follower.Position, restored.Position);
        }

        Output.WriteLine($"after {SnapshotCompareSeconds}s: {follower.Phases?.CurrentPhase?.Name} at {follower.Position}");
    }

    /// <summary>
    /// Stages the follow of
    /// <see cref="FollowGroundAtRunwayBarWaitTests.FollowGThenCrossOneLeftOneRight_LeaderRoutedAheadAfterTheRelease_WaitsAtTheOneRightBar"/>
    /// driving up to the 1R bar with a cached verdict that it waits there for its leader. Null on the repo's silent-skip path.
    /// </summary>
    private protected (SfoGround Ground, AircraftState Follower, AircraftState Leader)? StageCachedWaitVerdict()
    {
        if (StageBehind(TaxiToOneLeftBar, "1L", "TAXI A F", Type) is not { } staged)
        {
            return null;
        }

        (SfoGround ground, AircraftState follower, AircraftState leader) = staged;
        int restAt = SfoGroundHarness.TickUntil(ground.Engine, () => IsAtRestWithNoRouteLeft(leader), CrossBudgetSeconds, null);
        Assert.True(restAt >= 0, $"{Leader} never came to rest with no route left: {leader.Phases?.CurrentPhase?.Name}");
        AssertSent(ground, Follower, $"FOLLOWG {Leader}; CROSS 1L 1R");
        AssertSent(ground, Leader, "TAXI F 28L");
        int cachedAt = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => (follower.Phases?.CurrentPhase is FollowingPhase follow) && follow.LeadWaitVerdicts.Values.Contains(true),
            CrossBudgetSeconds,
            second => LogFollow(second, follower, leader)
        );
        Assert.True(cachedAt > 0, $"the follow never cached a wait verdict driving up to 1R: {follower.Phases?.CurrentPhase?.Name}");
        return staged;
    }

    /// <summary>
    /// Whether <paramref name="leader"/> stands past the merge of <paramref name="follower"/>'s follow with its tail not yet clear
    /// of it.
    /// </summary>
    private protected static bool LeaderPastTheMergeTailNotClear(SfoGround ground, AircraftState follower, AircraftState leader) =>
        (FollowRoutePlanner.Plan(ground.Layout, follower, leader, () => []) is FollowRoutePlan.Joinable { MergeAheadOfLead: false } plan)
        && (FollowRoutePlanner.LocateOnPath(ground.Layout, plan.LeadPathFromMerge, leader) is { } at)
        && !FollowRoutePlanner.LeadTailPastMerge(plan.LeadPathFromMerge, at, leader.AircraftType);

    /// <summary>
    /// Ticks <see cref="HeldSeconds"/> with <paramref name="follower"/> held by <paramref name="hold"/> short of 1R, failing if it
    /// leaves the hold or enters 1R; returns the warnings it raised meanwhile, in order.
    /// </summary>
    private protected static List<string> HoldAtTheBar(SfoGround ground, AircraftState follower, HoldingShortPhase hold)
    {
        RunwayInfo runway1R = SfoGroundHarness.Runway("1R");
        List<string> said = [];
        void Collect(string callsign, string warning)
        {
            if (callsign == Follower)
            {
                said.Add(warning);
            }
        }

        ground.Engine.WarningEmitted += Collect;
        for (int second = 1; second <= HeldSeconds; second++)
        {
            ground.Engine.TickOneSecond();
            Assert.Same(hold, follower.Phases?.CurrentPhase);
            Assert.False(RunwayOccupancy.IsOnPavement(follower, runway1R), $"t={second}s: entered 1R while holding for the leader");
        }

        ground.Engine.WarningEmitted -= Collect;
        return said;
    }

    /// <summary>
    /// Stages a follow armed at the 1R bar on F1 behind a leader held short of 1L on F (<see cref="LeaderDownF"/>), then
    /// <c>CROSS 1R</c>. Null on the repo's silent-skip path.
    /// </summary>
    private protected (SfoGround Ground, AircraftState Follower, AircraftState Leader)? StageNoRoomAtOneRight()
    {
        if (StageBehind(TaxiToOneRightBar, "1R", LeaderDownF, Type) is not { } staged)
        {
            return null;
        }

        AssertSent(staged.Ground, Follower, $"FOLLOWG {Leader}");
        FollowRoutePlan.Joinable plan = Assert.IsType<FollowRoutePlan.Joinable>(
            FollowRoutePlanner.Plan(staged.Ground.Layout, staged.Follower, staged.Leader, () => [])
        );
        GroundNode nearBar = staged.Ground.Layout.Nodes[Assert.IsType<HoldingShortPhase>(staged.Follower.Phases?.CurrentPhase).HoldShort.NodeId];
        double? roomFt = FollowingPhase.RoomBeyondFarBarFt(staged.Follower, plan, staged.Leader, new FollowingPhase.BarCrossing(nearBar, []));
        Output.WriteLine($"merge at node {plan.MergeNode} aheadOfLead={plan.MergeAheadOfLead}; room beyond 1R {roomFt?.ToString("F0") ?? "none"} ft");
        Assert.True(plan.MergeAheadOfLead, $"the merge at node {plan.MergeNode} is not ahead of the held leader");
        // No room: the tail would stop short of the far bar, or the give-way stop itself lies short of it (no far bar before it).
        Assert.True(roomFt is null or < 0.0, $"the follower fits beyond 1R ({roomFt:F0} ft of room)");
        AssertSent(staged.Ground, Follower, "CROSS 1R");
        return staged;
    }

    /// <summary>The leader's taxi from spot 1 down F, across 1L and 1R, held short of 1L: F meets the follower's F1 past 1R.</summary>
    private protected const string LeaderDownF = "TAXI A F RWY 28L";

    /// <summary>
    /// 1R's far bar on the follower's route to the merge with <paramref name="leader"/>'s path: the last bar of 1R on it.
    /// </summary>
    private protected static GroundNode FarBarOfOneRight(SfoGround ground, AircraftState follower, AircraftState leader)
    {
        FollowRoutePlan.Joinable plan = Assert.IsType<FollowRoutePlan.Joinable>(FollowRoutePlanner.Plan(ground.Layout, follower, leader, () => []));
        RunwayInfo runway1R = SfoGroundHarness.Runway("1R");
        return plan
            .PathToMerge.Segments.Select(s => s.Edge.ToNode)
            .Last(n => (n.Type == GroundNodeType.RunwayHoldShort) && (n.RunwayId is { } id) && id.Overlaps(runway1R.Id));
    }

    /// <summary>
    /// Fails unless the follower is following, giving way and at rest with its tail clear past 1R's <paramref name="farBar"/>
    /// and off 1R's pavement and hold line, its route short of the merge and its centre at the give-way stop (within
    /// <see cref="GiveWayRestToleranceFt"/> of it).
    /// </summary>
    private protected void AssertWaitingBeyondOneRight(SfoGround ground, AircraftState follower, AircraftState leader, GroundNode farBar)
    {
        RunwayInfo runway1R = SfoGroundHarness.Runway("1R");
        FollowingPhase follow = Assert.IsType<FollowingPhase>(follower.Phases?.CurrentPhase);
        TaxiRoute route = Assert.IsType<TaxiRoute>(follow.FollowRoute);
        Assert.True(follow.IsGivingWay, "the follower beyond 1R is not giving way to its leader");
        Assert.True(follower.GroundSpeed < SfoGroundHarness.StationarySpeedKts, $"the follower is rolling at {follower.GroundSpeed:F1} kt");
        Assert.True(
            FollowingPhase.IsClearPastBar(follower, runway1R, farBar),
            $"the follower's tail is not clear past 1R's far bar (node {farBar.Id})"
        );
        Assert.False(RunwayOccupancy.IsOnPavement(follower, runway1R), "the follower waits on 1R");
        Assert.False(FollowingPhase.IsInsideHoldLine(ground.Layout, runway1R, follower.Position), "the follower waits inside 1R's hold line");
        Assert.True(route.CurrentSegmentIndex < follow.MergeSegmentIndex, "the follower is past the merge");

        FollowRoutePlan.Joinable plan = Assert.IsType<FollowRoutePlan.Joinable>(FollowRoutePlanner.Plan(ground.Layout, follower, leader, () => []));
        List<DirectionalEdge> track = plan.LeadEdgeIntoMerge is { } into ? [into, plan.LeadPathFromMerge[0]] : [plan.LeadPathFromMerge[0]];
        (int NodeId, double ToStopFt)? stop = GroundConflictDetector.GiveWayStop(follower, plan.PathToMerge, track, leader, out string? why);
        Assert.True(stop is not null, $"no give-way stop for the follower waiting beyond 1R: {why}");
        Output.WriteLine($"waiting beyond 1R: {stop.Value.ToStopFt:F0} ft to the give-way stop at node {stop.Value.NodeId}");
        Assert.InRange(stop.Value.ToStopFt, 0.0, GiveWayRestToleranceFt);
    }

    /// <summary>How far (ft) short of its give-way stop a follower at rest there may stand.</summary>
    private protected const double GiveWayRestToleranceFt = 30.0;

    /// <summary>
    /// Whether the follower has left its give-way stop (rolls again), failing when it does before <paramref name="leader"/>'s tail
    /// is past the merge of <paramref name="plan"/>.
    /// </summary>
    private protected static bool AssertLeavesTheGiveWayStopOnlyBehindTheLeader(
        SfoGround ground,
        FollowRoutePlan.Joinable plan,
        AircraftState follower,
        AircraftState leader,
        int second
    )
    {
        if (follower.GroundSpeed < SfoGroundHarness.StationarySpeedKts)
        {
            return false;
        }

        bool tailPast =
            (FollowRoutePlanner.LocateOnPath(ground.Layout, plan.LeadPathFromMerge, leader) is { } at)
            && FollowRoutePlanner.LeadTailPastMerge(plan.LeadPathFromMerge, at, leader.AircraftType);
        Assert.True(tailPast, $"+{second}s: the follower left its give-way stop before {Leader}'s tail passed the merge at node {plan.MergeNode}");
        return true;
    }

    /// <summary>How long (s) the waiting follower is ticked with its leader held.</summary>
    private protected const int WaitSeconds = 120;

    /// <summary>
    /// Stages a follow armed at the 1R bar behind a held leader (<c>HOLD</c>, <c>FOLLOWG</c>, <c>CROSS 1R</c>), the merge of
    /// their routes ahead of the leader. Null on the repo's silent-skip path.
    /// </summary>
    private protected (SfoGround Ground, AircraftState Follower, AircraftState Leader)? StageWaitingAtOneRight()
    {
        if (Stage(TaxiToOneRightBar, "1R") is not { } staged)
        {
            return null;
        }

        AssertSent(staged.Ground, Leader, "HOLD");
        AssertSent(staged.Ground, Follower, $"FOLLOWG {Leader}");
        AssertSent(staged.Ground, Follower, "CROSS 1R");
        return staged;
    }

    /// <summary>
    /// Stops the leader with no route left: <paramref name="taxi"/> names no destination, so it ends held short of its last
    /// taxiway (<see cref="ToFJunction"/>), and the leader is ticked to rest there — with no route left, or stopped in the
    /// route-incomplete hold its route ends in, the route still active. A follower then has
    /// only the leader's trail and the edge it stands on to join — a path guessed from where it stands — so it gives way at the
    /// merge past the bar rather than waiting short of the runway.
    /// </summary>
    private protected void StandLeaderWithNoRoute(SfoGround ground, AircraftState leader, string taxi)
    {
        AssertSent(ground, Leader, taxi);
        int restAt = SfoGroundHarness.TickUntil(ground.Engine, () => IsAtRestWithNoRouteLeft(leader), CrossBudgetSeconds, null);
        Output.WriteLine($"{Leader} at rest with no route at +{restAt}s: {leader.Phases?.CurrentPhase?.Name} taxiway={leader.Ground.CurrentTaxiway}");
        Assert.True(restAt > 0, $"{Leader} never came to rest with no route left within {CrossBudgetSeconds}s of '{taxi}'");
    }

    /// <summary>
    /// Whether <paramref name="leader"/> is stopped with no route left to drive: none at all, a completed one, or one that ends in
    /// the route-incomplete hold it is holding at (a clearance with no destination, its route still active).
    /// </summary>
    private protected static bool IsAtRestWithNoRouteLeft(AircraftState leader) =>
        (
            (FollowingPhase.DrivenRouteOf(leader) is null or { IsComplete: true })
            || (leader.Phases?.CurrentPhase is HoldingShortPhase { HoldShort.Reason: HoldShortReason.RouteIncomplete })
        ) && (leader.GroundSpeed < SfoGroundHarness.StationarySpeedKts);

    /// <summary>The leader's taxi along L and LF that ends held short of F, short of the merge of its path with the follower's.</summary>
    private protected const string ToFJunction = "TAXI L LF F";

    private protected static readonly LatLon Origin = new(37.62, -122.38);

    /// <summary>
    /// A B738 lead at rest facing north dead ahead of <paramref name="follower"/>, which faces north at <see cref="Origin"/>,
    /// <paramref name="noseToTailFt"/> from the follower's nose to the lead's tail.
    /// </summary>
    private protected static AircraftState DeadAhead(AircraftState follower, double noseToTailFt)
    {
        double centresFt = (AircraftLength.ResolveFt(Type) / 2.0) + noseToTailFt + (AircraftLength.ResolveFt(Type) / 2.0);
        AircraftState lead = OnGround("LED9", GeoMath.ProjectPoint(Origin, new TrueHeading(0.0), centresFt / GeoMath.FeetPerNm), 0.0, 0.0);
        Assert.Equal(noseToTailFt, FollowingPhase.NoseToTailFt(follower, lead), 0.5);
        return lead;
    }

    private protected static AircraftState OnGround(string callsign, LatLon position, double headingDeg, double speedKts) =>
        new()
        {
            Callsign = callsign,
            AircraftType = Type,
            Position = position,
            TrueHeading = new TrueHeading(headingDeg),
            IsOnGround = true,
            IndicatedAirspeed = speedKts,
        };

    /// <summary>
    /// Re-routes <paramref name="leader"/> with <paramref name="reroute"/> while <paramref name="follower"/> rolls on
    /// <paramref name="route"/>'s current segment, then ticks it: while it moves on that incoming edge its heading stays within
    /// 90° of the edge's bearing, its follow route never drives the edge back, and it ends at rest or on a route ahead of it.
    /// A follower cannot stop at once, so its follow route may carry it onto an edge the leader's route runs against only while
    /// it brakes to rest (<see cref="HeadOnRun"/>).
    /// </summary>
    private protected void AssertNeverReversesOnItsOwnEdge(
        SfoGround ground,
        AircraftState follower,
        AircraftState leader,
        TaxiRoute route,
        Action<SfoGround, AircraftState, AircraftState> reroute
    )
    {
        DirectionalEdge incoming = route.Segments[route.CurrentSegmentIndex].Edge;
        double incomingDeg = incoming.ArrivalBearing;
        double rerouteKts = follower.GroundSpeed;
        reroute(ground, leader, follower);
        HashSet<(int, int)> againstLead = [.. (leader.Ground.AssignedTaxiRoute?.Segments ?? []).Select(s => (s.ToNodeId, s.FromNodeId))];
        TaxiRouteSegment? onAtReroute = (follower.Phases?.CurrentPhase as FollowingPhase)?.FollowRoute?.CurrentSegment;
        var headOn = new HeadOnRun(againstLead, onAtReroute, rerouteKts);
        double worstDeg = 0.0;
        for (int second = 1; second <= HeldSeconds; second++)
        {
            double beforeKts = follower.GroundSpeed;
            ground.Engine.TickOneSecond();
            LogFollow(second, follower, leader);
            headOn.Observe(second, follower, beforeKts);
            double offDeg = Math.Abs(GeoMath.SignedBearingDifference(follower.TrueHeading.Degrees, incomingDeg));
            bool onIncoming =
                GeoMath.DistanceToSegmentFt(follower.Position, incoming.FromNode.Position, incoming.ToNode.Position) <= OnIncomingEdgeFt;
            Output.WriteLine($"   heading {follower.TrueHeading.Degrees:F0}° ({offDeg:F0}° off the incoming edge), on it: {onIncoming}");
            if (onIncoming && (follower.GroundSpeed >= SfoGroundHarness.StationarySpeedKts))
            {
                worstDeg = Math.Max(worstDeg, offDeg);
            }

            TaxiRouteSegment? driving = (follower.Phases?.CurrentPhase as FollowingPhase)?.FollowRoute?.CurrentSegment;
            Assert.False(
                (driving is not null) && (driving.FromNodeId == incoming.ToNodeId) && (driving.ToNodeId == incoming.FromNodeId),
                $"+{second}s: the follow route drives its incoming edge #{incoming.FromNodeId}-#{incoming.ToNodeId} back"
            );
        }

        headOn.AssertBrakedToRest(Output);
        Output.WriteLine($"heading off the incoming edge's {incomingDeg:F0}° by at most {worstDeg:F0}° while moving on it");
        Assert.True(worstDeg <= 90.0, $"the follower turned about: its heading swung {worstDeg:F0}° off its incoming edge while moving on it");
        bool atRest = follower.GroundSpeed < SfoGroundHarness.StationarySpeedKts;
        TaxiRouteSegment? ahead = (follower.Phases?.CurrentPhase as FollowingPhase)?.FollowRoute?.CurrentSegment;
        bool forward =
            (ahead is not null) && (Math.Abs(GeoMath.SignedBearingDifference(follower.TrueHeading.Degrees, ahead.Edge.ArrivalBearing)) <= 90.0);
        Assert.True(atRest || forward, $"the follower ends rolling at {follower.GroundSpeed:F1} kt with no route ahead of it");
    }

    /// <summary>How far (ft) off its incoming edge's line a follower still counts as on that edge.</summary>
    private protected const double OnIncomingEdgeFt = 10.0;

    /// <summary>
    /// A follower's run onto an edge its leader's route runs against (<paramref name="againstLead"/>, directed moves), after the
    /// leader's re-route, other than the segment it was on at the re-route (<paramref name="onAtReroute"/>). It may enter one only
    /// braking to rest along a lost route at the firm rate (<see cref="CategoryPerformance.ExpediteExitDecelRate"/>) from the
    /// re-route on, as far as its stop at that rate from <paramref name="rerouteKts"/> carries it plus one sub-tick's travel; it
    /// comes to rest, and does not move on from there until its follow is released onto a route again.
    /// </summary>
    private protected sealed class HeadOnRun(HashSet<(int, int)> againstLead, TaxiRouteSegment? onAtReroute, double rerouteKts)
    {
        private const double RestDriftFt = 1.0;
        private LatLon? _entry;
        private LatLon? _rest;
        private double _againstFt;
        private double _firstDropKts;
        private bool _rested;

        public void Observe(int second, AircraftState follower, double beforeKts)
        {
            if (second == 1)
            {
                _firstDropKts = beforeKts - follower.GroundSpeed;
            }

            var follow = follower.Phases?.CurrentPhase as FollowingPhase;
            TaxiRouteSegment? driving = follow?.FollowRoute?.CurrentSegment;
            if ((driving is not null) && !IsOnAtReroute(driving) && againstLead.Contains((driving.FromNodeId, driving.ToNodeId)))
            {
                Assert.True(follow!.IsBrakingLostRoute, $"+{second}s: drives #{driving.FromNodeId}-#{driving.ToNodeId} head-on, not braking to rest");
                _entry ??= driving.Edge.FromNode.Position;
            }

            if (_entry is { } entry)
            {
                _againstFt = Math.Max(_againstFt, GeoMath.DistanceNm(entry, follower.Position) * GeoMath.FeetPerNm);
            }

            bool released = follow?.FollowRoute is not null;
            if (released || (follower.GroundSpeed >= SfoGroundHarness.StationarySpeedKts))
            {
                Assert.True(released || (_rest is null), $"+{second}s: moved on from rest at {follower.GroundSpeed:F1} kt with no route");
                _rest = null;
                return;
            }

            _rest ??= follower.Position;
            _rested |= _entry is not null;
            double driftFt = GeoMath.DistanceNm(_rest.Value, follower.Position) * GeoMath.FeetPerNm;
            Assert.True(driftFt <= RestDriftFt, $"+{second}s: drifted {driftFt:F1} ft from rest with no route");
        }

        public void AssertBrakedToRest(ITestOutputHelper output)
        {
            double firmRate = CategoryPerformance.ExpediteExitDecelRate(AircraftCategory.Jet);
            double feetPerSecondPerKt = GeoMath.FeetPerNm / 3600.0;
            double boundFt =
                ((rerouteKts * rerouteKts) / (2.0 * firmRate) * feetPerSecondPerKt)
                + (rerouteKts * feetPerSecondPerKt / SimulationEngine.PhysicsSubTickRate);
            output.WriteLine(
                $"head-on: drove {_againstFt:F1} ft against the leader's route (bound {boundFt:F1} ft from {rerouteKts:F1} kt), "
                    + $"first-second drop {_firstDropKts:F2} kt (firm rate {firmRate:F1} kt/s)"
            );
            if (_entry is null)
            {
                return;
            }

            Assert.True(_firstDropKts >= firmRate - FirmRateSlackKts, $"braked only {_firstDropKts:F2} kt in the first second, below the firm rate");
            Assert.True(_againstFt <= boundFt, $"drove {_againstFt:F1} ft against the leader's route, past its {boundFt:F1} ft firm stop");
            Assert.True(_rested, "never came to rest after driving onto the leader's route");
        }

        private bool IsOnAtReroute(TaxiRouteSegment driving) =>
            (onAtReroute is not null) && (driving.FromNodeId == onAtReroute.FromNodeId) && (driving.ToNodeId == onAtReroute.ToNodeId);
    }

    /// <summary>How far (kt) below the firm rate a first second of braking may fall, the brake applying from the re-route's first sub-tick.</summary>
    private protected const double FirmRateSlackKts = 0.25;

    /// <summary>
    /// Clears <paramref name="leader"/> on the first re-route it accepts whose re-planned follow joins its path at a node behind
    /// <paramref name="follower"/> (more than 90° off its heading), where the follower has already been, or that the follower
    /// could reach only head-on down the leader's path (<see cref="FollowRoutePlan.FollowerAhead"/>). The leader holds short of F
    /// on LF facing east, so its re-route carries on the way it faces: <c>TAXI F N</c> joins F at the LF merge, behind a
    /// follower already on F.
    /// </summary>
    private protected void RerouteBehindTheFollower(SfoGround ground, AircraftState leader, AircraftState follower)
    {
        string[] taxis = ["TAXI F N"];
        foreach (string taxi in taxis)
        {
            CommandResult result = ground.Engine.SendCommand(Leader, taxi);
            FollowRoutePlan plan = FollowRoutePlanner.Replan(ground.Layout, follower, leader, () => []);
            bool behind =
                (plan is FollowRoutePlan.Joinable joinable)
                && ground.Layout.Nodes.TryGetValue(joinable.MergeNode, out GroundNode? merge)
                && (
                    Math.Abs(GeoMath.SignedBearingDifference(follower.TrueHeading.Degrees, GeoMath.BearingTo(follower.Position, merge.Position)))
                    > 90.0
                );
            Output.WriteLine(
                $"{Leader} <- '{taxi}' -> success={result.Success} msg={result.Message} route={leader.Ground.AssignedTaxiRoute?.ToSummary()} "
                    + $"replan={plan.GetType().Name} mergeBehind={behind}"
            );
            if (result.Success && (behind || (plan is FollowRoutePlan.FollowerAhead)))
            {
                return;
            }
        }

        Assert.Fail($"none of [{string.Join(", ", taxis)}] put {Leader}'s path behind {Follower}");
    }

    /// <summary>
    /// Stages <see cref="StageRolling"/>'s crossing from the 1L bar under <c>CROSS 1L 1R</c> instead, the follower a
    /// <paramref name="followerType"/>, and ticks until it rolls on its follow route between 1L and 1R, inside neither's hold
    /// line, where braking at the taxi rate would carry its nose inside 1R's hold line: with
    /// <paramref name="firmStopMakesTheLine"/> where the firm rate stops it short, else where even the firm rate would not.
    /// Then re-routes the leader along F1 through it, losing the route.
    /// </summary>
    private protected (SfoGround Ground, AircraftState Follower, AircraftState Leader)? StageLostApproachingOneRight(
        bool firmStopMakesTheLine,
        string followerType
    )
    {
        if (StageBehind(TaxiToOneLeftBar, "1L", "TAXI A L F 28L", Type, followerType) is not { } staged)
        {
            return null;
        }

        (SfoGround ground, AircraftState follower, AircraftState leader) = staged;
        RunwayInfo runway1R = SfoGroundHarness.Runway("1R");
        StandLeaderWithNoRoute(ground, leader, ToFJunction);
        AssertSent(ground, Follower, $"FOLLOWG {Leader}; CROSS 1L 1R");
        int lostAt = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => IsLosingTheLineAt(ground.Layout, runway1R, follower, firmStopMakesTheLine),
            CrossBudgetSeconds,
            second =>
            {
                LogFollow(second, follower, leader);
                Output.WriteLine(
                    $"   nose+taxi stop inside 1R: {NoseStopInside(ground.Layout, runway1R, follower, TaxiStopFt(follower))} "
                        + $"nose+firm stop inside 1R: {NoseStopInside(ground.Layout, runway1R, follower, FirmStopFt(follower))}"
                );
            }
        );
        string where = firmStopMakesTheLine ? "the firm rate stops it short of" : "even the firm rate carries it inside";
        Assert.True(lostAt > 0, $"the follower never rolled to where {where} 1R's hold line within {CrossBudgetSeconds}s of CROSS");
        RerouteThroughTheFollower(ground, leader, follower);
        return (ground, follower, leader);
    }

    /// <summary>
    /// Whether <paramref name="follower"/> rolls at <see cref="LostRouteMinSpeedKts"/> or more on its follow route, inside no
    /// runway's hold line, with its taxi-rate stop carrying its nose inside <paramref name="runway"/>'s hold line, and its firm-rate
    /// stop short of it (<paramref name="firmStopMakesTheLine"/>) or, by <see cref="CrossedBarMarginFt"/> or more, inside it.
    /// </summary>
    private protected static bool IsLosingTheLineAt(AirportGroundLayout layout, RunwayInfo runway, AircraftState follower, bool firmStopMakesTheLine)
    {
        if (
            (follower.Phases?.CurrentPhase is not FollowingPhase { FollowRoute.IsComplete: false })
            || (follower.GroundSpeed < LostRouteMinSpeedKts)
            || (FollowingPhase.RunwayInsideHoldLine(follower, layout) is not null)
            || !NoseStopInside(layout, runway, follower, TaxiStopFt(follower))
        )
        {
            return false;
        }

        return firmStopMakesTheLine
            ? !NoseStopInside(layout, runway, follower, FirmStopFt(follower) + CrossedBarMarginFt)
            : NoseStopInside(layout, runway, follower, FirmStopFt(follower) - CrossedBarMarginFt);
    }

    /// <summary>How far (ft) clear of the hold line a staged firm-rate stop falls, either side, so the phase's own reckoning agrees.</summary>
    private protected const double CrossedBarMarginFt = 3.0;

    /// <summary>
    /// Whether the follower's nose, carried <paramref name="aheadFt"/> along its heading, is inside <paramref name="runway"/>'s hold line.
    /// </summary>
    private protected static bool NoseStopInside(AirportGroundLayout layout, RunwayInfo runway, AircraftState follower, double aheadFt)
    {
        double noseAheadFt = (AircraftLength.ResolveFt(follower.AircraftType) / 2.0) + aheadFt;
        LatLon point = GeoMath.ProjectPoint(follower.Position, follower.TrueHeading, noseAheadFt / GeoMath.FeetPerNm);
        return FollowingPhase.IsInsideHoldLine(layout, runway, point);
    }

    /// <summary>How far (ft) the follower rolls braking to rest at the jet taxi rate.</summary>
    private protected static double TaxiStopFt(AircraftState follower) => StopFt(follower, CategoryPerformance.TaxiDecelRate(AircraftCategory.Jet));

    /// <summary>How far (ft) the follower rolls braking to rest at the jet firm rate.</summary>
    private protected static double FirmStopFt(AircraftState follower) =>
        StopFt(follower, CategoryPerformance.ExpediteExitDecelRate(AircraftCategory.Jet));

    private protected static double StopFt(AircraftState follower, double rateKtsPerSec) =>
        (follower.GroundSpeed * follower.GroundSpeed) / (2.0 * rateKtsPerSec) * GeoMath.FeetPerNm / 3600.0;

    /// <summary>Fails when <paramref name="follower"/> is at rest with any part — nose, centre or tail — inside a runway's hold line.</summary>
    private protected static void AssertNeverAtRestInsideAHoldLine(SfoGround ground, AircraftState follower, int second)
    {
        RunwayInfo? inside = FollowingPhase.RunwayInsideHoldLine(follower, ground.Layout);
        Assert.False(
            (inside is not null) && (follower.GroundSpeed < SfoGroundHarness.StationarySpeedKts),
            $"+{second}s: at rest inside runway {inside?.Id}'s hold line"
        );
    }

    /// <summary>The nodes at the far end of <paramref name="node"/>'s straight edges on <paramref name="taxiway"/>.</summary>
    private protected static IEnumerable<GroundNode> StraightNeighboursOn(GroundNode node, string taxiway) =>
        node.Edges.OfType<GroundEdge>().Where(e => e.MatchesTaxiway(taxiway)).Select(e => e.OtherNode(node));

    /// <summary>How far (ft) off a lost follow route's centreline a follower braking along it may stand.</summary>
    private protected const double LostRouteOffsetFt = 2.0;

    /// <summary>The slowest a follower is rolling (kts) when its route is lost, so it is still braking a tick later.</summary>
    private protected const double LostRouteMinSpeedKts = 8.0;

    /// <summary>
    /// How far round a fillet arc, as a fraction of its length from both ends, a follower must be at a second's end to stand partway
    /// round it: a short arc is crossed in about a second at taxi speed, so a second's end lands in its middle fifth.
    /// </summary>
    private protected const double FilletStageFraction = 0.2;

    /// <summary>How far (ft) from both ends of a straight edge a follower must be to stand on the edge itself, not a node.</summary>
    private protected const double StraightMarginFt = 10.0;

    /// <summary>Speed (kts) below which a follower is at rest.</summary>
    private protected const double RestKts = 0.05;

    /// <summary>The follower, its follow and the follow route a lead re-route lost, with the follower braking along it.</summary>
    private protected sealed record LostRoute(SfoGround Ground, AircraftState Follower, FollowingPhase Follow, TaxiRoute Route);

    /// <summary>
    /// Arms a follow at the 1R bar and crosses 1R, ticks until the follower rolls on its follow route clear of every runway's
    /// hold line — along a straight edge, or with <paramref name="onFillet"/> partway round a fillet arc — then re-routes the
    /// leader along F1 through it and ticks once: the follower is braking along the route it lost.
    /// </summary>
    private protected LostRoute? StageLostRoute(bool onFillet)
    {
        if (StageRolling(onFillet) is not { } rolling)
        {
            return null;
        }

        (SfoGround ground, AircraftState follower, AircraftState leader, FollowingPhase follow, TaxiRoute route) = rolling;
        RerouteThroughTheFollower(ground, leader, follower);
        ground.Engine.TickOneSecond();
        LogFollow(0, follower, leader);
        Assert.True(follow.IsBrakingLostRoute, "the follower did not brake along the follow route its leader's re-route lost");
        Assert.Same(route, follow.FollowRoute);
        return new LostRoute(ground, follower, follow, route);
    }

    /// <summary>
    /// Arms a follow at the 1R bar behind a leader standing at the LF/F junction with no route left (<see cref="StandLeaderWithNoRoute"/>)
    /// and crosses 1R, then ticks until the follower rolls on its follow route
    /// clear of every runway's hold line — along a straight edge, or with <paramref name="onFillet"/> partway round a fillet arc.
    /// </summary>
    private protected (SfoGround Ground, AircraftState Follower, AircraftState Leader, FollowingPhase Follow, TaxiRoute Route)? StageRolling(
        bool onFillet
    )
    {
        if (Stage(TaxiToOneRightBar, "1R") is not { } staged)
        {
            return null;
        }

        (SfoGround ground, AircraftState follower, AircraftState leader) = staged;
        StandLeaderWithNoRoute(ground, leader, ToFJunction);
        AssertSent(ground, Follower, $"FOLLOWG {Leader}");
        AssertSent(ground, Follower, "CROSS 1R");
        int rollingAt = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => IsRollingOn(ground.Layout, follower, onFillet),
            CrossBudgetSeconds,
            second => LogFollow(second, follower, leader)
        );
        string where = onFillet ? "partway round a fillet" : "along a straight edge";
        Assert.True(rollingAt > 0, $"the follower never rolled {where} clear of every hold line within {CrossBudgetSeconds}s of CROSS");
        FollowingPhase follow = Assert.IsType<FollowingPhase>(follower.Phases?.CurrentPhase);
        TaxiRoute route = Assert.IsType<TaxiRoute>(follow.FollowRoute);
        return (ground, follower, leader, follow, route);
    }

    /// <summary>
    /// Whether <paramref name="follower"/> rolls at <see cref="LostRouteMinSpeedKts"/> or more on its follow route short of the
    /// merge, inside no runway's hold line, <see cref="StraightMarginFt"/> or more from both ends of a straight edge, or with
    /// <paramref name="onFillet"/> <see cref="FilletStageFraction"/> or more of the way round a fillet arc from both its ends.
    /// </summary>
    private protected static bool IsRollingOn(AirportGroundLayout layout, AircraftState follower, bool onFillet)
    {
        if (
            (follower.Phases?.CurrentPhase is not FollowingPhase { FollowRoute: { IsComplete: false } route } follow)
            || (route.CurrentSegmentIndex >= follow.MergeSegmentIndex)
            || (follower.GroundSpeed < LostRouteMinSpeedKts)
            || (FollowingPhase.RunwayInsideHoldLine(follower, layout) is not null)
        )
        {
            return false;
        }

        DirectionalEdge edge = route.Segments[route.CurrentSegmentIndex].Edge;
        double fromFt = GeoMath.DistanceNm(follower.Position, edge.FromNode.Position) * GeoMath.FeetPerNm;
        double toFt = GeoMath.DistanceNm(follower.Position, edge.ToNode.Position) * GeoMath.FeetPerNm;
        double edgeFt = edge.DistanceNm * GeoMath.FeetPerNm;
        return onFillet
            ? (edge.Edge is GroundArc) && (Math.Min(fromFt, toFt) >= edgeFt * FilletStageFraction)
            : (edge.Edge is GroundEdge) && (Math.Min(fromFt, toFt) >= StraightMarginFt);
    }

    /// <summary>
    /// Ticks <paramref name="lost"/>'s follower to rest, asserting its centre stays within <see cref="LostRouteOffsetFt"/> of the
    /// lost route's centreline every second, and that it drops the route once at rest.
    /// </summary>
    private protected void AssertBrakesAlongTheLostRouteToRest(LostRoute lost)
    {
        AircraftState follower = lost.Follower;
        double worstFt = FollowGroundOnGraphTests.OffRouteFt(lost.Route, follower.Position);
        int restAt = SfoGroundHarness.TickUntil(
            lost.Ground.Engine,
            () => follower.GroundSpeed < RestKts,
            HeldSeconds,
            second =>
            {
                double offFt = FollowGroundOnGraphTests.OffRouteFt(lost.Route, follower.Position);
                worstFt = Math.Max(worstFt, offFt);
                Output.WriteLine($"+{second}s gs={follower.GroundSpeed:F2} off route {offFt:F2} ft braking={lost.Follow.IsBrakingLostRoute}");
            }
        );

        Assert.True(restAt > 0, $"the follower is still rolling at {follower.GroundSpeed:F1} kt {HeldSeconds}s after losing its route");
        Assert.True(worstFt <= LostRouteOffsetFt, $"the follower's centre went {worstFt:F2} ft off the route it lost");
        Assert.False(lost.Follow.IsBrakingLostRoute, "the follower at rest still holds the route it lost");
        Assert.Null(lost.Follow.FollowRoute);
    }

    /// <summary>
    /// Clears <paramref name="leader"/> on the first re-route along F1 it accepts that leaves <paramref name="follower"/> nothing to
    /// join: a re-plan of the follow finds it ahead of the leader on its new route, no taxi path to it, or only a route whose
    /// first edge leaves back across the follower's heading, which the follow does not adopt after a lead re-route. The leader
    /// holds short of F on LF facing east, and F1 lies behind it, so it reaches F1 the way it faces: east on F, round by P and
    /// C, south on L and west along F1 through the follower (<c>TAXI F P C L F1 A</c>).
    /// </summary>
    private protected void RerouteThroughTheFollower(SfoGround ground, AircraftState leader, AircraftState follower)
    {
        string[] taxis = ["TAXI F P C L F1 A"];
        foreach (string taxi in taxis)
        {
            CommandResult result = ground.Engine.SendCommand(Leader, taxi);
            FollowRoutePlan plan = FollowRoutePlanner.Replan(ground.Layout, follower, leader, () => []);
            Output.WriteLine(
                $"{Leader} <- '{taxi}' -> success={result.Success} msg={result.Message} route={leader.Ground.AssignedTaxiRoute?.ToSummary()} "
                    + $"replan={plan.GetType().Name}"
            );
            if (result.Success && ((plan is FollowRoutePlan.FollowerAhead or FollowRoutePlan.NoPath) || LeavesBackAcrossHeading(plan, follower)))
            {
                return;
            }
        }

        Assert.Fail($"none of [{string.Join(", ", taxis)}] left {Follower} nothing to join on {Leader}'s new route");
    }

    /// <summary>Whether <paramref name="plan"/> is a joinable route whose first edge leaves back across the follower's heading.</summary>
    private protected static bool LeavesBackAcrossHeading(FollowRoutePlan plan, AircraftState follower)
    {
        if (plan is not FollowRoutePlan.Joinable joinable)
        {
            return false;
        }

        DirectionalEdge? first = joinable.PathToMerge.Segments.FirstOrDefault()?.Edge ?? joinable.LeadPathFromMerge.FirstOrDefault();
        return (first is { } edge) && !FollowRoutePlanner.DepartsAhead(follower, edge);
    }

    /// <summary>One line of the follower's follow state and the leader's phase and speed.</summary>
    private protected void LogFollow(int second, AircraftState follower, AircraftState leader)
    {
        var follow = follower.Phases?.CurrentPhase as FollowingPhase;
        string segment = "-";
        if (follow?.FollowRoute is { IsComplete: false } route)
        {
            DirectionalEdge edge = route.Segments[route.CurrentSegmentIndex].Edge;
            double fromFt = GeoMath.DistanceNm(follower.Position, edge.FromNode.Position) * GeoMath.FeetPerNm;
            double toFt = GeoMath.DistanceNm(follower.Position, edge.ToNode.Position) * GeoMath.FeetPerNm;
            segment =
                $"{route.CurrentSegmentIndex}/{route.Segments.Count} {edge.Edge.GetType().Name} {fromFt:F0}ft in, {toFt:F0}ft to go "
                + $"merge={follow.MergeSegmentIndex} givingWay={follow.IsGivingWay} route={route.ToSummary()}";
        }

        Output.WriteLine(
            $"t={second} {follower.Phases?.CurrentPhase?.Name} gs={follower.GroundSpeed:F1} seg={segment} braking={follow?.IsBrakingLostRoute} "
                + $"clearing={follow?.ClearingRoute is not null} {Leader} {leader.Phases?.CurrentPhase?.Name} gs={leader.GroundSpeed:F1}"
        );
    }

    /// <summary>
    /// The follower stopped with its tail and both wingtips past the hold line of the 1R bar nearest it on its side of 1R
    /// (<see cref="FollowingPhase.IsClearPastBar"/>), and no part of it — nose, centre or tail — inside any runway's hold line, 1L's
    /// included (<see cref="FollowingPhase.RunwayInsideHoldLine"/>).
    /// </summary>
    private protected void AssertStoppedClearPastOneRight(SfoGround ground, AircraftState follower)
    {
        RunwayInfo runway1R = SfoGroundHarness.Runway("1R");
        GroundNode? bar = ground
            .Layout.Nodes.Values.Where(n =>
                (n.Type == GroundNodeType.RunwayHoldShort)
                && (n.RunwayId is { } id)
                && id.Overlaps(runway1R.Id)
                && (RunwaySide(runway1R, n.Position) == RunwaySide(runway1R, follower.Position))
            )
            .MinBy(n => GeoMath.DistanceNm(n.Position, follower.Position));
        Assert.NotNull(bar);
        RunwayInfo? inside = FollowingPhase.RunwayInsideHoldLine(follower, ground.Layout);
        Output.WriteLine($"stopped beside 1R bar #{bar.Id}; inside the hold line of {inside?.Id.ToString() ?? "no runway"}");
        Assert.True(FollowingPhase.IsClearPastBar(follower, runway1R, bar), $"the follower stopped short of 1R bar #{bar.Id}'s hold line");
        Assert.Null(inside);
    }

    /// <summary>Which side of <paramref name="runway"/>'s centreline <paramref name="point"/> lies on: the sign of the cross-track.</summary>
    private protected static bool RunwaySide(RunwayInfo runway, LatLon point)
    {
        LatLon start = new(runway.Lat1, runway.Lon1);
        double along = GeoMath.BearingTo(start, new LatLon(runway.Lat2, runway.Lon2));
        double toPoint = GeoMath.BearingTo(start, point);
        return Math.Sin((toPoint - along) * Math.PI / 180.0) > 0.0;
    }

    /// <summary>
    /// Clears <paramref name="leader"/> on the first of <paramref name="taxis"/> it accepts that puts <paramref name="follower"/>
    /// ahead of it on its new route.
    /// </summary>
    private protected void AssertRerouted(SfoGround ground, AircraftState leader, AircraftState follower, string[] taxis)
    {
        foreach (string taxi in taxis)
        {
            CommandResult result = ground.Engine.SendCommand(Leader, taxi);
            Output.WriteLine(
                $"{Leader} <- '{taxi}' -> success={result.Success} msg={result.Message} route={leader.Ground.AssignedTaxiRoute?.ToSummary()}"
            );
            if (result.Success && (FollowRoutePlanner.Plan(ground.Layout, follower, leader, () => []) is FollowRoutePlan.FollowerAhead))
            {
                return;
            }
        }

        Assert.Fail($"none of [{string.Join(", ", taxis)}] put {Follower} ahead of {Leader} on its route");
    }

    private protected void AssertSent(SfoGround ground, string callsign, string command)
    {
        CommandResult result = ground.Engine.SendCommand(callsign, command);
        Output.WriteLine($"{callsign} <- '{command}' -> success={result.Success} msg={result.Message}");
        Assert.True(result.Success, $"'{command}' to {callsign} was rejected: {result.Message}");
    }

    private protected static void AssertFollowArmedBehindHold(PhaseList phases)
    {
        List<Phase> upcoming = [.. phases.Phases.Skip(phases.CurrentIndex + 1)];
        FollowingPhase follow = Assert.IsType<FollowingPhase>(Assert.Single(upcoming));
        Assert.Equal(Leader, follow.TargetCallsign);
        Assert.Equal(PhaseStatus.Pending, follow.Status);
    }

    /// <summary>
    /// Spawns the leader and the follower, clears both, and ticks until the follower holds short of
    /// <paramref name="barRunway"/> on F1. Null on the repo's silent-skip path (no navdata / no SFO layout).
    /// </summary>
    private protected (SfoGround Ground, AircraftState Follower, AircraftState Leader)? Stage(string followerTaxi, string barRunway) =>
        StageBehind(followerTaxi, barRunway, "TAXI A L F 28L", Type);

    /// <summary>As <see cref="Stage"/>, the leader a <paramref name="leaderType"/> cleared on <paramref name="leaderTaxi"/>.</summary>
    private protected (SfoGround Ground, AircraftState Follower, AircraftState Leader)? StageBehind(
        string followerTaxi,
        string barRunway,
        string leaderTaxi,
        string leaderType
    ) => StageBehind(followerTaxi, barRunway, leaderTaxi, leaderType, Type);

    /// <summary>As <see cref="StageBehind"/>, the follower a <paramref name="followerType"/> too.</summary>
    private protected (SfoGround Ground, AircraftState Follower, AircraftState Leader)? StageBehind(
        string followerTaxi,
        string barRunway,
        string leaderTaxi,
        string leaderType,
        string followerType
    )
    {
        if (SfoGroundHarness.Build(Output, autoCrossMode: AutoCrossRunwayMode.Off) is not { } ground)
        {
            Output.WriteLine("SKIP: SFO layout or navdata unavailable");
            return null;
        }

        AircraftState leader = SfoGroundHarness.SpawnAtSpot(ground, Leader, leaderType, "1");
        AircraftState follower = SfoGroundHarness.SpawnAtSpot(ground, Follower, followerType, "3");
        AssertSent(ground, Follower, followerTaxi);
        ground.Engine.TickOneSecond();
        AssertSent(ground, Leader, leaderTaxi);

        int held = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => (follower.Phases?.CurrentPhase is HoldingShortPhase hold) && SfoGroundHarness.HoldShortMatches(hold.HoldShort, barRunway),
            StageBudgetSeconds,
            null
        );
        Output.WriteLine($"staged at t={held}s: {Follower} {follower.Phases?.CurrentPhase?.Name}");
        Output.WriteLine($"leader: {Leader} {leader.Phases?.CurrentPhase?.Name} gs={leader.GroundSpeed:F1}");
        Assert.True(held > 0, $"{Follower} never held short of {barRunway} within {StageBudgetSeconds}s: {follower.Phases?.CurrentPhase?.Name}");
        return (ground, follower, leader);
    }
}
