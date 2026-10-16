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
        Assert.Equal("28L", departureBar.HoldShort.TargetName);

        CommandResult follow = ground.Engine.SendCommand(Follower, $"FOLLOWG {Leader}");
        output.WriteLine($"FOLLOWG {Leader} at the 28L bar -> success={follow.Success} msg={follow.Message}");
        Assert.False(follow.Success, "FOLLOWG at the departure bar mid-route must be refused: CROSS could never release it");
        Assert.Equal("holding short of departure runway 28L; issue LUAW or CTO", follow.Message);
        Assert.Same(departureBar, follower.Phases?.CurrentPhase);
    }

    /// <summary>
    /// A follow released at the 1R bar whose crossing clearance also covers the follower's own departure runway, 28L: at its 28L
    /// departure bar the follow still stops for a plain hold (<see cref="HoldShortReason.DestinationRunway"/>), not cleared by the
    /// crossing clearance, and never enters 28L — a crossing clearance is no line-up or takeoff clearance. <c>CROSS</c> refuses to
    /// name a runway the route departs from, so the follow behind <c>CROSS 1R</c> is replaced by one cleared across 1R and 28L.
    /// </summary>
    [Fact]
    public void FollowGThenCrossCoveringTheDepartureRunway_HoldsUnclearedAtTheDepartureBar()
    {
        if (Stage(TaxiToOneRightBar, "1R") is not { } staged)
        {
            return;
        }

        (SfoGround ground, AircraftState follower, AircraftState leader) = staged;
        AssertSent(ground, Follower, $"FOLLOWG {Leader}");
        AssertSent(ground, Follower, "CROSS 1R");
        PhaseList phases = Assert.IsType<PhaseList>(follower.Phases);
        CrossingRunwayPhase crossing = Assert.IsType<CrossingRunwayPhase>(phases.Phases[phases.CurrentIndex + 1]);
        RunwayIdentifier[] cleared = [SfoGroundHarness.Runway("1R").Id, SfoGroundHarness.Runway("28L").Id];
        phases.ReplaceUpcoming([crossing, new FollowingPhase(Leader) { CrossingClearedRunways = cleared }]);
        bool leaderLinedUp = false;
        int held = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => (follower.Phases?.CurrentPhase is HoldingShortPhase hold) && SfoGroundHarness.HoldShortMatches(hold.HoldShort, "28L"),
            DepartureBarBudgetSeconds,
            second =>
            {
                LogFollow(second, follower, leader);
                // Move the leader onto 28L so the follower comes up to the bar rather than stopping behind the leader.
                if (!leaderLinedUp && (leader.Phases?.CurrentPhase is HoldingShortPhase))
                {
                    leaderLinedUp = ground.Engine.SendCommand(Leader, "LUAW").Success;
                }
            }
        );
        Assert.True(held > 0, $"the follower never held short of 28L within {DepartureBarBudgetSeconds}s: {follower.Phases?.CurrentPhase?.Name}");
        FollowingPhase follow = Assert.IsType<FollowingPhase>(follower.Phases!.Phases[follower.Phases.CurrentIndex - 1]);
        Assert.Contains(follow.CrossingClearedRunways, r => r.Overlaps(SfoGroundHarness.Runway("28L").Id));

        HoldingShortPhase departureBar = Assert.IsType<HoldingShortPhase>(follower.Phases?.CurrentPhase);
        Assert.Equal(HoldShortReason.DestinationRunway, departureBar.HoldShort.Reason);
        RunwayInfo runway28L = SfoGroundHarness.Runway("28L");
        for (int second = 1; second <= HeldSeconds; second++)
        {
            ground.Engine.TickOneSecond();
            Assert.Same(departureBar, follower.Phases?.CurrentPhase);
            Assert.False(departureBar.HoldShort.IsCleared, $"t={second}s: the crossing clearance cleared the departure bar");
            Assert.False(RunwayOccupancy.IsOnPavement(follower, runway28L), $"t={second}s: entered 28L with no line-up or takeoff clearance");
        }
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
    /// The pilot's call when, cleared across, it holds short for the traffic it follows: the leader is "the traffic" in the
    /// terminal and spoken forms, and only the RPO form names it.
    /// </summary>
    [Fact]
    public void HoldingShortForFollowTraffic_SaysWaitingForTheTrafficToPass()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            output.WriteLine("SKIP: SFO layout or navdata unavailable");
            return;
        }

        AircraftState follower = SfoGroundHarness.SpawnAtSpot(ground, Follower, Type, "3");
        PilotSpeechText call = PilotResponder.BuildHoldingShortForFollowTraffic(follower, "1R", Leader);
        output.WriteLine($"terminal='{call.Terminal}' tts='{call.Tts}' rpo='{call.RpoTerminal}'");
        Assert.Equal("Holding short of runway 1R, waiting for the traffic to pass", call.Terminal);
        Assert.EndsWith(", holding short of runway one right, waiting for the traffic to pass.", call.Tts, StringComparison.Ordinal);
        Assert.Equal($"Holding short of runway 1R, waiting for {Leader} to pass", call.TerminalForRpo);
    }

    /// <summary>
    /// A follow armed at the 1R bar on F1 behind a leader held short of 1L on F, whose route down F meets the follower's just past
    /// 1R where F1 joins F: the follower has no room between 1R's far hold line and its give-way stop for the merge, so on
    /// <c>CROSS 1R</c> it holds at the near bar, the clearance armed. As the wait starts the pilot says it is holding short for the
    /// traffic and the controller is warned, once: not again on later ticks, nor after a snapshot restored mid-wait.
    /// </summary>
    [Fact]
    public void FollowGArmedAtOneRightBar_NoRoomBeyondOneRight_HoldsAtTheBarAndSaysSoOnce()
    {
        if (StageNoRoomAtOneRight() is not { } staged)
        {
            return;
        }

        (SfoGround ground, AircraftState follower, _) = staged;
        HoldingShortPhase hold = Assert.IsType<HoldingShortPhase>(follower.Phases?.CurrentPhase);
        List<string> said = HoldAtTheBar(ground, follower, hold);
        Assert.True(hold.IsWaitingForLead, "the cleared follower is not holding for its leader");
        Assert.Equal(
            [$"Holding short of runway 1R, waiting for {Leader} to pass", $"{Follower} cleared across RWY 1R, holding for the follow traffic"],
            said
        );

        StateSnapshotDto snapshot = ground.Engine.CaptureSnapshot();
        SfoGround restoredGround = Assert.IsType<SfoGround>(SfoGroundHarness.Build(output, autoCross: false));
        restoredGround.Engine.RestoreFromSnapshot(snapshot);
        AircraftState restored = Assert.IsType<AircraftState>(restoredGround.Engine.World.GetSnapshot().FirstOrDefault(a => a.Callsign == Follower));
        HoldingShortPhase restoredHold = Assert.IsType<HoldingShortPhase>(restored.Phases?.CurrentPhase);
        Assert.True(restoredHold.IsWaitingForLead, "the restored hold lost its wait for the leader");
        Assert.Empty(HoldAtTheBar(restoredGround, restored, restoredHold));
        Assert.Empty(HoldAtTheBar(ground, follower, hold));
    }

    /// <summary>
    /// A follower released at the 1L bar on <c>FOLLOWG; CROSS 1L 1R</c> — its leader stood with no route, so there was nothing to
    /// wait for — whose leader is then cleared down F, across 1L and 1R, to meet the follower's route just past 1R: the follow,
    /// cleared across 1R, still stops at the 1R bar, since the merge is now ahead of the leader with no room beyond 1R, and holds
    /// there with the clearance armed, saying so once.
    /// </summary>
    [Fact]
    public void FollowGThenCrossOneLeftOneRight_LeaderRoutedAheadAfterTheRelease_WaitsAtTheOneRightBar()
    {
        if (StageBehind(TaxiToOneLeftBar, "1L", "TAXI A F", Type) is not { } staged)
        {
            return;
        }

        (SfoGround ground, AircraftState follower, AircraftState leader) = staged;
        int restAt = SfoGroundHarness.TickUntil(
            ground.Engine,
            () =>
                (FollowingPhase.DrivenRouteOf(leader) is null or { IsComplete: true }) && (leader.GroundSpeed < SfoGroundHarness.StationarySpeedKts),
            CrossBudgetSeconds,
            null
        );
        Assert.True(restAt >= 0, $"{Leader} never came to rest with no route left: {leader.Phases?.CurrentPhase?.Name}");
        AssertSent(ground, Follower, $"FOLLOWG {Leader}; CROSS 1L 1R");
        int crossingAt = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => follower.Phases?.CurrentPhase is CrossingRunwayPhase,
            CrossBudgetSeconds,
            null
        );
        Assert.True(crossingAt > 0, $"the follower never started across 1L: {follower.Phases?.CurrentPhase?.Name}");
        AssertSent(ground, Leader, "TAXI F 28L");

        List<string> said = [];
        ground.Engine.WarningEmitted += (callsign, warning) =>
        {
            if (callsign == Follower)
            {
                said.Add(warning);
            }
        };
        RunwayInfo runway1R = SfoGroundHarness.Runway("1R");
        int heldAt = SfoGroundHarness.TickUntil(
            ground.Engine,
            () =>
                (follower.Phases?.CurrentPhase is HoldingShortPhase { IsWaitingForLead: true } hold)
                && SfoGroundHarness.HoldShortMatches(hold.HoldShort, "1R"),
            CrossBudgetSeconds,
            second =>
            {
                LogFollow(second, follower, leader);
                Assert.False(RunwayOccupancy.IsOnPavement(follower, runway1R), $"t={second}s: entered 1R with the merge ahead of {Leader}");
            }
        );
        Assert.True(heldAt > 0, $"the follower never held at the 1R bar for {Leader}: {follower.Phases?.CurrentPhase?.Name}");
        HoldingShortPhase oneRight = Assert.IsType<HoldingShortPhase>(follower.Phases?.CurrentPhase);
        HoldAtTheBar(ground, follower, oneRight);
        // The hold-short report any follow's stop at a bar makes, then the wait's own call and warning.
        Assert.Equal(
            [
                $"{Follower} holding short runway 1R/19L at F1",
                $"Holding short of runway 1R, waiting for {Leader} to pass",
                $"{Follower} cleared across RWY 1R, holding for the follow traffic",
            ],
            said
        );
    }

    /// <summary>
    /// The follow of <see cref="FollowGThenCrossOneLeftOneRight_LeaderRoutedAheadAfterTheRelease_WaitsAtTheOneRightBar"/>, driving
    /// up to the 1R bar it is cleared across with a cached verdict that it waits there for its leader, survives a snapshot round
    /// trip: the restored follower drives as the original and stops at the bar on the same tick. It does not guard the cached
    /// verdict itself — the restored follow judges the bar afresh within the second and reaches the same verdict — which
    /// <see cref="FollowingSnapshot_WithACachedWaitVerdict_RoundTripsTheVerdictsAndTheirSecond"/> does.
    /// </summary>
    [Fact]
    public void FollowGThenCrossOneLeftOneRight_SnapshotWithACachedWaitVerdict_StopsAtTheBarAsTheOriginal()
    {
        if (StageCachedWaitVerdict() is not { } staged)
        {
            return;
        }

        (SfoGround ground, AircraftState follower, _) = staged;
        string json = JsonSerializer.Serialize(ground.Engine.CaptureSnapshot(), RecordingJsonOptions.Default);
        SfoGround restoredGround = Assert.IsType<SfoGround>(SfoGroundHarness.Build(output, autoCross: false));
        restoredGround.Engine.RestoreFromSnapshot(
            Assert.IsType<StateSnapshotDto>(JsonSerializer.Deserialize<StateSnapshotDto>(json, RecordingJsonOptions.Default))
        );
        AircraftState restored = Assert.IsType<AircraftState>(restoredGround.Engine.FindAircraft(Follower));
        for (int second = 1; second <= CrossBudgetSeconds; second++)
        {
            ground.Engine.TickOneSecond();
            restoredGround.Engine.TickOneSecond();
            output.WriteLine($"+{second}s: original {follower.Phases?.CurrentPhase?.Name}, restored {restored.Phases?.CurrentPhase?.Name}");
            Assert.Equal(follower.Phases?.CurrentPhase?.Name, restored.Phases?.CurrentPhase?.Name);
            Assert.Equal(follower.Position, restored.Position);
            if (follower.Phases?.CurrentPhase is HoldingShortPhase { IsWaitingForLead: true })
            {
                return;
            }
        }

        Assert.Fail($"the follower never stopped at the 1R bar for {Leader} within {CrossBudgetSeconds}s of the snapshot");
    }

    /// <summary>
    /// A follow's cached wait verdicts at cleared bars and the second they were judged in survive its snapshot both ways: the
    /// follow of <see cref="StageCachedWaitVerdict"/>, snapshot, restored and snapshot again, writes the same verdicts and second.
    /// </summary>
    [Fact]
    public void FollowingSnapshot_WithACachedWaitVerdict_RoundTripsTheVerdictsAndTheirSecond()
    {
        if (StageCachedWaitVerdict() is not { } staged)
        {
            return;
        }

        FollowingPhase follow = Assert.IsType<FollowingPhase>(staged.Follower.Phases?.CurrentPhase);
        FollowingPhaseDto first = Assert.IsType<FollowingPhaseDto>(follow.ToSnapshot());
        FollowingPhaseDto second = Assert.IsType<FollowingPhaseDto>(FollowingPhase.FromSnapshot(first, staged.Ground.Layout).ToSnapshot());
        output.WriteLine($"verdicts {string.Join(", ", first.LeadWaitVerdicts ?? [])} at second {first.LeadWaitSecond}");
        Assert.Equal(follow.LeadWaitVerdicts, Assert.IsType<Dictionary<int, bool>>(first.LeadWaitVerdicts));
        Assert.True(first.LeadWaitSecond is > 0, $"the snapshot's verdict second is {first.LeadWaitSecond?.ToString() ?? "null"}");
        Assert.Equal(first.LeadWaitVerdicts, second.LeadWaitVerdicts);
        Assert.Equal(first.LeadWaitSecond, second.LeadWaitSecond);
    }

    /// <summary>
    /// Stages the follow of <see cref="FollowGThenCrossOneLeftOneRight_LeaderRoutedAheadAfterTheRelease_WaitsAtTheOneRightBar"/>
    /// driving up to the 1R bar with a cached verdict that it waits there for its leader. Null on the repo's silent-skip path.
    /// </summary>
    private (SfoGround Ground, AircraftState Follower, AircraftState Leader)? StageCachedWaitVerdict()
    {
        if (StageBehind(TaxiToOneLeftBar, "1L", "TAXI A F", Type) is not { } staged)
        {
            return null;
        }

        (SfoGround ground, AircraftState follower, AircraftState leader) = staged;
        int restAt = SfoGroundHarness.TickUntil(
            ground.Engine,
            () =>
                (FollowingPhase.DrivenRouteOf(leader) is null or { IsComplete: true }) && (leader.GroundSpeed < SfoGroundHarness.StationarySpeedKts),
            CrossBudgetSeconds,
            null
        );
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
    /// A follow armed at the 1R bar behind a leader stood with no route where LF meets F, past 1R's far bar on the follower's
    /// F1: with no known leader route there is no wait at the bar, and on <c>CROSS 1R</c> the follower crosses and gives way short
    /// of the merge, never at rest inside a hold line, and stops clear past 1R's far hold line.
    /// </summary>
    [Fact]
    public void FollowGArmedAtOneRightBar_LeaderWithNoRoutePastOneRight_GivesWayClearOfTheFarHoldLine()
    {
        if (Stage(TaxiToOneRightBar, "1R") is not { } staged)
        {
            return;
        }

        (SfoGround ground, AircraftState follower, AircraftState leader) = staged;
        StandLeaderWithNoRoute(ground, leader, ToFJunction);
        AssertSent(ground, Follower, $"FOLLOWG {Leader}; CROSS 1R");
        int followingAt = SfoGroundHarness.TickUntil(ground.Engine, () => follower.Phases?.CurrentPhase is FollowingPhase, CrossBudgetSeconds, null);
        Assert.True(followingAt > 0, $"the follow never started within {CrossBudgetSeconds}s of CROSS 1R: {follower.Phases?.CurrentPhase?.Name}");
        for (int second = 1; second <= WaitSeconds; second++)
        {
            ground.Engine.TickOneSecond();
            LogFollow(second, follower, leader);
            AssertNeverAtRestInsideAHoldLine(ground, follower, second);
        }

        Assert.True(follower.GroundSpeed < SfoGroundHarness.StationarySpeedKts, $"the follower is still rolling at {follower.GroundSpeed:F1} kt");
        AssertStoppedClearPastOneRight(ground, follower);
    }

    /// <summary>
    /// A follower cleared across 1L only, whose merge with its leader's route lies just past 1R with no room to wait beyond 1R:
    /// it crosses 1L and holds at the 1R bar its clearance does not cover; cleared across 1R there, it keeps holding for the
    /// leader as a hold at the bar would, saying so once.
    /// </summary>
    [Fact]
    public void FollowGThenCrossOneLeftOnly_NoRoomBeyondOneRight_HoldsAtOneRightThenWaitsThereOnCross()
    {
        if (StageBehind(TaxiToOneLeftBar, "1L", LeaderDownF, Type) is not { } staged)
        {
            return;
        }

        (SfoGround ground, AircraftState follower, AircraftState leader) = staged;
        AssertSent(ground, Follower, $"FOLLOWG {Leader}; CROSS 1L");
        int heldAt = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => (follower.Phases?.CurrentPhase is HoldingShortPhase hold) && SfoGroundHarness.HoldShortMatches(hold.HoldShort, "1R"),
            CrossBudgetSeconds,
            second => LogFollow(second, follower, leader)
        );
        Assert.True(heldAt > 0, $"the follower never held short of 1R after crossing 1L: {follower.Phases?.CurrentPhase?.Name}");
        HoldingShortPhase oneRight = Assert.IsType<HoldingShortPhase>(follower.Phases?.CurrentPhase);
        Assert.False(oneRight.IsWaitingForLead, "the follower not cleared across 1R holds there for its leader");

        AssertSent(ground, Follower, "CROSS 1R");
        List<string> said = HoldAtTheBar(ground, follower, oneRight);
        Assert.True(oneRight.IsWaitingForLead, "cleared across 1R, the follower is not holding for its leader");
        Assert.Equal(
            [$"Holding short of runway 1R, waiting for {Leader} to pass", $"{Follower} cleared across RWY 1R, holding for the follow traffic"],
            said
        );
    }

    /// <summary>
    /// A follow armed at the 1R bar on F1 behind a leader cleared down F across 1L and 1R, released with <c>CROSS 1R</c> once the
    /// leader has driven past the merge where F1 joins F but before its tail is clear of it: the follower waits at the bar for that
    /// tail without a word — no pilot call and no warning, since the leader is moving away past the merge — and then goes.
    /// </summary>
    [Fact]
    public void FollowGArmedAtOneRightBar_LeaderPastTheMergeTailNotClear_WaitsSilentlyThenGoes()
    {
        if (StageBehind(TaxiToOneRightBar, "1R", LeaderDownF, Type) is not { } staged)
        {
            return;
        }

        (SfoGround ground, AircraftState follower, AircraftState leader) = staged;
        HoldingShortPhase hold = Assert.IsType<HoldingShortPhase>(follower.Phases?.CurrentPhase);
        AssertSent(ground, Follower, $"FOLLOWG {Leader}");
        AssertSent(ground, Leader, "CROSS 1L 1R");
        int pastAt = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => LeaderPastTheMergeTailNotClear(ground, follower, leader),
            DepartureBarBudgetSeconds,
            second => LogFollow(second, follower, leader)
        );
        Assert.True(pastAt > 0, $"{Leader} never stood past the merge with its tail not clear of it");

        List<string> said = [];
        void Collect(string callsign, string warning)
        {
            if (callsign == Follower)
            {
                said.Add(warning);
            }
        }

        ground.Engine.WarningEmitted += Collect;
        AssertSent(ground, Follower, "CROSS 1R");
        bool waited = false;
        int goneAt = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => !ReferenceEquals(follower.Phases?.CurrentPhase, hold),
            CrossBudgetSeconds,
            second =>
            {
                waited |= hold.IsWaitingForLead;
                LogFollow(second, follower, leader);
            }
        );
        ground.Engine.WarningEmitted -= Collect;

        Assert.True(waited, "the follower released at 1R never waited for its leader's tail to clear the merge");
        Assert.True(goneAt > 0, $"the follower never left the 1R bar within {CrossBudgetSeconds}s of CROSS 1R");
        Assert.Empty(said);
    }

    /// <summary>
    /// The silent wait of <see cref="FollowGArmedAtOneRightBar_LeaderPastTheMergeTailNotClear_WaitsSilentlyThenGoes"/> turned real:
    /// the follower released at 1R waits without a word for its moving leader's tail to clear the merge, and the leader is then
    /// held with its tail still across it — a B744, long enough to stop from taxi speed with its tail short of the merge. On the next
    /// re-check of the wait the pilot says it is holding short for the traffic and the controller is warned, once for the wait.
    /// </summary>
    [Fact]
    public void FollowGArmedAtOneRightBar_LeaderStopsWithItsTailAcrossTheMerge_SaysTheWaitOnce()
    {
        if (StageBehind(TaxiToOneRightBar, "1R", LeaderDownF, "B744") is not { } staged)
        {
            return;
        }

        (SfoGround ground, AircraftState follower, AircraftState leader) = staged;
        HoldingShortPhase hold = Assert.IsType<HoldingShortPhase>(follower.Phases?.CurrentPhase);
        AssertSent(ground, Follower, $"FOLLOWG {Leader}");
        AssertSent(ground, Leader, "CROSS 1L 1R");
        int pastAt = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => LeaderPastTheMergeTailNotClear(ground, follower, leader),
            DepartureBarBudgetSeconds,
            second => LogFollow(second, follower, leader)
        );
        Assert.True(pastAt > 0, $"{Leader} never stood past the merge with its tail not clear of it");

        List<string> said = [];
        void Collect(string callsign, string warning)
        {
            if (callsign == Follower)
            {
                said.Add(warning);
            }
        }

        ground.Engine.WarningEmitted += Collect;
        AssertSent(ground, Follower, "CROSS 1R");
        AssertSent(ground, Leader, "HOLD");
        ground.Engine.TickOneSecond();
        LogFollow(1, follower, leader);
        Assert.True(hold.IsWaitingForLead, "the follower released at 1R is not waiting for its leader's tail to clear the merge");
        Assert.True(leader.GroundSpeed >= SfoGroundHarness.StationarySpeedKts, $"{Leader} stopped before the wait started");
        Assert.Empty(said);

        for (int second = 2; second <= HeldSeconds; second++)
        {
            ground.Engine.TickOneSecond();
            LogFollow(second, follower, leader);
            Assert.Same(hold, follower.Phases?.CurrentPhase);
        }

        ground.Engine.WarningEmitted -= Collect;
        Assert.True(LeaderPastTheMergeTailNotClear(ground, follower, leader), $"{Leader}'s tail cleared the merge before it stopped");
        Assert.Equal(
            [$"Holding short of runway 1R, waiting for {Leader} to pass", $"{Follower} cleared across RWY 1R, holding for the follow traffic"],
            said
        );
    }

    /// <summary>
    /// Whether <paramref name="leader"/> stands past the merge of <paramref name="follower"/>'s follow with its tail not yet clear
    /// of it.
    /// </summary>
    private static bool LeaderPastTheMergeTailNotClear(SfoGround ground, AircraftState follower, AircraftState leader) =>
        (FollowRoutePlanner.Plan(ground.Layout, follower, leader, () => []) is FollowRoutePlan.Joinable { MergeAheadOfLead: false } plan)
        && (FollowRoutePlanner.LocateOnPath(ground.Layout, plan.LeadPathFromMerge, leader) is { } at)
        && !FollowRoutePlanner.LeadTailPastMerge(plan.LeadPathFromMerge, at, leader.AircraftType);

    /// <summary>
    /// Ticks <see cref="HeldSeconds"/> with <paramref name="follower"/> held by <paramref name="hold"/> short of 1R, failing if it
    /// leaves the hold or enters 1R; returns the warnings it raised meanwhile, in order.
    /// </summary>
    private static List<string> HoldAtTheBar(SfoGround ground, AircraftState follower, HoldingShortPhase hold)
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
    private (SfoGround Ground, AircraftState Follower, AircraftState Leader)? StageNoRoomAtOneRight()
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
        output.WriteLine($"merge at node {plan.MergeNode} aheadOfLead={plan.MergeAheadOfLead}; room beyond 1R {roomFt?.ToString("F0") ?? "none"} ft");
        Assert.True(plan.MergeAheadOfLead, $"the merge at node {plan.MergeNode} is not ahead of the held leader");
        // No room: the tail would stop short of the far bar, or the give-way stop itself lies short of it (no far bar before it).
        Assert.True(roomFt is null or < 0.0, $"the follower fits beyond 1R ({roomFt:F0} ft of room)");
        AssertSent(staged.Ground, Follower, "CROSS 1R");
        return staged;
    }

    /// <summary>The leader's taxi from spot 1 down F, across 1L and 1R, held short of 1L: F meets the follower's F1 past 1R.</summary>
    private const string LeaderDownF = "TAXI A F RWY 28L";

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
        AssertRerouted(ground, leader, follower, ["TAXI L F F1 A", "TAXI F F1 A", "TAXI A F1 F"]);
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
        output.WriteLine($"both at rest at +{restAt}s: nose-to-tail {gapFt:F0} ft, stop gap {stopGapFt:F0} ft");
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

    /// <summary>
    /// 1R's far bar on the follower's route to the merge with <paramref name="leader"/>'s path: the last bar of 1R on it.
    /// </summary>
    private static GroundNode FarBarOfOneRight(SfoGround ground, AircraftState follower, AircraftState leader)
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
    private void AssertWaitingBeyondOneRight(SfoGround ground, AircraftState follower, AircraftState leader, GroundNode farBar)
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
        output.WriteLine($"waiting beyond 1R: {stop.Value.ToStopFt:F0} ft to the give-way stop at node {stop.Value.NodeId}");
        Assert.InRange(stop.Value.ToStopFt, 0.0, GiveWayRestToleranceFt);
    }

    /// <summary>How far (ft) short of its give-way stop a follower at rest there may stand.</summary>
    private const double GiveWayRestToleranceFt = 30.0;

    /// <summary>
    /// Whether the follower has left its give-way stop (rolls again), failing when it does before <paramref name="leader"/>'s tail
    /// is past the merge of <paramref name="plan"/>.
    /// </summary>
    private static bool AssertLeavesTheGiveWayStopOnlyBehindTheLeader(
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
    private const int WaitSeconds = 120;

    /// <summary>
    /// Stages a follow armed at the 1R bar behind a held leader (<c>HOLD</c>, <c>FOLLOWG</c>, <c>CROSS 1R</c>), the merge of
    /// their routes ahead of the leader. Null on the repo's silent-skip path.
    /// </summary>
    private (SfoGround Ground, AircraftState Follower, AircraftState Leader)? StageWaitingAtOneRight()
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
    /// Stops the leader with no route left: <paramref name="taxi"/> names no destination, so it ends where its last two taxiways
    /// meet (<see cref="ToFJunction"/>), and the leader is ticked to rest there. A follower then has
    /// only the leader's trail and the edge it stands on to join — a path guessed from where it stands — so it gives way at the
    /// merge past the bar rather than waiting short of the runway.
    /// </summary>
    private void StandLeaderWithNoRoute(SfoGround ground, AircraftState leader, string taxi)
    {
        AssertSent(ground, Leader, taxi);
        int restAt = SfoGroundHarness.TickUntil(
            ground.Engine,
            () =>
                (FollowingPhase.DrivenRouteOf(leader) is null or { IsComplete: true }) && (leader.GroundSpeed < SfoGroundHarness.StationarySpeedKts),
            CrossBudgetSeconds,
            null
        );
        output.WriteLine($"{Leader} at rest with no route at +{restAt}s: {leader.Phases?.CurrentPhase?.Name} taxiway={leader.Ground.CurrentTaxiway}");
        Assert.True(restAt > 0, $"{Leader} never came to rest with no route left within {CrossBudgetSeconds}s of '{taxi}'");
    }

    /// <summary>The leader's taxi along L and LF that ends where LF meets F, at the merge of its path with the follower's.</summary>
    private const string ToFJunction = "TAXI L LF F";

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
    [Fact(Skip = "Needs a fillet long enough to stage mid-arc; re-enabled by YAAT-675 (follower squares the fillet corner via a 14 ft stub).")]
    public void Following_RouteLostMidFillet_BrakesRoundTheFilletToRest()
    {
        if (StageLostRoute(onFillet: true) is { } lost)
        {
            AssertBrakesAlongTheLostRouteToRest(lost);
        }
    }

    /// <summary>
    /// A follower braking to rest along a lost route brakes for its lead (<see cref="FollowingPhase.LostRouteLeadGapDecelRate"/>)
    /// at the max-effort rate only when the taxi rate would end inside the stop gap behind a lead ahead of it, and at the taxi
    /// rate for a lead ahead outside that stop, a lead abeam or behind it however close, a lead off the ground, and no lead. No
    /// real-layout fixture brings a lead within a follower's stop on a lost route — the follow's gap curve keeps a follower
    /// outside its taxi-rate stop behind a standing lead — so the choice is checked on B738s placed around the follower.
    /// </summary>
    [Fact]
    public void Following_RouteLost_BrakesFirmOnlyForALeadAheadInsideTheTaxiRateStop()
    {
        TestVnasData.EnsureInitialized();
        AircraftCategory category = AircraftCategorization.Categorize(Type);
        double stopGapFt = FollowGap.StopGapFt(Type, category, Type, category);
        double taxiRate = CategoryPerformance.TaxiDecelRate(category);
        double maxEffortRate = CategoryPerformance.ExpediteExitDecelRate(category);
        const double SpeedKts = 15.0;
        double routineStopFt = GroundStopBraking.StoppingDistanceFt(SpeedKts, taxiRate);
        double maxEffortStopFt = GroundStopBraking.StoppingDistanceFt(SpeedKts, maxEffortRate);
        output.WriteLine($"stop gap {stopGapFt:F1} ft, stop at {SpeedKts} kt: taxi rate {routineStopFt:F1} ft, max effort {maxEffortStopFt:F1} ft");
        Assert.True(maxEffortStopFt < routineStopFt, "the max-effort rate stops no shorter than the taxi rate");

        AircraftState follower = OnGround("FOL9", Origin, 0.0, SpeedKts);
        Assert.Null(FollowingPhase.LostRouteLeadGapDecelRate(follower, category, DeadAhead(follower, stopGapFt + routineStopFt + 20.0)));
        double firmGapFt = stopGapFt + ((routineStopFt + maxEffortStopFt) / 2.0);
        Assert.Equal(maxEffortRate, FollowingPhase.LostRouteLeadGapDecelRate(follower, category, DeadAhead(follower, firmGapFt)));
        Assert.Equal(maxEffortRate, FollowingPhase.LostRouteLeadGapDecelRate(follower, category, DeadAhead(follower, stopGapFt)));

        Assert.Null(FollowingPhase.LostRouteLeadGapDecelRate(follower, category, null));
        AircraftState airborne = DeadAhead(follower, stopGapFt);
        airborne.IsOnGround = false;
        Assert.Null(FollowingPhase.LostRouteLeadGapDecelRate(follower, category, airborne));

        // Abeam and behind, a follower rolling fast enough that the gap alone would call for the max-effort rate.
        const double FastKts = 35.0;
        AircraftState fast = OnGround("FOL9", Origin, 0.0, FastKts);
        double fastRoutineStopFt = GroundStopBraking.StoppingDistanceFt(FastKts, taxiRate);
        double halfFt = AircraftLength.ResolveFt(Type) / 2.0;
        AircraftState abeam = OnGround("LED9", GeoMath.ProjectPoint(Origin, new TrueHeading(90.0), 80.0 / GeoMath.FeetPerNm), 0.0, 0.0);
        double behindFt = halfFt + 20.0 + halfFt;
        AircraftState behind = OnGround("LED9", GeoMath.ProjectPoint(Origin, new TrueHeading(180.0), behindFt / GeoMath.FeetPerNm), 180.0, 0.0);
        foreach ((string where, AircraftState lead) in new[] { ("abeam", abeam), ("behind", behind) })
        {
            double toStopFt = FollowingPhase.NoseToTailFt(fast, lead) - stopGapFt;
            output.WriteLine($"lead {where}: {toStopFt:F1} ft past the stop gap, taxi-rate stop from {FastKts} kt {fastRoutineStopFt:F1} ft");
            Assert.True(fastRoutineStopFt > toStopFt, $"the lead {where} is outside the taxi-rate stop, so the sector proves nothing");
            Assert.Null(FollowingPhase.LostRouteLeadGapDecelRate(fast, category, lead));
        }
    }

    private static readonly LatLon Origin = new(37.62, -122.38);

    /// <summary>
    /// A B738 lead at rest facing north dead ahead of <paramref name="follower"/>, which faces north at <see cref="Origin"/>,
    /// <paramref name="noseToTailFt"/> from the follower's nose to the lead's tail.
    /// </summary>
    private static AircraftState DeadAhead(AircraftState follower, double noseToTailFt)
    {
        double centresFt = (AircraftLength.ResolveFt(Type) / 2.0) + noseToTailFt + (AircraftLength.ResolveFt(Type) / 2.0);
        AircraftState lead = OnGround("LED9", GeoMath.ProjectPoint(Origin, new TrueHeading(0.0), centresFt / GeoMath.FeetPerNm), 0.0, 0.0);
        Assert.Equal(noseToTailFt, FollowingPhase.NoseToTailFt(follower, lead), 0.5);
        return lead;
    }

    private static AircraftState OnGround(string callsign, LatLon position, double headingDeg, double speedKts) =>
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
        StandLeaderWithNoRoute(ground, leader, ToFJunction);
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
    [Fact(Skip = "Needs a fillet long enough to stage mid-arc; re-enabled by YAAT-675 (follower squares the fillet corner via a 14 ft stub).")]
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
    /// A follower cannot stop at once, so its follow route may carry it onto an edge the leader's route runs against only while
    /// it brakes to rest (<see cref="HeadOnRun"/>).
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

        headOn.AssertBrakedToRest(output);
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
    /// A follower's run onto an edge its leader's route runs against (<paramref name="againstLead"/>, directed moves), after the
    /// leader's re-route, other than the segment it was on at the re-route (<paramref name="onAtReroute"/>). It may enter one only
    /// braking to rest along a lost route at the firm rate (<see cref="CategoryPerformance.ExpediteExitDecelRate"/>) from the
    /// re-route on, as far as its stop at that rate from <paramref name="rerouteKts"/> carries it plus one sub-tick's travel; it
    /// comes to rest, and does not move on from there until its follow is released onto a route again.
    /// </summary>
    private sealed class HeadOnRun(HashSet<(int, int)> againstLead, TaxiRouteSegment? onAtReroute, double rerouteKts)
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
    private const double FirmRateSlackKts = 0.25;

    /// <summary>
    /// Clears <paramref name="leader"/> on the first re-route it accepts whose re-planned follow joins its path at a node behind
    /// <paramref name="follower"/> (more than 90° off its heading), where the follower has already been, or that the follower
    /// could reach only head-on down the leader's path (<see cref="FollowRoutePlan.FollowerAhead"/>): <c>TAXI F L A</c>, which
    /// brings the leader up F1 toward the follower, re-plans so.
    /// </summary>
    private void RerouteBehindTheFollower(SfoGround ground, AircraftState leader, AircraftState follower)
    {
        string[] taxis = ["TAXI F A", "TAXI L A", "TAXI F B", "TAXI F B A", "TAXI F Z", "TAXI F L A", "TAXI F C", "TAXI F A 28R"];
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
            output.WriteLine(
                $"{Leader} <- '{taxi}' -> success={result.Success} msg={result.Message} route={leader.Ground.AssignedTaxiRoute?.ToSummary()} "
                    + $"replan={plan.GetType().Name} mergeBehind={behind}"
            );
            if (taxi == "TAXI F L A")
            {
                Assert.IsType<FollowRoutePlan.FollowerAhead>(plan);
            }

            if (result.Success && (behind || (plan is FollowRoutePlan.FollowerAhead)))
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
        StandLeaderWithNoRoute(ground, leader, ToFJunction);
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
    /// Arms a follow at the 1R bar behind a leader standing at the LF/F junction with no route left (<see cref="StandLeaderWithNoRoute"/>)
    /// and crosses 1R, then ticks until the follower rolls on its follow route
    /// clear of every runway's hold line — along a straight edge, or with <paramref name="onFillet"/> partway round a fillet arc.
    /// </summary>
    private (SfoGround Ground, AircraftState Follower, AircraftState Leader, FollowingPhase Follow, TaxiRoute Route)? StageRolling(bool onFillet)
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
            FollowRoutePlan plan = FollowRoutePlanner.Replan(ground.Layout, follower, leader, () => []);
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
            if (result.Success && (FollowRoutePlanner.Plan(ground.Layout, follower, leader, () => []) is FollowRoutePlan.FollowerAhead))
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
    private (SfoGround Ground, AircraftState Follower, AircraftState Leader)? Stage(string followerTaxi, string barRunway) =>
        StageBehind(followerTaxi, barRunway, "TAXI A L F 28L", Type);

    /// <summary>As <see cref="Stage"/>, the leader a <paramref name="leaderType"/> cleared on <paramref name="leaderTaxi"/>.</summary>
    private (SfoGround Ground, AircraftState Follower, AircraftState Leader)? StageBehind(
        string followerTaxi,
        string barRunway,
        string leaderTaxi,
        string leaderType
    )
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            output.WriteLine("SKIP: SFO layout or navdata unavailable");
            return null;
        }

        AircraftState leader = SfoGroundHarness.SpawnAtSpot(ground, Leader, leaderType, "1");
        AircraftState follower = SfoGroundHarness.SpawnAtSpot(ground, Follower, Type, "3");
        AssertSent(ground, Follower, followerTaxi);
        ground.Engine.TickOneSecond();
        AssertSent(ground, Leader, leaderTaxi);

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
