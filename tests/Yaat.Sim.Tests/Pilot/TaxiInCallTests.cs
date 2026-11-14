using Xunit;
using Yaat.Sim.ControllerAi;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
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

        AiPositionConfig ground = TestAiPositions.OakGround(_zoa);
        SimulationEngine engine = AiTestFixture.Load(AiTestFixture.OnFinalAtOak, _zoa, 7, [ground]);
        AircraftState aircraft = LandAndExit(engine);
        Assert.True(aircraft.Ground.AwaitingTaxiInCall);

        aircraft = AiTestFixture.TickUntil(
            engine,
            AiTestFixture.Callsign,
            ac => ac.PendingPilotRequest is { IsOpen: true, Kind: PilotPendingRequestKind.Taxi },
            10
        );
        PilotPendingRequest request = aircraft.PendingPilotRequest!;
        Assert.Equal("Oakland Ground", request.FacilityCallName);
        Assert.NotNull(request.ParkingName);
        Assert.False(ArrivalParkingPicker.IsGateName(request.ParkingName), request.ParkingName);
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

        SimulationEngine engine = AiTestFixture.Load(AiTestFixture.OnFinalAtOak, _zoa, 7, []);
        AircraftState aircraft = LandAndExit(engine);

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
        AiPositionConfig ground = TestAiPositions.OakGround(_zoa);
        AiPositionConfig tower = TestAiPositions.OakTower(_zoa);
        SimulationEngine engine = AiTestFixture.Load(AiTestFixture.OnFinalAtOak, _zoa, 7, [ground, tower]);
        AircraftState aircraft = LandAndExit(engine);

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

        AiPositionConfig ground = TestAiPositions.OakGround(_zoa);
        SimulationEngine engine = AiTestFixture.Load(AiTestFixture.OnFinalAtOak, _zoa, 7, []);
        SimScenarioState scenario = engine.Scenario!;
        scenario.SoloTrainingMode = true;
        scenario.StudentPosition = ground.Identity;
        scenario.StudentPositionType = "GND";
        AircraftState aircraft = LandAndExit(engine);

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

        PilotSpeechText gate = PilotResponder.BuildTaxiInRequest(aircraft, "Oakland Ground", "30", "W", "29");
        Assert.Equal("Oakland Ground, clear of runway 30 at W, taxi to gate 29.", gate.Terminal);
        Assert.Equal(
            $"Oakland Ground, southwest twelve thirty four, clear of runway three zero at {PhraseologyVerbalizer.SpellTaxiway("W")}, taxi to gate two nine.",
            gate.Tts
        );

        PilotSpeechText ramp = PilotResponder.BuildTaxiInRequest(
            new AircraftState { Callsign = "N152SP", AircraftType = "C172" },
            "ground",
            null,
            null,
            "SIG1"
        );
        Assert.Equal("ground, clear of the runway, taxi to parking SIG1.", ramp.Terminal);
        Assert.Contains("clear of the runway, taxi to parking sierra india golf one.", ramp.Tts);

        PilotSpeechText word = PilotResponder.BuildTaxiInRequest(
            new AircraftState { Callsign = "N152SP", AircraftType = "C172" },
            "ground",
            null,
            null,
            "SIGNATURE"
        );
        Assert.EndsWith("taxi to SIGNATURE.", word.Terminal);
        Assert.EndsWith("taxi to signature.", word.Tts);
    }

    /// <summary>
    /// A GA arrival at SMF, whose only non-gate names are listed as non-gate parking, asks for plain taxi to parking with no
    /// spot named, and the request records no parking name (the instructor assigns one).
    /// </summary>
    [Fact]
    public void SmfGaArrival_AsksForTaxiToParking_NamingNone()
    {
        if (_zoa is null)
        {
            return;
        }

        PilotPendingRequest request = SmfTaxiInRequest(AiTestFixture.Callsign, "C172");

        Assert.Null(request.ParkingName);
        Assert.True(request.TaxiIn);
        Assert.Contains("clear of runway 17L", request.LastPilotLine);
        Assert.EndsWith(", taxi to parking.", request.LastPilotLine);
        Assert.EndsWith(", taxi to parking.", request.LastPilotLineTts);
    }

    /// <summary>An airline arrival at SMF still names a lettered terminal gate, never a runway or terminal marker.</summary>
    [Fact]
    public void SmfAirlineArrival_StillNamesALetteredGate()
    {
        if (_zoa is null)
        {
            return;
        }

        PilotPendingRequest request = SmfTaxiInRequest("SWA1234", "B738");

        Assert.NotNull(request.ParkingName);
        Assert.True(request.TaxiIn);
        Assert.True(char.IsAsciiLetterUpper(request.ParkingName[0]), request.ParkingName);
        Assert.True(ArrivalParkingPicker.IsGateName(request.ParkingName), request.ParkingName);
        Assert.EndsWith($", taxi to gate {request.ParkingName}.", request.LastPilotLine);
    }

    [Fact]
    public void Snapshot_RoundTripsTheTaxiInFlag_WithAndWithoutAParkingName()
    {
        var unnamed = new PilotPendingRequest
        {
            Kind = PilotPendingRequestKind.Taxi,
            FirstRequestedAtSeconds = 10,
            LastPilotLine = "Sacramento Ground, clear of runway 17L at A, taxi to parking.",
            LastPilotLineTts = "…",
            TaxiIn = true,
        };
        var restored = PilotPendingRequest.FromSnapshot(unnamed.ToSnapshot());
        Assert.True(restored.TaxiIn);
        Assert.Null(restored.ParkingName);

        var departure = new PilotPendingRequest
        {
            Kind = PilotPendingRequestKind.Taxi,
            FirstRequestedAtSeconds = 10,
            LastPilotLine = "",
            LastPilotLineTts = "",
        };
        Assert.False(PilotPendingRequest.FromSnapshot(departure.ToSnapshot()).TaxiIn);

        // A snapshot from before the flag: a named parking was a taxi-in request.
        PilotPendingRequestDto older = new PilotPendingRequest
        {
            Kind = PilotPendingRequestKind.Taxi,
            FirstRequestedAtSeconds = 10,
            LastPilotLine = "",
            LastPilotLineTts = "",
            ParkingName = "SIG1",
        }.ToSnapshot();
        Assert.True(PilotPendingRequest.FromSnapshot(older).TaxiIn);
    }

    [Fact]
    public void TaxiInWording_NoParkingNamed()
    {
        PilotSpeechText line = PilotResponder.BuildTaxiInRequest(
            new AircraftState { Callsign = "N152SP", AircraftType = "C172" },
            "Sacramento Ground",
            "17L",
            null,
            null
        );

        Assert.Equal("Sacramento Ground, clear of runway 17L, taxi to parking.", line.Terminal);
        Assert.EndsWith(", clear of runway one seven left, taxi to parking.", line.Tts);
    }

    private PilotPendingRequest SmfTaxiInRequest(string callsign, string aircraftType)
    {
        AiPositionConfig ground = TestAiPositions.TowerCab(_zoa!, "SMF_GND", ControlRole.Ground, "SMF");
        string scenario = OnFinalAtSmf.Replace("CALLSIGN", callsign).Replace("ACTYPE", aircraftType);
        SimulationEngine engine = AiTestFixture.Load(scenario, _zoa!, 7, [ground]);
        Assert.True(engine.SendCommand(callsign, "CLAND").Success);
        AiTestFixture.TickUntil(engine, callsign, ac => ac.Phases?.CurrentPhase is HoldingAfterExitPhase, 400);
        AircraftState aircraft = AiTestFixture.TickUntil(
            engine,
            callsign,
            ac => ac.PendingPilotRequest is { IsOpen: true, Kind: PilotPendingRequestKind.Taxi },
            10
        );
        return aircraft.PendingPilotRequest!;
    }

    private const string OnFinalAtSmf = """
        {
          "id": "ai-on-final-smf",
          "name": "AI on final at SMF",
          "artccId": "ZOA",
          "primaryAirportId": "SMF",
          "aircraft": [
            {
              "id": "a1",
              "aircraftId": "CALLSIGN",
              "aircraftType": "ACTYPE",
              "transponderMode": "C",
              "startingConditions": { "type": "OnFinal", "runway": "17L", "distanceFromRunway": 4 },
              "flightplan": {
                "rules": "VFR", "departure": "KSMF", "destination": "KSMF", "cruiseAltitude": 1500, "cruiseSpeed": 100,
                "route": "", "remarks": "", "aircraftType": "ACTYPE"
              }
            }
          ]
        }
        """;

    private static AircraftState LandAndExit(SimulationEngine engine)
    {
        Assert.True(engine.SendCommand(AiTestFixture.Callsign, "CLAND").Success);
        return AiTestFixture.TickUntil(engine, AiTestFixture.Callsign, ac => ac.Phases?.CurrentPhase is HoldingAfterExitPhase, 400);
    }
}
