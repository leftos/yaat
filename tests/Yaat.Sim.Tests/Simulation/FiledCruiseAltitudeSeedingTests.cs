using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.LiveTraffic;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Every way an aircraft enters the world takes its filed altitude as the pilot's (<see cref="AircraftState.FiledCruiseAltitudeFt"/>)
/// the moment it appears: equal to <see cref="AircraftFlightPlan.Altitude"/>'s value, or null when the plan files none. One
/// fact per spawn route, on real navdata and the KOAK layout.
/// </summary>
public class FiledCruiseAltitudeSeedingTests(ITestOutputHelper output)
{
    private const string ScenarioCallsign = "UAL1402";

    /// <summary>A scenario aircraft spawned at load, whose preset commands the load dispatches: the filed 35,000.</summary>
    [Fact]
    public void ScenarioAircraft_AtLoad_TakesTheFiledAltitude()
    {
        if (Engine(ScenarioAircraftJson(spawnDelay: 0)) is not { } engine)
        {
            return;
        }

        AircraftState ac = Assert.IsType<AircraftState>(engine.FindAircraft(ScenarioCallsign));
        Assert.Equal(35000, ac.FiledCruiseAltitudeFt);
        AssertSeeded(ac);
    }

    /// <summary>A scenario aircraft held back by a spawn delay takes the filed 35,000 when its spawn is released.</summary>
    [Fact]
    public void DelayedSpawn_OnRelease_TakesTheFiledAltitude()
    {
        if (Engine(ScenarioAircraftJson(spawnDelay: 5)) is not { } engine)
        {
            return;
        }

        Assert.Null(engine.FindAircraft(ScenarioCallsign));
        AircraftState ac = TickUntilSpawned(engine, () => engine.FindAircraft(ScenarioCallsign), 30);
        Assert.Equal(35000, ac.FiledCruiseAltitudeFt);
        AssertSeeded(ac);
    }

    /// <summary>An IFR <c>ADD</c> at a fix with an altitude files no plan altitude (the altitude is where it flies): the pilot has none.</summary>
    [Fact]
    public void AddIfrAtAFixWithAnAltitude_FilesNoPlanAltitude_SoTakesNone()
    {
        if (Engine(EmptyScenarioJson) is not { } engine)
        {
            return;
        }

        CommandResult added = engine.SendCommand("", "ADD IFR L J @SUNOL 8000");
        Assert.True(added.Success, added.Message);
        AircraftState ac = Assert.Single(engine.World.GetSnapshot());
        output.WriteLine($"{ac.Callsign} plan altitude {ac.FlightPlan.Altitude.CruiseFeet}, filed {ac.FiledCruiseAltitudeFt}");
        Assert.Null(ac.FiledCruiseAltitudeFt);
        AssertSeeded(ac);
    }

    /// <summary>An arrival from a scenario aircraft generator (<c>AircraftGenerator</c>) takes its plan's altitude at spawn.</summary>
    [Fact]
    public void ArrivalGenerator_Spawn_TakesThePlansAltitude()
    {
        if (Engine(ArrivalGeneratorScenarioJson) is not { } engine)
        {
            return;
        }

        AircraftState ac = TickUntilSpawned(engine, () => engine.World.GetSnapshot().FirstOrDefault(), 120);
        output.WriteLine($"{ac.Callsign} plan altitude {ac.FlightPlan.Altitude.CruiseFeet}, filed {ac.FiledCruiseAltitudeFt}");
        AssertSeeded(ac);
    }

    /// <summary>A VFR arrival generator's aircraft files VFR at its spawn altitude and takes it.</summary>
    [Fact]
    public void VfrArrivalGenerator_Spawn_TakesTheFiledVfrAltitude()
    {
        if (Engine(VfrArrivalScenarioJson) is not { } engine)
        {
            return;
        }

        AircraftState ac = TickUntilSpawned(engine, () => engine.World.GetSnapshot().FirstOrDefault(), 120);
        output.WriteLine($"{ac.Callsign} plan altitude {ac.FlightPlan.Altitude.CruiseFeet}, filed {ac.FiledCruiseAltitudeFt}");
        Assert.NotNull(ac.FiledCruiseAltitudeFt);
        AssertSeeded(ac);
    }

    /// <summary>A live-traffic shadow assumed into the sim takes the feed plan's altitude at the assume.</summary>
    [Fact]
    public void LiveTrafficAssume_TakesTheFeedPlansAltitude()
    {
        TestVnasData.EnsureInitialized();
        var sample = new LiveTrafficSample(0, 37.9, -121.3, 11_000, 300, 90, 0, LiveTrafficSource.Stars, 4521);
        var plan = new AircraftFlightPlan
        {
            HasFlightPlan = true,
            FlightRules = "IFR",
            Departure = "KSFO",
            Destination = "KSMF",
            Altitude = PlannedAltitude.Ifr(23000),
        };
        AircraftState ac = LiveTrafficKinematics.CreateShadow("UAL123", "B738", sample, plan);
        DispatchContext ctx = TestDispatch.Context(new Random(1), findAircraft: cs => cs == ac.Callsign ? ac : null, listAircraft: () => [ac]);

        CommandResult assumed = CommandDispatcher.Dispatch(new AssumeCommand(), ac, ctx);

        Assert.True(assumed.Success, assumed.Message);
        Assert.Equal(23000, ac.FiledCruiseAltitudeFt);
        AssertSeeded(ac);
    }

    /// <summary>
    /// The altitude a live-traffic shadow took at spawn survives the assume: a controller's CRUISE amendment to the
    /// shadow's plan before the assume is data only and never changes the climb.
    /// </summary>
    [Fact]
    public void LiveTrafficAssume_KeepsTheSpawnAltitude_NotAControllersAmendment()
    {
        TestVnasData.EnsureInitialized();
        var sample = new LiveTrafficSample(0, 37.9, -121.3, 11_000, 300, 90, 0, LiveTrafficSource.Stars, 4521);
        var plan = new AircraftFlightPlan
        {
            HasFlightPlan = true,
            FlightRules = "IFR",
            Departure = "KSFO",
            Destination = "KSMF",
            Altitude = PlannedAltitude.Ifr(23000),
        };
        AircraftState ac = LiveTrafficKinematics.CreateShadow("UAL123", "B738", sample, plan);
        ac.ClearToFiledAltitude();
        DispatchContext ctx = TestDispatch.Context(new Random(1), findAircraft: cs => cs == ac.Callsign ? ac : null, listAircraft: () => [ac]);

        CommandResult cruised = TrackEngine.HandleCruise(ac, 170);
        Assert.True(cruised.Success, cruised.Message);

        CommandResult assumed = CommandDispatcher.Dispatch(new AssumeCommand(), ac, ctx);

        Assert.True(assumed.Success, assumed.Message);
        Assert.Equal(23000, ac.FiledCruiseAltitudeFt);
    }

    private static void AssertSeeded(AircraftState ac) => Assert.Equal(ac.FlightPlan.Altitude.CruiseFeet, ac.FiledCruiseAltitudeFt);

    private static AircraftState TickUntilSpawned(SimulationEngine engine, Func<AircraftState?> spawned, int budgetSeconds)
    {
        for (int t = 0; (t < budgetSeconds) && (spawned() is null); t++)
        {
            engine.TickOneSecond();
        }

        return spawned() ?? throw new InvalidOperationException($"nothing spawned within {budgetSeconds} s");
    }

    private SimulationEngine? Engine(string scenarioJson)
    {
        TestVnasData.EnsureInitialized();
        var groundData = new TestAirportGroundData();
        if ((TestVnasData.NavigationDb is null) || (groundData.GetLayout("OAK") is null))
        {
            output.WriteLine("SKIP: navdata or the OAK ground layout is not available");
            return null;
        }

        var engine = new SimulationEngine(groundData, PushPlanStore.CreateShipped());
        foreach (string warning in engine.LoadScenario(scenarioJson, 42, MagneticDeclination.EvaluationDateUtc))
        {
            output.WriteLine($"[WARN] {warning}");
        }

        return engine;
    }

    private static string ScenarioAircraftJson(int spawnDelay) =>
        $$"""
            {
              "id": "filed-altitude-seeding", "name": "Filed altitude seeding", "artccId": "ZOA", "primaryAirportId": "OAK",
              "atc": [], "aircraftGenerators": [],
              "aircraft": [
                {
                  "id": "a1", "aircraftId": "{{ScenarioCallsign}}", "aircraftType": "B738", "transponderMode": "C",
                  "spawnDelay": {{spawnDelay}},
                  "startingConditions": { "type": "Parking", "parking": "S7" },
                  "flightplan": {
                    "rules": "IFR", "departure": "KOAK", "destination": "KLAX", "cruiseAltitude": 35000, "cruiseSpeed": 430,
                    "route": "", "remarks": "", "aircraftType": "B738"
                  },
                  "presetCommands": [ {"id": "p1", "command": "SQ 4421", "timeOffset": 0} ]
                }
              ]
            }
            """;

    private const string EmptyScenarioJson = """
        { "id": "filed-altitude-seeding-empty", "name": "Filed altitude seeding (empty)", "artccId": "ZOA",
          "primaryAirportId": "OAK", "atc": [], "aircraftGenerators": [], "aircraft": [] }
        """;

    private const string ArrivalGeneratorScenarioJson = """
        {
          "id": "filed-altitude-seeding-gen", "name": "Filed altitude seeding (generator)", "artccId": "ZOA",
          "primaryAirportId": "OAK", "aircraft": [], "initializationTriggers": [],
          "aircraftGenerators": [
            {
              "id": "gen-28R", "runway": "28R", "engineType": "Jet", "weightCategory": "Large", "initialDistance": 10,
              "maxDistance": 50, "intervalDistance": 5, "startTimeOffset": 0, "maxTime": 3600, "intervalTime": 300,
              "randomizeInterval": false, "randomizeWeightCategory": false
            }
          ]
        }
        """;

    private const string VfrArrivalScenarioJson = """
        {
          "id": "filed-altitude-seeding-vfr", "name": "Filed altitude seeding (VFR arrival)", "artccId": "ZOA",
          "primaryAirportId": "OAK", "aircraft": [],
          "vfrArrivalGenerators": [
            {
              "id": "vfr-south", "bearingFrom": 120, "bearingTo": 200, "initialDistance": 12, "maxDistance": 20,
              "altitudeMin": 2500, "altitudeMax": 3500, "intervalTime": 60
            }
          ]
        }
        """;
}
