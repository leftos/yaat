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
/// FOLLOWG at a runway bar while the lead is rerouted, held short of the merge or at the end of its route.
/// </summary>
public class FollowGroundAtRunwayBarLeadTests(ITestOutputHelper output) : FollowGroundAtRunwayBarTestBase(output)
{
    /// <summary>
    /// At the departure bar a completed taxi route ends at, CROSS takes the aircraft across, so a FOLLOWG there arms
    /// the follow as at any other runway bar.
    /// </summary>
    [Fact]
    public void FollowGAtDepartureBarOfCompletedRoute_ArmsTheFollow()
    {
        if (SfoGroundHarness.Build(Output, autoCrossMode: AutoCrossRunwayMode.Off) is not { } ground)
        {
            Output.WriteLine("SKIP: SFO layout or navdata unavailable");
            return;
        }

        AircraftState departure = SfoGroundHarness.SpawnAtSpot(ground, Leader, Type, "1");
        SfoGroundHarness.SpawnAtSpot(ground, Follower, Type, "3");
        AssertSent(ground, Leader, "TAXI A L F 28L");

        int held = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => (departure.Phases?.CurrentPhase is HoldingShortPhase hold) && SfoGroundHarness.HoldShortMatches(hold.HoldShort, "28L"),
            StageBudgetSeconds,
            null
        );
        Assert.True(held > 0, $"{Leader} never held short of 28L within {StageBudgetSeconds}s: {departure.Phases?.CurrentPhase?.Name}");
        HoldingShortPhase bar = Assert.IsType<HoldingShortPhase>(departure.Phases?.CurrentPhase);
        Assert.Equal(HoldShortReason.DestinationRunway, bar.HoldShort.Reason);
        Assert.True(departure.Ground.AssignedTaxiRoute is null or { IsComplete: true }, "the taxi route should have completed at its departure bar");

        CommandResult follow = ground.Engine.SendCommand(Leader, $"FOLLOWG {Follower}");
        Output.WriteLine($"FOLLOWG {Follower} at the 28L bar -> success={follow.Success} msg={follow.Message}");
        Assert.True(follow.Success, follow.Message);
        Assert.Same(bar, departure.Phases?.CurrentPhase);
        FollowingPhase armed = Assert.IsType<FollowingPhase>(Assert.Single(departure.Phases!.Phases.Skip(departure.Phases.CurrentIndex + 1)));
        Assert.Equal(Follower, armed.TargetCallsign);

        // CROSS releases it there, putting the crossing in front of the armed follow; the route it arrived on stays.
        TaxiRoute? route = departure.Ground.AssignedTaxiRoute;
        AssertSent(ground, Leader, "CROSS 28L");
        List<Phase> upcoming = [.. departure.Phases!.Phases.Skip(departure.Phases.CurrentIndex + 1)];
        Assert.Collection(upcoming, p => Assert.IsType<CrossingRunwayPhase>(p), p => Assert.IsType<FollowingPhase>(p));

        int following = SfoGroundHarness.TickUntil(ground.Engine, () => departure.Phases?.CurrentPhase is FollowingPhase, CrossBudgetSeconds, null);
        Assert.True(following > 0, $"never started following within {CrossBudgetSeconds}s of CROSS 28L: {departure.Phases?.CurrentPhase?.Name}");
        Assert.False(RunwayOccupancy.IsOnPavement(departure, SfoGroundHarness.Runway("28L")), "started following while still on 28L pavement");
        Assert.Same(route, departure.Ground.AssignedTaxiRoute);
    }

    [Fact]
    public void FollowGArmedAtOneRightBar_SurvivesSnapshotRoundTrip()
    {
        if (Stage(TaxiToOneRightBar, "1R") is not { } staged)
        {
            return;
        }

        (SfoGround ground, AircraftState follower, _) = staged;
        AssertSent(ground, Follower, $"FOLLOWG {Leader}");
        int holdNode = Assert.IsType<HoldingShortPhase>(follower.Phases?.CurrentPhase).HoldShort.NodeId;

        StateSnapshotDto snapshot = ground.Engine.CaptureSnapshot();
        SfoGround restoredGround = Assert.IsType<SfoGround>(SfoGroundHarness.Build(Output, autoCrossMode: AutoCrossRunwayMode.Off));
        restoredGround.Engine.RestoreFromSnapshot(snapshot);
        AircraftState restored = Assert.IsType<AircraftState>(restoredGround.Engine.World.GetSnapshot().FirstOrDefault(a => a.Callsign == Follower));

        HoldingShortPhase restoredHold = Assert.IsType<HoldingShortPhase>(restored.Phases?.CurrentPhase);
        Assert.Equal(holdNode, restoredHold.HoldShort.NodeId);
        Assert.True(SfoGroundHarness.HoldShortMatches(restoredHold.HoldShort, "1R"), $"restored hold targets {restoredHold.HoldShort.TargetName}");
        AssertFollowArmedBehindHold(restored.Phases!);
        Assert.NotNull(restored.Ground.AssignedTaxiRoute);

        // The restored aircraft goes on exactly as the original does once CROSS releases the armed hold.
        AssertSent(ground, Follower, "CROSS 1R");
        AssertSent(restoredGround, Follower, "CROSS 1R");
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
    /// A follow armed at the 1R bar whose leader is re-routed before the release onto a route along F1 through the follower's
    /// own position: on <c>CROSS 1R</c> the follower crosses, its follow plans for the first time, finds itself ahead of the
    /// leader on the leader's new route, and holds in position for good, logging once that its route is lost.
    /// </summary>
    [Fact]
    public void FollowGArmedAtOneRightBar_LeaderReroutedThroughTheFollower_HoldsAndLogsRouteLost()
    {
        if (Stage(TaxiToOneRightBar, "1R") is not { } staged)
        {
            return;
        }

        (SfoGround ground, AircraftState follower, AircraftState leader) = staged;
        var tap = new CapturingSimLogProvider(LogLevel.Information, capacity: 500);
        SimLogBuilder.CreateForTest(Output).EnableCategory("FollowingPhase", LogLevel.Information).CaptureInto(tap).InitializeSimLog();
        AssertSent(ground, Follower, $"FOLLOWG {Leader}");
        AssertRerouted(ground, leader, follower, ["TAXI L F F1 A", "TAXI F F1 A", "TAXI A F1 F"]);
        AssertSent(ground, Follower, "CROSS 1R");

        int followingAt = SfoGroundHarness.TickUntil(ground.Engine, () => follower.Phases?.CurrentPhase is FollowingPhase, CrossBudgetSeconds, null);
        Assert.True(followingAt > 0, $"the armed follow never started within {CrossBudgetSeconds}s of CROSS: {follower.Phases?.CurrentPhase?.Name}");
        SfoGroundHarness.TickUntil(ground.Engine, () => follower.GroundSpeed < SfoGroundHarness.StationarySpeedKts, CrossBudgetSeconds, null);
        for (int second = 1; second <= HeldSeconds; second++)
        {
            ground.Engine.TickOneSecond();
        }

        FollowingPhase follow = Assert.IsType<FollowingPhase>(follower.Phases?.CurrentPhase);
        Output.WriteLine($"following at t={followingAt}s; now gs={follower.GroundSpeed:F1} unjoinable={follow.IsUnjoinable}");
        Assert.True(follow.IsUnjoinable, "the follow ahead of its re-routed leader did not hold for good");
        Assert.Null(follow.FollowRoute);
        Assert.True(follower.GroundSpeed < SfoGroundHarness.StationarySpeedKts, $"the follower is still rolling at {follower.GroundSpeed:F1} kt");
        AssertStoppedClearPastOneRight(ground, follower);
        Assert.Single(
            tap.Drain(),
            r =>
                (r.Level == LogLevel.Information)
                && r.Message.Contains(Follower, StringComparison.Ordinal)
                && r.Message.Contains("route lost", StringComparison.Ordinal)
        );
    }

    /// <summary>
    /// A follow under way past 1R whose leader is then re-routed onto a route along F1 through the follower's position: the re-plan
    /// finds the follower ahead of the leader, and it holds for good and says once that it cannot follow and wants taxi
    /// instructions — not again however long it holds.
    /// </summary>
    [Fact]
    public void FollowingPastOneRight_LeaderReroutedThroughTheFollower_SaysUnableOnce()
    {
        if (Stage(TaxiToOneRightBar, "1R") is not { } staged)
        {
            return;
        }

        (SfoGround ground, AircraftState follower, AircraftState leader) = staged;
        AssertSent(ground, Follower, $"FOLLOWG {Leader}");
        AssertSent(ground, Follower, "CROSS 1R");
        // The leader is held on L: the reroute must come while the follower drives its follow route, and a route ahead onto F1
        // exists only from L, before the L/LF junction.
        AssertSent(ground, Leader, "HOLD");
        int drivingAt = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => (follower.Phases?.CurrentPhase is FollowingPhase { FollowRoute.IsComplete: false }) && (follower.GroundSpeed > 3.0),
            CrossBudgetSeconds,
            second => LogFollow(second, follower, leader)
        );
        Assert.True(drivingAt > 0, $"the follow never drove a route within {CrossBudgetSeconds}s of CROSS 1R: {follower.Phases?.CurrentPhase?.Name}");

        List<string> unable = [];
        void Note(string from, string text)
        {
            if ((from == Follower) && text.Contains("no taxi route", StringComparison.OrdinalIgnoreCase))
            {
                unable.Add(text);
            }
        }

        ground.Engine.WarningEmitted += Note;
        ground.Engine.TerminalEntryEmitted += entry => Note(entry.Callsign, entry.Message);
        AssertRerouted(ground, leader, follower, ["TAXI L F F1 A"]);
        for (int second = 1; second <= HeldSeconds * 2; second++)
        {
            ground.Engine.TickOneSecond();
            LogFollow(second, follower, leader);
        }

        FollowingPhase follow = Assert.IsType<FollowingPhase>(follower.Phases?.CurrentPhase);
        Assert.True(follow.IsUnjoinable, "the follow ahead of its re-routed leader did not hold for good");
        Assert.Equal([$"Unable to follow {Leader}, no taxi route to its path, request taxi instructions"], unable);
    }

    /// <summary>
    /// A follow armed at the 1R bar behind a leader held short of where its route meets the follower's (LF onto F at the merge):
    /// the merge lies beyond 1R with room for the follower between 1R's far hold line and its give-way stop, so on
    /// <c>CROSS 1R</c> it crosses and comes to rest there, tail clear past the far hold line and nose short of the give-way stop,
    /// still giving way and never turning down L.
    /// </summary>
    [Fact]
    public void FollowGArmedAtOneRightBar_LeadHeldShortOfTheMerge_CrossesAndWaitsBeyondOneRight()
    {
        if (StageWaitingAtOneRight() is not { } staged)
        {
            return;
        }

        (SfoGround ground, AircraftState follower, AircraftState leader) = staged;
        GroundNode farBar = FarBarOfOneRight(ground, follower, leader);
        for (int second = 1; second <= WaitSeconds; second++)
        {
            ground.Engine.TickOneSecond();
            LogFollow(second, follower, leader);
            Assert.NotEqual("L", follower.Ground.CurrentTaxiway);
        }

        AssertWaitingBeyondOneRight(ground, follower, leader, farBar);
    }

    /// <summary>
    /// The follower waiting beyond 1R for its held leader (as above) passes its give-way stop only once the leader, released, has
    /// its tail past the merge, and falls in behind it: when the leader holds short of 28L both come to rest with the follower's
    /// nose-to-tail gap at its stop gap.
    /// </summary>
    [Fact]
    public void FollowGArmedAtOneRightBar_LeadHeldShortOfTheMerge_ThenClosesBehindItOnRelease()
    {
        if (StageWaitingAtOneRight() is not { } staged)
        {
            return;
        }

        (SfoGround ground, AircraftState follower, AircraftState leader) = staged;
        FollowRoutePlan.Joinable plan = Assert.IsType<FollowRoutePlan.Joinable>(FollowRoutePlanner.Plan(ground.Layout, follower, leader, () => []));
        Assert.True(plan.MergeAheadOfLead, $"the merge at node {plan.MergeNode} is not ahead of the held leader");
        GroundNode farBar = FarBarOfOneRight(ground, follower, leader);
        for (int second = 1; second <= WaitSeconds; second++)
        {
            ground.Engine.TickOneSecond();
            Assert.NotEqual("L", follower.Ground.CurrentTaxiway);
        }

        AssertWaitingBeyondOneRight(ground, follower, leader, farBar);
        AssertSent(ground, Leader, "RES");
        bool passedStop = false;
        int restAt = SfoGroundHarness.TickUntil(
            ground.Engine,
            () =>
                (leader.Phases?.CurrentPhase is HoldingShortPhase hold)
                && SfoGroundHarness.HoldShortMatches(hold.HoldShort, "28L")
                && (leader.GroundSpeed < SfoGroundHarness.StationarySpeedKts)
                && (follower.GroundSpeed < SfoGroundHarness.StationarySpeedKts)
                && (follower.Phases?.CurrentPhase is FollowingPhase),
            DepartureBarBudgetSeconds,
            second =>
            {
                LogFollow(second, follower, leader);
                passedStop = passedStop || AssertLeavesTheGiveWayStopOnlyBehindTheLeader(ground, plan, follower, leader, second);
            }
        );

        AircraftCategory category = AircraftCategorization.Categorize(Type);
        double stopGapFt = FollowGap.StopGapFt(Type, category, Type, category);
        double gapFt = FollowingPhase.NoseToTailFt(follower, leader);
        Output.WriteLine($"both at rest at +{restAt}s: nose-to-tail {gapFt:F0} ft, stop gap {stopGapFt:F0} ft");
        Assert.True(restAt > 0, $"{Leader} and {Follower} never both came to rest with {Leader} short of 28L within {DepartureBarBudgetSeconds}s");
        Assert.True(passedStop, "the follower never left its give-way stop beyond 1R");
        Assert.InRange(gapFt, stopGapFt - 10.0, stopGapFt + 20.0);
    }

    /// <summary>
    /// A follower taxiing between 1L and 1R, given <c>FOLLOWG</c> and <c>CROSS 1R</c> there behind a leader held short of the
    /// merge of their routes beyond 1R, with room to wait between 1R's far hold line and its give-way stop: it crosses 1R and
    /// comes to rest there, and leaves that stop only once the leader, released, has its tail past the merge.
    /// </summary>
    [Fact]
    public void FollowGWhileTaxiing_LeadHeldShortOfTheMerge_CrossesAndWaitsBeyondOneRight()
    {
        if (Stage(TaxiToOneLeftBar, "1L") is not { } staged)
        {
            return;
        }

        (SfoGround ground, AircraftState follower, AircraftState leader) = staged;
        AssertSent(ground, Leader, "HOLD");
        AssertSent(ground, Follower, "CROSS 1L");
        int taxiingAt = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => (follower.Phases?.CurrentPhase is TaxiingPhase) && (FollowingPhase.RunwayInsideHoldLine(follower, ground.Layout) is null),
            CrossBudgetSeconds,
            null
        );
        Assert.True(taxiingAt > 0, $"the follower never taxied on clear of 1L within {CrossBudgetSeconds}s of CROSS 1L");
        AssertSent(ground, Follower, $"FOLLOWG {Leader}; CROSS 1R");
        FollowRoutePlan.Joinable plan = Assert.IsType<FollowRoutePlan.Joinable>(FollowRoutePlanner.Plan(ground.Layout, follower, leader, () => []));
        Assert.True(plan.MergeAheadOfLead, $"the merge at node {plan.MergeNode} is not ahead of the held leader");
        GroundNode farBar = FarBarOfOneRight(ground, follower, leader);

        for (int second = 1; second <= WaitSeconds; second++)
        {
            ground.Engine.TickOneSecond();
            LogFollow(second, follower, leader);
            Assert.NotEqual("L", follower.Ground.CurrentTaxiway);
        }

        AssertWaitingBeyondOneRight(ground, follower, leader, farBar);
        AssertSent(ground, Leader, "RES");
        bool passedStop = false;
        int passedAt = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => passedStop,
            DepartureBarBudgetSeconds,
            second =>
            {
                LogFollow(second, follower, leader);
                passedStop = AssertLeavesTheGiveWayStopOnlyBehindTheLeader(ground, plan, follower, leader, second);
            }
        );
        Assert.True(passedAt > 0, $"the follower never left its give-way stop beyond 1R within {DepartureBarBudgetSeconds}s of {Leader}'s release");
    }
}
