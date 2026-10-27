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
/// YAAT-577, replayed from the OAK S1-OAK-5 bundle: NKS2904, cleared on a node-list taxi with no runway and then
/// <c>FOLLOWG UPS947</c>, moved up behind UPS947 as it lined up on runway 30 from W1 and held at the same W1 bar. That bar is
/// the lead's departure bar, and the follower has no departure runway of its own, so it holds there as a departure hold for
/// runway 30 (the end, never the combined "30/12"): <c>RES</c> is refused, and no runway is assigned to it by the hold.
/// </summary>
public class Oak577FollowBehindDepartingLeadReplayTests(ITestOutputHelper output) : Oak577FollowBehindDepartingLeadReplayTestBase(output)
{
    [Fact]
    public void FollowBehindLeadDepartingThirty_HoldsAtTheLeadsDepartureBarAsADepartureHoldForThirty()
    {
        if (Start() is not { } started)
        {
            return;
        }

        (SimulationEngine engine, SessionRecording recording, AirportGroundLayout layout, int leadBarNodeId) = started;
        engine.Replay(recording, BeforeHoldSecond);
        // QXE2225 now queues ahead of NKS2904 at the moved W1 bar; the fixture is about the follow.
        engine.DeleteAircraft("QXE2225", "DEL");
        HoldingShortPhase hold = TickUntilFollowerHolds(engine, layout.Nodes[leadBarNodeId], (int)BeforeHoldSecond, live: true);

        Output.WriteLine($"hold: node={hold.HoldShort.NodeId} reason={hold.HoldShort.Reason} target={hold.HoldShort.TargetName}");
        Assert.Contains(TestLayoutNodes.RunwayHoldShortsOnTaxiway(layout, LeadRunway, "W1"), n => n.Id == hold.HoldShort.NodeId);
        Assert.Equal(leadBarNodeId, hold.HoldShort.NodeId);
        Assert.Equal(HoldShortReason.DestinationRunway, hold.HoldShort.Reason);
        Assert.Equal(LeadRunway, hold.HoldShort.TargetName);

        AircraftState follower = Assert.IsType<AircraftState>(engine.FindAircraft(Follower));
        Assert.Null(follower.Procedure.DepartureRunway);
        Assert.Null(follower.Phases?.AssignedRunway);
        Output.WriteLine($"pending request: {follower.PendingPilotRequest?.Kind}");

        CommandResult res = engine.SendCommand(Follower, "RES");
        Output.WriteLine($"RES -> success={res.Success} msg={res.Message}");
        Assert.False(res.Success, "RES must not release a follower held at its departure bar onto the runway");
        Assert.Same(hold, follower.Phases?.CurrentPhase);
    }

    /// <summary>
    /// A follower whose flight plan names no departure airport (a VFR local with nothing filed) is not taken for an arrival: it
    /// holds at the lead's departure bar as a departure hold for 30, never as a crossing RES would release onto the runway.
    /// </summary>
    [Fact]
    public void FollowerWithNoDepartureAirportFiled_HoldsAtTheLeadsDepartureBarAsADepartureHold()
    {
        if (Start() is not { } started)
        {
            return;
        }

        (SimulationEngine engine, SessionRecording recording, AirportGroundLayout layout, int leadBarNodeId) = started;
        engine.Replay(recording, BeforeHoldSecond);
        // QXE2225 now queues ahead of NKS2904 at the moved W1 bar; the fixture is about the follow.
        engine.DeleteAircraft("QXE2225", "DEL");
        Assert.IsType<AircraftState>(engine.FindAircraft(Follower)).FlightPlan.Departure = "";

        HoldingShortPhase hold = TickUntilFollowerHolds(engine, layout.Nodes[leadBarNodeId], (int)BeforeHoldSecond, live: true);
        AssertDepartureHoldAtTheLeadsBar(hold, leadBarNodeId);
    }
}
