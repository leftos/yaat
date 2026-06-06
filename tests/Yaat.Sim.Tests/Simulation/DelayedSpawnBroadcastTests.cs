using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests.Simulation;

public class DelayedSpawnBroadcastTests
{
    private static SimulationEngine BuildEngine()
    {
        return new SimulationEngine(new NullGroundData());
    }

    private static void SetupScenarioWithDelayedSpawns(SimulationEngine engine, params int[] spawnAtSeconds)
    {
        engine.Scenario = new SimScenarioState
        {
            ScenarioId = "test",
            ScenarioName = "test",
            RngSeed = 42,
            OriginalScenarioJson = "{}",
        };

        foreach (int seconds in spawnAtSeconds)
        {
            var aircraft = new LoadedAircraft
            {
                State = new AircraftState
                {
                    Callsign = $"TST{seconds}",
                    AircraftType = "B738",
                    Position = new LatLon(37.72, -122.22),
                    TrueHeading = new TrueHeading(090),
                    Altitude = 5000,
                    IndicatedAirspeed = 250,
                    IsOnGround = false,
                    FlightPlan = new AircraftFlightPlan
                    {
                        Departure = "OAK",
                        CruiseAltitude = 10000,
                        FlightRules = "IFR",
                    },
                },
            };
            engine.Scenario.DelayedQueue.Add(new DelayedSpawn { Aircraft = aircraft, SpawnAtSeconds = seconds });
        }
    }

    [Fact]
    public void EmitsNoDelayedSpawnsLeft_WhenLastDelayedSpawnFires()
    {
        var engine = BuildEngine();
        SetupScenarioWithDelayedSpawns(engine, 5, 10);

        // Advance past first spawn only
        engine.Scenario!.ElapsedSeconds = 5;
        engine.TickPrePhysics();
        var entries = engine.DrainTerminalEntries();
        Assert.DoesNotContain(entries, e => e.Message.Contains("No delayed spawns left"));

        // Advance past second (last) spawn
        engine.Scenario.ElapsedSeconds = 10;
        engine.TickPrePhysics();
        entries = engine.DrainTerminalEntries();
        Assert.Contains(entries, e => e.Message == "[Scenario] No delayed spawns left");
    }

    [Fact]
    public void DoesNotEmit_WhenNoDelayedSpawnsExist()
    {
        var engine = BuildEngine();
        SetupScenarioWithDelayedSpawns(engine); // no delayed spawns

        engine.Scenario!.ElapsedSeconds = 100;
        engine.TickPrePhysics();
        var entries = engine.DrainTerminalEntries();
        Assert.DoesNotContain(entries, e => e.Message.Contains("No delayed spawns left"));
    }

    private sealed class NullGroundData : IAirportGroundData
    {
        public AirportGroundLayout? GetLayout(string airportId) => null;

        public string? GetSourceGeoJson(string airportId) => null;
    }
}
