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
public class Oak577FollowBehindDepartingLeadReplayTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/oak576-push-te-parked-neighbour-recording.yaat-bug-report-bundle.zip";
    private const string Follower = "NKS2904";
    private const string Lead = "UPS947";
    private const string LeadRunway = "30";

    /// <summary>A second while UPS947 holds short of 30 at W1 on its own taxi route, before the CTO at t=876.</summary>
    private const double LeadHeldSecond = 870;

    /// <summary>One second before NKS2904 took its hold at the W1 bar in the recording (t=885).</summary>
    private const double BeforeHoldSecond = 884;

    /// <summary>A second while the follower still taxis up to the W1 bar, about 340 ft short of it, behind the lead lining up.</summary>
    private const double ShortOfTheBarSecond = 890;

    /// <summary>The last second the follower is given to take its hold.</summary>
    private const int HoldBudgetSecond = 960;

    /// <summary>Height (ft) above the field the lead is put at when it is staged as departed.</summary>
    private const double DepartedLeadHeightFt = 1500;

    [Fact]
    public void FollowBehindLeadDepartingThirty_HoldsAtTheLeadsDepartureBarAsADepartureHoldForThirty()
    {
        if (Start() is not { } started)
        {
            return;
        }

        (SimulationEngine engine, SessionRecording recording, AirportGroundLayout layout, int leadBarNodeId) = started;
        engine.Replay(recording, BeforeHoldSecond);
        HoldingShortPhase hold = TickUntilFollowerHolds(engine, layout.Nodes[leadBarNodeId], (int)BeforeHoldSecond, live: true);

        output.WriteLine($"hold: node={hold.HoldShort.NodeId} reason={hold.HoldShort.Reason} target={hold.HoldShort.TargetName}");
        Assert.Contains(TestLayoutNodes.RunwayHoldShortsOnTaxiway(layout, LeadRunway, "W1"), n => n.Id == hold.HoldShort.NodeId);
        Assert.Equal(leadBarNodeId, hold.HoldShort.NodeId);
        Assert.Equal(HoldShortReason.DestinationRunway, hold.HoldShort.Reason);
        Assert.Equal(LeadRunway, hold.HoldShort.TargetName);

        AircraftState follower = Assert.IsType<AircraftState>(engine.FindAircraft(Follower));
        Assert.Null(follower.Procedure.DepartureRunway);
        Assert.Null(follower.Phases?.AssignedRunway);
        output.WriteLine($"pending request: {follower.PendingPilotRequest?.Kind}");

        CommandResult res = engine.SendCommand(Follower, "RES");
        output.WriteLine($"RES -> success={res.Success} msg={res.Message}");
        Assert.False(res.Success, "RES must not release a follower held at its departure bar onto the runway");
        Assert.Same(hold, follower.Phases?.CurrentPhase);
    }

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
        StageLeadDeparted(engine);

        HoldingShortPhase hold = TickUntilFollowerHolds(engine, layout.Nodes[leadBarNodeId], (int)ShortOfTheBarSecond, live: true);
        AssertDepartureHoldAtTheLeadsBar(hold, leadBarNodeId);
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
        Assert.IsType<AircraftState>(engine.FindAircraft(Follower)).FlightPlan.Departure = "";

        HoldingShortPhase hold = TickUntilFollowerHolds(engine, layout.Nodes[leadBarNodeId], (int)BeforeHoldSecond, live: true);
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
        StageLeadDeparted(engine);
        engine.ReplayOneSecond();
        Assert.IsType<FollowingPhase>(engine.FindAircraft(Follower)?.Phases?.CurrentPhase);
        engine.DeleteAircraft(Lead, "DEL");
        Assert.Null(engine.FindAircraft(Lead));

        HoldingShortPhase hold = TickUntilFollowerHolds(engine, layout.Nodes[leadBarNodeId], (int)ShortOfTheBarSecond + 1, live: true);
        AssertDepartureHoldAtTheLeadsBar(hold, leadBarNodeId);
    }

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

    private static void AssertDepartureHoldAtTheLeadsBar(HoldingShortPhase hold, int leadBarNodeId)
    {
        Assert.Equal(leadBarNodeId, hold.HoldShort.NodeId);
        Assert.Equal(HoldShortReason.DestinationRunway, hold.HoldShort.Reason);
        Assert.Equal(LeadRunway, hold.HoldShort.TargetName);
    }

    /// <summary>Puts the lead in the air above the field with nothing left to fly: it has left the ground by departing.</summary>
    private void StageLeadDeparted(SimulationEngine engine)
    {
        AircraftState lead = Assert.IsType<AircraftState>(engine.FindAircraft(Lead));
        output.WriteLine($"{Lead} staged departed from {lead.Phases?.CurrentPhase?.Name}");
        lead.Phases = new PhaseList();
        lead.IsOnGround = false;
        lead.Altitude += DepartedLeadHeightFt;
    }

    /// <summary>
    /// Loads the recording and replays to <see cref="LeadHeldSecond"/>, where the lead's taxi route names its departure bar, in a
    /// probe engine: a fresh engine, the recording, the OAK layout and that bar's node. Null on the repo's silent-skip path.
    /// </summary>
    private (SimulationEngine Engine, SessionRecording Recording, AirportGroundLayout Layout, int LeadBarNodeId)? Start()
    {
        SessionRecording? recording = RecordingLoader.Load(RecordingPath);
        TestVnasData.EnsureInitialized();
        var groundData = RecordingGroundData.Evergreen("FOLLOWG behind a lead departing 30 from W1");
        if ((recording is null) || (TestVnasData.NavigationDb is null) || (groundData.GetLayout("OAK") is not { } layout))
        {
            return null;
        }

        var probe = new SimulationEngine(groundData, PushPlanStore.CreateShipped());
        probe.Replay(recording, LeadHeldSecond);
        AircraftState lead = Assert.IsType<AircraftState>(probe.FindAircraft(Lead));
        TaxiRoute leadRoute = Assert.IsType<TaxiRoute>(lead.Ground.AssignedTaxiRoute);
        HoldShortPoint departure = Assert.Single(leadRoute.HoldShortPoints, h => h.Reason == HoldShortReason.DestinationRunway);
        output.WriteLine(
            $"{Lead} at t={LeadHeldSecond}: {lead.Phases?.CurrentPhase?.Name}, route {leadRoute.ToSummary()}, departs node {departure.NodeId}"
        );

        return (new SimulationEngine(groundData, PushPlanStore.CreateShipped()), recording, layout, departure.NodeId);
    }

    /// <summary>
    /// Replays a second at a time from <paramref name="fromSecond"/> (or, with <paramref name="live"/> false, ticks a restored
    /// engine) until the follower holds short, to <see cref="HoldBudgetSecond"/>; its hold.
    /// </summary>
    private HoldingShortPhase TickUntilFollowerHolds(SimulationEngine engine, GroundNode leadBar, int fromSecond, bool live)
    {
        for (int t = fromSecond + 1; t <= HoldBudgetSecond; t++)
        {
            if (live)
            {
                engine.ReplayOneSecond();
            }
            else
            {
                engine.TickOneSecond();
            }

            AircraftState follower = Assert.IsType<AircraftState>(engine.FindAircraft(Follower));
            AircraftState? lead = engine.FindAircraft(Lead);
            output.WriteLine(
                $"t={t} {Follower} {follower.Phases?.CurrentPhase?.Name} gs={follower.GroundSpeed:F1} "
                    + $"bar {GeoMath.DistanceNm(follower.Position, leadBar.Position) * GeoMath.FeetPerNm:F0} ft "
                    + $"{Lead} {lead?.Phases?.CurrentPhase?.Name} onGround={lead?.IsOnGround}"
            );
            if (follower.Phases?.CurrentPhase is HoldingShortPhase hold)
            {
                return hold;
            }
        }

        Assert.Fail($"{Follower} never held short by t={HoldBudgetSecond}");
        return null!;
    }
}
