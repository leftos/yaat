using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// <c>CROSS</c> to a <c>FOLLOWG</c> follower: held at a bar on its follow route it crosses along that route; following, a runway
/// on its route ahead is pre-cleared; a runway on neither its follow route nor its lead's path is refused, the follower kept held
/// with its follow armed; a lead with no trail past the bar leaves the shortest crossing from the bar.
/// </summary>
public class FollowGroundCrossOnRouteTests(ITestOutputHelper output) : FollowGroundAtRunwayBarTestBase(output)
{
    /// <summary>The leader's taxi from spot 1 down F across 1L and 1R, cleared across both, to 28L.</summary>
    private const string LeaderAcrossF = "TAXI A F RWY 28L CROSS 1L 1R";

    /// <summary>A runway on neither the follower's follow route nor its leader's path.</summary>
    private const string OffRouteRunway = "28R";

    /// <summary>How far (ft) off its follow route's centreline a follower crossing along it may stand.</summary>
    private const double OnFollowRouteFt = 15.0;

    /// <summary>The ground speed (kt) below which a held aircraft counts as stopped.</summary>
    private const double StoppedKts = 0.5;

    /// <summary>The speed (kt) the follower rolls faster than before a HOLD that must brake it.</summary>
    private const double MovingKts = 8.0;

    /// <summary>A jet's taxi brake rate (kt/s, <c>CategoryPerformance.TaxiDecelRate</c>), the hardest a HOLD may brake a B738.</summary>
    private const double JetTaxiBrakeKtsPerSecond = 5.0;

    /// <summary>Rounding slack (kt) on one second of braking.</summary>
    private const double BrakeSlackKts = 0.01;

    /// <summary>
    /// Slack (kt) on one second of braking where a HOLD ends a follow on or behind a runway crossing: no second may shed more than
    /// a jet's taxi brake rate plus this.
    /// </summary>
    private const double CrossingBrakeSlackKts = 0.5;

    /// <summary>The second after a HOLD that breaks off a follow mid-crossing at which a restore comparison sends RES.</summary>
    private const int ResAfterHoldSeconds = 3;

    /// <summary>How far (ft) a restored follower may stand from the live one it was restored from in any second.</summary>
    private const double RestoredAgreesFt = 0.5;

    /// <summary>A follower of the two-runway tests longer than the space between 1L's far bar and 1R's near bar on F1/F.</summary>
    private const string LongFollowerType = "B744";

    /// <summary>
    /// A follower of the two-runway tests that fits between 1L's far bar and 1R's near bar on F1/F with less than
    /// <see cref="RunwayTailClearance.MarginFt"/> to spare, so the reach of a crossing that ends at rest puts its nose past 1R's bar.
    /// </summary>
    private const string FittingFollowerType = "B763";

    /// <summary>
    /// How far (ft) past 1L's far bar a <see cref="LongFollowerType"/>'s centre is when the HOLD of
    /// <see cref="LateHoldFollowingOffTheFirstOfTwoRunways_WithTheTailStillInsideIt_HoldsNotClearOfIt"/> comes: past the end of the
    /// slice a crossing that ends at rest plans for it there, its tail still inside 1L's holding position marking.
    /// </summary>
    private const double CentrePastTheSliceEndFt = 75.0;

    /// <summary>
    /// The nearest (ft) before 1L's far bar a <see cref="LongFollowerType"/>'s centre is when the HOLD of
    /// <see cref="HoldOvershootingTheNewStop_DoesNotReportHoldingShort"/> comes.
    /// </summary>
    private const double OvershootHoldNearestFt = 40.0;

    /// <summary>
    /// The farthest (ft) before 1L's far bar a <see cref="LongFollowerType"/>'s centre is when the HOLD of
    /// <see cref="HoldOvershootingTheNewStop_DoesNotReportHoldingShort"/> comes.
    /// </summary>
    private const double OvershootHoldFarthestFt = 80.0;

    private const string OakRecordingPath = "TestData/yaat1527-oak-followg-cross-recording.yaat-bug-report-bundle.zip";
    private const string OakFollower = "N9671E";
    private const double OakRestoreSecond = 1880;
    private const int OakHoldBudgetSecond = 1913;

    [Fact]
    public void HeldOnTheFollowRoute_Cross_CrossesAlongTheFollowRoute()
    {
        if (StageHeldAtOneRightOnTheFollowRoute() is not { } staged)
        {
            return;
        }

        (SfoGround ground, AircraftState follower, AircraftState leader, HoldingShortPhase hold, TaxiRoute followRoute) = staged;
        GroundNode farBar = FarBarAlong(ground, followRoute, hold.HoldShort.NodeId, "1R");
        AssertSent(ground, Follower, "CROSS 1R");

        CrossingRunwayPhase crossing = AssertCrossesAlong(ground, follower, leader, followRoute);
        Assert.Equal(hold.HoldShort.NodeId, crossing.ApproachNodeId);
        Assert.Equal(farBar.Id, crossing.TargetNodeId);
        int followingAt = SfoGroundHarness.TickUntil(ground.Engine, () => follower.Phases?.CurrentPhase is FollowingPhase, CrossBudgetSeconds, null);
        Assert.True(followingAt >= 0, $"the follower is not following after crossing 1R: {follower.Phases?.CurrentPhase?.Name}");
    }

    [Fact]
    public void HeldBesideTheTrailCase_KoakReplay_CrossesOnC()
    {
        if (StartOak() is not { } run)
        {
            return;
        }

        AircraftState follower = Assert.IsType<AircraftState>(run.Engine.FindAircraft(OakFollower));
        HoldingShortPhase hold = ReplayUntilOakFollowerHolds(run.Engine);
        TaxiRoute followRoute = Assert.IsType<TaxiRoute>(HeldFollowRoute(follower));
        CommandResult result = run.Engine.SendCommand(OakFollower, "CROSS 15");
        Output.WriteLine($"CROSS 15: success={result.Success} '{result.Message}'");
        Assert.True(result.Success, result.Message);

        int crossingAt = SfoGroundHarness.TickUntil(run.Engine, () => follower.Phases?.CurrentPhase is CrossingRunwayPhase, HeldSeconds, null);
        Assert.True(crossingAt >= 0, $"{OakFollower} never started crossing 15: {follower.Phases?.CurrentPhase?.Name}");
        CrossingRunwayPhase crossing = Assert.IsType<CrossingRunwayPhase>(follower.Phases?.CurrentPhase);
        HashSet<int> routeNodes = RouteNodes(followRoute);
        Output.WriteLine($"held at {hold.HoldShort.NodeId}; crossing {crossing.ApproachNodeId} -> {crossing.TargetNodeId}");
        Assert.Contains(crossing.ApproachNodeId, routeNodes);
        Assert.Contains(crossing.TargetNodeId, routeNodes);
    }

    [Fact]
    public void FollowingWithRunwayAhead_Cross_ArmsAPreClearance()
    {
        if (StageBehind(TaxiToOneLeftBar, "1L", LeaderAcrossF, Type) is not { } staged)
        {
            return;
        }

        (SfoGround ground, AircraftState follower, AircraftState leader) = staged;
        AssertSent(ground, Follower, $"FOLLOWG {Leader}; CROSS 1L");
        int followingAt = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => follower.Phases?.CurrentPhase is FollowingPhase { FollowRoute: not null },
            CrossBudgetSeconds,
            second => LogFollow(second, follower, leader)
        );
        Assert.True(followingAt >= 0, $"the follower never followed on a route after crossing 1L: {follower.Phases?.CurrentPhase?.Name}");
        FollowingPhase follow = Assert.IsType<FollowingPhase>(follower.Phases?.CurrentPhase);
        Assert.True(
            FollowRoutePlanner.PathCrossesRunway(RouteEdges(follow.FollowRoute!), RunwayIdentifier.Parse("1R")),
            "1R is not on the follow route"
        );

        CommandResult result = ground.Engine.SendCommand(Follower, "CROSS 1R");
        Output.WriteLine($"CROSS 1R: success={result.Success} '{result.Message}'");
        Assert.True(result.Success, result.Message);
        Assert.Contains(follow.CrossingClearedRunways, r => r.Overlaps(RunwayIdentifier.Parse("1R")));

        RunwayInfo runway1R = SfoGroundHarness.Runway("1R");
        bool wasOnOneRight = false;
        for (int second = 1; second <= CrossBudgetSeconds; second++)
        {
            ground.Engine.TickOneSecond();
            LogFollow(second, follower, leader);
            Assert.False(
                (follower.Phases?.CurrentPhase is HoldingShortPhase held)
                    && SfoGroundHarness.HoldShortMatches(held.HoldShort, "1R")
                    && !held.Requirements.Any(r => r.IsSatisfied),
                $"+{second}s: the follower stopped at the 1R bar it was cleared across"
            );
            wasOnOneRight |= RunwayOccupancy.IsOnPavement(follower, runway1R);
        }

        Assert.True(wasOnOneRight, $"the follower never crossed 1R within {CrossBudgetSeconds}s of its pre-clearance");
    }

    [Fact]
    public void PreClearedBeyondAnUnclearedBar_HeldThereThenCrossed_KeepsThePreClearance()
    {
        if (SfoGroundHarness.Build(Output, autoCrossMode: AutoCrossRunwayMode.Off) is not { } ground)
        {
            Output.WriteLine("SKIP: SFO layout or navdata unavailable");
            return;
        }

        AircraftState leader = SfoGroundHarness.SpawnAtSpot(ground, Leader, Type, "1");
        AircraftState follower = SfoGroundHarness.SpawnAtSpot(ground, Follower, Type, "3");
        AssertSent(ground, Follower, TaxiToOneLeftBar);
        ground.Engine.TickOneSecond();
        AssertSent(ground, Leader, LeaderAcrossF);
        AssertSent(ground, Follower, $"FOLLOWG {Leader}");
        int routedAt = SfoGroundHarness.TickUntil(
            ground.Engine,
            () =>
                (follower.Phases?.CurrentPhase is FollowingPhase { FollowRoute: { } route })
                && FollowRoutePlanner.PathCrossesRunway(RouteEdges(route), RunwayIdentifier.Parse("1L"))
                && FollowRoutePlanner.PathCrossesRunway(RouteEdges(route), RunwayIdentifier.Parse("1R")),
            CrossBudgetSeconds,
            second => LogFollow(second, follower, leader)
        );
        Assert.True(routedAt >= 0, $"the follower never followed on a route across 1L and 1R: {follower.Phases?.CurrentPhase?.Name}");
        AssertSent(ground, Follower, "CROSS 1R");

        int heldAt = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => (follower.Phases?.CurrentPhase is HoldingShortPhase hold) && SfoGroundHarness.HoldShortMatches(hold.HoldShort, "1L"),
            StageBudgetSeconds,
            second => LogFollow(second, follower, leader)
        );
        Assert.True(heldAt >= 0, $"the follower never held at the uncleared 1L bar: {follower.Phases?.CurrentPhase?.Name}");
        PhaseList phases = Assert.IsType<PhaseList>(follower.Phases);
        FollowingPhase resume = Assert.IsType<FollowingPhase>(phases.Phases[phases.CurrentIndex + 1]);
        Assert.Contains(resume.CrossingClearedRunways, r => r.Overlaps(RunwayIdentifier.Parse("1R")));

        AssertSent(ground, Follower, "CROSS 1L");
        RunwayInfo runway1R = SfoGroundHarness.Runway("1R");
        bool wasOnOneRight = false;
        for (int second = 1; second <= CrossBudgetSeconds; second++)
        {
            ground.Engine.TickOneSecond();
            LogFollow(second, follower, leader);
            Assert.False(
                (follower.Phases?.CurrentPhase is HoldingShortPhase held)
                    && SfoGroundHarness.HoldShortMatches(held.HoldShort, "1R")
                    && !held.Requirements.Any(r => r.IsSatisfied),
                $"+{second}s: the follower stopped at the 1R bar it was pre-cleared across"
            );
            wasOnOneRight |= RunwayOccupancy.IsOnPavement(follower, runway1R);
        }

        Assert.True(wasOnOneRight, $"the follower never crossed 1R within {CrossBudgetSeconds}s of CROSS 1L");
    }

    [Fact]
    public void FollowingWithOneLeftAndOneRightAhead_CrossBoth_NeverStopsAtOneRight()
    {
        if (StageFollowingAcross("1L", "1R") is not { } staged)
        {
            return;
        }

        (SfoGround ground, AircraftState follower, AircraftState leader) = staged;
        AssertSent(ground, Follower, "CROSS 1L 1R");
        RunwayInfo runway1R = SfoGroundHarness.Runway("1R");
        bool wasOnOneRight = false;
        for (int second = 1; second <= StageBudgetSeconds; second++)
        {
            ground.Engine.TickOneSecond();
            LogFollow(second, follower, leader);
            Assert.False(
                (follower.Phases?.CurrentPhase is HoldingShortPhase held) && SfoGroundHarness.HoldShortMatches(held.HoldShort, "1R"),
                $"+{second}s: the follower held at the 1R bar it was cleared across with 1L"
            );
            wasOnOneRight |= RunwayOccupancy.IsOnPavement(follower, runway1R);
        }

        Assert.True(wasOnOneRight, $"the follower never crossed 1R within {StageBudgetSeconds}s of CROSS 1L 1R");
    }

    [Fact]
    public void Following_CrossOfARunwayOffTheRoute_IsRefusedAndChangesNothing()
    {
        if (StageFollowingAcross("1L", "1R") is not { } staged)
        {
            return;
        }

        (SfoGround ground, AircraftState follower, _) = staged;
        FollowingPhase follow = Assert.IsType<FollowingPhase>(follower.Phases?.CurrentPhase);
        List<RunwayIdentifier> before = [.. follow.CrossingClearedRunways];
        CommandResult result = ground.Engine.SendCommand(Follower, $"CROSS {OffRouteRunway}");
        Output.WriteLine($"CROSS {OffRouteRunway}: success={result.Success} '{result.Message}'");

        Assert.False(result.Success, $"CROSS {OffRouteRunway} off the follower's route was accepted");
        PilotSpeechText unable = Assert.IsType<PilotSpeechText>(result.PilotUnable);
        Assert.Equal($"Unable, runway {OffRouteRunway} is not on our route, request taxi instructions", unable.Terminal);
        Assert.Same(follow, follower.Phases?.CurrentPhase);
        Assert.Equal(before, follow.CrossingClearedRunways);
    }

    [Fact]
    public void ResCrossAtTheHeldBar_CrossesAlongTheFollowRoute()
    {
        if (StageHeldAtOneRightOnTheFollowRoute() is not { } staged)
        {
            return;
        }

        (SfoGround ground, AircraftState follower, AircraftState leader, HoldingShortPhase hold, TaxiRoute followRoute) = staged;
        GroundNode farBar = FarBarAlong(ground, followRoute, hold.HoldShort.NodeId, "1R");
        AssertSent(ground, Follower, "RES CROSS 1R");

        CrossingRunwayPhase crossing = AssertCrossesAlong(ground, follower, leader, followRoute);
        Assert.Equal(hold.HoldShort.NodeId, crossing.ApproachNodeId);
        Assert.Equal(farBar.Id, crossing.TargetNodeId);
    }

    [Fact]
    public void ResCrossNamingARunwayOffTheRoute_IsRefusedAndStaysHeld()
    {
        if (StageHeldAtOneRightOnTheFollowRoute() is not { } staged)
        {
            return;
        }

        (SfoGround ground, AircraftState follower, _, HoldingShortPhase hold, _) = staged;
        CommandResult result = ground.Engine.SendCommand(Follower, $"RES CROSS 1R {OffRouteRunway}");
        Output.WriteLine($"RES CROSS 1R {OffRouteRunway}: success={result.Success} '{result.Message}'");

        Assert.False(result.Success, $"RES CROSS 1R {OffRouteRunway} naming a runway off the follower's route was accepted");
        PilotSpeechText unable = Assert.IsType<PilotSpeechText>(result.PilotUnable);
        Assert.Equal($"Unable, runway {OffRouteRunway} is not on our route, request taxi instructions", unable.Terminal);
        Assert.DoesNotContain(hold.Requirements, r => r.IsSatisfied);
        AssertStillHeldWithTheFollowArmed(ground, follower, hold);
    }

    [Fact]
    public void PreClearance_SurvivesSnapshotRoundTrip()
    {
        if (StageFollowingAcross("1L", "1R") is not { } staged)
        {
            return;
        }

        (SfoGround ground, AircraftState follower, _) = staged;
        AssertSent(ground, Follower, "CROSS 1R");
        AircraftState restored = RestoredFollower(ground);
        FollowingPhase follow = Assert.IsType<FollowingPhase>(restored.Phases?.CurrentPhase);
        Assert.Contains(follow.CrossingClearedRunways, r => r.Overlaps(RunwayIdentifier.Parse("1R")));
        AssertRestoredGoesOnAsTheOriginal(ground, follower);
    }

    [Fact]
    public void PreClearanceCarriedPastAHold_SurvivesSnapshotRoundTrip()
    {
        if (StageFollowingAcross("1L", "1R") is not { } staged)
        {
            return;
        }

        (SfoGround ground, AircraftState follower, AircraftState leader) = staged;
        AssertSent(ground, Follower, "CROSS 1R");
        int heldAt = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => (follower.Phases?.CurrentPhase is HoldingShortPhase hold) && SfoGroundHarness.HoldShortMatches(hold.HoldShort, "1L"),
            StageBudgetSeconds,
            second => LogFollow(second, follower, leader)
        );
        Assert.True(heldAt >= 0, $"the follower never held at the uncleared 1L bar: {follower.Phases?.CurrentPhase?.Name}");

        AircraftState restored = RestoredFollower(ground);
        PhaseList phases = Assert.IsType<PhaseList>(restored.Phases);
        FollowingPhase resume = Assert.IsType<FollowingPhase>(phases.Phases[phases.CurrentIndex + 1]);
        Assert.Contains(resume.CrossingClearedRunways, r => r.Overlaps(RunwayIdentifier.Parse("1R")));
        AssertSent(ground, Follower, "CROSS 1L");
        AssertRestoredGoesOnAsTheOriginal(ground, follower);
    }

    [Fact]
    public void LegacySnapshotWithTheBeenOnFlag_MigratesToEveryClearedRunwayBeenOn()
    {
        if (StageFollowingAcross("1L", "1R") is not { } staged)
        {
            return;
        }

        (SfoGround ground, _, _) = staged;
        AssertSent(ground, Follower, "CROSS 1R");
        StateSnapshotDto snapshot = ground.Engine.CaptureSnapshot();
        FollowingPhaseDto follow = Assert.Single(
            Assert.IsType<PhaseListDto>(snapshot.Aircraft.Single(a => a.Callsign == Follower).Phases).Phases.OfType<FollowingPhaseDto>()
        );
        Assert.NotEmpty(follow.CrossingClearedRunways);
        snapshot.SchemaVersion = 39;
        follow.BeenOnClearedRunways = null;
        follow.LegacyHasBeenOnClearedRunway = true;

        SnapshotSchemaMigrator.Migrate(snapshot);

        Assert.Equal(follow.CrossingClearedRunways, follow.BeenOnClearedRunways);
        Assert.Null(follow.LegacyHasBeenOnClearedRunway);
    }

    [Fact]
    public void FollowingCrossThenHold_HoldsOnlyOnceAcrossOneRight()
    {
        if (StageFollowingAcross("1L", "1R") is not { } staged)
        {
            return;
        }

        (SfoGround ground, AircraftState follower, AircraftState leader) = staged;
        AssertSent(ground, Follower, "CROSS 1L 1R; HOLD");
        RunwayInfo runway1R = SfoGroundHarness.Runway("1R");
        bool wasOnOneRight = false;
        int clearOfOneRightAt = -1;
        int heldAt = -1;
        for (int second = 1; (second <= StageBudgetSeconds) && (heldAt < 0); second++)
        {
            ground.Engine.TickOneSecond();
            LogFollow(second, follower, leader);
            bool onOneRight = RunwayOccupancy.IsOnPavement(follower, runway1R);
            wasOnOneRight |= onOneRight;
            if (wasOnOneRight && !onOneRight && (clearOfOneRightAt < 0))
            {
                clearOfOneRightAt = second;
            }

            if (follower.Ground.Hold is not null)
            {
                heldAt = second;
            }
        }

        Output.WriteLine($"on 1R: {wasOnOneRight}, clear of 1R at +{clearOfOneRightAt}s, HOLD applied at +{heldAt}s");
        Assert.True(heldAt > 0, $"the HOLD chained behind CROSS 1L 1R never applied within {StageBudgetSeconds}s: stranded");
        Assert.True(wasOnOneRight && (clearOfOneRightAt > 0) && (heldAt >= clearOfOneRightAt), "the HOLD applied before the follower crossed 1R");
    }

    [Fact]
    public void FollowingCrossThenHold_AfterOneLOnly_DoesNotFireEarly()
    {
        if (StageFollowingAcross("1L", "1R") is not { } staged)
        {
            return;
        }

        (SfoGround ground, AircraftState follower, AircraftState leader) = staged;
        AssertSent(ground, Follower, "CROSS 1L 1R; HOLD");
        RunwayInfo runway1L = SfoGroundHarness.Runway("1L");
        RunwayInfo runway1R = SfoGroundHarness.Runway("1R");
        bool wasOnOneLeft = false;
        int secondsBetween = 0;
        for (int second = 1; (second <= StageBudgetSeconds) && !RunwayOccupancy.IsOnPavement(follower, runway1R); second++)
        {
            ground.Engine.TickOneSecond();
            LogFollow(second, follower, leader);
            bool onOneLeft = RunwayOccupancy.IsOnPavement(follower, runway1L);
            wasOnOneLeft |= onOneLeft;
            if (wasOnOneLeft && !onOneLeft && !RunwayOccupancy.IsOnPavement(follower, runway1R))
            {
                secondsBetween++;
                Assert.True(follower.Ground.Hold is null, $"+{second}s: the HOLD chained behind CROSS 1L 1R applied between 1L and 1R");
            }
        }

        Output.WriteLine($"{secondsBetween}s between 1L and 1R");
        Assert.True(secondsBetween > 0, $"the follower was never between 1L and 1R within {StageBudgetSeconds}s");
    }

    [Fact]
    public void FollowingCrossThenHold_ArmedBetweenTheRunways_SurvivesSnapshotRoundTrip()
    {
        if (StageFollowingAcross("1L", "1R") is not { } staged)
        {
            return;
        }

        (SfoGround ground, AircraftState follower, AircraftState leader) = staged;
        AssertSent(ground, Follower, "CROSS 1L 1R; HOLD");
        RunwayInfo runway1L = SfoGroundHarness.Runway("1L");
        RunwayInfo runway1R = SfoGroundHarness.Runway("1R");
        bool wasOnOneLeft = false;
        int betweenAt = SfoGroundHarness.TickUntil(
            ground.Engine,
            () =>
            {
                bool onOneLeft = RunwayOccupancy.IsOnPavement(follower, runway1L);
                wasOnOneLeft |= onOneLeft;
                return wasOnOneLeft && !onOneLeft && !RunwayOccupancy.IsOnPavement(follower, runway1R);
            },
            StageBudgetSeconds,
            second => LogFollow(second, follower, leader)
        );
        Assert.True(betweenAt >= 0, $"the follower was never between 1L and 1R within {StageBudgetSeconds}s");

        StateSnapshotDto snapshot = ground.Engine.CaptureSnapshot();
        SfoGround restoredGround = Assert.IsType<SfoGround>(SfoGroundHarness.Build(Output, autoCrossMode: AutoCrossRunwayMode.Off));
        restoredGround.Engine.RestoreFromSnapshot(snapshot);
        AircraftState restored = Assert.IsType<AircraftState>(restoredGround.Engine.FindAircraft(Follower));
        AircraftState restoredLeader = Assert.IsType<AircraftState>(restoredGround.Engine.FindAircraft(Leader));
        CommandBlock armed = Assert.Single(restored.Queue.Blocks, b => b.Trigger is { Type: BlockTriggerType.AfterRunwayCrossing });
        Assert.False(armed.IsApplied, "the HOLD applied before the snapshot, between 1L and 1R");
        IReadOnlyList<RunwayIdentifier> waitsOn = Assert.IsAssignableFrom<IReadOnlyList<RunwayIdentifier>>(armed.Trigger!.FollowCrossingRunways);
        Assert.Collection(
            waitsOn,
            r => Assert.True(r.Overlaps(RunwayIdentifier.Parse("1L"))),
            r => Assert.True(r.Overlaps(RunwayIdentifier.Parse("1R")))
        );

        AssertHoldsOnlyOnceClearOfOneRight(restoredGround, restored, restoredLeader);
    }

    [Fact]
    public void HoldToAFollowingAircraft_BreaksOffTheFollow()
    {
        if (StageFollowingAcross("1L") is not { } staged)
        {
            return;
        }

        (SfoGround ground, AircraftState follower, AircraftState leader) = staged;
        AssertBreaksOffMovingAndComesToRest(ground, follower, leader);
    }

    [Fact]
    public void ResAfterTheBreakOff_IsRefusedAndStaysPut()
    {
        if (StageFollowingAcross("1L") is not { } staged)
        {
            return;
        }

        (SfoGround ground, AircraftState follower, AircraftState leader) = staged;
        AssertBreaksOffMovingAndComesToRest(ground, follower, leader);
        LatLon restedAt = follower.Position;
        CommandResult result = ground.Engine.SendCommand(Follower, "RES");
        Output.WriteLine($"RES: success={result.Success} '{result.Message}'");
        Assert.False(result.Success, "RES after HOLD broke off the follow was accepted");
        PilotSpeechText unable = Assert.IsType<PilotSpeechText>(result.PilotUnable);
        Assert.Equal("unable, request taxi instructions.", unable.Terminal);
        for (int second = 1; second <= HeldSeconds; second++)
        {
            ground.Engine.TickOneSecond();
            Assert.False(IsFollowing(follower), $"+{second}s: RES resumed the follow HOLD broke off: {follower.Phases?.CurrentPhase?.Name}");
        }

        double movedFt = GeoMath.DistanceNm(restedAt, follower.Position) * GeoMath.FeetPerNm;
        Assert.True(movedFt < 1.0, $"the follower moved {movedFt:F1} ft after the refused RES");
    }

    [Fact]
    public void ResAfterALeadGoneStop_ResumesTaxi()
    {
        if (SfoGroundHarness.Build(Output, autoCrossMode: AutoCrossRunwayMode.Off) is not { } ground)
        {
            Output.WriteLine("SKIP: SFO layout or navdata unavailable");
            return;
        }

        AircraftState leader = SfoGroundHarness.SpawnAtSpot(ground, Leader, Type, "1");
        AircraftState follower = SfoGroundHarness.SpawnAtSpot(ground, Follower, Type, "3");
        AssertSent(ground, Leader, LeaderAcrossF);
        AssertSent(ground, Follower, $"FOLLOWG {Leader}");
        int movingAt = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => (follower.Phases?.CurrentPhase is FollowingPhase) && (follower.IndicatedAirspeed > MovingKts),
            CrossBudgetSeconds,
            second => LogFollow(second, follower, leader)
        );
        Assert.True(movingAt >= 0, $"the follower never rolled faster than {MovingKts} kt while following");
        ground.Engine.World.RemoveAircraft(Leader);
        int restAt = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => (follower.Phases?.CurrentPhase is HoldingInPositionPhase) && (follower.IndicatedAirspeed < StoppedKts),
            CrossBudgetSeconds,
            null
        );
        Assert.True(
            restAt >= 0,
            $"the follower never came to rest holding in position once its lead was gone: {follower.Phases?.CurrentPhase?.Name}"
        );
        PhaseList phases = Assert.IsType<PhaseList>(follower.Phases);
        Assert.Equal(phases.Phases.Count - 1, phases.CurrentIndex);
        Assert.False(Assert.IsType<HoldingInPositionPhase>(phases.CurrentPhase).EndsBrokenOffFollow);

        CommandResult unheld = ground.Engine.SendCommand(Follower, "RES");
        Output.WriteLine($"RES unheld: success={unheld.Success} '{unheld.Message}'");
        Assert.False(unheld.Success, "RES to an unheld follower its lead's loss stopped was accepted");
        Assert.Equal("Aircraft is not held", unheld.Message);

        AssertSent(ground, Follower, "HOLD");
        CommandResult result = ground.Engine.SendCommand(Follower, "RES");
        Output.WriteLine($"RES: success={result.Success} '{result.Message}'");
        Assert.True(result.Success, $"RES to a follower its lead's loss stopped was refused: '{result.Message}'");
        Assert.Equal("Resume taxi", result.Message);
        Assert.Null(follower.Ground.Hold);
    }

    [Fact]
    public void ResAtAHoldShortBeforeTheBreakOffHold_IsRefused()
    {
        if (StageHeldAtOneRightOnTheFollowRoute() is not { } staged)
        {
            return;
        }

        (SfoGround ground, AircraftState follower, AircraftState leader, HoldingShortPhase hold, _) = staged;
        AssertSent(ground, Follower, "HOLD");
        PhaseList phases = Assert.IsType<PhaseList>(follower.Phases);
        Assert.Same(hold, phases.CurrentPhase);
        Assert.IsType<HoldingInPositionPhase>(Assert.Single(phases.Phases.Skip(phases.CurrentIndex + 1)));
        LatLon heldAt = follower.Position;

        AssertResRefused(ground);
        for (int second = 1; second <= HeldSeconds; second++)
        {
            ground.Engine.TickOneSecond();
            LogFollow(second, follower, leader);
            Assert.Same(hold, follower.Phases?.CurrentPhase);
        }

        double movedFt = GeoMath.DistanceNm(heldAt, follower.Position) * GeoMath.FeetPerNm;
        Assert.True(movedFt < 1.0, $"the follower held short of 1R moved {movedFt:F1} ft after the refused RES");
    }

    /// <summary>
    /// A follower held short of its departure runway with a follow armed behind the bar, its follow broken off by HOLD, keeps the
    /// departure bar's own refusal of RES, which asks for a takeoff or line-up clearance, rather than the break-off's.
    /// </summary>
    [Fact]
    public void ResAtTheDepartureBarAfterABreakOff_KeepsTheDepartureBarRefusal()
    {
        if (SfoGroundHarness.Build(Output, autoCrossMode: AutoCrossRunwayMode.Off) is not { } ground)
        {
            Output.WriteLine("SKIP: SFO layout or navdata unavailable");
            return;
        }

        AircraftState follower = SfoGroundHarness.SpawnAtSpot(ground, Follower, Type, "1");
        SfoGroundHarness.SpawnAtSpot(ground, Leader, Type, "3");
        AssertSent(ground, Follower, "TAXI A L F 28L");
        int heldAt = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => (follower.Phases?.CurrentPhase is HoldingShortPhase hold) && SfoGroundHarness.HoldShortMatches(hold.HoldShort, "28L"),
            StageBudgetSeconds,
            null
        );
        Assert.True(heldAt > 0, $"{Follower} never held short of 28L within {StageBudgetSeconds}s: {follower.Phases?.CurrentPhase?.Name}");
        HoldingShortPhase bar = Assert.IsType<HoldingShortPhase>(follower.Phases?.CurrentPhase);
        Assert.Equal(HoldShortReason.DestinationRunway, bar.HoldShort.Reason);
        AssertSent(ground, Follower, $"FOLLOWG {Leader}");
        AssertSent(ground, Follower, "HOLD");
        PhaseList phases = Assert.IsType<PhaseList>(follower.Phases);
        Assert.Same(bar, phases.CurrentPhase);
        Assert.True(Assert.IsType<HoldingInPositionPhase>(Assert.Single(phases.Phases.Skip(phases.CurrentIndex + 1))).EndsBrokenOffFollow);

        CommandResult result = ground.Engine.SendCommand(Follower, "RES");
        Output.WriteLine($"RES: success={result.Success} '{result.Message}'");
        Assert.False(result.Success, "RES at the departure bar was accepted");
        string message = Assert.IsType<string>(result.Message);
        Assert.True(
            message.Contains("CTO", StringComparison.Ordinal) || message.Contains("LUAW", StringComparison.Ordinal),
            $"RES at the departure bar was not refused for a takeoff or line-up clearance: '{result.Message}'"
        );
    }

    [Fact]
    public void BreakOffHoldRefusal_SurvivesSnapshotRoundTrip()
    {
        if (StageFollowingAcross("1L") is not { } staged)
        {
            return;
        }

        (SfoGround ground, AircraftState follower, AircraftState leader) = staged;
        AssertBreaksOffMovingAndComesToRest(ground, follower, leader);
        (SfoGround restoredGround, AircraftState restored) = Restored(ground);
        Assert.True(Assert.IsType<HoldingInPositionPhase>(restored.Phases?.CurrentPhase).EndsBrokenOffFollow);
        AssertResRefused(restoredGround);
    }

    [Fact]
    public void HoldOnARunwayCrossedInsideTheFollow_ThenRes_FinishesTheCrossingTailClear()
    {
        if (StageFollowingAcross("1L", "1R") is not { } staged)
        {
            return;
        }

        (SfoGround ground, AircraftState follower, AircraftState leader) = staged;
        AssertSent(ground, Follower, "CROSS 1L 1R");
        RunwayInfo runway1R = SfoGroundHarness.Runway("1R");
        int onAt = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => RunwayOccupancy.IsOnPavement(follower, runway1R),
            StageBudgetSeconds,
            second => LogFollow(second, follower, leader)
        );
        Assert.True(onAt >= 0, $"the follower never got onto 1R within {StageBudgetSeconds}s");
        AssertSent(ground, Follower, "HOLD");
        SfoGroundHarness.TickUntil(ground.Engine, () => follower.IndicatedAirspeed < StoppedKts, HeldSeconds, null);
        Output.WriteLine($"held: {follower.Phases?.CurrentPhase?.Name}, on 1R: {RunwayOccupancy.IsOnPavement(follower, runway1R)}");

        AssertResComesToRestPastTheFarBar(ground, follower, leader, runway1R);
    }

    [Fact]
    public void HoldDuringTheArmedFollowCrossing_ThenRes_EndsHoldingInPosition()
    {
        if (StageHeldAtOneRightOnTheFollowRoute() is not { } staged)
        {
            return;
        }

        (SfoGround ground, AircraftState follower, AircraftState leader, _, _) = staged;
        AssertSent(ground, Follower, "RES CROSS 1R");
        int crossingAt = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => follower.Phases?.CurrentPhase is CrossingRunwayPhase,
            CrossBudgetSeconds,
            null
        );
        Assert.True(crossingAt >= 0, $"the follower never started crossing 1R: {follower.Phases?.CurrentPhase?.Name}");
        ground.Engine.TickOneSecond();
        AssertSent(ground, Follower, "HOLD");
        Assert.False(IsFollowing(follower), "the follow armed behind the crossing survived HOLD");

        AssertResComesToRestPastTheFarBar(ground, follower, leader, SfoGroundHarness.Runway("1R"));
    }

    /// <summary>
    /// A guard: the follower held at 1R by its follow is released along its follow route (<c>ReleaseAlongFollowerRoute</c>, else
    /// <c>FindCrossingFromBar</c>), a crossing over its own path, which ends at the far bar, so its tail is the layout walk, which
    /// carries <see cref="RunwayTailClearance.MarginFt"/> whether a HOLD flags it before it is built or after. A snapshot at the
    /// HOLD's tick (0) and one 3 s after it go on as the live run.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(ResAfterHoldSeconds)]
    public void HoldDuringTheArmedFollowCrossing_RestoredMidCrossing_GoesOnAsTheLiveRun(int snapshotAfterHoldSeconds)
    {
        if (StageHeldAtOneRightOnTheFollowRoute() is not { } staged)
        {
            return;
        }

        (SfoGround ground, AircraftState follower, AircraftState leader, _, _) = staged;
        AssertSent(ground, Follower, "RES CROSS 1R");
        HoldOnceTheCrossingIsUnderWay(ground, follower, leader);
        AssertRestoredAfterTheHoldGoesOnAsTheLiveRun(ground, follower, snapshotAfterHoldSeconds);
    }

    /// <summary>
    /// A follower held at the 1R bar of its own taxi route, its follow armed there, is released across 1R on a crossing that slices
    /// its taxi route, which goes on past the far bar; a HOLD with the crossing under way flags it to end at rest. A snapshot at the
    /// HOLD's tick (0) and one 3 s after it go on as the live run. A guard: a restore applies the tail decision its snapshot carries,
    /// so it agrees with the live run whether or not the flag re-plans the tail.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(ResAfterHoldSeconds)]
    public void HoldDuringACrossingOfItsTaxiRoute_WithTheFollowArmed_RestoredGoesOnAsTheLiveRun(int snapshotAfterHoldSeconds)
    {
        if (StageHeldDuringACrossingOfItsTaxiRoute() is not { } staged)
        {
            return;
        }

        (SfoGround ground, AircraftState follower, _, _) = staged;
        AssertRestoredAfterTheHoldGoesOnAsTheLiveRun(ground, follower, snapshotAfterHoldSeconds);
    }

    /// <summary>
    /// The crossing of <see cref="HoldDuringACrossingOfItsTaxiRoute_WithTheFollowArmed_RestoredGoesOnAsTheLiveRun"/>, released: a
    /// HOLD that ends a follow ends at rest with the tail and wingtips clear of the far 1R bar's holding position marking. Flagged
    /// while under way, the crossing re-plans its tail with the reach of one that ends at rest, ½ its length plus
    /// <see cref="RunwayTailClearance.MarginFt"/>; the margin is measured along the route, not across the bar, and F1 meets 1R at an
    /// angle. Measured: with the tail kept at ½ the length the follower rests 0.73 ft short of clearing the bar (red); re-planned,
    /// 8.35 ft past it.
    /// </summary>
    [Fact]
    public void HoldDuringACrossingOfItsTaxiRoute_WithTheFollowArmed_ThenRes_RestsWithTheTailClearOfTheFarBar()
    {
        if (StageHeldDuringACrossingOfItsTaxiRoute() is not { } staged)
        {
            return;
        }

        (SfoGround ground, AircraftState follower, AircraftState leader, int farBarId) = staged;
        for (int second = 1; second <= ResAfterHoldSeconds; second++)
        {
            ground.Engine.TickOneSecond();
        }

        AssertSent(ground, Follower, "RES");
        int restAt = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => (follower.Phases?.CurrentPhase is HoldingInPositionPhase) && (follower.IndicatedAirspeed < StoppedKts),
            CrossBudgetSeconds,
            second => LogFollow(second, follower, leader)
        );
        Assert.True(restAt >= 0, $"the follower never came to rest holding in position: {follower.Phases?.CurrentPhase?.Name ?? "no phase"}");
        double? shortFt = FollowingPhase.ShortOfBarFt(ground.Layout, farBarId, follower);
        Output.WriteLine($"at rest {shortFt:F2} ft short of clearing the far 1R bar #{farBarId}");
        Assert.True(shortFt <= 0.0, $"the follower came to rest {shortFt:F2} ft short of clearing the far 1R bar #{farBarId}");
    }

    /// <summary>
    /// A follower that fits between 1L's far bar and 1R's near bar along its follow route with less than
    /// <see cref="RunwayTailClearance.MarginFt"/> to spare (<see cref="FittingFollowerType"/>), held on 1L's pavement during the follow
    /// and released, comes to rest with its tail and wingtips clear of 1L's far bar and its nose short of 1R's near bar: a crossing
    /// that ends at rest cuts its reach where the nose meets the next runway's bar. Clear of 1L, it holds with no tail inside 1L and
    /// makes no not-clear report.
    /// </summary>
    [Fact]
    public void HoldOnTheFirstOfTwoRunways_ATypeThatFitsBetweenThem_RestsWithItsNoseShortOfTheSecondRunwaysBar()
    {
        if (StageOnOneLeftAlongTheFollowRoute(FittingFollowerType) is not { } staged)
        {
            return;
        }

        (SfoGround ground, AircraftState follower, AircraftState leader, TaxiRoute? route) = staged;
        (int farBarId, double? gapFt) = HoldOnOneLeft(ground, follower, route);
        double lengthFt = AircraftLength.ResolveFt(FittingFollowerType);
        Assert.True(
            (gapFt is { } gap) && (lengthFt < gap) && (gap < lengthFt + RunwayTailClearance.MarginFt),
            $"the {FittingFollowerType}, {lengthFt:F1} ft long, does not fit the {gapFt:F1} ft between the bars with under "
                + $"{RunwayTailClearance.MarginFt} ft to spare"
        );
        List<string> lines = CapturePilotLines(ground, follower);
        ResThenRestHoldingInPosition(ground, follower, leader);
        AssertTailClearOfOneLeft(ground, follower, farBarId);
        AssertNoseShortOfOneRight(ground, follower, lengthFt);
        AssertHoldsWithNoTailInsideOneLeft(follower, lines);
    }

    /// <summary>
    /// A <see cref="LongFollowerType"/>, longer than the space between 1L's far bar and 1R's near bar, held on 1L's pavement during the
    /// follow, cleared across both, and released, comes to rest with its nose short of 1R's near bar and its tail inside 1L's holding
    /// position marking, so it holds as an occupant of 1L that reports holding short of 1R, not clear of 1L, and still refuses RES.
    /// The owner's ruling: "Never encroach — best effort to clear the crossed runway, but the nose never passes another runway's hold
    /// bar, cleared or not. A type that fits ends clear of both; a B744 stops nose short of 1R with its tail on 1L."
    /// </summary>
    [Fact]
    public void HoldOnTheFirstOfTwoRunways_ATypeLongerThanTheSpaceBetweenThem_RestsWithItsNoseShortOfTheSecondRunwaysBar()
    {
        if (StageOnOneLeftAlongTheFollowRoute(LongFollowerType) is not { } staged)
        {
            return;
        }

        (SfoGround ground, AircraftState follower, AircraftState leader, TaxiRoute? route) = staged;
        _ = HoldOnOneLeft(ground, follower, route);
        List<string> lines = CapturePilotLines(ground, follower);
        ResThenRestHoldingInPosition(ground, follower, leader);
        AssertHoldsShortOfOneRightNotClearOfOneLeft(ground, follower, lines);
        // With no active runways listed, the ends named are the nearest.
        Assert.Contains("holding short of runway 19L at F, not clear of runway 19R.", lines);
        AssertNoseShortOfOneRight(ground, follower, AircraftLength.ResolveFt(LongFollowerType));
        HoldingInPositionPhase hold = Assert.IsType<HoldingInPositionPhase>(follower.Phases?.CurrentPhase);
        Assert.Contains("GND", hold.NotClearReportPositions);
        (_, AircraftState restored) = Restored(ground);
        HoldingInPositionPhase restoredHold = Assert.IsType<HoldingInPositionPhase>(restored.Phases?.CurrentPhase);
        Assert.Equal(hold.NotClearReportPositions, restoredHold.NotClearReportPositions);
    }

    /// <summary>
    /// <see cref="HoldOnTheFirstOfTwoRunways_ATypeLongerThanTheSpaceBetweenThem_RestsWithItsNoseShortOfTheSecondRunwaysBar"/>
    /// with SFO landing and departing 1L, 1R, 28L and 28R (<c>ARWY 01L 01R 28L 28R</c>): a pilot names an active runway by its
    /// active end, so the B744 holding short of 1R/19L with its tail on 1L/19R says "1R" and "1L", not the nearer 19L and 19R.
    /// </summary>
    [Fact]
    public void HoldOnTheFirstOfTwoRunways_ATypeLongerThanTheSpaceBetweenThem_WithOneLeftAndOneRightActive_NamesTheActiveEnds()
    {
        if (StageOnOneLeftAlongTheFollowRoute(LongFollowerType) is not { } staged)
        {
            return;
        }

        (SfoGround ground, AircraftState follower, AircraftState leader, TaxiRoute? route) = staged;
        ground.Engine.Scenario!.ActiveRunways = ActiveRunways.Empty.With(
            "SFO",
            [.. new[] { "01L", "01R", "28L", "28R" }.Select(end => new ActiveRunway(end, ActiveRunwayUse.Both))]
        );
        _ = HoldOnOneLeft(ground, follower, route);
        List<string> lines = CapturePilotLines(ground, follower);
        ResThenRestHoldingInPosition(ground, follower, leader);
        AssertHoldsShortOfOneRightNotClearOfOneLeft(ground, follower, lines);
        Assert.Contains("holding short of runway 1R at F, not clear of runway 1L.", lines);
    }

    /// <summary>
    /// A <see cref="LongFollowerType"/> following across 1L and 1R, cleared across both, given a HOLD at the follow's speed with its
    /// centre <see cref="OvershootHoldNearestFt"/> to <see cref="OvershootHoldFarthestFt"/> ft before 1L's far bar: the crossing it is
    /// held on plans its stop with the nose short of 1R's near bar, but braking from there it comes to rest with its nose past that
    /// bar, judged as a <c>CLRWY</c> judges it (<see cref="ClearRunwayPhase.IsNosePastBar"/>). Not holding short of 1R, its hold
    /// records no held-short runway and the pilot never says it is holding short of one.
    /// </summary>
    [Fact]
    public void HoldOvershootingTheNewStop_DoesNotReportHoldingShort()
    {
        if (StageOnOneLeftAlongTheFollowRoute(LongFollowerType) is not { } staged)
        {
            return;
        }

        (SfoGround ground, AircraftState follower, AircraftState leader, TaxiRoute? route) = staged;
        (GroundNode farBar, TrueHeading onward) = OneLeftFarBarOn(route);
        int beforeAt = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => CentrePastFt(follower, farBar, onward) >= -OvershootHoldFarthestFt,
            StageBudgetSeconds,
            second => LogFollow(second, follower, leader)
        );
        Assert.True(beforeAt >= 0, $"the follower's centre never got within {OvershootHoldFarthestFt} ft of 1L's far bar #{farBar.Id}");
        double beforeFt = -CentrePastFt(follower, farBar, onward);
        Output.WriteLine($"HOLD with the centre {beforeFt:F1} ft before 1L's far bar #{farBar.Id} at {follower.IndicatedAirspeed:F1} kt");
        Assert.True(beforeFt >= OvershootHoldNearestFt, $"the centre was only {beforeFt:F1} ft before 1L's far bar #{farBar.Id} at the HOLD");
        List<string> lines = CapturePilotLines(ground, follower);
        AssertSent(ground, Follower, "HOLD");
        int restAt = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => (follower.Phases?.CurrentPhase is HoldingInPositionPhase) && (follower.IndicatedAirspeed < StoppedKts),
            CrossBudgetSeconds,
            second => LogFollow(second, follower, leader)
        );
        Assert.True(restAt >= 0, $"the follower never came to rest holding in position: {follower.Phases?.CurrentPhase?.Name ?? "no phase"}");

        RecordTailOnOneLeft(ground, follower, farBar.Id);
        RunwayInfo runway1R = SfoGroundHarness.Runway("1R");
        GroundNode oneRightBar = Assert.IsType<GroundNode>(RunwayHoldBars.NearestSameSideBar(ground.Layout, runway1R, follower.Position));
        LatLon nose = GeoMath.ProjectPoint(
            follower.Position,
            follower.TrueHeading,
            AircraftLength.ResolveFt(LongFollowerType) / 2.0 / GeoMath.FeetPerNm
        );
        double noseShortFt = RunwayHoldBars.CentrelineFt(runway1R, nose) - RunwayHoldBars.CentrelineFt(runway1R, oneRightBar.Position);
        Output.WriteLine($"at rest the nose is {noseShortFt:F2} ft short of 1R's near bar #{oneRightBar.Id}");
        Assert.True(
            ClearRunwayPhase.IsNosePastBar(follower, farBar, oneRightBar),
            $"the nose rests {noseShortFt:F2} ft short of 1R's near bar #{oneRightBar.Id}: the HOLD did not carry it past its stop"
        );

        HoldingInPositionPhase hold = Assert.IsType<HoldingInPositionPhase>(follower.Phases?.CurrentPhase);
        foreach (string line in lines)
        {
            Output.WriteLine($"pilot: {line}");
        }

        Output.WriteLine($"holding short of {hold.HeldShortOfRunway ?? "none"}, tail inside {hold.TailInsideRunway ?? "none"}");
        Assert.Null(hold.HeldShortOfRunway);
        Assert.DoesNotContain(lines, line => line.Contains("holding short of runway", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(hold.TailInsideRunway, new[] { "1L", "19R" });
        Assert.Single(lines, line => line.Contains("Holding position, not clear of runway", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A <see cref="LongFollowerType"/> held at the 1L bar of its own taxi route, which holds short of 1R less than its length past
    /// 1L's far bar, with its follow armed there, held by a HOLD during the crossing into the follow and released, comes to rest with
    /// its nose short of that hold short and its tail inside 1L's holding position marking, so it holds as an occupant of 1L that
    /// reports holding short of 1R, not clear of 1L, and still refuses RES. A guard of the owner's ruling: "an explicit hold short of
    /// a runway should win. A HOLD exits the current runway, a HS should however always never encroach on the resulting runway."
    /// </summary>
    [Fact]
    public void HoldDuringTheArmedFollowCrossingOfTheFirstOfTwoRunways_RestsWithItsNoseShortOfTheSecondRunwaysHoldShort()
    {
        if (StageArmedFollowCrossingOfOneLeft(LongFollowerType) is not { } staged)
        {
            return;
        }

        (SfoGround ground, AircraftState follower, AircraftState leader, TaxiRoute? route) = staged;
        _ = HoldOnOneLeft(ground, follower, route);
        GroundNode holdShort = OneRightHoldShortOn(ground, route);
        List<string> lines = CapturePilotLines(ground, follower);
        ResThenRestHoldingInPosition(ground, follower, leader);
        AssertHoldsShortOfOneRightNotClearOfOneLeft(ground, follower, lines);
        AssertNoseShortOfOneRightBar(follower, AircraftLength.ResolveFt(LongFollowerType), holdShort);
    }

    /// <summary>
    /// A guard: a <see cref="LongFollowerType"/> taxiing along F with 1R pre-cleared, held at 1L's bar, its follow armed there and
    /// cleared across 1L: its crossing of 1L slices the taxi route, whose hold short of 1R is cleared. Snapshotted with the crossing
    /// under way and restored into a second world, both are given a HOLD in the same second, before the restored crossing has ticked,
    /// and a RES in the same second after it: the restored crossing plans the stop the live one plans, its nose short of 1R's near
    /// bar, and goes on as the live run. Not red first: before a HOLD on a restored crossing not yet built built it as its first tick
    /// would, none of three stagings diverged (this one, and the F1 route and the F route with <c>FOLLOWG; CROSS 1L 1R</c>, which
    /// leaves the taxi route's 1R hold short uncleared): 0.000 ft apart every second, the restored nose resting 3.03 ft (2.68 ft on F1)
    /// short of 1R's near bar. Right after both HOLDs the restored crossing has built its slice and plans the live one's tail decision
    /// and held-short bar: measured red with that build removed from <see cref="CrossingRunwayPhase.EndAtRest"/>, the restored
    /// crossing had no crossing route and no held-short bar where the live one planned 1R's near bar #889.
    /// </summary>
    [Fact]
    public void HoldRightAfterARestore_PlansTheSameStopAsTheLiveRun()
    {
        if (StageBehind($"{LeaderDownF} CROSS 1R", "1L", LeaderAcrossF, Type, LongFollowerType) is not { } staged)
        {
            return;
        }

        (SfoGround ground, AircraftState follower, AircraftState leader) = staged;
        AssertSent(ground, Follower, $"FOLLOWG {Leader}");
        AssertSent(ground, Follower, "CROSS 1L");
        TickUntilTheCrossingIsUnderWay(ground, follower, leader);
        TaxiRoute route = Assert.IsType<TaxiRoute>(follower.Ground.AssignedTaxiRoute);
        string holdShorts = string.Join(", ", route.HoldShortPoints.Select(h => $"#{h.NodeId} cleared={h.IsCleared}"));
        Output.WriteLine($"crossing on taxi route {route.ToSummary()}; hold shorts {holdShorts}");
        (SfoGround restoredGround, AircraftState restored) = Restored(ground);
        AssertSent(ground, Follower, "HOLD");
        AssertSent(restoredGround, Follower, "HOLD");
        AssertRestoredCrossingPlansAsTheLiveOne(follower, restored);
        ResBothAndTickToRest((ground, follower), (restoredGround, restored), 1);
        AssertNoseShortOfOneRight(restoredGround, restored, AircraftLength.ResolveFt(LongFollowerType));
    }

    /// <summary>
    /// The held-short bar a crossing that ends at rest plans with its tail decision is restored as snapshotted, not derived again from
    /// the route. No SFO staging has a taxiway hold short binding such a crossing's stop (no taxiway meets F between 1L's far bar and
    /// 1R's near bar), so rather than clearing a hold short the test removes the bar from the crossing's snapshot DTO: a
    /// <see cref="LongFollowerType"/>'s crossing of 1L, flagged by a HOLD to end at rest, plans 1R's near bar; its snapshot with the
    /// bar removed, the plan a binding taxiway hold short leaves, is restored in place on the same route, which derives that bar, and
    /// rebuilt it still snapshots no bar.
    /// </summary>
    [Fact]
    public void HeldShortBar_IsRestoredAsSnapshotted_NotRederivedFromTheRoute()
    {
        if (StageArmedFollowCrossingOfOneLeft(LongFollowerType) is not { } staged)
        {
            return;
        }

        (SfoGround ground, AircraftState follower, _, _) = staged;
        AssertSent(ground, Follower, "HOLD");
        CrossingRunwayPhase live = Assert.IsType<CrossingRunwayPhase>(follower.Phases?.CurrentPhase);
        CrossingRunwayPhaseDto planned = Assert.IsType<CrossingRunwayPhaseDto>(live.ToSnapshot());
        Output.WriteLine($"the live crossing plans held-short bar #{planned.HeldShortBarNodeId}");
        Assert.NotNull(planned.TailClearanceSuppressed);
        Assert.NotNull(planned.HeldShortBarNodeId);

        JsonObject json = Assert.IsType<JsonObject>(JsonSerializer.SerializeToNode(planned));
        Assert.True(json.Remove(nameof(CrossingRunwayPhaseDto.HeldShortBarNodeId)), "the snapshot wrote no held-short bar");
        CrossingRunwayPhaseDto plannedNone = Assert.IsType<CrossingRunwayPhaseDto>(json.Deserialize<CrossingRunwayPhaseDto>());
        PhaseList phases = Assert.IsType<PhaseList>(follower.Phases);
        phases.Phases[phases.CurrentIndex] = CrossingRunwayPhase.FromSnapshot(plannedNone, ground.Layout);
        ground.Engine.TickOneSecond();

        CrossingRunwayPhase rebuilt = Assert.IsType<CrossingRunwayPhase>(follower.Phases?.CurrentPhase);
        CrossingRunwayPhaseDto after = Assert.IsType<CrossingRunwayPhaseDto>(rebuilt.ToSnapshot());
        Output.WriteLine($"restored with no bar and rebuilt: held-short bar #{after.HeldShortBarNodeId?.ToString() ?? "none"}");
        Assert.NotNull(rebuilt.CrossingRoute);
        Assert.Null(after.HeldShortBarNodeId);
    }

    /// <summary>
    /// A <see cref="LongFollowerType"/> following on <see cref="LeaderAcrossF"/> with no taxi route of its own, held at 1L's bar on the
    /// trail and cleared across 1L alone, crosses over its own path, which ends at 1L's far bar, so its tail clearance walks the layout
    /// on past that bar (<c>AppendLayoutTailClearance</c>). Held by a HOLD once the crossing is under way and released, it comes to
    /// rest with its nose short of 1R's near bar and its tail inside 1L's holding position marking, so it holds as an occupant of 1L
    /// that reports holding short of 1R, not clear of 1L, and still refuses RES. A guard of the owner's ruling: "Never encroach —
    /// best effort to clear the crossed runway, but the nose never passes another runway's hold bar, cleared or not." Measured red
    /// before the walk looked ahead for another runway's bar: the nose 54.58 ft past 1R's near bar #889.
    /// </summary>
    [Fact]
    public void HoldDuringAnOwnPathCrossingOfTheFirstOfTwoRunways_RestsWithItsNoseShortOfTheSecondRunwaysBar()
    {
        if (StageOwnPathCrossingOfOneLeft() is not { } staged)
        {
            return;
        }

        (SfoGround ground, AircraftState follower, AircraftState leader, _) = staged;
        AssertSent(ground, Follower, "HOLD");
        List<string> lines = CapturePilotLines(ground, follower);
        ResThenRestHoldingInPosition(ground, follower, leader);
        AssertHoldsShortOfOneRightNotClearOfOneLeft(ground, follower, lines);
        AssertNoseShortOfOneRight(ground, follower, AircraftLength.ResolveFt(LongFollowerType));
    }

    /// <summary>
    /// The crossing of <see cref="HoldDuringAnOwnPathCrossingOfTheFirstOfTwoRunways_RestsWithItsNoseShortOfTheSecondRunwaysBar"/>
    /// with no HOLD: the layout walk past 1L's far bar ends the slice with the nose short of 1R's near bar. It asserts the nose
    /// computed from the slice's geometry, ½ the length ahead of the slice's last node along its last segment, where the crossing
    /// completes, not where the aircraft comes to rest. Measured red with the walk's look-ahead for another runway's bar taken out:
    /// that nose 55.72 ft past 1R's near bar #889.
    /// </summary>
    [Fact]
    public void UnflaggedOwnPathCrossing_WalkStopsTheNoseShortOfTheSecondRunwaysBar()
    {
        if (StageOwnPathCrossingOfOneLeft() is not { } staged)
        {
            return;
        }

        (SfoGround ground, AircraftState follower, AircraftState leader, CrossingRunwayPhase crossing) = staged;
        TaxiRoute slice = Assert.IsType<TaxiRoute>(crossing.CrossingRoute);
        DirectionalEdge last = slice.Segments[^1].Edge;
        double halfLengthNm = AircraftLength.ResolveFt(LongFollowerType) / 2.0 / GeoMath.FeetPerNm;
        LatLon nose = GeoMath.ProjectPoint(last.ToNode.Position, new TrueHeading(last.ArrivalBearing), halfLengthNm);
        RunwayInfo runway1R = SfoGroundHarness.Runway("1R");
        GroundNode oneRightBar = Assert.IsType<GroundNode>(RunwayHoldBars.NearestSameSideBar(ground.Layout, runway1R, nose));
        bool onTheNearSide = RunwayHoldBars.IsRightOfCentreline(runway1R, nose) == RunwayHoldBars.IsRightOfCentreline(runway1R, oneRightBar.Position);
        double noseShortFt = RunwayHoldBars.CentrelineFt(runway1R, nose) - RunwayHoldBars.CentrelineFt(runway1R, oneRightBar.Position);
        Output.WriteLine($"where the crossing completes the nose is {noseShortFt:F2} ft short of 1R's near bar #{oneRightBar.Id}");
        int completedAt = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => follower.Phases?.CurrentPhase != crossing,
            CrossBudgetSeconds,
            second => LogFollow(second, follower, leader)
        );
        Assert.True(completedAt >= 0, $"the crossing of 1L never completed within {CrossBudgetSeconds}s");
        Assert.True(
            onTheNearSide && (noseShortFt > 0.0),
            $"the crossing completes with the nose {-noseShortFt:F2} ft past 1R's near bar #{oneRightBar.Id}"
        );
    }

    /// <summary>
    /// Stages a <see cref="LongFollowerType"/> following on <see cref="LeaderAcrossF"/> with no taxi route of its own, held at 1L's bar
    /// on the trail, cleared across 1L alone, and ticks until its crossing over its own path, which ends at 1L's far bar, is under way.
    /// Returns that crossing; null on the repo's silent-skip path.
    /// </summary>
    private (SfoGround Ground, AircraftState Follower, AircraftState Leader, CrossingRunwayPhase Crossing)? StageOwnPathCrossingOfOneLeft()
    {
        if (SfoGroundHarness.Build(Output, autoCrossMode: AutoCrossRunwayMode.Off) is not { } ground)
        {
            Output.WriteLine("SKIP: SFO layout or navdata unavailable");
            return null;
        }

        AircraftState leader = SfoGroundHarness.SpawnAtSpot(ground, Leader, Type, "1");
        AircraftState follower = SfoGroundHarness.SpawnAtSpot(ground, Follower, LongFollowerType, "3");
        AssertSent(ground, Leader, LeaderAcrossF);
        AssertSent(ground, Follower, $"FOLLOWG {Leader}");
        int heldAt = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => (follower.Phases?.CurrentPhase is HoldingShortPhase hold) && SfoGroundHarness.HoldShortMatches(hold.HoldShort, "1L"),
            StageBudgetSeconds,
            second => LogFollow(second, follower, leader)
        );
        Assert.True(
            heldAt >= 0,
            $"the follower never held short of 1L on the trail within {StageBudgetSeconds}s: {follower.Phases?.CurrentPhase?.Name}"
        );
        AssertSent(ground, Follower, "CROSS 1L");
        TickUntilTheCrossingIsUnderWay(ground, follower, leader);
        CrossingRunwayPhase crossing = Assert.IsType<CrossingRunwayPhase>(follower.Phases?.CurrentPhase);
        TaxiRoute ownPath = Assert.IsType<TaxiRoute>(crossing.OwnPath);
        Output.WriteLine($"crossing 1L from bar #{crossing.ApproachNodeId} to #{crossing.TargetNodeId} over its own path {ownPath.ToSummary()}");
        Assert.Equal(crossing.TargetNodeId, ownPath.Segments[^1].ToNodeId);
        return (ground, follower, leader, crossing);
    }

    /// <summary>
    /// A <see cref="LongFollowerType"/> following across 1L and 1R, cleared across both, given a HOLD only once its centre is
    /// <see cref="CentrePastTheSliceEndFt"/> past 1L's far bar: the follow's crossing has passed that bar, so the HOLD breaks off the
    /// follow itself, which brakes to rest at its taxi brake rate. Its tail is still inside 1L's holding position marking at the HOLD,
    /// but it stops well past it, clear of 1L, so it holds with no tail inside and makes no not-clear report. Its nose is already past
    /// 1R's bar at the HOLD (centre 75 ft + ½ its length 116 ft past 1L's far bar, the bars 186 ft apart), so it rolls on over 1R's
    /// bar and comes to rest fouling 1R.
    /// </summary>
    [Fact]
    public void LateHoldFollowingOffTheFirstOfTwoRunways_StopsPastIt_HoldsWithNoTailInside()
    {
        if (StageLateOnOneLeft() is not { } staged)
        {
            return;
        }

        (SfoGround ground, AircraftState follower, AircraftState leader, (GroundNode Bar, TrueHeading Onward) farBar) = staged;
        List<string> lines = CapturePilotLines(ground, follower);
        HoldThenBrakeToRest(ground, follower, leader, farBar);
        AssertHoldsWithNoTailInsideOneLeft(follower, lines);
        Assert.Equal(RunwayUseKind.Fouling, RunwayOccupancy.Classify(follower, SfoGroundHarness.Runway("1R"), ground.Layout)?.Kind);
    }

    /// <summary>
    /// The staging of <see cref="LateHoldFollowingOffTheFirstOfTwoRunways_StopsPastIt_HoldsWithNoTailInside"/> with no HOLD: where the
    /// HOLD would come, the follower's tail is still inside 1L's holding position marking, and the runway a broken-off follow would
    /// hold not clear of (<see cref="FollowingPhase.TailInsideRunwayEnd"/>) is an end of 1L/19R; once its tail and wingtips are past
    /// 1L's far bar, there is none. Red first with the helper made to return null.
    /// </summary>
    [Fact]
    public void TailInsideRunwayEnd_ATailStillInsideTheRunwayJustCrossed_NamesIt()
    {
        if (StageLateOnOneLeft() is not { } staged)
        {
            return;
        }

        (SfoGround ground, AircraftState follower, AircraftState leader, (GroundNode Bar, TrueHeading Onward) farBar) = staged;
        RunwayInfo runway1L = SfoGroundHarness.Runway("1L");
        string? inside = TailInsideRunwayEnd(ground, follower);
        Output.WriteLine(
            $"centre {CentrePastFt(follower, farBar.Bar, farBar.Onward):F1} ft past 1L's far bar #{farBar.Bar.Id}: tail inside {inside ?? "none"}"
        );
        Assert.True(
            (inside is { } end) && RunwayIdentifier.Parse(end).Overlaps(runway1L.Id),
            $"the tail-inside runway {inside ?? "none"} is not an end of 1L/19R"
        );

        int clearAt = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => FollowingPhase.IsClearPastBar(follower, runway1L, farBar.Bar),
            CrossBudgetSeconds,
            second => LogFollow(second, follower, leader)
        );
        Assert.True(clearAt >= 0, $"the follower's tail never cleared 1L's far bar #{farBar.Bar.Id}");
        string? clear = TailInsideRunwayEnd(ground, follower);
        Output.WriteLine($"tail clear of #{farBar.Bar.Id} after {clearAt}s: tail inside {clear ?? "none"}");
        Assert.Null(clear);
    }

    /// <summary>
    /// The staging of <see cref="TailInsideRunwayEnd_ATailStillInsideTheRunwayJustCrossed_NamesIt"/> with the follower's own runway
    /// 19R (the runway it landed on, <see cref="PhaseList.AssignedRunway"/>) and SFO landing 1L (<c>ARWY 01L</c>): the runway it
    /// is not clear of is named by its own end, 19R, not the active 1L, as the exit's own not-clear call names it.
    /// </summary>
    [Fact]
    public void TailInsideRunwayEnd_OfTheRunwayItLandedOn_WithTheOtherEndActive_NamesItsOwnEnd()
    {
        if (StageLateOnOneLeft() is not { } staged)
        {
            return;
        }

        (SfoGround ground, AircraftState follower, _, _) = staged;
        follower.Phases!.AssignedRunway = Yaat.Sim.Data.NavigationDatabase.Instance.GetRunway("SFO", "19R");
        ActiveRunways oneLeft = ActiveRunways.Empty.With("SFO", [new ActiveRunway("01L", ActiveRunwayUse.Both)]);
        string? inside = Assert
            .IsType<FollowingPhase>(follower.Phases.CurrentPhase)
            .TailInsideRunwayEnd(CommandDispatcher.BuildMinimalContext(follower, oneLeft, ground.Layout));
        Output.WriteLine($"tail inside {inside ?? "none"} with 19R assigned and 1L active");

        Assert.Equal("19R", inside);
    }

    /// <summary>The runway <paramref name="follower"/>'s follow would hold not clear of were it broken off where it stands.</summary>
    private static string? TailInsideRunwayEnd(SfoGround ground, AircraftState follower) =>
        Assert
            .IsType<FollowingPhase>(follower.Phases?.CurrentPhase)
            .TailInsideRunwayEnd(CommandDispatcher.BuildMinimalContext(follower, ActiveRunways.Empty, ground.Layout));

    /// <summary>
    /// Stages a <see cref="LongFollowerType"/> following across 1L and 1R, cleared across both, and ticks until its centre is
    /// <see cref="CentrePastTheSliceEndFt"/> past 1L's far bar on its follow route. Returns that bar and the way the route leaves it;
    /// null on the repo's silent-skip path.
    /// </summary>
    private (SfoGround Ground, AircraftState Follower, AircraftState Leader, (GroundNode Bar, TrueHeading Onward) FarBar)? StageLateOnOneLeft()
    {
        if (StageOnOneLeftAlongTheFollowRoute(LongFollowerType) is not { } staged)
        {
            return null;
        }

        (SfoGround ground, AircraftState follower, AircraftState leader, TaxiRoute? route) = staged;
        (GroundNode farBar, TrueHeading onward) = OneLeftFarBarOn(route);
        TickUntilTheCentreIsPastTheSliceEnd(ground, follower, leader, farBar, onward);
        return (ground, follower, leader, (farBar, onward));
    }

    /// <summary>1L's far bar on <paramref name="route"/>, the last 1L bar it reaches, and the way the route leaves it.</summary>
    private static (GroundNode Bar, TrueHeading Onward) OneLeftFarBarOn(TaxiRoute? route)
    {
        TaxiRoute followRoute = Assert.IsType<TaxiRoute>(route);
        RunwayIdentifier oneLeft = SfoGroundHarness.Runway("1L").Id;
        int farIndex = followRoute.Segments.FindLastIndex(s =>
            (s.Edge.ToNode.Type == GroundNodeType.RunwayHoldShort) && (s.Edge.ToNode.RunwayId is { } runway) && runway.Overlaps(oneLeft)
        );
        Assert.True((farIndex >= 0) && (farIndex + 1 < followRoute.Segments.Count), "the follow route shows no 1L bar with a segment past it");
        return (followRoute.Segments[farIndex].Edge.ToNode, new TrueHeading(followRoute.Segments[farIndex + 1].Edge.DepartureBearing));
    }

    /// <summary>
    /// How far (ft) <paramref name="follower"/>'s centre is past <paramref name="farBar"/>, measured along <paramref name="onward"/>,
    /// the way the route leaves it.
    /// </summary>
    private static double CentrePastFt(AircraftState follower, GroundNode farBar, TrueHeading onward) =>
        GeoMath.AlongTrackDistanceNm(follower.Position, farBar.Position, onward) * GeoMath.FeetPerNm;

    /// <summary>Ticks until <paramref name="follower"/>'s centre is <see cref="CentrePastTheSliceEndFt"/> past 1L's far bar.</summary>
    private void TickUntilTheCentreIsPastTheSliceEnd(
        SfoGround ground,
        AircraftState follower,
        AircraftState leader,
        GroundNode farBar,
        TrueHeading onward
    )
    {
        int pastAt = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => CentrePastFt(follower, farBar, onward) >= CentrePastTheSliceEndFt,
            StageBudgetSeconds,
            second => LogFollow(second, follower, leader)
        );
        Assert.True(pastAt >= 0, $"the follower's centre never got {CentrePastTheSliceEndFt} ft past 1L's far bar #{farBar.Id}");
    }

    /// <summary>
    /// Sends HOLD to <paramref name="follower"/> and ticks until it rests holding in position, failing if any second sheds more than
    /// its taxi brake rate (<see cref="CrossingBrakeSlackKts"/> of slack) or its speed rises (<see cref="BrakeSlackKts"/> of rounding
    /// slack); prints where its nose rests against 1R's near bar.
    /// </summary>
    private void HoldThenBrakeToRest(SfoGround ground, AircraftState follower, AircraftState leader, (GroundNode Bar, TrueHeading Onward) farBar)
    {
        double previousKts = follower.IndicatedAirspeed;
        AssertSent(ground, Follower, "HOLD");
        Output.WriteLine(
            $"HOLD with the centre {CentrePastFt(follower, farBar.Bar, farBar.Onward):F1} ft past 1L's far bar #{farBar.Bar.Id} at "
                + $"{previousKts:F2} kt; now {follower.Phases?.CurrentPhase?.Name ?? "no phase"}"
        );
        double brakeKtsPerSecond = CategoryPerformance.TaxiDecelRate(AircraftCategorization.Categorize(follower.AircraftType));
        bool atRest = false;
        for (int second = 1; (second <= CrossBudgetSeconds) && !atRest; second++)
        {
            ground.Engine.TickOneSecond();
            LogFollow(second, follower, leader);
            double kts = follower.IndicatedAirspeed;
            string phase = follower.Phases?.CurrentPhase?.Name ?? "no phase";
            Assert.True(previousKts - kts <= brakeKtsPerSecond + CrossingBrakeSlackKts, $"+{second}s: braked {previousKts - kts:F2} kt ({phase})");
            Assert.True(kts <= previousKts + BrakeSlackKts, $"+{second}s after HOLD: sped up from {previousKts:F2} to {kts:F2} kt ({phase})");
            previousKts = kts;
            atRest = (follower.Phases?.CurrentPhase is HoldingInPositionPhase) && (kts < StoppedKts);
        }

        Assert.True(atRest, $"the follower never came to rest holding in position: {follower.Phases?.CurrentPhase?.Name ?? "no phase"}");
        RunwayInfo runway1R = SfoGroundHarness.Runway("1R");
        GroundNode oneRightBar = Assert.IsType<GroundNode>(RunwayHoldBars.NearestSameSideBar(ground.Layout, runway1R, follower.Position));
        double halfLengthNm = AircraftLength.ResolveFt(follower.AircraftType) / 2.0 / GeoMath.FeetPerNm;
        LatLon nose = GeoMath.ProjectPoint(follower.Position, follower.TrueHeading, halfLengthNm);
        double noseShortFt = RunwayHoldBars.CentrelineFt(runway1R, nose) - RunwayHoldBars.CentrelineFt(runway1R, oneRightBar.Position);
        Output.WriteLine($"at rest the nose is {noseShortFt:F2} ft short of 1R's near bar #{oneRightBar.Id}");
    }

    [Fact]
    public void HoldWhileHeldShortWithTheFollowArmed_StaysAtRestShortOfTheRunway()
    {
        if (StageHeldAtOneRightOnTheFollowRoute() is not { } staged)
        {
            return;
        }

        (SfoGround ground, AircraftState follower, AircraftState leader, HoldingShortPhase hold, _) = staged;
        LatLon heldAt = follower.Position;
        double previousKts = follower.IndicatedAirspeed;
        Assert.True(previousKts < StoppedKts, $"the follower is not at rest at the 1R bar: {previousKts:F2} kt");
        AssertSent(ground, Follower, "HOLD");
        Assert.False(IsFollowing(follower), "the follow armed behind the hold short survived HOLD");
        PhaseList phases = Assert.IsType<PhaseList>(follower.Phases);
        Assert.Same(hold, phases.CurrentPhase);
        Assert.IsType<HoldingInPositionPhase>(Assert.Single(phases.Phases.Skip(phases.CurrentIndex + 1)));
        for (int second = 1; second <= HeldSeconds; second++)
        {
            ground.Engine.TickOneSecond();
            LogFollow(second, follower, leader);
            double brakedKts = previousKts - follower.IndicatedAirspeed;
            Assert.True(brakedKts <= JetTaxiBrakeKtsPerSecond + CrossingBrakeSlackKts, $"+{second}s: braked {brakedKts:F2} kt in one second");
            Assert.True(follower.IndicatedAirspeed < StoppedKts, $"+{second}s: the held follower rolls at {follower.IndicatedAirspeed:F2} kt");
            previousKts = follower.IndicatedAirspeed;
        }

        double movedFt = GeoMath.DistanceNm(heldAt, follower.Position) * GeoMath.FeetPerNm;
        Assert.True(movedFt < 1.0, $"the follower held short of 1R moved {movedFt:F1} ft after HOLD");
        Assert.True(
            FollowingPhase.IsClearOfRunwayBars(follower, ground.Layout, SfoGroundHarness.Runway("1L")),
            "the follower held short of 1R with its tail or a wingtip inside 1L's holding position markings"
        );
    }

    [Fact]
    public void FollowingCrossInReverseOrderThenHold_HoldsOnlyOnceAcrossOneRight()
    {
        if (StageFollowingAcross("1L", "1R") is not { } staged)
        {
            return;
        }

        (SfoGround ground, AircraftState follower, AircraftState leader) = staged;
        AssertSent(ground, Follower, "CROSS 1R 1L; HOLD");
        AssertHoldsOnlyOnceClearOfOneRight(ground, follower, leader);
    }

    [Fact]
    public void FollowingCrossThenHoldThenAnother_FiresTheThirdBlockAfterTheHold()
    {
        if (StageFollowingAcross("1L", "1R") is not { } staged)
        {
            return;
        }

        (SfoGround ground, AircraftState follower, AircraftState leader) = staged;
        AssertSent(ground, Follower, "CROSS 1L 1R; HOLD; RWY 28R");
        AssertHoldsOnlyOnceClearOfOneRight(ground, follower, leader);
        int assignedAt = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => string.Equals(follower.Procedure.DepartureRunway, "28R", StringComparison.OrdinalIgnoreCase),
            HeldSeconds,
            null
        );
        Assert.True(assignedAt >= 0, $"the RWY 28R chained after HOLD never applied: runway {follower.Procedure.DepartureRunway ?? "none"}");
        Assert.DoesNotContain(follower.Queue.Blocks, b => !b.IsApplied);
    }

    [Fact]
    public void FollowingCrossThenHold_BreaksOffTheFollowWhenTheHoldFires()
    {
        if (StageFollowingAcross("1L", "1R") is not { } staged)
        {
            return;
        }

        (SfoGround ground, AircraftState follower, AircraftState leader) = staged;
        AssertSent(ground, Follower, "CROSS 1L 1R; HOLD");
        int heldAt = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => follower.Ground.Hold is not null,
            StageBudgetSeconds,
            second => LogFollow(second, follower, leader)
        );
        Assert.True(heldAt >= 0, $"the HOLD chained behind CROSS 1L 1R never applied within {StageBudgetSeconds}s");
        AssertComesToRestClearOfOneRight(ground, follower, leader, SfoGroundHarness.Runway("1R"));
        Assert.False(IsFollowing(follower), $"the follower still follows once the chained HOLD fired: {follower.Phases?.CurrentPhase?.Name}");
    }

    [Fact]
    public void HeldAtABar_CrossOfARunwayOffTheRoute_IsRefusedAndStaysHeld()
    {
        if (StageHeldAtOneRightOnTheFollowRoute() is not { } staged)
        {
            return;
        }

        (SfoGround ground, AircraftState follower, _, HoldingShortPhase hold, _) = staged;
        CommandResult result = ground.Engine.SendCommand(Follower, $"CROSS {OffRouteRunway}");
        Output.WriteLine($"CROSS {OffRouteRunway}: success={result.Success} '{result.Message}'");

        Assert.False(result.Success, $"CROSS {OffRouteRunway} off the follower's route was accepted");
        PilotSpeechText unable = Assert.IsType<PilotSpeechText>(result.PilotUnable);
        Assert.Equal($"Unable, runway {OffRouteRunway} is not on our route, request taxi instructions", unable.Terminal);
        Assert.EndsWith(", unable, runway two eight right isn't on our route, request taxi.", unable.Tts, StringComparison.Ordinal);
        AssertStillHeldWithTheFollowArmed(ground, follower, hold);
    }

    [Fact]
    public void MultiRunwayCross_WithOneOffRoute_IsRefusedWhole()
    {
        if (StageHeldAtOneRightOnTheFollowRoute() is not { } staged)
        {
            return;
        }

        (SfoGround ground, AircraftState follower, _, HoldingShortPhase hold, _) = staged;
        CommandResult result = ground.Engine.SendCommand(Follower, $"CROSS 1R {OffRouteRunway}");
        Output.WriteLine($"CROSS 1R {OffRouteRunway}: success={result.Success} '{result.Message}'");

        Assert.False(result.Success, $"CROSS 1R {OffRouteRunway} naming a runway off the follower's route was accepted");
        PilotSpeechText unable = Assert.IsType<PilotSpeechText>(result.PilotUnable);
        Assert.Equal($"Unable, runway {OffRouteRunway} is not on our route, request taxi instructions", unable.Terminal);
        Assert.DoesNotContain(hold.Requirements, r => r.IsSatisfied);
        FollowingPhase armed = AssertStillHeldWithTheFollowArmed(ground, follower, hold);
        Assert.Empty(armed.CrossingClearedRunways);
    }

    [Fact]
    public void LeadWithNoTrail_Cross_KeepsTodaysCrossing()
    {
        if (Stage(TaxiToOneRightBar, "1R") is not { } staged)
        {
            return;
        }

        (SfoGround ground, AircraftState follower, AircraftState leader) = staged;
        StandLeaderWithNoRoute(ground, leader, ToFJunction);
        AssertSent(ground, Follower, $"FOLLOWG {Leader}");
        HoldingShortPhase hold = Assert.IsType<HoldingShortPhase>(follower.Phases?.CurrentPhase);
        AssertSent(ground, Follower, "CROSS 1R");

        int crossingAt = SfoGroundHarness.TickUntil(ground.Engine, () => follower.Phases?.CurrentPhase is CrossingRunwayPhase, HeldSeconds, null);
        Assert.True(crossingAt >= 0, $"the follower never started crossing 1R: {follower.Phases?.CurrentPhase?.Name}");
        CrossingRunwayPhase crossing = Assert.IsType<CrossingRunwayPhase>(follower.Phases?.CurrentPhase);
        Assert.Equal(hold.HoldShort.NodeId, crossing.ApproachNodeId);
        GroundNode farBar = ground.Layout.Nodes[crossing.TargetNodeId];
        Assert.Equal(GroundNodeType.RunwayHoldShort, farBar.Type);
        Assert.True(farBar.RunwayId?.Overlaps(RunwayIdentifier.Parse("1R")), $"the crossing ends at node {farBar.Id}, not a 1R bar");
        int followingAt = SfoGroundHarness.TickUntil(ground.Engine, () => follower.Phases?.CurrentPhase is FollowingPhase, CrossBudgetSeconds, null);
        Assert.True(followingAt >= 0, $"the follower is not following after crossing 1R: {follower.Phases?.CurrentPhase?.Name}");
    }

    /// <summary>
    /// Stages the follower released across 1L behind <see cref="LeaderAcrossF"/>, following until it holds at the 1R bar on F1,
    /// with the follow that took the hold still carrying its follow route. Null on the repo's silent-skip path.
    /// </summary>
    private (
        SfoGround Ground,
        AircraftState Follower,
        AircraftState Leader,
        HoldingShortPhase Hold,
        TaxiRoute FollowRoute
    )? StageHeldAtOneRightOnTheFollowRoute()
    {
        if (StageBehind(TaxiToOneLeftBar, "1L", LeaderAcrossF, Type) is not { } staged)
        {
            return null;
        }

        (SfoGround ground, AircraftState follower, AircraftState leader) = staged;
        AssertSent(ground, Follower, $"FOLLOWG {Leader}; CROSS 1L");
        int heldAt = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => (follower.Phases?.CurrentPhase is HoldingShortPhase hold) && SfoGroundHarness.HoldShortMatches(hold.HoldShort, "1R"),
            CrossBudgetSeconds,
            second => LogFollow(second, follower, leader)
        );
        Assert.True(heldAt >= 0, $"the follower never held short of 1R after crossing 1L: {follower.Phases?.CurrentPhase?.Name}");
        HoldingShortPhase oneRight = Assert.IsType<HoldingShortPhase>(follower.Phases?.CurrentPhase);
        TaxiRoute followRoute = Assert.IsType<TaxiRoute>(HeldFollowRoute(follower));
        IReadOnlyList<DirectionalEdge> path = RouteEdges(followRoute);
        Output.WriteLine($"held at 1R bar #{oneRight.HoldShort.NodeId}; follow route {followRoute.ToSummary()}");
        foreach (string runway in new[] { "1L", "1R", "28L", "28R" })
        {
            Output.WriteLine($"   {runway} on the follow route: {FollowRoutePlanner.PathCrossesRunway(path, RunwayIdentifier.Parse(runway))}");
        }

        return (ground, follower, leader, oneRight, followRoute);
    }

    /// <summary>
    /// Spawns the leader and the follower, clears the follower to the 1L bar and the leader on <see cref="LeaderAcrossF"/>, then
    /// has the follower follow at once, while it taxis, and ticks until its follow route crosses every one of
    /// <paramref name="runways"/>. Null on the repo's silent-skip path.
    /// </summary>
    private (SfoGround Ground, AircraftState Follower, AircraftState Leader)? StageFollowingAcross(params string[] runways) =>
        StageFollowingAcrossAs(Type, runways);

    /// <summary>As <see cref="StageFollowingAcross"/>, the follower a <paramref name="followerType"/>.</summary>
    private (SfoGround Ground, AircraftState Follower, AircraftState Leader)? StageFollowingAcrossAs(string followerType, params string[] runways)
    {
        if (SfoGroundHarness.Build(Output, autoCrossMode: AutoCrossRunwayMode.Off) is not { } ground)
        {
            Output.WriteLine("SKIP: SFO layout or navdata unavailable");
            return null;
        }

        AircraftState leader = SfoGroundHarness.SpawnAtSpot(ground, Leader, Type, "1");
        AircraftState follower = SfoGroundHarness.SpawnAtSpot(ground, Follower, followerType, "3");
        AssertSent(ground, Follower, TaxiToOneLeftBar);
        ground.Engine.TickOneSecond();
        AssertSent(ground, Leader, LeaderAcrossF);
        AssertSent(ground, Follower, $"FOLLOWG {Leader}");
        int routedAt = SfoGroundHarness.TickUntil(
            ground.Engine,
            () =>
                (follower.Phases?.CurrentPhase is FollowingPhase { FollowRoute: { } route })
                && runways.All(r => FollowRoutePlanner.PathCrossesRunway(RouteEdges(route), RunwayIdentifier.Parse(r))),
            CrossBudgetSeconds,
            second => LogFollow(second, follower, leader)
        );
        Assert.True(routedAt >= 0, $"the follower never followed on a route across {string.Join(" and ", runways)}");
        return (ground, follower, leader);
    }

    /// <summary>
    /// Ticks <paramref name="ground"/> until <paramref name="follower"/>'s chained HOLD applies, failing unless it applies and only
    /// once the follower has crossed 1R and is clear of it.
    /// </summary>
    private void AssertHoldsOnlyOnceClearOfOneRight(SfoGround ground, AircraftState follower, AircraftState leader)
    {
        RunwayInfo runway1R = SfoGroundHarness.Runway("1R");
        bool wasOnOneRight = false;
        int clearOfOneRightAt = -1;
        int heldAt = -1;
        for (int second = 1; (second <= StageBudgetSeconds) && (heldAt < 0); second++)
        {
            ground.Engine.TickOneSecond();
            LogFollow(second, follower, leader);
            bool onOneRight = RunwayOccupancy.IsOnPavement(follower, runway1R);
            wasOnOneRight |= onOneRight;
            if (wasOnOneRight && !onOneRight && (clearOfOneRightAt < 0))
            {
                clearOfOneRightAt = second;
            }

            if (follower.Ground.Hold is not null)
            {
                heldAt = second;
            }
        }

        Output.WriteLine($"on 1R: {wasOnOneRight}, clear of 1R at +{clearOfOneRightAt}s, HOLD applied at +{heldAt}s");
        Assert.True(heldAt > 0, $"the HOLD never applied within {StageBudgetSeconds}s: stranded");
        Assert.True(wasOnOneRight && (clearOfOneRightAt > 0) && (heldAt >= clearOfOneRightAt), "the HOLD applied before the follower crossed 1R");
        AssertComesToRestClearOfOneRight(ground, follower, leader, runway1R);
    }

    /// <summary>
    /// Ticks until <paramref name="follower"/> is at rest with a phase current, failing unless it comes to rest within
    /// <see cref="CrossBudgetSeconds"/> with its tail and wingtips past the far bars of <paramref name="runway1R"/>.
    /// </summary>
    private void AssertComesToRestClearOfOneRight(SfoGround ground, AircraftState follower, AircraftState leader, RunwayInfo runway1R)
    {
        int restAt = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => (follower.Phases?.CurrentPhase is HoldingInPositionPhase) && (follower.IndicatedAirspeed < StoppedKts),
            CrossBudgetSeconds,
            second => LogFollow(second, follower, leader)
        );
        Assert.True(restAt >= 0, $"the follower never came to rest holding in position: {follower.Phases?.CurrentPhase?.Name ?? "no phase"}");
        Assert.True(
            FollowingPhase.IsClearOfRunwayBars(follower, ground.Layout, runway1R),
            "the follower came to rest with its tail or a wingtip inside 1R's holding position markings"
        );
    }

    /// <summary>
    /// Sends RES and ticks until <paramref name="follower"/> is at rest holding in position, failing if any one second from the
    /// speed read before the RES sheds more than a jet's taxi brake rate (<see cref="CrossingBrakeSlackKts"/> of slack), or unless
    /// its tail and wingtips are past the hold line of the far 1R bar it crossed to: the 1R bar on its side of the runway nearest it.
    /// </summary>
    private void AssertResComesToRestPastTheFarBar(SfoGround ground, AircraftState follower, AircraftState leader, RunwayInfo runway1R)
    {
        double previousKts = follower.IndicatedAirspeed;
        AssertSent(ground, Follower, "RES");
        bool atRest = false;
        for (int second = 1; (second <= CrossBudgetSeconds) && !atRest; second++)
        {
            ground.Engine.TickOneSecond();
            LogFollow(second, follower, leader);
            double brakedKts = previousKts - follower.IndicatedAirspeed;
            Assert.True(
                brakedKts <= JetTaxiBrakeKtsPerSecond + CrossingBrakeSlackKts,
                $"+{second}s after RES: braked {brakedKts:F2} kt in one second ({follower.Phases?.CurrentPhase?.Name ?? "no phase"})"
            );
            previousKts = follower.IndicatedAirspeed;
            atRest = (follower.Phases?.CurrentPhase is HoldingInPositionPhase) && (follower.IndicatedAirspeed < StoppedKts);
        }

        Assert.True(atRest, $"the follower never came to rest holding in position: {follower.Phases?.CurrentPhase?.Name ?? "no phase"}");
        bool right = RunwayHoldBars.IsRightOfCentreline(runway1R, follower.Position);
        GroundNode farBar = RunwayHoldBars
            .RunwayBars(ground.Layout, runway1R)
            .Where(b => RunwayHoldBars.IsRightOfCentreline(runway1R, b.Position) == right)
            .MinBy(b => GeoMath.DistanceNm(b.Position, follower.Position))!;
        Assert.True(
            FollowingPhase.IsClearPastBar(follower, runway1R, farBar),
            $"the follower came to rest with its tail or a wingtip short of the far 1R bar #{farBar.Id}"
        );
    }

    /// <summary>
    /// Waits for <paramref name="follower"/> to roll faster than <see cref="MovingKts"/>, sends HOLD, and fails unless it
    /// brakes no harder than a jet's taxi brake rate and comes to rest holding in position, no longer following.
    /// </summary>
    private void AssertBreaksOffMovingAndComesToRest(SfoGround ground, AircraftState follower, AircraftState leader)
    {
        int movingAt = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => (follower.Phases?.CurrentPhase is FollowingPhase) && (follower.IndicatedAirspeed > MovingKts),
            CrossBudgetSeconds,
            second => LogFollow(second, follower, leader)
        );
        Assert.True(movingAt >= 0, $"the follower never rolled faster than {MovingKts} kt while following");
        double previousKts = follower.IndicatedAirspeed;
        AssertSent(ground, Follower, "HOLD");
        bool atRest = false;
        for (int second = 1; (second <= HeldSeconds) && !atRest; second++)
        {
            ground.Engine.TickOneSecond();
            double brakedKts = previousKts - follower.IndicatedAirspeed;
            Assert.True(brakedKts <= JetTaxiBrakeKtsPerSecond + BrakeSlackKts, $"+{second}s: braked {brakedKts:F2} kt in one second");
            previousKts = follower.IndicatedAirspeed;
            atRest = (follower.Phases?.CurrentPhase is HoldingInPositionPhase) && (follower.IndicatedAirspeed < StoppedKts);
        }

        Assert.True(atRest, $"the follower never came to rest holding in position: {follower.Phases?.CurrentPhase?.Name}");
        Assert.False(IsFollowing(follower), $"the follower still follows after HOLD: {follower.Phases?.CurrentPhase?.Name}");
    }

    /// <summary>Whether <paramref name="aircraft"/> follows, or has a follow armed ahead in its phase list.</summary>
    private static bool IsFollowing(AircraftState aircraft) =>
        (aircraft.Phases is { } phases) && phases.Phases.Skip(phases.CurrentIndex).Any(static phase => phase is FollowingPhase);

    /// <summary>The follower as a fresh SFO engine restored from <paramref name="ground"/>'s snapshot has it.</summary>
    private AircraftState RestoredFollower(SfoGround ground) => Restored(ground).Follower;

    /// <summary>A fresh SFO engine restored from <paramref name="ground"/>'s snapshot, and the follower as it has it.</summary>
    private (SfoGround Ground, AircraftState Follower) Restored(SfoGround ground)
    {
        StateSnapshotDto snapshot = ground.Engine.CaptureSnapshot();
        SfoGround restoredGround = Assert.IsType<SfoGround>(SfoGroundHarness.Build(Output, autoCrossMode: AutoCrossRunwayMode.Off));
        restoredGround.Engine.RestoreFromSnapshot(snapshot);
        return (restoredGround, Assert.IsType<AircraftState>(restoredGround.Engine.World.GetSnapshot().FirstOrDefault(a => a.Callsign == Follower)));
    }

    /// <summary>
    /// Stages the follower held at the 1R bar of its own taxi route (<see cref="FollowGroundAtRunwayBarTestBase.TaxiToOneRightBar"/>),
    /// arms a follow of the leader there and releases it across 1R: with the taxi route resuming from that bar, the crossing slices
    /// the taxi route (<c>TaxiingPhase.BuildCrossingFromRouteBar</c>), which goes on past the far bar. Then sends HOLD once the
    /// crossing is under way. Returns the far bar the crossing drives to; null on the repo's silent-skip path.
    /// </summary>
    private (SfoGround Ground, AircraftState Follower, AircraftState Leader, int FarBarId)? StageHeldDuringACrossingOfItsTaxiRoute()
    {
        if (Stage(TaxiToOneRightBar, "1R") is not { } staged)
        {
            return null;
        }

        (SfoGround ground, AircraftState follower, AircraftState leader) = staged;
        HoldingShortPhase hold = Assert.IsType<HoldingShortPhase>(follower.Phases?.CurrentPhase);
        AssertSent(ground, Follower, $"FOLLOWG {Leader}");
        TaxiRoute route = Assert.IsType<TaxiRoute>(follower.Ground.AssignedTaxiRoute);
        Assert.False(route.IsComplete, "the follower's taxi route is complete at the 1R bar");
        Assert.Equal(hold.HoldShort.NodeId, route.CurrentSegment?.FromNodeId);
        AssertSent(ground, Follower, "CROSS 1R");
        int farBarId = HoldOnceTheCrossingIsUnderWay(ground, follower, leader);
        Output.WriteLine($"crossing 1R from bar #{hold.HoldShort.NodeId} to #{farBarId} on taxi route {route.ToSummary()}");
        return (ground, follower, leader, farBarId);
    }

    /// <summary>
    /// Ticks until <paramref name="follower"/> crosses with its nose past the near bar, then sends HOLD, failing unless the follow
    /// armed behind the crossing goes; returns the far bar the crossing drives to.
    /// </summary>
    private int HoldOnceTheCrossingIsUnderWay(SfoGround ground, AircraftState follower, AircraftState leader)
    {
        TickUntilTheCrossingIsUnderWay(ground, follower, leader);
        AssertSent(ground, Follower, "HOLD");
        Assert.False(IsFollowing(follower), "the follow armed behind the crossing survived HOLD");
        return Assert.IsType<CrossingRunwayPhase>(follower.Phases?.CurrentPhase).TargetNodeId;
    }

    /// <summary>Ticks until <paramref name="follower"/> crosses a runway with its nose past the near bar, failing if it never does.</summary>
    private void TickUntilTheCrossingIsUnderWay(SfoGround ground, AircraftState follower, AircraftState leader)
    {
        int underWayAt = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => (follower.Phases?.CurrentPhase is CrossingRunwayPhase crossing) && (crossing.NosePastNearMarkingFt(follower, ground.Layout) > 0.0),
            CrossBudgetSeconds,
            second => LogFollow(second, follower, leader)
        );
        Assert.True(underWayAt >= 0, $"the follower's nose never got past the near bar of a crossing: {follower.Phases?.CurrentPhase?.Name}");
    }

    /// <summary>
    /// Sends HOLD to <paramref name="follower"/> crossing 1L, failing unless a crossing is current after it, and prints how far along
    /// <paramref name="route"/> 1R's near bar lies past the crossing's far bar, beside the follower's length and the reach of a
    /// crossing that ends at rest. Returns the far bar and that gap (null when the route does not show it).
    /// </summary>
    private (int FarBarId, double? GapFt) HoldOnOneLeft(SfoGround ground, AircraftState follower, TaxiRoute? route)
    {
        AssertSent(ground, Follower, "HOLD");
        CrossingRunwayPhase crossing = Assert.IsType<CrossingRunwayPhase>(follower.Phases?.CurrentPhase);
        double lengthFt = AircraftLength.ResolveFt(follower.AircraftType);
        double? gapFt = AlongRouteGapFt(route, crossing.TargetNodeId, SfoGroundHarness.Runway("1R").Id);
        Output.WriteLine(
            $"{follower.AircraftType} {lengthFt:F1} ft long; 1L far bar #{crossing.TargetNodeId}: 1R's near bar {gapFt:F1} ft on along "
                + $"the route; reach {(lengthFt / 2.0) + RunwayTailClearance.MarginFt:F1} ft"
        );
        return (crossing.TargetNodeId, gapFt);
    }

    /// <summary>Ticks until the held <paramref name="follower"/> stops, sends RES and ticks until it rests holding in position.</summary>
    private void ResThenRestHoldingInPosition(SfoGround ground, AircraftState follower, AircraftState leader)
    {
        int stoppedAt = SfoGroundHarness.TickUntil(ground.Engine, () => follower.IndicatedAirspeed < StoppedKts, HeldSeconds, null);
        Assert.True(stoppedAt >= 0, $"the held follower never stopped within {HeldSeconds}s: {follower.IndicatedAirspeed:F2} kt");
        AssertSent(ground, Follower, "RES");
        int restAt = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => (follower.Phases?.CurrentPhase is HoldingInPositionPhase) && (follower.IndicatedAirspeed < StoppedKts),
            CrossBudgetSeconds,
            second => LogFollow(second, follower, leader)
        );
        Assert.True(restAt >= 0, $"the follower never came to rest holding in position: {follower.Phases?.CurrentPhase?.Name ?? "no phase"}");
    }

    /// <summary>
    /// Fails unless <paramref name="follower"/>'s tail and wingtips are past the hold line of 1L's bar <paramref name="farBarId"/>.
    /// </summary>
    private void AssertTailClearOfOneLeft(SfoGround ground, AircraftState follower, int farBarId)
    {
        RecordTailOnOneLeft(ground, follower, farBarId);
        Assert.True(
            FollowingPhase.IsClearPastBar(follower, SfoGroundHarness.Runway("1L"), ground.Layout.Nodes[farBarId]),
            $"the follower came to rest with its tail or a wingtip short of 1L's far bar #{farBarId}"
        );
    }

    /// <summary>
    /// Prints how far <paramref name="follower"/>'s tail or a wingtip is short of the hold line of 1L's bar <paramref name="farBarId"/>.
    /// </summary>
    private void RecordTailOnOneLeft(SfoGround ground, AircraftState follower, int farBarId) =>
        Output.WriteLine(
            $"at rest {FollowingPhase.ShortOfBarFt(ground.Layout, farBarId, follower):F2} ft short of clearing 1L's far bar #{farBarId}"
        );

    /// <summary>The pilot lines and warnings <paramref name="ground"/>'s engine emits for <paramref name="aircraft"/> from now on.</summary>
    private static List<string> CapturePilotLines(SfoGround ground, AircraftState aircraft)
    {
        List<string> lines = [];
        ground.Engine.WarningEmitted += (callsign, line) =>
        {
            if (callsign == aircraft.Callsign)
            {
                lines.Add(line);
            }
        };
        ground.Engine.TerminalEntryEmitted += entry =>
        {
            if (entry.Callsign == aircraft.Callsign)
            {
                lines.Add(entry.Message);
            }
        };
        return lines;
    }

    /// <summary>
    /// Fails unless <paramref name="follower"/>, at rest with its nose at 1R's bar and its tail inside 1L's holding position marking,
    /// holds in the break-off hold as an occupant of 1L, has reported holding short of 1R, not clear of 1L, in <paramref name="lines"/>,
    /// and still refuses RES.
    /// </summary>
    private void AssertHoldsShortOfOneRightNotClearOfOneLeft(SfoGround ground, AircraftState follower, List<string> lines)
    {
        HoldingInPositionPhase hold = Assert.IsType<HoldingInPositionPhase>(follower.Phases?.CurrentPhase);
        foreach (string line in lines)
        {
            Output.WriteLine($"pilot: {line}");
        }

        Output.WriteLine($"holding short of {hold.HeldShortOfRunway ?? "none"}, tail inside {hold.TailInsideRunway ?? "none"}");
        Assert.True(hold.EndsBrokenOffFollow, "the hold the HOLD broke the follow off into lost its break-off flag");
        RunwayInfo runway1L = SfoGroundHarness.Runway("1L");
        Assert.True(
            (hold.TailInsideRunway is { } tailInside) && RunwayIdentifier.Parse(tailInside).Overlaps(runway1L.Id),
            $"the hold's tail-inside runway {hold.TailInsideRunway ?? "none"} is not an end of 1L/19R"
        );
        Assert.True(
            (hold.HeldShortOfRunway is { } heldShort) && RunwayIdentifier.Parse(heldShort).Overlaps(SfoGroundHarness.Runway("1R").Id),
            $"the hold's held-short runway {hold.HeldShortOfRunway ?? "none"} is not an end of 1R/19L"
        );
        // Fouling comes from position (the tail past 1L's hold line, off its pavement) and was already the case before the hold
        // recorded the tail: the hold does not assign 1L, so the tail-inside phase arm of the classification is not reached.
        Assert.Equal(RunwayUseKind.Fouling, RunwayOccupancy.Classify(follower, runway1L, ground.Layout)?.Kind);
        Assert.True(RunwayOccupancy.IsTailInsideRunwayState(follower), "the hold does not count the follower's tail as inside 1L");
        Assert.Single(
            lines,
            line =>
                line.Contains($"holding short of runway {hold.HeldShortOfRunway}", StringComparison.OrdinalIgnoreCase)
                && line.Contains($"not clear of runway {hold.TailInsideRunway}", StringComparison.OrdinalIgnoreCase)
        );
        AssertResRefused(ground);
    }

    /// <summary>
    /// Fails unless <paramref name="follower"/> holds with no tail inside 1L's holding position marking and has made no not-clear
    /// report in <paramref name="lines"/>.
    /// </summary>
    private void AssertHoldsWithNoTailInsideOneLeft(AircraftState follower, List<string> lines)
    {
        HoldingInPositionPhase hold = Assert.IsType<HoldingInPositionPhase>(follower.Phases?.CurrentPhase);
        foreach (string line in lines)
        {
            Output.WriteLine($"pilot: {line}");
        }

        Output.WriteLine($"holding short of {hold.HeldShortOfRunway ?? "none"}, tail inside {hold.TailInsideRunway ?? "none"}");
        Assert.Null(hold.TailInsideRunway);
        Assert.DoesNotContain(lines, line => line.Contains("not clear of runway", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Fails unless the nose of <paramref name="follower"/>, <paramref name="lengthFt"/> long, is short of the hold line of the 1R bar
    /// nearest it on its side of 1R (<see cref="AssertNoseShortOfOneRightBar"/>).
    /// </summary>
    private void AssertNoseShortOfOneRight(SfoGround ground, AircraftState follower, double lengthFt) =>
        AssertNoseShortOfOneRightBar(
            follower,
            lengthFt,
            Assert.IsType<GroundNode>(RunwayHoldBars.NearestSameSideBar(ground.Layout, SfoGroundHarness.Runway("1R"), follower.Position))
        );

    /// <summary>The node of <paramref name="route"/>'s uncleared hold short of 1R, failing unless it has one.</summary>
    private static GroundNode OneRightHoldShortOn(SfoGround ground, TaxiRoute? route)
    {
        RunwayIdentifier oneRight = SfoGroundHarness.Runway("1R").Id;
        HoldShortPoint? holdShort = route?.HoldShortPoints.FirstOrDefault(h =>
            (!h.IsCleared)
            && ground.Layout.Nodes.TryGetValue(h.NodeId, out GroundNode? node)
            && (node.RunwayId is { } runway)
            && runway.Overlaps(oneRight)
        );
        Assert.NotNull(holdShort);
        return ground.Layout.Nodes[holdShort.NodeId];
    }

    /// <summary>
    /// Fails unless the nose of <paramref name="follower"/>, <paramref name="lengthFt"/> long, is short of the hold line of the 1R bar
    /// <paramref name="nearBar"/>: on its side of 1R, farther from 1R's centreline than the bar.
    /// </summary>
    private void AssertNoseShortOfOneRightBar(AircraftState follower, double lengthFt, GroundNode nearBar)
    {
        RunwayInfo runway1R = SfoGroundHarness.Runway("1R");
        LatLon nose = GeoMath.ProjectPoint(follower.Position, follower.TrueHeading, lengthFt / 2.0 / GeoMath.FeetPerNm);
        bool onTheNearSide = RunwayHoldBars.IsRightOfCentreline(runway1R, nose) == RunwayHoldBars.IsRightOfCentreline(runway1R, nearBar.Position);
        double noseShortFt = RunwayHoldBars.CentrelineFt(runway1R, nose) - RunwayHoldBars.CentrelineFt(runway1R, nearBar.Position);
        Output.WriteLine($"at rest the nose is {noseShortFt:F2} ft short of 1R's near bar #{nearBar.Id}");
        Assert.True(
            onTheNearSide && (noseShortFt > 0.0),
            $"the follower came to rest with its nose {-noseShortFt:F2} ft past 1R's near bar #{nearBar.Id}"
        );
    }

    /// <summary>
    /// Stages a <paramref name="followerType"/> following across 1L and 1R, cleared across both, and ticks until it is on 1L's
    /// pavement, inside its holding position markings. Returns the follow route it drives; null on the repo's silent-skip path.
    /// </summary>
    private (SfoGround Ground, AircraftState Follower, AircraftState Leader, TaxiRoute? Route)? StageOnOneLeftAlongTheFollowRoute(string followerType)
    {
        if (StageFollowingAcrossAs(followerType, "1L", "1R") is not { } staged)
        {
            return null;
        }

        (SfoGround ground, AircraftState follower, AircraftState leader) = staged;
        AssertSent(ground, Follower, "CROSS 1L 1R");
        RunwayInfo runway1L = SfoGroundHarness.Runway("1L");
        int onAt = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => RunwayOccupancy.IsOnPavement(follower, runway1L),
            StageBudgetSeconds,
            second => LogFollow(second, follower, leader)
        );
        Assert.True(onAt >= 0, $"the follower never got onto 1L within {StageBudgetSeconds}s");
        return (ground, follower, leader, Assert.IsType<FollowingPhase>(follower.Phases?.CurrentPhase).FollowRoute);
    }

    /// <summary>
    /// Stages a <paramref name="followerType"/> held at the 1L bar of its own taxi route, arms a follow of the leader there with 1L
    /// cleared, and ticks until the crossing into the armed follow is under way. Returns its taxi route; null on the repo's
    /// silent-skip path.
    /// </summary>
    private (SfoGround Ground, AircraftState Follower, AircraftState Leader, TaxiRoute? Route)? StageArmedFollowCrossingOfOneLeft(string followerType)
    {
        if (StageBehind(TaxiToOneLeftBar, "1L", LeaderAcrossF, Type, followerType) is not { } staged)
        {
            return null;
        }

        (SfoGround ground, AircraftState follower, AircraftState leader) = staged;
        AssertSent(ground, Follower, $"FOLLOWG {Leader}; CROSS 1L");
        TickUntilTheCrossingIsUnderWay(ground, follower, leader);
        return (ground, follower, leader, follower.Ground.AssignedTaxiRoute);
    }

    /// <summary>
    /// How far (ft) along <paramref name="route"/> the first hold-short bar of <paramref name="toRunway"/> lies past the node
    /// <paramref name="fromNodeId"/>; null when the route does not reach that node or no such bar follows it.
    /// </summary>
    private static double? AlongRouteGapFt(TaxiRoute? route, int fromNodeId, RunwayIdentifier toRunway)
    {
        int from = route?.Segments.FindIndex(s => s.ToNodeId == fromNodeId) ?? -1;
        if ((route is null) || (from < 0))
        {
            return null;
        }

        int to = route.Segments.FindIndex(
            from + 1,
            s => (s.Edge.ToNode.Type == GroundNodeType.RunwayHoldShort) && (s.Edge.ToNode.RunwayId is { } runway) && runway.Overlaps(toRunway)
        );
        return (to < 0) ? null : route.PrefixDistanceFt(to + 1) - route.PrefixDistanceFt(from + 1);
    }

    /// <summary>
    /// After a HOLD that breaks off <paramref name="follower"/>'s follow mid-crossing: ticks <paramref name="snapshotAfterHoldSeconds"/>,
    /// restores the engine's snapshot into a second SFO engine, ticks both to <see cref="ResAfterHoldSeconds"/> after the HOLD, sends
    /// both RES and ticks both until the live follower rests holding in position, failing unless the restored follower is in the live
    /// one's phase and within <see cref="RestoredAgreesFt"/> of it every second.
    /// </summary>
    private void AssertRestoredAfterTheHoldGoesOnAsTheLiveRun(SfoGround ground, AircraftState follower, int snapshotAfterHoldSeconds)
    {
        for (int second = 1; second <= snapshotAfterHoldSeconds; second++)
        {
            ground.Engine.TickOneSecond();
        }

        Output.WriteLine($"snapshot {snapshotAfterHoldSeconds}s after HOLD, RES {ResAfterHoldSeconds}s after it");
        (SfoGround restoredGround, AircraftState restored) = Restored(ground);
        ResBothAndTickToRest((ground, follower), (restoredGround, restored), snapshotAfterHoldSeconds + 1);
    }

    /// <summary>
    /// After a HOLD sent to both the live follower and the one restored from it: ticks both from <paramref name="firstSecond"/> to
    /// <see cref="ResAfterHoldSeconds"/> after the HOLD, sends both RES and ticks both until the live follower rests holding in
    /// position, failing unless the restored follower is in the live one's phase and within <see cref="RestoredAgreesFt"/> of it every
    /// second.
    /// </summary>
    private void ResBothAndTickToRest(
        (SfoGround Ground, AircraftState Follower) live,
        (SfoGround Ground, AircraftState Follower) restored,
        int firstSecond
    )
    {
        for (int second = firstSecond; second <= ResAfterHoldSeconds; second++)
        {
            TickBothAndAssertTheyAgree(live, restored, $"+{second}s after HOLD");
        }

        AssertSent(live.Ground, Follower, "RES");
        AssertSent(restored.Ground, Follower, "RES");
        bool atRest = false;
        for (int second = 1; (second <= CrossBudgetSeconds) && !atRest; second++)
        {
            TickBothAndAssertTheyAgree(live, restored, $"+{second}s after RES");
            atRest = (live.Follower.Phases?.CurrentPhase is HoldingInPositionPhase) && (live.Follower.IndicatedAirspeed < StoppedKts);
        }

        Assert.True(atRest, $"the follower never came to rest holding in position: {live.Follower.Phases?.CurrentPhase?.Name ?? "no phase"}");
        HoldingInPositionPhase liveHold = Assert.IsType<HoldingInPositionPhase>(live.Follower.Phases?.CurrentPhase);
        HoldingInPositionPhase restoredHold = Assert.IsType<HoldingInPositionPhase>(restored.Follower.Phases?.CurrentPhase);
        Output.WriteLine(
            $"live hold: short of {liveHold.HeldShortOfRunway ?? "none"}, tail inside {liveHold.TailInsideRunway ?? "none"}; "
                + $"restored hold: short of {restoredHold.HeldShortOfRunway ?? "none"}, tail inside {restoredHold.TailInsideRunway ?? "none"}"
        );
        Assert.Equal(liveHold.HeldShortOfRunway, restoredHold.HeldShortOfRunway);
        Assert.Equal(liveHold.TailInsideRunway, restoredHold.TailInsideRunway);
        Assert.Equal(liveHold.EndsBrokenOffFollow, restoredHold.EndsBrokenOffFollow);
        Assert.Equal(liveHold.NotClearReportPositions, restoredHold.NotClearReportPositions);
    }

    /// <summary>
    /// Right after a HOLD sent to both <paramref name="live"/> and the follower restored from it, before either has ticked again:
    /// fails unless the restored crossing has built its slice and plans the live one's tail decision and held-short bar.
    /// </summary>
    private void AssertRestoredCrossingPlansAsTheLiveOne(AircraftState live, AircraftState restored)
    {
        CrossingRunwayPhase liveCrossing = Assert.IsType<CrossingRunwayPhase>(live.Phases?.CurrentPhase);
        CrossingRunwayPhase restoredCrossing = Assert.IsType<CrossingRunwayPhase>(restored.Phases?.CurrentPhase);
        CrossingRunwayPhaseDto livePlan = Assert.IsType<CrossingRunwayPhaseDto>(liveCrossing.ToSnapshot());
        CrossingRunwayPhaseDto restoredPlan = Assert.IsType<CrossingRunwayPhaseDto>(restoredCrossing.ToSnapshot());
        Output.WriteLine(
            $"after HOLD: live tail suppressed={livePlan.TailClearanceSuppressed} segments={livePlan.TailFullSegmentCount} "
                + $"cut={livePlan.TailCutFt:F2} bar=#{livePlan.HeldShortBarNodeId}; restored route built={restoredCrossing.CrossingRoute is not null} "
                + $"tail suppressed={restoredPlan.TailClearanceSuppressed} segments={restoredPlan.TailFullSegmentCount} "
                + $"cut={restoredPlan.TailCutFt:F2} bar=#{restoredPlan.HeldShortBarNodeId}"
        );
        Assert.NotNull(restoredCrossing.CrossingRoute);
        Assert.Equal(livePlan.TailClearanceSuppressed, restoredPlan.TailClearanceSuppressed);
        Assert.Equal(livePlan.TailFullSegmentCount, restoredPlan.TailFullSegmentCount);
        Assert.Equal(livePlan.TailCutFt, restoredPlan.TailCutFt);
        Assert.Equal(livePlan.HeldShortBarNodeId, restoredPlan.HeldShortBarNodeId);
    }

    /// <summary>
    /// Ticks the live world and the world restored from it one second each, failing unless the restored follower is in the live
    /// one's phase and within <see cref="RestoredAgreesFt"/> of it.
    /// </summary>
    private void TickBothAndAssertTheyAgree(
        (SfoGround Ground, AircraftState Follower) live,
        (SfoGround Ground, AircraftState Follower) restored,
        string when
    )
    {
        live.Ground.Engine.TickOneSecond();
        restored.Ground.Engine.TickOneSecond();
        double apartFt = GeoMath.DistanceNm(live.Follower.Position, restored.Follower.Position) * GeoMath.FeetPerNm;
        Output.WriteLine(
            $"{when}: live {live.Follower.Phases?.CurrentPhase?.Name} {live.Follower.IndicatedAirspeed:F2} kt, "
                + $"restored {restored.Follower.Phases?.CurrentPhase?.Name} {restored.Follower.IndicatedAirspeed:F2} kt, {apartFt:F3} ft apart"
        );
        Assert.Equal(live.Follower.Phases?.CurrentPhase?.Name, restored.Follower.Phases?.CurrentPhase?.Name);
        Assert.True(apartFt <= RestoredAgreesFt, $"{when}: the restored follower stands {apartFt:F3} ft from the live one");
    }

    /// <summary>Sends <see cref="Follower"/> a RES on <paramref name="ground"/>, failing unless it is refused for taxi instructions.</summary>
    private void AssertResRefused(SfoGround ground)
    {
        CommandResult result = ground.Engine.SendCommand(Follower, "RES");
        Output.WriteLine($"RES: success={result.Success} '{result.Message}'");
        Assert.False(result.Success, "RES with nothing after the hold a HOLD broke the follow off into was accepted");
        PilotSpeechText unable = Assert.IsType<PilotSpeechText>(result.PilotUnable);
        Assert.Equal("unable, request taxi instructions.", unable.Terminal);
    }

    /// <summary>The follow route of the follow that took <paramref name="follower"/>'s current hold: the phase before it.</summary>
    private static TaxiRoute? HeldFollowRoute(AircraftState follower)
    {
        PhaseList phases = Assert.IsType<PhaseList>(follower.Phases);
        return (phases.Phases[phases.CurrentIndex - 1] as FollowingPhase)?.FollowRoute;
    }

    private static IReadOnlyList<DirectionalEdge> RouteEdges(TaxiRoute route) => [.. route.Segments.Select(s => s.Edge)];

    private static HashSet<int> RouteNodes(TaxiRoute route) => [.. route.Segments.SelectMany(s => new[] { s.FromNodeId, s.ToNodeId })];

    /// <summary>The first bar of <paramref name="runway"/> on <paramref name="route"/> past the bar at <paramref name="barNodeId"/>.</summary>
    private static GroundNode FarBarAlong(SfoGround ground, TaxiRoute route, int barNodeId, string runway)
    {
        var id = RunwayIdentifier.Parse(runway);
        int from = route.Segments.FindIndex(s => s.FromNodeId == barNodeId);
        Assert.True(from >= 0, $"the follow route does not leave the bar #{barNodeId}");
        return route
            .Segments.Skip(from)
            .Select(s => ground.Layout.Nodes[s.ToNodeId])
            .First(n => (n.Type == GroundNodeType.RunwayHoldShort) && (n.RunwayId is { } nodeRunway) && nodeRunway.Overlaps(id));
    }

    /// <summary>
    /// Ticks until <paramref name="follower"/> crosses, then through the crossing, failing if it stands more than
    /// <see cref="OnFollowRouteFt"/> off <paramref name="followRoute"/>; returns the crossing phase.
    /// </summary>
    private CrossingRunwayPhase AssertCrossesAlong(SfoGround ground, AircraftState follower, AircraftState leader, TaxiRoute followRoute)
    {
        int crossingAt = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => follower.Phases?.CurrentPhase is CrossingRunwayPhase,
            CrossBudgetSeconds,
            null
        );
        Assert.True(crossingAt >= 0, $"the follower never started crossing: {follower.Phases?.CurrentPhase?.Name}");
        CrossingRunwayPhase crossing = Assert.IsType<CrossingRunwayPhase>(follower.Phases?.CurrentPhase);
        Output.WriteLine($"crossing {crossing.ApproachNodeId} -> {crossing.TargetNodeId}");
        for (int second = 1; (second <= CrossBudgetSeconds) && (follower.Phases?.CurrentPhase == crossing); second++)
        {
            ground.Engine.TickOneSecond();
            LogFollow(second, follower, leader);
            double offFt = FollowGroundOnGraphTests.OffRouteFt(followRoute, follower.Position);
            Assert.True(offFt <= OnFollowRouteFt, $"+{second}s: the crossing follower stands {offFt:F1} ft off its follow route");
        }

        return crossing;
    }

    /// <summary>
    /// Fails unless <paramref name="follower"/> still holds at <paramref name="hold"/> with a follow of the leader armed behind it,
    /// over <see cref="HeldSeconds"/>; returns the armed follow.
    /// </summary>
    private static FollowingPhase AssertStillHeldWithTheFollowArmed(SfoGround ground, AircraftState follower, HoldingShortPhase hold)
    {
        Assert.Same(hold, follower.Phases?.CurrentPhase);
        PhaseList phases = Assert.IsType<PhaseList>(follower.Phases);
        FollowingPhase armed = Assert.IsType<FollowingPhase>(phases.Phases[phases.CurrentIndex + 1]);
        Assert.Equal(Leader, armed.TargetCallsign);
        for (int second = 1; second <= HeldSeconds; second++)
        {
            ground.Engine.TickOneSecond();
            Assert.Same(hold, follower.Phases?.CurrentPhase);
        }

        return armed;
    }

    /// <summary>
    /// Loads the YAAT-1527 KOAK recording, restores its snapshot at <see cref="OakRestoreSecond"/> and arms the recorded actions
    /// after it. Null on the repo's silent-skip path.
    /// </summary>
    private (SimulationEngine Engine, AirportGroundLayout Layout)? StartOak()
    {
        TestVnasData.EnsureInitialized();
        RecordingArchive? archive = RecordingLoader.OpenArchive(OakRecordingPath);
        var groundData = RecordingGroundData.Evergreen("FOLLOWG behind a lead released across 15/33 on C at the C/J junction");
        if ((archive is null) || (TestVnasData.NavigationDb is null) || (groundData.GetLayout("OAK") is not { } layout))
        {
            archive?.Dispose();
            return null;
        }

        using (archive)
        {
            SessionRecording recording = archive.ToBaseSessionRecording();
            var engine = new SimulationEngine(groundData, PushPlanStore.CreateShipped());
            engine.Replay(recording, 0);
            TimedSnapshot snapshot = Assert.IsType<TimedSnapshot>(archive.ReadSnapshotAt(OakRestoreSecond));
            engine.RestoreFromSnapshot(snapshot.State);
            engine.ArmReplay(recording.Actions);
            return (engine, layout);
        }
    }

    private HoldingShortPhase ReplayUntilOakFollowerHolds(SimulationEngine engine)
    {
        for (int t = (int)OakRestoreSecond + 1; t <= OakHoldBudgetSecond; t++)
        {
            engine.ReplayOneSecond();
            AircraftState follower = Assert.IsType<AircraftState>(engine.FindAircraft(OakFollower));
            if (follower.Phases?.CurrentPhase is HoldingShortPhase hold)
            {
                Output.WriteLine($"t={t} {OakFollower} holds at node {hold.HoldShort.NodeId} ({hold.HoldShort.TargetName})");
                return hold;
            }
        }

        Assert.Fail($"{OakFollower} never held short by t={OakHoldBudgetSecond}");
        return null!;
    }
}
