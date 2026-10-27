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
/// YAAT-577, replayed: the lead leaves the ground by departing while the follower is still short of its bar.
/// </summary>
public class Oak577FollowBehindDepartingLeadReplayDepartedTests(ITestOutputHelper output) : Oak577FollowBehindDepartingLeadReplayTestBase(output)
{
    /// <summary>
    /// The lead leaves the ground by departing while the follower, with no route of its own, is still short of the lead's
    /// departure bar: the follower taxis on along the lead's path to that bar and holds there, ready for departure on 30.
    /// </summary>
    [Fact]
    public void LeadDepartsBeforeTheFollowerReachesItsBar_FollowerTaxisOnAndHoldsThereAsADepartureHold()
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

        HoldingShortPhase hold = TickUntilFollowerHolds(engine, layout.Nodes[leadBarNodeId], (int)ShortOfTheBarSecond, live: true);
        AssertDepartureHoldAtTheLeadsBar(hold, leadBarNodeId);
    }

    /// <summary>
    /// A lead seen departing counts as departed even once it is deleted (auto-deleted past the departure delete range, or
    /// <c>DEL</c>) while the follower is still taxiing on: the follower reaches the lead's departure bar and holds there.
    /// </summary>
    [Fact]
    public void LeadDeletedAfterItWasSeenDeparting_FollowerStillTaxisOnAndHoldsAtItsBar()
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
        engine.DeleteAircraft(Lead, "DEL");
        Assert.Null(engine.FindAircraft(Lead));

        HoldingShortPhase hold = TickUntilFollowerHolds(engine, layout.Nodes[leadBarNodeId], (int)ShortOfTheBarSecond + 1, live: true);
        AssertDepartureHoldAtTheLeadsBar(hold, leadBarNodeId);
    }
}
