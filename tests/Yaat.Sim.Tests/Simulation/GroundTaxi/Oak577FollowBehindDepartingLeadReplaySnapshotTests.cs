using System.Text.Json;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// YAAT-577, replayed: a snapshot taken mid-follow keeps the lead's departure bar.
/// </summary>
public class Oak577FollowBehindDepartingLeadReplaySnapshotTests(ITestOutputHelper output) : Oak577FollowBehindDepartingLeadReplayTestBase(output)
{
    /// <summary>
    /// A snapshot taken mid-follow, short of the bar, after the follow has seen the lead depart, keeps the lead's departure bar
    /// and that it saw the lead depart, through JSON: restored, with the lead then deleted, the follower still taxis on to that
    /// bar and holds there as a departure hold for 30, as the original does.
    /// </summary>
    [Fact]
    public void SnapshotShortOfTheBar_KeepsTheLeadsDepartureBar()
    {
        if (Start() is not { } started)
        {
            return;
        }

        (SimulationEngine engine, SessionRecording recording, AirportGroundLayout layout, int leadBarNodeId) = started;
        engine.Replay(recording, ShortOfTheBarSecond);
        // QXE2225 now queues ahead of NKS2904 at the moved W1 bar; the fixture is about the follow.
        engine.DeleteAircraft("QXE2225", "DEL");
        StageLeadDeparted(engine);
        engine.ReplayOneSecond();
        Assert.IsType<FollowingPhase>(engine.FindAircraft(Follower)?.Phases?.CurrentPhase);

        var restored = new SimulationEngine(
            RecordingGroundData.Evergreen("FOLLOWG behind a lead departing 30 from W1"),
            PushPlanStore.CreateShipped()
        );
        string snapshotJson = JsonSerializer.Serialize(engine.CaptureSnapshot(), RecordingJsonOptions.Default);

        // The restore keeps the caller's scenario (its JSON is not in the snapshot), so the restored engine takes the replayed one.
        restored.Scenario = engine.Scenario;
        restored.RestoreFromSnapshot(
            Assert.IsType<StateSnapshotDto>(JsonSerializer.Deserialize<StateSnapshotDto>(snapshotJson, RecordingJsonOptions.Default))
        );
        restored.DeleteAircraft(Lead, "DEL");

        HoldingShortPhase hold = TickUntilFollowerHolds(restored, layout.Nodes[leadBarNodeId], (int)ShortOfTheBarSecond + 1, live: false);
        AssertDepartureHoldAtTheLeadsBar(hold, leadBarNodeId);
    }
}
