using Xunit;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.ControllerAi;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Pilot;

/// <summary>
/// The arrival's taxi-in call (AIM 4-3-21.c) end to end: a C172 on final at OAK lands, exits, and asks whoever answers
/// ground calls for taxi to the parking it picked.
/// </summary>
public class TaxiInCallTests
{
    private readonly ArtccConfigRoot? _zoa = TestArtccConfig.LoadZoa();

    public TaxiInCallTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void AiGround_HearsClearOfTheRunway_WithTheParkingThePilotPicked()
    {
        if (_zoa is null)
        {
            return;
        }

        var ground = TestAiPositions.OakGround(_zoa);
        var engine = AiTestFixture.Load(AiTestFixture.OnFinalAtOak, _zoa, 7, [ground]);
        var aircraft = LandAndExit(engine);
        Assert.True(aircraft.Ground.AwaitingTaxiInCall);

        aircraft = AiTestFixture.TickUntil(
            engine,
            AiTestFixture.Callsign,
            ac => ac.PendingPilotRequest is { IsOpen: true, Kind: PilotPendingRequestKind.Taxi },
            10
        );
        var request = aircraft.PendingPilotRequest!;
        Assert.Equal("Oakland Ground", request.FacilityCallName);
        Assert.NotNull(request.ParkingName);
        Assert.False(ArrivalParkingPicker.IsGateNumber(request.ParkingName), request.ParkingName);
        Assert.StartsWith("Oakland Ground, clear of runway 28R at ", request.LastPilotLine);
        Assert.EndsWith($", taxi to parking {request.ParkingName}.", request.LastPilotLine);
        Assert.Contains("november one five two sierra papa, clear of runway two eight right at ", request.LastPilotLineTts);
        Assert.False(aircraft.Ground.AwaitingTaxiInCall);
        Assert.Contains(ground.PositionId, aircraft.AiInitialContactPositionIds);
        Assert.False(aircraft.HasMadeInitialContact);

        // Unanswered, the pilot asks again on its own clock.
        double first = request.LastRequestedAtSeconds;
        AiTestFixture.Tick(engine, (int)PilotRequestTracker.NormalFollowUpDelaySeconds + 5);
        Assert.True(aircraft.PendingPilotRequest!.LastRequestedAtSeconds > first);
        Assert.Equal(request.FirstRequestedAtSeconds, aircraft.PendingPilotRequest.FirstRequestedAtSeconds);

        // A taxi clearance to the spot answers it and the aircraft goes.
        Assert.True(engine.SendCommand(AiTestFixture.Callsign, $"TAXIAUTO @{request.ParkingName}").Success);
        Assert.False(aircraft.PendingPilotRequest!.IsOpen);
        Assert.Equal(request.ParkingName, aircraft.Ground.AssignedTaxiRoute?.DestinationParking);
    }

    [Fact]
    public void NobodyAnsweringGround_TheCallWaits()
    {
        if (_zoa is null)
        {
            return;
        }

        var engine = AiTestFixture.Load(AiTestFixture.OnFinalAtOak, _zoa, 7, []);
        var aircraft = LandAndExit(engine);

        AiTestFixture.Tick(engine, 30);
        Assert.Null(aircraft.PendingPilotRequest);
        Assert.True(aircraft.Ground.AwaitingTaxiInCall);
        Assert.IsType<HoldingAfterExitPhase>(aircraft.Phases?.CurrentPhase);
    }

    [Fact]
    public void SeparateLocalStaffed_TheCallWaitsUntilTheTowerSendsThePilotToGround()
    {
        if (_zoa is null)
        {
            return;
        }

        // A tower of its own answers at OAK: the pilot stays with it after landing until sent to ground (AIM 4-3-14.c).
        var ground = TestAiPositions.OakGround(_zoa);
        var tower = TestAiPositions.OakTower(_zoa);
        var engine = AiTestFixture.Load(AiTestFixture.OnFinalAtOak, _zoa, 7, [ground, tower]);
        var aircraft = LandAndExit(engine);

        AiTestFixture.Tick(engine, 30);
        Assert.False(aircraft.PendingPilotRequest is { IsOpen: true, Kind: PilotPendingRequestKind.Taxi });
        Assert.True(aircraft.Ground.AwaitingTaxiInCall);
        Assert.False(aircraft.Ground.ReleasedToGround);

        Assert.True(engine.SendCommand(AiTestFixture.Callsign, "CT OAK_GND").Success);
        Assert.True(aircraft.Ground.ReleasedToGround);
        aircraft = AiTestFixture.TickUntil(
            engine,
            AiTestFixture.Callsign,
            ac => ac.PendingPilotRequest is { IsOpen: true, Kind: PilotPendingRequestKind.Taxi },
            10
        );
        Assert.Equal("Oakland Ground", aircraft.PendingPilotRequest!.FacilityCallName);
        Assert.False(aircraft.Ground.ReleasedToGround);
        Assert.False(aircraft.Ground.AwaitingTaxiInCall);
    }

    [Fact]
    public void SoloGroundStudent_GetsTheCall_AsInitialContact()
    {
        if (_zoa is null)
        {
            return;
        }

        var ground = TestAiPositions.OakGround(_zoa);
        var engine = AiTestFixture.Load(AiTestFixture.OnFinalAtOak, _zoa, 7, []);
        var scenario = engine.Scenario!;
        scenario.SoloTrainingMode = true;
        scenario.StudentPosition = ground.Identity;
        scenario.StudentPositionType = "GND";
        var aircraft = LandAndExit(engine);

        // (The student also heard the pilot's final call; the taxi-in request supersedes it.)
        aircraft = AiTestFixture.TickUntil(
            engine,
            AiTestFixture.Callsign,
            ac => ac.PendingPilotRequest is { IsOpen: true, Kind: PilotPendingRequestKind.Taxi },
            10
        );
        Assert.Equal("Oakland Ground", aircraft.PendingPilotRequest!.FacilityCallName);
        Assert.NotNull(aircraft.PendingPilotRequest.ParkingName);
        Assert.True(aircraft.HasMadeInitialContact);
    }

    [Fact]
    public void Snapshot_RoundTripsTheParkingNameAndTheFlag()
    {
        var request = new PilotPendingRequest
        {
            Kind = PilotPendingRequestKind.Taxi,
            FirstRequestedAtSeconds = 10,
            LastRequestedAtSeconds = 10,
            NextFollowUpDueSeconds = 130,
            LastPilotLine = "Oakland Ground, clear of runway 28R at W, taxi to parking SIG1.",
            LastPilotLineTts = "…",
            FacilityCallName = "Oakland Ground",
            ParkingName = "SIG1",
        };
        Assert.Equal("SIG1", PilotPendingRequest.FromSnapshot(request.ToSnapshot()).ParkingName);

        var ops = new AircraftGroundOps { AwaitingTaxiInCall = true };
        Assert.True(AircraftGroundOps.FromSnapshot(ops.ToSnapshot(), null).AwaitingTaxiInCall);
        Assert.False(AircraftGroundOps.FromSnapshot(new AircraftGroundOps().ToSnapshot(), null).AwaitingTaxiInCall);
    }

    [Fact]
    public void TaxiInWording_GateVersusParking()
    {
        var aircraft = new AircraftState { Callsign = "SWA1234", AircraftType = "B738" };

        var gate = PilotResponder.BuildTaxiInRequest(aircraft, "Oakland Ground", "30", "W", "29");
        Assert.Equal("Oakland Ground, clear of runway 30 at W, taxi to gate 29.", gate.Terminal);
        Assert.Equal(
            $"Oakland Ground, southwest twelve thirty four, clear of runway three zero at {PhraseologyVerbalizer.SpellTaxiway("W")}, taxi to gate two nine.",
            gate.Tts
        );

        var ramp = PilotResponder.BuildTaxiInRequest(new AircraftState { Callsign = "N152SP", AircraftType = "C172" }, "ground", null, null, "SIG1");
        Assert.Equal("ground, clear of the runway, taxi to parking SIG1.", ramp.Terminal);
        Assert.Contains("clear of the runway, taxi to parking sierra india golf one.", ramp.Tts);
    }

    private static AircraftState LandAndExit(SimulationEngine engine)
    {
        Assert.True(engine.SendCommand(AiTestFixture.Callsign, "CLAND").Success);
        return AiTestFixture.TickUntil(engine, AiTestFixture.Callsign, ac => ac.Phases?.CurrentPhase is HoldingAfterExitPhase, 400);
    }
}
