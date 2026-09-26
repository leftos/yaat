using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
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
        output.WriteLine($"terminal='{readback.Terminal}' tts='{readback.Tts}'");
        // The leader is "the traffic" to the pilot (docs/pilot-phraseology.md); the hold-short is read back with the runway.
        Assert.Equal("follow the traffic, hold short of runway 1R", readback.Terminal);
        Assert.StartsWith("follow the traffic, hold short of runway one right, ", readback.Tts, StringComparison.Ordinal);
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
