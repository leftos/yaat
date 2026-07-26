using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Pilot;

public sealed class M104PendingRequestTests
{
    [Fact]
    public void RecordRequest_SchedulesNormalFollowUp()
    {
        var ac = NewAircraft();

        PilotRequestTracker.RecordRequest(ac, PilotPendingRequestKind.Taxi, nowSeconds: 10, ReadyToTaxiLine, PilotRequestContext.None);

        Assert.NotNull(ac.PendingPilotRequest);
        Assert.Equal(PilotPendingRequestKind.Taxi, ac.PendingPilotRequest.Kind);
        Assert.Equal(PilotPendingRequestResponseState.None, ac.PendingPilotRequest.ResponseState);
        Assert.Equal(10, ac.PendingPilotRequest.FirstRequestedAtSeconds);
        Assert.Equal(10, ac.PendingPilotRequest.LastRequestedAtSeconds);
        Assert.Equal(130, ac.PendingPilotRequest.NextFollowUpDueSeconds);
    }

    [Fact]
    public void TickPendingRequests_QueuesFollowUpAtDueTime()
    {
        var ac = NewAircraft();
        ac.IsOnGround = true;
        var scenario = NewScenario(elapsedSeconds: 129);
        PilotRequestTracker.RecordRequest(ac, PilotPendingRequestKind.Taxi, nowSeconds: 10, ReadyToTaxiLine, PilotRequestContext.None);

        PilotProactive.TickPendingRequests(ac, scenario);
        Assert.Empty(ac.PendingPilotTransmissions);

        scenario.ElapsedSeconds = 130;
        PilotProactive.TickPendingRequests(ac, scenario);

        var transmission = Assert.Single(ac.PendingPilotTransmissions);
        // Re-queued follow-up: terminal (SAY) form is the callsign-free stored form (SAY column carries the callsign).
        Assert.Equal("ground, ready to taxi.", transmission.Text);
        Assert.Equal(130, ac.PendingPilotRequest!.LastRequestedAtSeconds);
        Assert.Equal(250, ac.PendingPilotRequest.NextFollowUpDueSeconds);
    }

    [Theory]
    [InlineData("STBY")]
    [InlineData("ROGER")]
    public void SendCommand_AcknowledgementMovesPendingRequestToStandby(string command)
    {
        var engine = new SimulationEngine(new TestAirportGroundData()) { Scenario = NewScenario(elapsedSeconds: 20) };
        var ac = NewAircraft();
        PilotRequestTracker.RecordRequest(ac, PilotPendingRequestKind.Taxi, nowSeconds: 10, ReadyToTaxiLine, PilotRequestContext.None);
        engine.World.AddAircraft(ac);

        var result = engine.SendCommand(ac.Callsign, command);

        Assert.True(result.Success, result.Message);
        Assert.Equal(PilotPendingRequestResponseState.Standby, ac.PendingPilotRequest!.ResponseState);
        Assert.Equal(110, ac.PendingPilotRequest.NextFollowUpDueSeconds);
        Assert.True(ac.HasControllerAcknowledgedInitialContact);
        Assert.Empty(ac.PendingPilotTransmissions);
    }

    [Fact]
    public void SendCommand_TaxiClosesPendingTaxiRequest()
    {
        var ac = NewAircraft();
        PilotRequestTracker.RecordRequest(ac, PilotPendingRequestKind.Taxi, nowSeconds: 10, ReadyToTaxiLine, PilotRequestContext.None);
        var compound = new CompoundCommand([new ParsedBlock(null, [new TaxiCommand(["A"], [], DestinationRunway: "28R")])]);

        PilotRequestTracker.ApplyControllerResponse(ac, compound, nowSeconds: 20);

        Assert.Equal(PilotPendingRequestResponseState.Satisfied, ac.PendingPilotRequest!.ResponseState);
    }

    [Fact]
    public void ClearedForTakeoff_ClosesPendingTakeoffRequest()
    {
        var ac = NewAircraft();
        PilotRequestTracker.RecordRequest(ac, PilotPendingRequestKind.Takeoff, nowSeconds: 10, ReadyForDepartureLine, PilotRequestContext.None);
        var compound = new CompoundCommand([new ParsedBlock(null, [new ClearedForTakeoffCommand(new DefaultDeparture())])]);

        PilotRequestTracker.ApplyControllerResponse(ac, compound, nowSeconds: 20);

        Assert.Equal(PilotPendingRequestResponseState.Satisfied, ac.PendingPilotRequest!.ResponseState);
    }

    // Issue #307: whatever left a ground-only request open, an airborne aircraft must never re-announce
    // it — "holding short runway 28R, ready for departure" from 3000 ft was the reported symptom.
    [Theory]
    [InlineData(PilotPendingRequestKind.Taxi)]
    [InlineData(PilotPendingRequestKind.Takeoff)]
    public void TryQueueFollowUp_AirborneAircraftClosesGroundOnlyRequest(PilotPendingRequestKind kind)
    {
        var ac = NewAircraft();
        ac.IsOnGround = false;
        PilotRequestTracker.RecordRequest(ac, kind, nowSeconds: 10, ReadyForDepartureLine, PilotRequestContext.None);

        Assert.False(PilotRequestTracker.TryQueueFollowUp(ac, nowSeconds: 130));

        Assert.Equal(PilotPendingRequestResponseState.Superseded, ac.PendingPilotRequest!.ResponseState);
        Assert.Empty(ac.PendingPilotTransmissions);
    }

    [Fact]
    public void TryQueueFollowUp_OnGroundAircraftStillFollowsUp()
    {
        var ac = NewAircraft();
        ac.IsOnGround = true;
        PilotRequestTracker.RecordRequest(ac, PilotPendingRequestKind.Takeoff, nowSeconds: 10, ReadyForDepartureLine, PilotRequestContext.None);

        Assert.True(PilotRequestTracker.TryQueueFollowUp(ac, nowSeconds: 130));

        Assert.Single(ac.PendingPilotTransmissions);
    }

    // An airborne-only request kind is untouched by the ground-only guard.
    [Fact]
    public void TryQueueFollowUp_AirborneApproachRequestStillFollowsUp()
    {
        var ac = NewAircraft();
        ac.IsOnGround = false;
        PilotRequestTracker.RecordRequest(ac, PilotPendingRequestKind.Approach, nowSeconds: 10, ReadyForDepartureLine, PilotRequestContext.None);

        Assert.True(PilotRequestTracker.TryQueueFollowUp(ac, nowSeconds: 130));

        Assert.Single(ac.PendingPilotTransmissions);
    }

    [Fact]
    public void AtParking_InitialCallupRecordsTaxiRequest()
    {
        var ac = NewAircraft();
        ac.IsOnGround = true;
        ac.Ground = new AircraftGroundOps { ParkingSpot = "KILO RAMP" };
        var phase = new AtParkingPhase();
        var ctx = new PhaseContext
        {
            Aircraft = ac,
            Targets = ac.Targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = 1,
            Logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            SoloTrainingMode = true,
            ScenarioElapsedSeconds = 5,
            ScenarioId = "TEST-SCENARIO",
            SoloParkingInitialCallupRatePercent = 100,
        };

        phase.OnStart(ctx);
        phase.ElapsedSeconds = 5;
        phase.OnTick(ctx);

        Assert.Single(ac.PendingPilotTransmissions);
        Assert.NotNull(ac.PendingPilotRequest);
        Assert.Equal(PilotPendingRequestKind.Taxi, ac.PendingPilotRequest.Kind);
        Assert.Equal(125, ac.PendingPilotRequest.NextFollowUpDueSeconds);
    }

    // Terminal (SAY, callsign-free) + spoken (TTS, callsign spelled) forms of a ready-to-taxi call,
    // mirroring what the six proactive builders produce.
    private static readonly PilotSpeechText ReadyToTaxiLine = new(
        "ground, ready to taxi.",
        "ground, november one two three alpha bravo, ready to taxi."
    );

    private static readonly PilotSpeechText ReadyForDepartureLine = new(
        "tower, holding short runway 28R, ready for departure.",
        "tower, november one two three alpha bravo holding short runway two eight right, ready for departure."
    );

    private static AircraftState NewAircraft() =>
        new()
        {
            Callsign = "N123AB",
            AircraftType = "C172",
            Position = new LatLon(37.0, -122.0),
            TrueHeading = new TrueHeading(270),
            TrueTrack = new TrueHeading(270),
            FlightPlan = new AircraftFlightPlan { FlightRules = "VFR", HasFlightPlan = true },
        };

    private static SimScenarioState NewScenario(double elapsedSeconds) =>
        new()
        {
            ScenarioId = "test",
            ScenarioName = "Test",
            RngSeed = 1,
            OriginalScenarioJson = "{}",
            SoloTrainingMode = true,
            ElapsedSeconds = elapsedSeconds,
        };
}
