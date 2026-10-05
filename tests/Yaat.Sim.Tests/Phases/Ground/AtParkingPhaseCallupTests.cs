using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.ControllerAi;
using Yaat.Sim.ControllerAi.Brains;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.ControllerAi;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Phases.Ground;

/// <summary>
/// The autonomous ready-to-taxi call-up fires for any ground-spawned aircraft with no taxi clearance yet,
/// wherever it sits: at a named stand, or at a ground coordinate (which says "at the ramp"). A runway
/// spawn never gets an <see cref="AtParkingPhase"/>, so it never calls (YAAT-308 case 2). An aircraft
/// parked at the end of a taxi is not a ground spawn and must not call again (case 3).
/// </summary>
public class AtParkingPhaseCallupTests
{
    private readonly ArtccConfigRoot? _zoa = TestArtccConfig.LoadZoa();

    private readonly ITestOutputHelper _output;

    public AtParkingPhaseCallupTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    // Taxiway K at OAK (the H2 case's ground spawn): N52417 is an arrival (KLVK→KOAK) the scenario placed
    // on the surface, and it calls for taxi like any other ground spawn. A normal SIG1 parking spawn rides
    // along as a control: the control proves the call-up still fires, so the K assertion cannot pass by the
    // call-up simply being removed.
    private const string TaxiwayKAndParkedAtOak = """
        {
          "id": "callup-twy-k",
          "name": "Ground coordinate spawn on K",
          "artccId": "ZOA",
          "primaryAirportId": "OAK",
          "aircraft": [
            {
              "id": "a1",
              "aircraftId": "N52417",
              "aircraftType": "C172",
              "transponderMode": "C",
              "startingConditions": { "type": "Coordinates", "coordinates": { "lat": 37.73212069, "lon": -122.22503752 } },
              "flightplan": { "rules": "VFR", "departure": "KLVK", "destination": "KOAK", "cruiseAltitude": 1500, "cruiseSpeed": 100, "route": "", "remarks": "", "aircraftType": "C172" },
              "airportId": "OAK"
            },
            {
              "id": "a2",
              "aircraftId": "N152SP",
              "aircraftType": "C172",
              "transponderMode": "C",
              "startingConditions": { "type": "Parking", "parking": "SIG1" },
              "flightplan": { "rules": "VFR", "departure": "KOAK", "destination": "KOAK", "cruiseAltitude": 1500, "cruiseSpeed": 100, "route": "", "remarks": "", "aircraftType": "C172" }
            }
          ]
        }
        """;

    private const string ParkedAtOak = """
        {
          "id": "callup-taxi-arrival",
          "name": "Parked after a taxi",
          "artccId": "ZOA",
          "primaryAirportId": "OAK",
          "aircraft": [
            {
              "id": "a1",
              "aircraftId": "N152SP",
              "aircraftType": "C172",
              "transponderMode": "C",
              "startingConditions": { "type": "Parking", "parking": "SIG1" },
              "flightplan": { "rules": "VFR", "departure": "KOAK", "destination": "KOAK", "cruiseAltitude": 1500, "cruiseSpeed": 100, "route": "", "remarks": "", "aircraftType": "C172" }
            }
          ]
        }
        """;

    // A coordinate spawn that departs from the airport it sits on still checks in. The point is on the
    // south GA ramp between GA7 and GA8, so it has no spot name and falls back to "at the ramp".
    private const string CoordinateDepartureAtOak = """
        {
          "id": "callup-ramp-departure",
          "name": "Coordinate departure on the ramp",
          "artccId": "ZOA",
          "primaryAirportId": "OAK",
          "aircraft": [
            {
              "id": "a1",
              "aircraftId": "N123SP",
              "aircraftType": "C172",
              "transponderMode": "C",
              "startingConditions": { "type": "Coordinates", "coordinates": { "lat": 37.732479, "lon": -122.215235 } },
              "flightplan": { "rules": "VFR", "departure": "KOAK", "destination": "KSFO", "cruiseAltitude": 1500, "cruiseSpeed": 100, "route": "", "remarks": "", "aircraftType": "C172" },
              "airportId": "OAK"
            }
          ]
        }
        """;

    // A runway spawn ("OnRunway") never gets an AtParkingPhase: AircraftInitializer.InitializeOnRunway
    // builds LinedUpAndWaitingPhase → TakeoffPhase → InitialClimbPhase, so it makes no ready-to-taxi call.
    private const string OnRunwayAtOak = """
        {
          "id": "callup-runway",
          "name": "Runway spawn",
          "artccId": "ZOA",
          "primaryAirportId": "OAK",
          "aircraft": [
            {
              "id": "a1",
              "aircraftId": "N321RW",
              "aircraftType": "C172",
              "transponderMode": "C",
              "startingConditions": { "type": "OnRunway", "runway": "28R" },
              "flightplan": { "rules": "VFR", "departure": "KOAK", "destination": "KOAK", "cruiseAltitude": 1500, "cruiseSpeed": 100, "route": "", "remarks": "", "aircraftType": "C172" },
              "airportId": "OAK"
            }
          ]
        }
        """;

    private const string CoordinateLocalVfrAtOak = """
        {
          "id": "callup-ramp-local",
          "name": "Coordinate local VFR on the ramp",
          "artccId": "ZOA",
          "primaryAirportId": "OAK",
          "aircraft": [
            {
              "id": "a1",
              "aircraftId": "N123SP",
              "aircraftType": "C172",
              "transponderMode": "C",
              "startingConditions": { "type": "Coordinates", "coordinates": { "lat": 37.732479, "lon": -122.215235 } },
              "flightplan": { "rules": "VFR", "departure": "KOAK", "destination": "KOAK", "cruiseAltitude": 1500, "cruiseSpeed": 100, "route": "", "remarks": "", "aircraftType": "C172" },
              "airportId": "OAK"
            }
          ]
        }
        """;

    [Fact]
    public void GroundCoordinateSpawnOnTaxiway_CallsForTaxi()
    {
        if (_zoa is null)
        {
            return;
        }

        (SimulationEngine engine, List<TerminalEntry> said) = Load(TaxiwayKAndParkedAtOak);

        Tick(engine, 30);

        List<TerminalEntry> lines =
        [
            .. said.Where(e =>
                string.Equals(e.Callsign, "N52417", StringComparison.OrdinalIgnoreCase)
                && e.Message.Contains("on taxiway K", StringComparison.OrdinalIgnoreCase)
            ),
        ];
        Assert.Single(lines);
        // The arrival names the taxiway it sits on and, bound for this field, asks for parking instead of "ready to taxi".
        Assert.Equal("Oakland Ground, on taxiway K, with information Alpha, request taxi to parking.", lines[0].Message);
        Assert.Empty(ReadyToTaxiLines(said, "N52417"));
        Assert.Single(ReadyToTaxiLines(said, "N152SP"));
    }

    [Fact]
    public void TaxiwaySpawnAskingForParking_AiGroundTaxisItToParking_NeverToARunway()
    {
        if (_zoa is null)
        {
            return;
        }

        AiPositionConfig ground = TestAiPositions.OakGround(_zoa);
        SimulationEngine engine = AiTestFixture.LoadWith(TaxiwayKAndParkedAtOak, _zoa, 7, [ground], "30", p => new GroundBrain(p));

        AircraftState asked = AiTestFixture.TickUntil(engine, "N52417", ac => ac.PendingPilotRequest is { Kind: PilotPendingRequestKind.Taxi }, 30);
        string? parking = asked.PendingPilotRequest!.ParkingName;
        Assert.NotNull(parking);

        AiTestFixture.TickUntil(engine, "N52417", ac => !(ac.PendingPilotRequest?.IsOpen ?? false), 60);
        string aiId = AiConnectionId.Format(ground.PositionId);
        RecordedCommand answer = Assert.Single(
            engine.Scenario!.ActionLog.OfType<RecordedCommand>(),
            a => (a.ConnectionId == aiId) && string.Equals(a.Callsign, "N52417", StringComparison.OrdinalIgnoreCase)
        );
        Assert.Equal($"TAXIAUTO @{parking}", answer.Command);
    }

    [Fact]
    public void CoordinateSpawnedDeparture_CallsOnceFromTheRamp()
    {
        if (_zoa is null)
        {
            return;
        }

        (SimulationEngine engine, List<TerminalEntry> said) = Load(CoordinateDepartureAtOak);

        Tick(engine, 30);

        List<TerminalEntry> lines = ReadyToTaxiLines(said, "N123SP");
        Assert.Single(lines);
        Assert.Contains("at the ramp", lines[0].Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CoordinateSpawnedLocalVfr_Calls()
    {
        if (_zoa is null)
        {
            return;
        }

        (SimulationEngine engine, List<TerminalEntry> said) = Load(CoordinateLocalVfrAtOak);

        Tick(engine, 30);

        List<TerminalEntry> lines = ReadyToTaxiLines(said, "N123SP");
        Assert.Single(lines);
        Assert.Contains("at the ramp", lines[0].Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RunwaySpawn_NoCallup()
    {
        if (_zoa is null)
        {
            return;
        }

        (SimulationEngine engine, List<TerminalEntry> said) = Load(OnRunwayAtOak);

        Tick(engine, 30);

        Assert.Empty(ReadyToTaxiLines(said, "N321RW"));
    }

    // TaxiingPhase inserts a fresh AtParkingPhase at the destination stand. Leaving the first stand uncalled ended the
    // stand call (AtParkingPhase.OnEnd sets the plan to None), so the new stand starts no call.
    [Fact]
    public void StandCallTaxiedOffByTheControllerBeforeItsCall_NeverCallsAtTheNextStand()
    {
        if (_zoa is null)
        {
            return;
        }

        (SimulationEngine engine, List<TerminalEntry> said) = Load(ParkedAtOak);

        // Taxi before the 5 s first-call delay: the aircraft leaves the stand without ever placing its
        // ready-to-taxi call. When it later parks at GA7 the call-up must not start from scratch.
        Tick(engine, 1);
        CommandResult taxi = engine.SendCommand("N152SP", "TAXIAUTO @GA7");
        Assert.True(taxi.Success, taxi.Message);
        Assert.Equal(InitialCallupPlan.None, engine.FindAircraft("N152SP")!.Ground.InitialCallup);
        Assert.True(TickUntilAtStand(engine, "N152SP", "GA7", 600), "the aircraft should reach GA7");

        Tick(engine, 600);

        Assert.Empty(ReadyToTaxiLines(said, "N152SP"));
    }

    // An arrival on a two-mile final to OAK 28R lands, exits, and taxis to stand GA7: it is not a ground spawn, so it never
    // calls ready to taxi, there or after it parks. The SIG1 stand spawn is the control that proves the call-up still fires.
    private const string ArrivalAndParkedAtOak = """
        {
          "id": "callup-arrival",
          "name": "Arrival taxis to a stand",
          "artccId": "ZOA",
          "primaryAirportId": "OAK",
          "aircraft": [
            {
              "id": "a1",
              "aircraftId": "N52417",
              "aircraftType": "C172",
              "transponderMode": "C",
              "startingConditions": { "type": "OnFinal", "runway": "28R", "distanceFromRunway": 2 },
              "flightplan": { "rules": "VFR", "departure": "KLVK", "destination": "KOAK", "cruiseAltitude": 1500, "cruiseSpeed": 100, "route": "", "remarks": "", "aircraftType": "C172" },
              "airportId": "OAK"
            },
            {
              "id": "a2",
              "aircraftId": "N152SP",
              "aircraftType": "C172",
              "transponderMode": "C",
              "startingConditions": { "type": "Parking", "parking": "SIG1" },
              "flightplan": { "rules": "VFR", "departure": "KOAK", "destination": "KOAK", "cruiseAltitude": 1500, "cruiseSpeed": 100, "route": "", "remarks": "", "aircraftType": "C172" }
            }
          ]
        }
        """;

    [Fact]
    public void ArrivalThatLandsAndTaxisToAStand_NeverCallsReadyToTaxi()
    {
        if (_zoa is null)
        {
            return;
        }

        (SimulationEngine engine, List<TerminalEntry> said) = Load(ArrivalAndParkedAtOak);
        Tick(engine, 1);
        AircraftState? arrival = engine.FindAircraft("N52417");
        Assert.NotNull(arrival);
        Assert.Equal(InitialCallupPlan.None, arrival.Ground.InitialCallup);
        CommandResult cland = engine.SendCommand("N52417", "CLAND");
        Assert.True(cland.Success, cland.Message);

        Assert.True(TickUntilPhase<HoldingAfterExitPhase>(engine, "N52417", 600), "the arrival should land and exit the runway");
        CommandResult taxi = engine.SendCommand("N52417", "TAXIAUTO @GA7");
        Assert.True(taxi.Success, taxi.Message);
        Assert.True(TickUntilAtStand(engine, "N52417", "GA7", 900), "the arrival should reach GA7");

        Tick(engine, 130);

        Assert.Empty(ReadyToTaxiLines(said, "N52417"));
        // The control calls, and with nobody answering repeats it on the follow-up cadence.
        Assert.NotEmpty(ReadyToTaxiLines(said, "N152SP"));
    }

    [Fact]
    public void StandCallPushedByTheControllerBeforeItsCall_CallsAfterThePushInstead()
    {
        if (_zoa is null)
        {
            return;
        }

        (SimulationEngine engine, List<TerminalEntry> said) = Load(ParkedAtOak);
        Assert.Equal(InitialCallupPlan.StandCall, engine.FindAircraft("N152SP")!.Ground.InitialCallup);

        // The controller pushes it before the 5 s first-call delay: the crew reports ready to taxi after engine start, so
        // the stand call becomes the after-push call, and it makes no call from the stand or while pushing.
        Tick(engine, 1);
        CommandResult push = engine.SendCommand("N152SP", "PUSH");
        Assert.True(push.Success, push.Message);
        Assert.Equal(InitialCallupPlan.AfterPush, engine.FindAircraft("N152SP")!.Ground.InitialCallup);

        Assert.True(TickUntilPhase<HoldingAfterPushbackPhase>(engine, "N152SP", 300), "the push should complete");

        Assert.Equal(InitialCallupPlan.AfterPush, engine.FindAircraft("N152SP")!.Ground.InitialCallup);
        Assert.Empty(ReadyToTaxiLines(said, "N152SP"));

        // The piston's post-push setup delay is 30 s; the call names the stand the push left.
        Tick(engine, 32);
        Assert.Contains(", pushed back from parking SIG1, ", Assert.Single(ReadyToTaxiLines(said, "N152SP")).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void StandCallTowedByTheControllerWithPushmBeforeItsCall_CallsAfterThePushInstead()
    {
        if (SfoGroundHarness.Build(_output, autoCross: false) is not { } ground)
        {
            return;
        }

        AircraftState ac = SfoGroundHarness.SpawnParked(ground, "UAL790", "B738", "C9");
        // Armed as the loader arms a stand spawn: the harness started its AtParkingPhase on the default plan, which marked
        // the decision processed.
        ac.Ground.InitialCallup = InitialCallupPlan.StandCall;
        ac.Ground.InitialCallupDecisionProcessed = false;

        CommandResult move = ground.Engine.SendCommand(ac.Callsign, "PUSHM $5A $5B");

        Assert.True(move.Success, move.Message);
        Assert.Equal(InitialCallupPlan.AfterPush, ac.Ground.InitialCallup);
    }

    private (SimulationEngine Engine, List<TerminalEntry> Said) Load(string scenarioJson)
    {
        var engine = new SimulationEngine(new TestAirportGroundData());
        List<string> warnings = engine.LoadScenario(scenarioJson, 42, MagneticDeclination.EvaluationDateUtc);
        Assert.DoesNotContain(warnings, w => w.Contains("error", StringComparison.OrdinalIgnoreCase));
        SimScenarioState scenario = engine.Scenario!;
        scenario.ArtccConfig = _zoa;
        scenario.SetAiStaffedPositions([TestAiPositions.OakGround(_zoa!)]);
        var said = new List<TerminalEntry>();
        engine.TerminalEntryEmitted += entry => said.Add(entry);
        return (engine, said);
    }

    private static bool TickUntilAtStand(SimulationEngine engine, string callsign, string stand, int maxSeconds)
    {
        for (int i = 0; i < maxSeconds; i++)
        {
            engine.TickOneSecond();
            AircraftState? ac = engine.FindAircraft(callsign);
            if (ac?.Phases?.CurrentPhase is AtParkingPhase && string.Equals(ac.Ground.ParkingSpot, stand, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TickUntilPhase<TPhase>(SimulationEngine engine, string callsign, int maxSeconds)
        where TPhase : Phase
    {
        for (int i = 0; i < maxSeconds; i++)
        {
            engine.TickOneSecond();
            if (engine.FindAircraft(callsign)?.Phases?.CurrentPhase is TPhase)
            {
                return true;
            }
        }

        return false;
    }

    private static List<TerminalEntry> ReadyToTaxiLines(List<TerminalEntry> said, string callsign) =>
        [
            .. said.Where(e =>
                string.Equals(e.Callsign, callsign, StringComparison.OrdinalIgnoreCase)
                && e.Message.Contains("ready to taxi", StringComparison.OrdinalIgnoreCase)
            ),
        ];

    private static void Tick(SimulationEngine engine, int seconds)
    {
        for (int i = 0; i < seconds; i++)
        {
            engine.TickOneSecond();
        }
    }
}
