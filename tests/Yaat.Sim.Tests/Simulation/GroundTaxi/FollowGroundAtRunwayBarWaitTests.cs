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
/// FOLLOWG at a runway bar when there is no room beyond it or the leader's tail is not clear: the follower waits.
/// </summary>
public class FollowGroundAtRunwayBarWaitTests(ITestOutputHelper output) : FollowGroundAtRunwayBarTestBase(output)
{
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
        SfoGround restoredGround = Assert.IsType<SfoGround>(SfoGroundHarness.Build(Output, autoCross: false));
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
        SfoGround restoredGround = Assert.IsType<SfoGround>(SfoGroundHarness.Build(Output, autoCross: false));
        restoredGround.Engine.RestoreFromSnapshot(
            Assert.IsType<StateSnapshotDto>(JsonSerializer.Deserialize<StateSnapshotDto>(json, RecordingJsonOptions.Default))
        );
        AircraftState restored = Assert.IsType<AircraftState>(restoredGround.Engine.FindAircraft(Follower));
        for (int second = 1; second <= CrossBudgetSeconds; second++)
        {
            ground.Engine.TickOneSecond();
            restoredGround.Engine.TickOneSecond();
            Output.WriteLine($"+{second}s: original {follower.Phases?.CurrentPhase?.Name}, restored {restored.Phases?.CurrentPhase?.Name}");
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
        Output.WriteLine($"verdicts {string.Join(", ", first.LeadWaitVerdicts ?? [])} at second {first.LeadWaitSecond}");
        Assert.Equal(follow.LeadWaitVerdicts, Assert.IsType<Dictionary<int, bool>>(first.LeadWaitVerdicts));
        Assert.True(first.LeadWaitSecond is > 0, $"the snapshot's verdict second is {first.LeadWaitSecond?.ToString() ?? "null"}");
        Assert.Equal(first.LeadWaitVerdicts, second.LeadWaitVerdicts);
        Assert.Equal(first.LeadWaitSecond, second.LeadWaitSecond);
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
}
