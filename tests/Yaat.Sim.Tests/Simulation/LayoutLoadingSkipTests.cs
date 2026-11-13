using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Phases;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// The per-tick readers skip an airport whose layout is loading and cache nothing about it, and the landing clearance
/// attaches its layout at the admission: a generator neither spawns nor spaces against a loading runway's field, an
/// aircraft cleared to land while its airport loads attaches the admitted layout, and a snapshot restore keeps a delayed
/// spawn's deferred airport deferred without reading it from the source.
/// </summary>
public class LayoutLoadingSkipTests
{
    private const string GeneratorScenarioJson = """
        {
          "id": "layout-loading-generator",
          "name": "Layout loading generator",
          "artccId": "ZOA",
          "primaryAirportId": "OAK",
          "aircraft": [],
          "initializationTriggers": [],
          "aircraftGenerators": [
            {
              "id": "gen",
              "runway": "28R",
              "engineType": "Jet",
              "weightCategory": "Large",
              "initialDistance": 15,
              "maxDistance": 50,
              "intervalDistance": 5,
              "startTimeOffset": 0,
              "maxTime": 3600,
              "intervalTime": 600,
              "randomizeInterval": false,
              "randomizeWeightCategory": false
            }
          ]
        }
        """;

    private const string OnFinalAtSmfJson = """
        {
          "id": "layout-loading-ctl",
          "name": "Layout loading landing clearance",
          "artccId": "ZOA",
          "primaryAirportId": "OAK",
          "aircraft": [
            {
              "id": "a1",
              "aircraftId": "N152SP",
              "aircraftType": "C172",
              "transponderMode": "C",
              "airportId": "SMF",
              "startingConditions": { "type": "OnFinal", "runway": "17L", "distanceFromRunway": 4 },
              "flightplan": {
                "rules": "VFR",
                "departure": "KOAK",
                "destination": "KSMF",
                "cruiseAltitude": 1500,
                "cruiseSpeed": 100,
                "route": "",
                "remarks": "",
                "aircraftType": "C172"
              }
            }
          ]
        }
        """;

    public LayoutLoadingSkipTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void ArrivalGenerator_AtALoadingField_NeitherSpawnsNorReadsWhileHeld_AndSpawnsAfterTheAdmission()
    {
        var data = new PartlyLoadingGroundData("SMF");
        SimulationEngine engine = Load(data, GeneratorScenarioJson);
        SimScenarioState scenario = engine.Scenario!;
        GeneratorState gen = MoveGeneratorToSmf17L(engine, null);
        double due = gen.NextSpawnSeconds;

        engine.TickOneSecond();
        Assert.Contains("SMF", scenario.DeferredLayoutAirports);
        int readsWhenDeferred = data.ReadCount("SMF");
        for (int i = 0; i < 3; i++)
        {
            engine.TickOneSecond();
        }

        Assert.Empty(engine.GeneratorSpawnLog);
        Assert.Empty(engine.World.GetSnapshot());
        Assert.Equal(due, gen.NextSpawnSeconds);
        // The only reads while held are the admission's poll, one each second: the generator reads nothing from the source.
        Assert.Equal(readsWhenDeferred + 3, data.ReadCount("SMF"));

        data.Release("SMF");
        engine.TickOneSecond();
        Assert.DoesNotContain("SMF", scenario.DeferredLayoutAirports);
        GeneratorSpawnRecord spawn = Assert.Single(engine.GeneratorSpawnLog);
        AircraftState spawned = Assert.Single(engine.World.GetSnapshot());

        // No random draw was made while held: a control engine on the same seed whose generator first falls due on the
        // spawn's second, never held, draws the same arrival.
        SimulationEngine control = Load(new PartlyLoadingGroundData(), GeneratorScenarioJson);
        MoveGeneratorToSmf17L(control, spawn.ElapsedSeconds);
        while (control.Scenario!.ElapsedSeconds < engine.Scenario!.ElapsedSeconds)
        {
            control.TickOneSecond();
        }

        AircraftState drawn = Assert.Single(control.World.GetSnapshot());
        Assert.Equal(drawn.Callsign, spawned.Callsign);
        Assert.Equal(drawn.AircraftType, spawned.AircraftType);
    }

    [Fact]
    public void SameRunwayProtection_AtALoadingField_StampsNoCeiling()
    {
        var data = new PartlyLoadingGroundData("SMF");
        (SimulationEngine engine, AircraftState follower) = SmfFinalPair(data);

        // The first read through the gate defers SMF; every pass after it finds the field loading.
        Assert.Null(engine.ResolveAirportLayout("SMF"));
        for (int i = 0; i < 3; i++)
        {
            engine.TickPrePhysics();
        }

        Assert.Null(follower.Approach.SameRunwayProtectionCeilingKts);
        Assert.Null(follower.Targets.SpeedCeiling);

        // The same pair once SMF is admitted is protected, so the nothing above is the loading field's doing.
        data.Release("SMF");
        engine.ApplyRecordedLayoutAdmitted(new RecordedLayoutAdmitted(engine.Scenario!.ElapsedSeconds, "SMF"));
        engine.TickPrePhysics();
        Assert.NotNull(follower.Approach.SameRunwayProtectionCeilingKts);
    }

    [Fact]
    public void SameRunwayProtection_OwnedWhenTheFieldDefers_IsNeitherHeldNorReleased()
    {
        (SimulationEngine engine, AircraftState follower) = SmfFinalPair(new PartlyLoadingGroundData());
        engine.TickPrePhysics();
        double? ceiling = follower.Approach.SameRunwayProtectionCeilingKts;
        Assert.NotNull(ceiling);
        double dropout = follower.Approach.SameRunwayProtectionDropoutSeconds;

        // Out of the conflict while SMF is deferred: loaded, the pass would release the ceiling here.
        engine.ApplyRecordedLayoutDeferred(new RecordedLayoutDeferred(engine.Scenario!.ElapsedSeconds, "SMF"));
        follower.Position = GeoMath.ProjectPoint(follower.Position, follower.TrueHeading.ToReciprocal(), 34.0);
        for (int i = 0; i < 3; i++)
        {
            engine.TickPrePhysics();
        }

        Assert.Equal(ceiling, follower.Approach.SameRunwayProtectionCeilingKts);
        Assert.Equal(ceiling, follower.Targets.SpeedCeiling);
        Assert.Equal(dropout, follower.Approach.SameRunwayProtectionDropoutSeconds);

        engine.ApplyRecordedLayoutAdmitted(new RecordedLayoutAdmitted(engine.Scenario.ElapsedSeconds, "SMF"));
        engine.TickPrePhysics();
        Assert.Null(follower.Approach.SameRunwayProtectionCeilingKts);
    }

    // A turboprop leader 8 nm and a faster jet follower 11 nm out on the SMF 17L final, the protection pass switched on and
    // the generators off, as SameRunwayArrivalProtectionEngineTests sets up its OAK 30 pair.
    private static (SimulationEngine Engine, AircraftState Follower) SmfFinalPair(PartlyLoadingGroundData data)
    {
        SimulationEngine engine = Load(data, GeneratorScenarioJson);
        engine.Scenario!.SoloArrivalGeneratorRatePercent = 0;
        engine.Scenario.AutoArrivalSpacingOnOccupiedRunway = true;
        RunwayInfo runway = NavigationDatabase.Instance.GetRunway("SMF", "17L")!;
        Arrival(engine, runway, "MANUAL1", "DH8D", 8.0);
        return (engine, Arrival(engine, runway, "MANUAL3", "B739", 11.0));
    }

    private static AircraftState Arrival(SimulationEngine engine, RunwayInfo runway, string callsign, string type, double distanceNm)
    {
        AircraftCategory category = AircraftCategorization.Categorize(type);
        PhaseInitResult init = AircraftInitializer.InitializeOnFinal(runway, category, callsign, requestedDistanceNm: distanceNm, aircraftType: type);
        var aircraft = new AircraftState
        {
            Callsign = callsign,
            AircraftType = type,
            Position = init.Position,
            TrueHeading = init.TrueHeading,
            Altitude = init.Altitude,
            IndicatedAirspeed = init.Speed,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan { Destination = "KSMF" },
            Phases = init.Phases,
        };
        engine.World.AddAircraft(aircraft);
        return aircraft;
    }

    // Replaces the scenario's OAK generator with the same config on SMF 17L, due at nextSpawnSeconds (else as loaded).
    private static GeneratorState MoveGeneratorToSmf17L(SimulationEngine engine, double? nextSpawnSeconds)
    {
        SimScenarioState scenario = engine.Scenario!;
        GeneratorState oak = Assert.Single(scenario.Generators);
        var gen = new GeneratorState
        {
            Config = oak.Config,
            Runway = NavigationDatabase.Instance.GetRunway("SMF", "17L")!,
            NextSpawnSeconds = nextSpawnSeconds ?? oak.NextSpawnSeconds,
        };
        scenario.Generators.Clear();
        scenario.Generators.Add(gen);
        return gen;
    }

    [Fact]
    public void ClearedToLand_WhileItsAirportLoads_HasNoLayout_AndAttachesTheAdmittedOneAtTheAdmission()
    {
        var data = new PartlyLoadingGroundData("SMF");
        SimulationEngine engine = Load(data, OnFinalAtSmfJson);
        AircraftState aircraft = engine.World.FindAircraft("N152SP")!;
        aircraft.Ground.Layout = null;
        aircraft.Ground.LayoutAirportId = null;

        CommandResult cleared = engine.SendCommand("N152SP", "CLAND");

        Assert.True(cleared.Success, cleared.Message);
        Assert.Null(aircraft.Ground.Layout);
        Assert.Equal(["SMF"], aircraft.Ground.PendingLayoutAirportIds);

        data.Release("SMF");
        engine.TickOneSecond();
        AirportGroundLayout? smf = engine.ResolveAirportLayout("SMF");
        Assert.NotNull(smf);
        Assert.Same(smf, aircraft.Ground.Layout);
        Assert.Null(aircraft.Ground.PendingLayoutAirportIds);
    }

    [Fact]
    public void Snapshot_WithADelayedSpawnAtADeferredAirport_RestoresItDeferred_WithoutReadingTheSource()
    {
        var data = new PartlyLoadingGroundData("SMF");
        SimulationEngine live = Load(data, GeneratorScenarioJson);
        live.TickOneSecond();

        // SMF is deferred by this read: the first read of it through the gate finds it loading.
        Assert.Null(live.ResolveAirportLayout("SMF"));
        Assert.Contains("SMF", live.Scenario!.DeferredLayoutAirports);
        var state = new AircraftState
        {
            Callsign = "N3DQ",
            AircraftType = "C172",
            Position = new LatLon(38.6, -121.5),
            Altitude = 3000,
            FlightPlan = new AircraftFlightPlan { Departure = "KSMF", Destination = "KSMF" },
        };
        state.Ground.LayoutAirportId = "SMF";
        live.Scenario.DelayedQueue.Add(
            new DelayedSpawn
            {
                Aircraft = new LoadedAircraft { State = state },
                SpawnAtSeconds = 3600,
            }
        );
        StateSnapshotDto snapshot = live.CaptureSnapshot();

        // The restore's source holds SMF already, as a room's full pin does: SMF still comes back deferred and unread.
        var pinned = new PartlyLoadingGroundData();
        SimulationEngine restored = Load(pinned, GeneratorScenarioJson);
        int readsBefore = pinned.ReadCount("SMF");
        restored.RestoreFromSnapshot(snapshot);

        Assert.Equal(readsBefore, pinned.ReadCount("SMF"));
        Assert.Contains("SMF", restored.Scenario!.DeferredLayoutAirports);
        DelayedSpawn delayed = Assert.Single(restored.Scenario.DelayedQueue);
        Assert.Null(delayed.Aircraft.State.Ground.Layout);
        Assert.Equal("SMF", delayed.Aircraft.State.Ground.LayoutAirportId);
    }

    private static SimulationEngine Load(PartlyLoadingGroundData data, string scenarioJson)
    {
        var engine = new SimulationEngine(data, PushPlanStore.CreateShipped());
        engine.LoadScenario(scenarioJson, 42, MagneticDeclination.EvaluationDateUtc);
        return engine;
    }
}
