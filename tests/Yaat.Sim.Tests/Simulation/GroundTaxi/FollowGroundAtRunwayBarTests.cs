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
/// <c>FOLLOWG</c> issued to an aircraft holding short of a runway arms the follow rather than being refused: a follow
/// is not a crossing clearance (7110.65 §3-7-2d issues the crossing clearance in addition to the follow), so the
/// aircraft stays held until <c>CROSS</c>, crosses along the painted line, and only then follows its leader.
///
/// <para>SFO, 28/28 configuration: the follower leaves spot 3 on <c>TAXI A F1 F RWY 28L</c> — which crosses 1L and
/// then 1R on F1 — and holds short of 1L, or of 1R when the clearance adds <c>CROSS 1L HS 1R</c>; the leader leaves
/// spot 1 on <c>TAXI A L F 28L</c>, the A-L-F flow that joins F east of the 1R/1L pair.</para>
/// </summary>
public class FollowGroundAtRunwayBarTests(ITestOutputHelper output)
{
    private const string Type = "B738";
    private const string Follower = "FOL1";
    private const string Leader = "LED1";
    private const string TaxiToOneRightBar = "TAXI A F1 F RWY 28L CROSS 1L HS 1R";
    private const string TaxiToOneLeftBar = "TAXI A F1 F RWY 28L";
    private const int StageBudgetSeconds = 600;
    private const int HeldSeconds = 20;
    private const int CrossBudgetSeconds = 180;
    private const int DepartureBarBudgetSeconds = 600;
    private const int SnapshotCompareSeconds = 60;

    [Fact]
    public void FollowGAtOneRightBar_StaysHeldUntilCross_ThenCrossesAndFollows()
    {
        if (Stage(TaxiToOneRightBar, "1R") is not { } staged)
        {
            return;
        }

        (SfoGround ground, AircraftState follower, _) = staged;
        HoldingShortPhase hold = Assert.IsType<HoldingShortPhase>(follower.Phases?.CurrentPhase);
        TaxiRoute route = Assert.IsType<TaxiRoute>(follower.Ground.AssignedTaxiRoute);

        CommandResult armed = ground.Engine.SendCommand(Follower, $"FOLLOWG {Leader}");
        output.WriteLine($"FOLLOWG {Leader} -> success={armed.Success} msg={armed.Message}");
        Assert.True(armed.Success, $"FOLLOWG at the 1R bar should arm the follow but got: {armed.Message}");
        Assert.Equal($"Follow {Leader}, hold short of 1R", armed.Message);

        // Armed, not started: the hold is still current, its ground hold untouched, the route kept, and the follow queued behind it.
        Assert.Same(hold, follower.Phases?.CurrentPhase);
        Assert.Null(follower.Ground.Hold);
        Assert.Same(route, follower.Ground.AssignedTaxiRoute);
        AssertFollowArmedBehindHold(follower.Phases!);

        RunwayInfo runway1R = SfoGroundHarness.Runway("1R");
        for (int second = 1; second <= HeldSeconds; second++)
        {
            ground.Engine.TickOneSecond();
            Assert.Same(hold, follower.Phases?.CurrentPhase);
            Assert.True(
                follower.GroundSpeed < SfoGroundHarness.StationarySpeedKts,
                $"t={second}s: moved off the bar at {follower.GroundSpeed:F1} kt"
            );
            Assert.False(RunwayOccupancy.IsOnPavement(follower, runway1R), $"t={second}s: entered 1R before any crossing clearance");
        }

        CommandResult cross = ground.Engine.SendCommand(Follower, "CROSS 1R");
        output.WriteLine($"CROSS 1R -> success={cross.Success} msg={cross.Message}");
        Assert.True(cross.Success, cross.Message);

        bool crossed = false;
        bool wasOn1R = false;
        int followingAt = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => follower.Phases?.CurrentPhase is FollowingPhase,
            CrossBudgetSeconds,
            second =>
            {
                crossed |= follower.Phases?.CurrentPhase is CrossingRunwayPhase;
                wasOn1R |= RunwayOccupancy.IsOnPavement(follower, runway1R);
                if (follower.Phases?.CurrentPhase is HoldingShortPhase again && SfoGroundHarness.HoldShortMatches(again.HoldShort, "1R"))
                {
                    Assert.True(ReferenceEquals(again, hold), $"t={second}s: held short of 1R again (node {again.HoldShort.NodeId}) after CROSS");
                }
            }
        );

        output.WriteLine($"following at t={followingAt}s: {follower.Phases?.CurrentPhase?.Name} gs={follower.GroundSpeed:F1}");
        Assert.True(followingAt > 0, $"never started following within {CrossBudgetSeconds}s of CROSS: {follower.Phases?.CurrentPhase?.Name}");
        Assert.True(crossed, "reached FollowingPhase without a CrossingRunwayPhase — the follow would have driven over 1R off the painted line");
        Assert.True(wasOn1R, "never put a wheel on 1R, so it did not cross it");
        Assert.False(RunwayOccupancy.IsOnPavement(follower, runway1R), "started following while still on 1R pavement");
        Assert.Equal(Leader, Assert.IsType<FollowingPhase>(follower.Phases?.CurrentPhase).TargetCallsign);
    }

    /// <summary>
    /// A follower stopped at a crossing bar by its own follow, then crossed, keeps the taxi route it was cleared on:
    /// the resume follow's route cursor no longer starts at the bar, so the crossing is found on the layout and handed
    /// to the crossing phase rather than swapped in as the aircraft's route. With the route intact, the follow files
    /// the follower's own departure bar as <see cref="HoldShortReason.DestinationRunway"/> — the bar RES refuses to
    /// release onto the runway — and a FOLLOWG there, where CROSS is refused, is refused too.
    /// </summary>
    [Fact]
    public void FollowerStoppedAtOneRight_Cross_KeepsRouteAndFilesDepartureBarAsDestination()
    {
        if (Stage(TaxiToOneLeftBar, "1L") is not { } staged)
        {
            return;
        }

        (SfoGround ground, AircraftState follower, AircraftState leader) = staged;
        TaxiRoute route = Assert.IsType<TaxiRoute>(follower.Ground.AssignedTaxiRoute);
        AssertSent(ground, Follower, $"FOLLOWG {Leader}");
        AssertSent(ground, Follower, "CROSS 1L");

        int stopped = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => (follower.Phases?.CurrentPhase is HoldingShortPhase hold) && SfoGroundHarness.HoldShortMatches(hold.HoldShort, "1R"),
            CrossBudgetSeconds,
            null
        );
        output.WriteLine($"stopped at t={stopped}s: {follower.Phases?.CurrentPhase?.Name}");
        Assert.True(stopped > 0, $"the follow never stopped at a 1R bar within {CrossBudgetSeconds}s: {follower.Phases?.CurrentPhase?.Name}");
        AssertFollowArmedBehindHold(follower.Phases!);

        AssertSent(ground, Follower, "CROSS 1R");
        int following = SfoGroundHarness.TickUntil(ground.Engine, () => follower.Phases?.CurrentPhase is FollowingPhase, CrossBudgetSeconds, null);
        Assert.True(following > 0, $"never resumed following within {CrossBudgetSeconds}s of CROSS 1R: {follower.Phases?.CurrentPhase?.Name}");
        Assert.Same(route, follower.Ground.AssignedTaxiRoute);

        bool leaderLinedUp = false;
        int held = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => (follower.Phases?.CurrentPhase is HoldingShortPhase hold) && !SfoGroundHarness.HoldShortMatches(hold.HoldShort, "1R"),
            DepartureBarBudgetSeconds,
            _ =>
            {
                // Move the leader onto 28L so the follower comes up to the bar rather than stopping behind the leader.
                if (!leaderLinedUp && (leader.Phases?.CurrentPhase is HoldingShortPhase))
                {
                    CommandResult luaw = ground.Engine.SendCommand(Leader, "LUAW");
                    output.WriteLine($"{Leader} LUAW -> success={luaw.Success} msg={luaw.Message}");
                    leaderLinedUp = luaw.Success;
                }
            }
        );

        HoldingShortPhase departureBar = Assert.IsType<HoldingShortPhase>(follower.Phases?.CurrentPhase);
        output.WriteLine($"held at t={held}s short of {departureBar.HoldShort.TargetName} ({departureBar.HoldShort.Reason})");
        Assert.True(SfoGroundHarness.HoldShortMatches(departureBar.HoldShort, "28L"), $"held short of {departureBar.HoldShort.TargetName}, not 28L");
        Assert.Equal(HoldShortReason.DestinationRunway, departureBar.HoldShort.Reason);

        CommandResult follow = ground.Engine.SendCommand(Follower, $"FOLLOWG {Leader}");
        output.WriteLine($"FOLLOWG {Leader} at the 28L bar -> success={follow.Success} msg={follow.Message}");
        Assert.False(follow.Success, "FOLLOWG at the departure bar mid-route must be refused: CROSS could never release it");
        Assert.Equal("holding short of departure runway 28L; issue LUAW or CTO", follow.Message);
        Assert.Same(departureBar, follower.Phases?.CurrentPhase);
    }

    /// <summary>
    /// <c>FOLLOWG X; CROSS 1L 1R</c> — one clearance for both runways (7110.65 §3-7-2c) — takes the follower across
    /// 1L and 1R without stopping at the 1R bar between them.
    /// </summary>
    [Fact]
    public void FollowGThenCrossOneLeftOneRight_CrossesBothWithoutStopping()
    {
        if (Stage(TaxiToOneLeftBar, "1L") is not { } staged)
        {
            return;
        }

        (SfoGround ground, AircraftState follower, _) = staged;
        AssertSent(ground, Follower, $"FOLLOWG {Leader}; CROSS 1L 1R");

        RunwayInfo runway1R = SfoGroundHarness.Runway("1R");
        bool wasOn1R = false;
        int clear = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => (follower.Phases?.CurrentPhase is FollowingPhase) && wasOn1R && !RunwayOccupancy.IsOnPavement(follower, runway1R),
            CrossBudgetSeconds,
            second =>
            {
                wasOn1R |= RunwayOccupancy.IsOnPavement(follower, runway1R);
                if ((follower.Phases?.CurrentPhase is HoldingShortPhase hold) && SfoGroundHarness.HoldShortMatches(hold.HoldShort, "1R"))
                {
                    Assert.Fail($"t={second}s: stopped at the 1R bar (node {hold.HoldShort.NodeId}) though CROSS 1L 1R cleared it");
                }
            }
        );

        output.WriteLine($"clear of 1R at t={clear}s: {follower.Phases?.CurrentPhase?.Name}");
        Assert.True(clear > 0, $"never came off 1R following {Leader} within {CrossBudgetSeconds}s: {follower.Phases?.CurrentPhase?.Name}");
        Assert.Equal(Leader, Assert.IsType<FollowingPhase>(follower.Phases?.CurrentPhase).TargetCallsign);
    }

    /// <summary>
    /// A follower mid-crossing on a path handed to its crossing phase (the own-path branch: stopped at 1R by its own
    /// follow, then CROSS 1R) restores from a snapshot and goes on exactly as the original.
    /// </summary>
    [Fact]
    public void OwnPathCrossing_SurvivesSnapshotRoundTripMidCrossing()
    {
        if (Stage(TaxiToOneLeftBar, "1L") is not { } staged)
        {
            return;
        }

        (SfoGround ground, AircraftState follower, _) = staged;
        AssertSent(ground, Follower, $"FOLLOWG {Leader}");
        AssertSent(ground, Follower, "CROSS 1L");
        int stopped = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => (follower.Phases?.CurrentPhase is HoldingShortPhase hold) && SfoGroundHarness.HoldShortMatches(hold.HoldShort, "1R"),
            CrossBudgetSeconds,
            null
        );
        Assert.True(stopped > 0, $"the follow never stopped at a 1R bar within {CrossBudgetSeconds}s: {follower.Phases?.CurrentPhase?.Name}");

        AssertSent(ground, Follower, "CROSS 1R");
        int crossing = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => follower.Phases?.CurrentPhase is CrossingRunwayPhase,
            CrossBudgetSeconds,
            null
        );
        Assert.True(crossing > 0, $"never started crossing 1R: {follower.Phases?.CurrentPhase?.Name}");
        CrossingRunwayPhaseDto dto = Assert.IsType<CrossingRunwayPhaseDto>(follower.Phases!.CurrentPhase!.ToSnapshot());
        Assert.NotNull(dto.OwnPath);

        AssertRestoredGoesOnAsTheOriginal(ground, follower);
    }

    /// <summary>
    /// After <c>FOLLOWG X; CROSS 1L 1R</c> has taken the follower across 1L, a snapshot restores the follow with the
    /// runways it may still cross, and the restored follower goes on exactly as the original.
    /// </summary>
    [Fact]
    public void FollowAcrossOneLeftOneRight_SurvivesSnapshotRoundTripAfterOneLeft()
    {
        if (Stage(TaxiToOneLeftBar, "1L") is not { } staged)
        {
            return;
        }

        (SfoGround ground, AircraftState follower, _) = staged;
        AssertSent(ground, Follower, $"FOLLOWG {Leader}; CROSS 1L 1R");
        int following = SfoGroundHarness.TickUntil(ground.Engine, () => follower.Phases?.CurrentPhase is FollowingPhase, CrossBudgetSeconds, null);
        Assert.True(following > 0, $"never started following after crossing 1L: {follower.Phases?.CurrentPhase?.Name}");
        Assert.NotEmpty(Assert.IsType<FollowingPhase>(follower.Phases?.CurrentPhase).CrossingClearedRunways);

        AssertRestoredGoesOnAsTheOriginal(ground, follower);
    }

    /// <summary>
    /// <c>RES CROSS 1R</c> at the 1R bar with a follow armed is a crossing clearance for the held runway, so it
    /// releases the hold as CROSS does: the aircraft crosses 1R and then follows.
    /// </summary>
    [Fact]
    public void ResCrossingTheHeldRunway_WithFollowArmed_CrossesAndFollows()
    {
        if (Stage(TaxiToOneRightBar, "1R") is not { } staged)
        {
            return;
        }

        (SfoGround ground, AircraftState follower, _) = staged;
        TaxiRoute route = Assert.IsType<TaxiRoute>(follower.Ground.AssignedTaxiRoute);
        AssertSent(ground, Follower, $"FOLLOWG {Leader}");
        AssertSent(ground, Follower, "RES CROSS 1R");

        bool crossed = false;
        int following = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => follower.Phases?.CurrentPhase is FollowingPhase,
            CrossBudgetSeconds,
            _ => crossed |= follower.Phases?.CurrentPhase is CrossingRunwayPhase
        );
        Assert.True(following > 0, $"never started following within {CrossBudgetSeconds}s of RES CROSS 1R: {follower.Phases?.CurrentPhase?.Name}");
        Assert.True(crossed, "reached FollowingPhase without a CrossingRunwayPhase");
        Assert.False(RunwayOccupancy.IsOnPavement(follower, SfoGroundHarness.Runway("1R")), "started following while still on 1R pavement");
        Assert.Same(route, follower.Ground.AssignedTaxiRoute);
    }

    /// <summary>
    /// Captures the engine, restores it into a second SFO engine, and ticks both side by side: the restored follower
    /// must be in the same phase at the same position every second.
    /// </summary>
    private void AssertRestoredGoesOnAsTheOriginal(SfoGround ground, AircraftState follower)
    {
        StateSnapshotDto snapshot = ground.Engine.CaptureSnapshot();
        SfoGround restoredGround = Assert.IsType<SfoGround>(SfoGroundHarness.Build(output, autoCross: false));
        restoredGround.Engine.RestoreFromSnapshot(snapshot);
        AircraftState restored = Assert.IsType<AircraftState>(restoredGround.Engine.World.GetSnapshot().FirstOrDefault(a => a.Callsign == Follower));

        for (int second = 1; second <= SnapshotCompareSeconds; second++)
        {
            ground.Engine.TickOneSecond();
            restoredGround.Engine.TickOneSecond();
            Assert.Equal(follower.Phases?.CurrentPhase?.Name, restored.Phases?.CurrentPhase?.Name);
            Assert.Equal(follower.Position, restored.Position);
        }

        output.WriteLine($"after {SnapshotCompareSeconds}s: {follower.Phases?.CurrentPhase?.Name} at {follower.Position}");
    }

    [Fact]
    public void ResWithFollowArmedAtOneRightBar_IsRefused_AircraftStaysHeld()
    {
        if (Stage(TaxiToOneRightBar, "1R") is not { } staged)
        {
            return;
        }

        (SfoGround ground, AircraftState follower, _) = staged;
        HoldingShortPhase hold = Assert.IsType<HoldingShortPhase>(follower.Phases?.CurrentPhase);
        AssertSent(ground, Follower, $"FOLLOWG {Leader}");

        CommandResult res = ground.Engine.SendCommand(Follower, "RES");
        output.WriteLine($"RES -> success={res.Success} msg={res.Message}");
        Assert.False(res.Success, "RES must not release a runway bar with a follow armed behind it");
        Assert.Equal("unable, holding short of 1R — issue CROSS 1R", res.Message);

        for (int second = 1; second <= HeldSeconds; second++)
        {
            ground.Engine.TickOneSecond();
            Assert.Same(hold, follower.Phases?.CurrentPhase);
            Assert.True(follower.GroundSpeed < SfoGroundHarness.StationarySpeedKts, $"t={second}s: moved at {follower.GroundSpeed:F1} kt after RES");
        }

        AssertFollowArmedBehindHold(follower.Phases!);
    }

    [Fact]
    public void FollowGArmedAtOneRightBar_ReadbackAddsTheHoldShort()
    {
        if (Stage(TaxiToOneRightBar, "1R") is not { } staged)
        {
            return;
        }

        (SfoGround ground, AircraftState follower, _) = staged;
        AssertSent(ground, Follower, $"FOLLOWG {Leader}");

        PilotSpeechText readback = Assert.IsType<PilotSpeechText>(
            PilotResponder.BuildReadback(CommandParser.ParseCompound($"FOLLOWG {Leader}").Value!, follower)
        );
        output.WriteLine($"terminal='{readback.Terminal}' tts='{readback.Tts}' rpo='{readback.RpoTerminal}'");
        // The leader is "the traffic" to the pilot (docs/pilot-phraseology.md); the hold-short is read back with the runway.
        Assert.Equal("follow the traffic, hold short of runway 1R", readback.Terminal);
        Assert.StartsWith("follow the traffic, hold short of runway one right, ", readback.Tts, StringComparison.Ordinal);
        // The solo/student forms never carry the leader's callsign; it survives only in the RPO form.
        Assert.Equal($"follow {Leader}, hold short of runway 1R", readback.RpoTerminal);
    }

    /// <summary>
    /// At the departure bar a completed taxi route ends at, CROSS takes the aircraft across, so a FOLLOWG there arms
    /// the follow as at any other runway bar.
    /// </summary>
    [Fact]
    public void FollowGAtDepartureBarOfCompletedRoute_ArmsTheFollow()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            output.WriteLine("SKIP: SFO layout or navdata unavailable");
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
        output.WriteLine($"FOLLOWG {Follower} at the 28L bar -> success={follow.Success} msg={follow.Message}");
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
        SfoGround restoredGround = Assert.IsType<SfoGround>(SfoGroundHarness.Build(output, autoCross: false));
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

        output.WriteLine($"after {SnapshotCompareSeconds}s: {follower.Phases?.CurrentPhase?.Name} at {follower.Position}");
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
        SimLogBuilder.CreateForTest(output).EnableCategory("FollowingPhase", LogLevel.Information).CaptureInto(tap).InitializeSimLog();
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
        output.WriteLine($"following at t={followingAt}s; now gs={follower.GroundSpeed:F1} unjoinable={follow.IsUnjoinable}");
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
    /// A follower rolling along a straight taxiway edge past 1R on its follow route loses that route when its leader is re-routed
    /// along F1 through it: it keeps the route and brakes to rest along it, its centre never more than
    /// <see cref="LostRouteOffsetFt"/> off the route's centreline, and drops the route once at rest.
    /// </summary>
    [Fact]
    public void Following_RouteLostOnAStraight_BrakesAlongTheRouteToRest()
    {
        if (StageLostRoute(onFillet: false) is { } lost)
        {
            AssertBrakesAlongTheLostRouteToRest(lost);
        }
    }

    /// <summary>
    /// A follower partway round a fillet arc on its follow route loses that route when its leader is re-routed along F1 through
    /// it: it brakes to rest round the fillet's curve, its centre never more than <see cref="LostRouteOffsetFt"/> off it, rather
    /// than rolling on along its heading, and drops the route once at rest.
    /// </summary>
    [Fact]
    public void Following_RouteLostMidFillet_BrakesRoundTheFilletToRest()
    {
        if (StageLostRoute(onFillet: true) is { } lost)
        {
            AssertBrakesAlongTheLostRouteToRest(lost);
        }
    }

    /// <summary>
    /// A follower on 1R's pavement, crossing it on its follow route under <c>CROSS 1L 1R</c>, loses that route when its leader is
    /// re-routed along F1 through it: its clearing route takes over the same tick rather than braking along the lost route, and
    /// it stops clear past the 1R hold line, never stopped on the runway.
    /// </summary>
    [Fact]
    public void Following_RouteLostOnARunway_ClearsPastTheHoldLine()
    {
        if (Stage(TaxiToOneLeftBar, "1L") is not { } staged)
        {
            return;
        }

        (SfoGround ground, AircraftState follower, AircraftState leader) = staged;
        RunwayInfo runway1R = SfoGroundHarness.Runway("1R");
        AssertSent(ground, Leader, "HOLD");
        AssertSent(ground, Follower, $"FOLLOWG {Leader}; CROSS 1L 1R");
        int onRunwayAt = SfoGroundHarness.TickUntil(
            ground.Engine,
            () =>
                (follower.Phases?.CurrentPhase is FollowingPhase { FollowRoute.IsComplete: false })
                && RunwayOccupancy.IsOnPavement(follower, runway1R)
                && (follower.GroundSpeed > SfoGroundHarness.StationarySpeedKts),
            CrossBudgetSeconds,
            second => LogFollow(second, follower, leader)
        );
        Assert.True(onRunwayAt > 0, $"the follower never rolled on 1R on its follow route within {CrossBudgetSeconds}s of CROSS");
        FollowingPhase follow = Assert.IsType<FollowingPhase>(follower.Phases?.CurrentPhase);

        RerouteThroughTheFollower(ground, leader, follower);
        ground.Engine.TickOneSecond();
        LogFollow(onRunwayAt + 1, follower, leader);
        Assert.Null(follow.FollowRoute);
        Assert.False(follow.IsBrakingLostRoute, "the follower on 1R braked along its lost route rather than clearing the runway");
        Assert.NotNull(follow.ClearingRoute);

        int restAt = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => (follow.ClearingRoute is null) && (follower.GroundSpeed < SfoGroundHarness.StationarySpeedKts),
            CrossBudgetSeconds,
            second =>
            {
                LogFollow(second, follower, leader);
                RunwayInfo? under = RunwayOccupancy
                    .AirportRunways(ground.Layout.AirportId)
                    .FirstOrDefault(r => RunwayOccupancy.IsOnPavement(follower, r));
                Assert.False(
                    (under is not null) && (follower.GroundSpeed < SfoGroundHarness.StationarySpeedKts),
                    $"+{second}s: stopped on runway {under?.Id} pavement"
                );
            }
        );
        Assert.True(restAt > 0, $"the follower never came to rest clear of 1R within {CrossBudgetSeconds}s of losing its route");
        AssertStoppedClearPastOneRight(ground, follower);
    }

    /// <summary>
    /// A follower crossing 1L and 1R under <c>CROSS 1L 1R</c> loses its follow route between the two runways, where braking at
    /// the taxi rate would stop it inside 1R's hold line but the firm rate stops it short: it brakes at the firm rate, harder
    /// than the taxi rate, and comes to rest short of 1R, no part of it ever at rest inside a runway's hold line.
    /// </summary>
    [Fact]
    public void Following_RouteLostShortOfACrossedBar_StopsShortAtTheFirmRate()
    {
        if (StageLostApproachingOneRight(firmStopMakesTheLine: true) is not { } staged)
        {
            return;
        }

        (SfoGround ground, AircraftState follower, AircraftState leader) = staged;
        RunwayInfo runway1R = SfoGroundHarness.Runway("1R");
        double worstDropKts = 0.0;
        double lastKts = follower.GroundSpeed;
        int restAt = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => follower.GroundSpeed < RestKts,
            HeldSeconds,
            second =>
            {
                LogFollow(second, follower, leader);
                worstDropKts = Math.Max(worstDropKts, lastKts - follower.GroundSpeed);
                lastKts = follower.GroundSpeed;
                AssertNeverAtRestInsideAHoldLine(ground, follower, second);
                Assert.False(RunwayOccupancy.IsOnPavement(follower, runway1R), $"+{second}s: the follower rolled onto 1R");
            }
        );

        double taxiRate = CategoryPerformance.TaxiDecelRate(AircraftCategory.Jet);
        output.WriteLine($"rest at +{restAt}s; hardest one-second speed drop {worstDropKts:F2} kt (taxi rate {taxiRate:F1} kt/s)");
        Assert.True(restAt > 0, $"the follower is still rolling at {follower.GroundSpeed:F1} kt {HeldSeconds}s after losing its route");
        Assert.True(worstDropKts > taxiRate, $"the follower braked no harder than the taxi rate ({worstDropKts:F2} kt in a second)");
        Assert.Null(FollowingPhase.RunwayInsideHoldLine(follower, ground.Layout));
    }

    /// <summary>
    /// A follower crossing 1L and 1R under <c>CROSS 1L 1R</c> loses its follow route between the two runways too close to 1R's
    /// hold line for even the firm rate to stop it short: it carries on across 1R under its crossing clearance and comes to rest
    /// clear past 1R's far hold line, no part of it ever at rest inside a runway's hold line.
    /// </summary>
    [Fact]
    public void Following_RouteLostTooCloseToACrossedBar_CrossesAndClearsBeyond()
    {
        if (StageLostApproachingOneRight(firmStopMakesTheLine: false) is not { } staged)
        {
            return;
        }

        (SfoGround ground, AircraftState follower, AircraftState leader) = staged;
        FollowingPhase follow = Assert.IsType<FollowingPhase>(follower.Phases?.CurrentPhase);
        int restAt = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => (follow.ClearingRoute is null) && !follow.IsBrakingLostRoute && (follower.GroundSpeed < SfoGroundHarness.StationarySpeedKts),
            CrossBudgetSeconds,
            second =>
            {
                LogFollow(second, follower, leader);
                AssertNeverAtRestInsideAHoldLine(ground, follower, second);
            }
        );
        Assert.True(restAt > 0, $"the follower never came to rest within {CrossBudgetSeconds}s of losing its route");
        AssertStoppedClearPastOneRight(ground, follower);
    }

    /// <summary>
    /// A follower rolling on its follow route past 1R whose leader is re-routed onto a path that joins behind it, where the
    /// follower has already been: it never turns about on its own edge — while it moves on that edge, its heading
    /// stays within 90° of the edge's bearing, and its follow route never drives the edge back — and it ends at rest, or on a
    /// route that leads on ahead of it. Off the edge, a forward turn onto another taxiway may swing it further.
    /// </summary>
    [Fact]
    public void Following_LeadReroutedBehindTheFollower_NeverReversesOnItsOwnEdge()
    {
        if (StageRolling(onFillet: false) is { } rolling)
        {
            AssertNeverReversesOnItsOwnEdge(rolling.Ground, rolling.Follower, rolling.Leader, rolling.Route, RerouteBehindTheFollower);
        }
    }

    /// <summary>
    /// A follower partway round the F1-F fillet arc on its follow route whose leader is re-routed along F1 behind it: the
    /// re-plan's route starts at the arc's far end and runs straight back round the arc, the way the follower came. A route the
    /// follow would install from its start, leaving back across the heading, is never adopted: it never turns about on the arc
    /// it is rolling round, and its follow route never drives the arc back.
    /// </summary>
    [Fact]
    public void Following_LeadReroutedBehindTheFollowerMidFillet_NeverReversesOnTheArc()
    {
        if (StageRolling(onFillet: true) is { } rolling)
        {
            AssertNeverReversesOnItsOwnEdge(rolling.Ground, rolling.Follower, rolling.Leader, rolling.Route, RerouteThroughTheFollower);
        }
    }

    /// <summary>
    /// Re-routes <paramref name="leader"/> with <paramref name="reroute"/> while <paramref name="follower"/> rolls on
    /// <paramref name="route"/>'s current segment, then ticks it: while it moves on that incoming edge its heading stays within
    /// 90° of the edge's bearing, its follow route never drives the edge back, and it ends at rest or on a route ahead of it.
    /// </summary>
    private void AssertNeverReversesOnItsOwnEdge(
        SfoGround ground,
        AircraftState follower,
        AircraftState leader,
        TaxiRoute route,
        Action<SfoGround, AircraftState, AircraftState> reroute
    )
    {
        DirectionalEdge incoming = route.Segments[route.CurrentSegmentIndex].Edge;
        double incomingDeg = incoming.ArrivalBearing;
        reroute(ground, leader, follower);
        double worstDeg = 0.0;
        for (int second = 1; second <= HeldSeconds; second++)
        {
            ground.Engine.TickOneSecond();
            LogFollow(second, follower, leader);
            double offDeg = Math.Abs(GeoMath.SignedBearingDifference(follower.TrueHeading.Degrees, incomingDeg));
            bool onIncoming =
                GeoMath.DistanceToSegmentFt(follower.Position, incoming.FromNode.Position, incoming.ToNode.Position) <= OnIncomingEdgeFt;
            output.WriteLine($"   heading {follower.TrueHeading.Degrees:F0}° ({offDeg:F0}° off the incoming edge), on it: {onIncoming}");
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

        output.WriteLine($"heading off the incoming edge's {incomingDeg:F0}° by at most {worstDeg:F0}° while moving on it");
        Assert.True(worstDeg <= 90.0, $"the follower turned about: its heading swung {worstDeg:F0}° off its incoming edge while moving on it");
        bool atRest = follower.GroundSpeed < SfoGroundHarness.StationarySpeedKts;
        TaxiRouteSegment? ahead = (follower.Phases?.CurrentPhase as FollowingPhase)?.FollowRoute?.CurrentSegment;
        bool forward =
            (ahead is not null) && (Math.Abs(GeoMath.SignedBearingDifference(follower.TrueHeading.Degrees, ahead.Edge.ArrivalBearing)) <= 90.0);
        Assert.True(atRest || forward, $"the follower ends rolling at {follower.GroundSpeed:F1} kt with no route ahead of it");
    }

    /// <summary>How far (ft) off its incoming edge's line a follower still counts as on that edge.</summary>
    private const double OnIncomingEdgeFt = 10.0;

    /// <summary>
    /// Clears <paramref name="leader"/> on the first re-route it accepts whose re-planned follow joins its path at a node behind
    /// <paramref name="follower"/> (more than 90° off its heading), where the follower has already been.
    /// </summary>
    private void RerouteBehindTheFollower(SfoGround ground, AircraftState leader, AircraftState follower)
    {
        string[] taxis = ["TAXI F A", "TAXI L A", "TAXI F B", "TAXI F B A", "TAXI F Z", "TAXI F L A", "TAXI F C", "TAXI F A 28R"];
        foreach (string taxi in taxis)
        {
            CommandResult result = ground.Engine.SendCommand(Leader, taxi);
            FollowRoutePlan plan = FollowRoutePlanner.Replan(ground.Layout, follower, leader);
            bool behind =
                (plan is FollowRoutePlan.Joinable joinable)
                && ground.Layout.Nodes.TryGetValue(joinable.MergeNode, out GroundNode? merge)
                && (
                    Math.Abs(GeoMath.SignedBearingDifference(follower.TrueHeading.Degrees, GeoMath.BearingTo(follower.Position, merge.Position)))
                    > 90.0
                );
            output.WriteLine(
                $"{Leader} <- '{taxi}' -> success={result.Success} msg={result.Message} route={leader.Ground.AssignedTaxiRoute?.ToSummary()} "
                    + $"replan={plan.GetType().Name} mergeBehind={behind}"
            );
            if (result.Success && behind)
            {
                return;
            }
        }

        Assert.Fail($"none of [{string.Join(", ", taxis)}] put {Leader}'s path behind {Follower}");
    }

    /// <summary>
    /// Stages <see cref="StageRolling"/>'s crossing from the 1L bar under <c>CROSS 1L 1R</c> instead, and ticks until the follower
    /// rolls on its follow route between 1L and 1R, inside neither's hold line, where braking at the taxi rate would carry its
    /// nose inside 1R's hold line: with <paramref name="firmStopMakesTheLine"/> where the firm rate stops it short, else where
    /// even the firm rate would not. Then re-routes the leader along F1 through it, losing the route.
    /// </summary>
    private (SfoGround Ground, AircraftState Follower, AircraftState Leader)? StageLostApproachingOneRight(bool firmStopMakesTheLine)
    {
        if (Stage(TaxiToOneLeftBar, "1L") is not { } staged)
        {
            return null;
        }

        (SfoGround ground, AircraftState follower, AircraftState leader) = staged;
        RunwayInfo runway1R = SfoGroundHarness.Runway("1R");
        AssertSent(ground, Leader, "HOLD");
        AssertSent(ground, Follower, $"FOLLOWG {Leader}; CROSS 1L 1R");
        int lostAt = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => IsLosingTheLineAt(ground.Layout, runway1R, follower, firmStopMakesTheLine),
            CrossBudgetSeconds,
            second =>
            {
                LogFollow(second, follower, leader);
                output.WriteLine(
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
    private static bool IsLosingTheLineAt(AirportGroundLayout layout, RunwayInfo runway, AircraftState follower, bool firmStopMakesTheLine)
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
    private const double CrossedBarMarginFt = 3.0;

    /// <summary>
    /// Whether the follower's nose, carried <paramref name="aheadFt"/> along its heading, is inside <paramref name="runway"/>'s hold line.
    /// </summary>
    private static bool NoseStopInside(AirportGroundLayout layout, RunwayInfo runway, AircraftState follower, double aheadFt)
    {
        double noseAheadFt = (AircraftLength.ResolveFt(follower.AircraftType) / 2.0) + aheadFt;
        LatLon point = GeoMath.ProjectPoint(follower.Position, follower.TrueHeading, noseAheadFt / GeoMath.FeetPerNm);
        return FollowingPhase.IsInsideHoldLine(layout, runway, point);
    }

    /// <summary>How far (ft) the follower rolls braking to rest at the jet taxi rate.</summary>
    private static double TaxiStopFt(AircraftState follower) => StopFt(follower, CategoryPerformance.TaxiDecelRate(AircraftCategory.Jet));

    /// <summary>How far (ft) the follower rolls braking to rest at the jet firm rate.</summary>
    private static double FirmStopFt(AircraftState follower) => StopFt(follower, CategoryPerformance.ExpediteExitDecelRate(AircraftCategory.Jet));

    private static double StopFt(AircraftState follower, double rateKtsPerSec) =>
        (follower.GroundSpeed * follower.GroundSpeed) / (2.0 * rateKtsPerSec) * GeoMath.FeetPerNm / 3600.0;

    /// <summary>Fails when <paramref name="follower"/> is at rest with any part — nose, centre or tail — inside a runway's hold line.</summary>
    private static void AssertNeverAtRestInsideAHoldLine(SfoGround ground, AircraftState follower, int second)
    {
        RunwayInfo? inside = FollowingPhase.RunwayInsideHoldLine(follower, ground.Layout);
        Assert.False(
            (inside is not null) && (follower.GroundSpeed < SfoGroundHarness.StationarySpeedKts),
            $"+{second}s: at rest inside runway {inside?.Id}'s hold line"
        );
    }

    /// <summary>
    /// A follower braking along the follow route its leader's re-route lost, snapshotted through the recording JSON and restored
    /// into a second engine, keeps braking along it: ticked side by side, the two stand at the same position every second, and
    /// both drop the route once at rest.
    /// </summary>
    [Fact]
    public void Following_SnapshotWhileBrakingAlongTheLostRoute_StopsAsTheOriginal()
    {
        if (StageLostRoute(onFillet: false) is not { } lost)
        {
            return;
        }

        string json = JsonSerializer.Serialize(lost.Ground.Engine.CaptureSnapshot(), RecordingJsonOptions.Default);
        Assert.Contains("BrakingLostRoute", json, StringComparison.OrdinalIgnoreCase);
        SfoGround restoredGround = Assert.IsType<SfoGround>(SfoGroundHarness.Build(output, autoCross: false));
        restoredGround.Engine.RestoreFromSnapshot(
            Assert.IsType<StateSnapshotDto>(JsonSerializer.Deserialize<StateSnapshotDto>(json, RecordingJsonOptions.Default))
        );
        AircraftState restored = Assert.IsType<AircraftState>(restoredGround.Engine.FindAircraft(Follower));
        FollowingPhase restoredFollow = Assert.IsType<FollowingPhase>(restored.Phases?.CurrentPhase);
        Assert.True(restoredFollow.IsBrakingLostRoute, "the restored follow forgot it was braking along its lost route");
        Assert.NotNull(restoredFollow.FollowRoute);

        for (int second = 1; second <= HeldSeconds; second++)
        {
            lost.Ground.Engine.TickOneSecond();
            restoredGround.Engine.TickOneSecond();
            Assert.Equal(lost.Follower.Phases?.CurrentPhase?.Name, restored.Phases?.CurrentPhase?.Name);
            Assert.Equal(lost.Follower.Position, restored.Position);
        }

        output.WriteLine($"after {HeldSeconds}s: original at {lost.Follower.Position}, restored at {restored.Position}");
        Assert.True(restored.GroundSpeed < RestKts, $"the restored follower is still rolling at {restored.GroundSpeed:F1} kt");
        Assert.False(restoredFollow.IsBrakingLostRoute, "the restored follower at rest still holds the route it lost");
    }

    /// <summary>
    /// A follow not braking along a lost route writes no <c>BrakingLostRoute</c> into its snapshot, so its phase JSON is the
    /// same as before the field existed.
    /// </summary>
    [Fact]
    public void Following_SnapshotWhileNotBraking_OmitsBrakingLostRoute()
    {
        if (StageRolling(onFillet: false) is not { } rolling)
        {
            return;
        }

        FollowingPhase follow = rolling.Follow;
        Assert.False(follow.IsBrakingLostRoute);
        string json = JsonSerializer.Serialize(Assert.IsType<FollowingPhaseDto>(follow.ToSnapshot()), RecordingJsonOptions.Default);
        output.WriteLine(json.Length > 400 ? json[..400] : json);
        Assert.DoesNotContain("BrakingLostRoute", json, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The brake rate a follower braking along a lost route uses onto the route's end: the firm rate
    /// (<see cref="CategoryPerformance.ExpediteExitDecelRate"/>) when the end lies inside its stopping distance at the taxi rate,
    /// else none (the taxi rate). No real layout puts a lost route's end inside the stop (a KOAK or SFO follower keeps planning
    /// ahead), so the choice is pinned as the pure function the brake uses.
    /// </summary>
    [Theory]
    [InlineData(AircraftCategory.Jet, 20.0, 50.0, true)]
    [InlineData(AircraftCategory.Jet, 20.0, 80.0, false)]
    [InlineData(AircraftCategory.Jet, 5.0, 50.0, false)]
    [InlineData(AircraftCategory.Piston, 20.0, 100.0, true)]
    [InlineData(AircraftCategory.Piston, 20.0, 200.0, false)]
    [InlineData(AircraftCategory.Turboprop, 15.0, 0.0, true)]
    public void LostRouteEndDecelRate_EndInsideTheTaxiStop_IsTheFirmRate(AircraftCategory category, double speedKts, double toEndFt, bool firm)
    {
        double taxiStopFt = speedKts * speedKts / (2.0 * CategoryPerformance.TaxiDecelRate(category)) * (GeoMath.FeetPerNm / 3600.0);
        output.WriteLine($"{category} at {speedKts} kt stops in {taxiStopFt:F0} ft at the taxi rate; route end {toEndFt} ft ahead");
        double? rate = FollowingPhase.LostRouteEndDecelRate(category, speedKts, toEndFt);
        Assert.Equal(firm ? CategoryPerformance.ExpediteExitDecelRate(category) : null, rate);
    }

    /// <summary>
    /// A crossed bar ahead of a follower braking along its lost route is one it crosses only when the route reaches it
    /// (<see cref="FollowingPhase.BarsOnRestOfRoute"/>): at SFO, a route along G toward 1L that turns onto B at the G/B junction,
    /// short of 1L's bar on G, does not cross that bar, and a route on along G to the bar does. Pinned on the routes rather than
    /// end to end: on KOAK and SFO no turn-off lies close enough to a bar for a follower's taxi-rate stop to reach the line.
    /// </summary>
    [Fact]
    public void BarsOnRestOfRoute_RouteTurningOffShortOfTheBar_DoesNotCrossIt()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        AirportGroundLayout layout = ground.Layout;
        List<GroundNode> bars = TestLayoutNodes.RunwayHoldShortsOnTaxiway(layout, "1L", "G");
        GroundNode junction = layout
            .Nodes.Values.Where(n => StraightNeighboursOn(n, "G").Any() && StraightNeighboursOn(n, "B").Any())
            .MinBy(n => bars.Min(bar => GeoMath.DistanceNm(bar.Position, n.Position)))!;
        GroundNode bar = bars.MinBy(b => GeoMath.DistanceNm(b.Position, junction.Position))!;
        GroundNode gBack = StraightNeighboursOn(junction, "G").MaxBy(n => GeoMath.DistanceNm(n.Position, bar.Position))!;
        GroundNode bSide = StraightNeighboursOn(junction, "B").OrderBy(n => n.Id).First();
        WakeTurbulenceData.WakeClass wake = WakeTurbulenceData.WakeClassForType(Type, AircraftCategory.Jet);
        TaxiRoute turnOff = Assert.IsType<TaxiRoute>(TaxiPathfinder.FindRoute(layout, gBack.Id, bSide.Id, AircraftCategory.Jet, wake));
        TaxiRoute onToTheBar = Assert.IsType<TaxiRoute>(TaxiPathfinder.FindRoute(layout, gBack.Id, bar.Id, AircraftCategory.Jet, wake));
        double barFt = GeoMath.DistanceNm(junction.Position, bar.Position) * GeoMath.FeetPerNm;
        output.WriteLine($"G/B junction #{junction.Id}, 1L bar #{bar.Id} {barFt:F0} ft on");
        output.WriteLine($"turn-off {turnOff.ToSummary()}, on to the bar {onToTheBar.ToSummary()}");

        Assert.Empty(FollowingPhase.BarsOnRestOfRoute(turnOff, [bar]));
        Assert.Equal([bar.Id], FollowingPhase.BarsOnRestOfRoute(onToTheBar, [bar]).Select(b => b.Id));
    }

    /// <summary>The nodes at the far end of <paramref name="node"/>'s straight edges on <paramref name="taxiway"/>.</summary>
    private static IEnumerable<GroundNode> StraightNeighboursOn(GroundNode node, string taxiway) =>
        node.Edges.OfType<GroundEdge>().Where(e => e.MatchesTaxiway(taxiway)).Select(e => e.OtherNode(node));

    /// <summary>How far (deg) a ground heading may turn in one physics sub-tick, with margin, at taxi speeds.</summary>
    private const double SubTickTurnMarginDeg = 5.0;

    /// <summary>
    /// A follower crossing 1R up F1 behind a held leader re-plans at each node arrival. At the F1/L junction a re-plan joins the
    /// leader's path sooner along L, leaving the junction back across the follower's heading; a route installed from its start
    /// is adopted only when its first edge leaves within 90° of the heading, so the follower keeps the route it is driving
    /// rather than swerve onto L. Stepped a physics sub-tick at a time, so each adoption is judged against the heading the
    /// follower had just before it.
    /// </summary>
    [Fact]
    public void Following_ReplanAtAJunction_NeverAdoptsARouteLeavingBackAcrossItsHeading()
    {
        if (Stage(TaxiToOneRightBar, "1R") is not { } staged)
        {
            return;
        }

        (SfoGround ground, AircraftState follower, AircraftState leader) = staged;
        AssertSent(ground, Leader, "HOLD");
        AssertSent(ground, Follower, $"FOLLOWG {Leader}");
        AssertSent(ground, Follower, "CROSS 1R");
        SimulationEngine engine = ground.Engine;
        using var tap = new CapturingSimLogProvider(LogLevel.Debug, capacity: 500);
        SimLogBuilder.CreateForTest(output).EnableCategory("FollowingPhase", LogLevel.Debug).CaptureInto(tap).InitializeSimLog();
        TaxiRoute? previous = null;
        int adoptions = 0;
        bool roundTheFillet = false;
        for (int second = 1; second <= CrossBudgetSeconds; second++)
        {
            engine.BeginSecond();
            engine.OpenSecond(engine.BareHost);
            engine.RunPrePhysics(engine.BareHost);
            for (int sub = 0; sub < SimulationEngine.PhysicsSubTickRate; sub++)
            {
                double headingBefore = follower.TrueHeading.Degrees;
                engine.RunPhysicsSubTick(1.0 / SimulationEngine.PhysicsSubTickRate, sub);
                TaxiRoute? route = (follower.Phases?.CurrentPhase as FollowingPhase)?.FollowRoute;
                if ((route is not null) && (previous is not null) && !ReferenceEquals(route, previous))
                {
                    adoptions++;
                    double departsDeg = route.Segments[0].Edge.DepartureBearing;
                    double offDeg = Math.Abs(GeoMath.SignedBearingDifference(headingBefore, departsDeg));
                    Assert.True(
                        offDeg <= 90.0 + SubTickTurnMarginDeg,
                        $"t={second} sub-tick {sub}: adopted {route.ToSummary()} whose first edge leaves at {departsDeg:F0}°, {offDeg:F0}° off "
                            + $"the heading {headingBefore:F0}°"
                    );
                }

                previous = route ?? previous;
                roundTheFillet |= IsRollingOn(ground.Layout, follower, onFillet: true);
            }

            engine.RunPostPhysics(engine.BareHost);
            engine.RunEndOfSecond(engine.BareHost);
            LogFollow(second, follower, leader);
            if (roundTheFillet)
            {
                break;
            }
        }

        output.WriteLine($"{adoptions} follow routes adopted after the first");
        Assert.True(roundTheFillet, "the follower never rolled round the F1-F fillet it was following on");
        Assert.True(
            tap.Drain().Any(r => r.Message.Contains("keeping the route", StringComparison.Ordinal)),
            "no re-plan at a junction offered a route leaving back across the heading, so the guard was never reached"
        );
        Assert.Equal(0, tap.DroppedCount);
    }

    /// <summary>How far (ft) off a lost follow route's centreline a follower braking along it may stand.</summary>
    private const double LostRouteOffsetFt = 2.0;

    /// <summary>The slowest a follower is rolling (kts) when its route is lost, so it is still braking a tick later.</summary>
    private const double LostRouteMinSpeedKts = 8.0;

    /// <summary>
    /// How far round a fillet arc, as a fraction of its length from both ends, a follower must be at a second's end to stand partway
    /// round it: a short arc is crossed in about a second at taxi speed, so a second's end lands in its middle fifth.
    /// </summary>
    private const double FilletStageFraction = 0.2;

    /// <summary>How far (ft) from both ends of a straight edge a follower must be to stand on the edge itself, not a node.</summary>
    private const double StraightMarginFt = 10.0;

    /// <summary>Speed (kts) below which a follower is at rest.</summary>
    private const double RestKts = 0.05;

    /// <summary>The follower, its follow and the follow route a lead re-route lost, with the follower braking along it.</summary>
    private sealed record LostRoute(SfoGround Ground, AircraftState Follower, FollowingPhase Follow, TaxiRoute Route);

    /// <summary>
    /// Arms a follow at the 1R bar and crosses 1R, ticks until the follower rolls on its follow route clear of every runway's
    /// hold line — along a straight edge, or with <paramref name="onFillet"/> partway round a fillet arc — then re-routes the
    /// leader along F1 through it and ticks once: the follower is braking along the route it lost.
    /// </summary>
    private LostRoute? StageLostRoute(bool onFillet)
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
    /// Arms a follow at the 1R bar behind a held leader and crosses 1R, then ticks until the follower rolls on its follow route
    /// clear of every runway's hold line — along a straight edge, or with <paramref name="onFillet"/> partway round a fillet arc.
    /// </summary>
    private (SfoGround Ground, AircraftState Follower, AircraftState Leader, FollowingPhase Follow, TaxiRoute Route)? StageRolling(bool onFillet)
    {
        if (Stage(TaxiToOneRightBar, "1R") is not { } staged)
        {
            return null;
        }

        (SfoGround ground, AircraftState follower, AircraftState leader) = staged;
        AssertSent(ground, Leader, "HOLD");
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
    private static bool IsRollingOn(AirportGroundLayout layout, AircraftState follower, bool onFillet)
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
    private void AssertBrakesAlongTheLostRouteToRest(LostRoute lost)
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
                output.WriteLine($"+{second}s gs={follower.GroundSpeed:F2} off route {offFt:F2} ft braking={lost.Follow.IsBrakingLostRoute}");
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
    /// first edge leaves back across the follower's heading, which the follow does not adopt after a lead re-route.
    /// </summary>
    private void RerouteThroughTheFollower(SfoGround ground, AircraftState leader, AircraftState follower)
    {
        string[] taxis = ["TAXI L F F1 A", "TAXI F F1 A", "TAXI A F1 F"];
        foreach (string taxi in taxis)
        {
            CommandResult result = ground.Engine.SendCommand(Leader, taxi);
            FollowRoutePlan plan = FollowRoutePlanner.Replan(ground.Layout, follower, leader);
            output.WriteLine(
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
    private static bool LeavesBackAcrossHeading(FollowRoutePlan plan, AircraftState follower)
    {
        if (plan is not FollowRoutePlan.Joinable joinable)
        {
            return false;
        }

        DirectionalEdge? first = joinable.PathToMerge.Segments.FirstOrDefault()?.Edge ?? joinable.LeadPathFromMerge.FirstOrDefault();
        return (first is { } edge) && !FollowRoutePlanner.DepartsAhead(follower, edge);
    }

    /// <summary>One line of the follower's follow state and the leader's phase and speed.</summary>
    private void LogFollow(int second, AircraftState follower, AircraftState leader)
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

        output.WriteLine(
            $"t={second} {follower.Phases?.CurrentPhase?.Name} gs={follower.GroundSpeed:F1} seg={segment} braking={follow?.IsBrakingLostRoute} "
                + $"clearing={follow?.ClearingRoute is not null} {Leader} {leader.Phases?.CurrentPhase?.Name} gs={leader.GroundSpeed:F1}"
        );
    }

    /// <summary>
    /// The follower stopped with its tail and both wingtips past the hold line of the 1R bar nearest it on its side of 1R
    /// (<see cref="FollowingPhase.IsClearPastBar"/>), and no part of it — nose, centre or tail — inside any runway's hold line, 1L's
    /// included (<see cref="FollowingPhase.RunwayInsideHoldLine"/>).
    /// </summary>
    private void AssertStoppedClearPastOneRight(SfoGround ground, AircraftState follower)
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
        output.WriteLine($"stopped beside 1R bar #{bar.Id}; inside the hold line of {inside?.Id.ToString() ?? "no runway"}");
        Assert.True(FollowingPhase.IsClearPastBar(follower, runway1R, bar), $"the follower stopped short of 1R bar #{bar.Id}'s hold line");
        Assert.Null(inside);
    }

    /// <summary>Which side of <paramref name="runway"/>'s centreline <paramref name="point"/> lies on: the sign of the cross-track.</summary>
    private static bool RunwaySide(RunwayInfo runway, LatLon point)
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
    private void AssertRerouted(SfoGround ground, AircraftState leader, AircraftState follower, string[] taxis)
    {
        foreach (string taxi in taxis)
        {
            CommandResult result = ground.Engine.SendCommand(Leader, taxi);
            output.WriteLine(
                $"{Leader} <- '{taxi}' -> success={result.Success} msg={result.Message} route={leader.Ground.AssignedTaxiRoute?.ToSummary()}"
            );
            if (result.Success && (FollowRoutePlanner.Plan(ground.Layout, follower, leader) is FollowRoutePlan.FollowerAhead))
            {
                return;
            }
        }

        Assert.Fail($"none of [{string.Join(", ", taxis)}] put {Follower} ahead of {Leader} on its route");
    }

    private void AssertSent(SfoGround ground, string callsign, string command)
    {
        CommandResult result = ground.Engine.SendCommand(callsign, command);
        output.WriteLine($"{callsign} <- '{command}' -> success={result.Success} msg={result.Message}");
        Assert.True(result.Success, $"'{command}' to {callsign} was rejected: {result.Message}");
    }

    private static void AssertFollowArmedBehindHold(PhaseList phases)
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
    private (SfoGround Ground, AircraftState Follower, AircraftState Leader)? Stage(string followerTaxi, string barRunway)
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            output.WriteLine("SKIP: SFO layout or navdata unavailable");
            return null;
        }

        AircraftState leader = SfoGroundHarness.SpawnAtSpot(ground, Leader, Type, "1");
        AircraftState follower = SfoGroundHarness.SpawnAtSpot(ground, Follower, Type, "3");
        AssertSent(ground, Follower, followerTaxi);
        ground.Engine.TickOneSecond();
        AssertSent(ground, Leader, "TAXI A L F 28L");

        int held = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => (follower.Phases?.CurrentPhase is HoldingShortPhase hold) && SfoGroundHarness.HoldShortMatches(hold.HoldShort, barRunway),
            StageBudgetSeconds,
            null
        );
        output.WriteLine($"staged at t={held}s: {Follower} {follower.Phases?.CurrentPhase?.Name}");
        output.WriteLine($"leader: {Leader} {leader.Phases?.CurrentPhase?.Name} gs={leader.GroundSpeed:F1}");
        Assert.True(held > 0, $"{Follower} never held short of {barRunway} within {StageBudgetSeconds}s: {follower.Phases?.CurrentPhase?.Name}");
        return (ground, follower, leader);
    }
}
