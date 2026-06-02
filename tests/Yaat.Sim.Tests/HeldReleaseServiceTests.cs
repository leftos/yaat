using Xunit;
using Yaat.Sim;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests;

public class HeldReleaseServiceTests
{
    private static SimScenarioState NewScenario(double elapsed = 0) =>
        new()
        {
            ScenarioId = "test",
            ScenarioName = "test",
            RngSeed = 0,
            OriginalScenarioJson = "{}",
            ElapsedSeconds = elapsed,
        };

    private static AircraftState ParkedDeparture(string callsign, string departure, bool vfr = false, double spawnedAt = 0)
    {
        var phases = new PhaseList();
        phases.Add(new AtParkingPhase());
        return new AircraftState
        {
            Callsign = callsign,
            AircraftType = "B738",
            IsOnGround = true,
            SpawnedAtSeconds = spawnedAt,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = departure,
                Destination = "KLAX",
                FlightRules = vfr ? "VFR" : "IFR",
            },
            Phases = phases,
        };
    }

    private static LoadedAircraft RunwaySpawn(string callsign, string departure)
    {
        var phases = new PhaseList();
        phases.Add(new LinedUpAndWaitingPhase());
        var state = new AircraftState
        {
            Callsign = callsign,
            AircraftType = "B738",
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = departure,
                Destination = "KLAX",
                FlightRules = "IFR",
            },
            Phases = phases,
        };
        return new LoadedAircraft { State = state };
    }

    [Fact]
    public void Arm_HoldsOnGroundIfrDeparture()
    {
        var scenario = NewScenario();
        var world = new SimulationWorld();
        var ac = ParkedDeparture("N1", "KSJC");
        world.AddAircraft(ac);

        var result = HeldReleaseService.Arm(scenario, world, "SJC");

        Assert.True(result.Success);
        // FAA/ICAO prefix tolerance: armed "SJC" matches flight-plan "KSJC".
        Assert.True(HeldReleaseService.IsAirportArmed(scenario, "KSJC"));
        Assert.True(ac.Ground.HeldForRelease);
    }

    [Fact]
    public void Arm_DoesNotHoldVfrDeparture()
    {
        var scenario = NewScenario();
        var world = new SimulationWorld();
        var ac = ParkedDeparture("N2", "KSJC", vfr: true);
        world.AddAircraft(ac);

        HeldReleaseService.Arm(scenario, world, "SJC");

        Assert.False(ac.Ground.HeldForRelease);
    }

    [Fact]
    public void Arm_DoesNotHoldDepartureFromOtherAirport()
    {
        var scenario = NewScenario();
        var world = new SimulationWorld();
        var ac = ParkedDeparture("N9", "KPAO");
        world.AddAircraft(ac);

        HeldReleaseService.Arm(scenario, world, "SJC");

        Assert.False(ac.Ground.HeldForRelease);
    }

    [Fact]
    public void Release_ClearsGroundHold_AndMarksReleased()
    {
        var scenario = NewScenario(elapsed: 50);
        var world = new SimulationWorld();
        var ac = ParkedDeparture("N3", "KSJC");
        world.AddAircraft(ac);
        HeldReleaseService.Arm(scenario, world, "SJC");

        var result = HeldReleaseService.Release(scenario, world, new SerializableRandom(0), "N3", null);

        Assert.True(result.Success);
        Assert.False(ac.Ground.HeldForRelease);
        Assert.True(ac.Ground.ReleasedForDeparture);
        Assert.Equal(50, ac.Ground.ReleasedAtSeconds);
    }

    [Fact]
    public void Release_ByAirport_ReleasesNextPending()
    {
        var scenario = NewScenario(elapsed: 10);
        var world = new SimulationWorld();
        var first = ParkedDeparture("N1", "KSJC", spawnedAt: 1);
        var second = ParkedDeparture("N2", "KSJC", spawnedAt: 2);
        world.AddAircraft(first);
        world.AddAircraft(second);
        HeldReleaseService.Arm(scenario, world, "SJC");

        HeldReleaseService.Release(scenario, world, new SerializableRandom(0), "SJC", null);

        // The earlier-spawned departure releases first.
        Assert.True(first.Ground.ReleasedForDeparture);
        Assert.False(second.Ground.ReleasedForDeparture);
        Assert.True(second.Ground.HeldForRelease);
    }

    [Fact]
    public void Disarm_AutoReleasesHeldGroundDepartures()
    {
        var scenario = NewScenario();
        var world = new SimulationWorld();
        var ac = ParkedDeparture("N4", "KSJC");
        world.AddAircraft(ac);
        HeldReleaseService.Arm(scenario, world, "SJC");

        var result = HeldReleaseService.Disarm(scenario, world, "SJC");

        Assert.True(result.Success);
        Assert.False(HeldReleaseService.IsAirportArmed(scenario, "SJC"));
        Assert.False(ac.Ground.HeldForRelease);
        Assert.True(ac.Ground.ReleasedForDeparture);
    }

    [Fact]
    public void HeldRunwaySpawn_IsHeld_AndAppearsInRundown()
    {
        var scenario = NewScenario(elapsed: 200);
        var world = new SimulationWorld();
        var spawn = RunwaySpawn("N5", "KSJC");
        scenario.DelayedQueue.Add(
            new DelayedSpawn
            {
                Aircraft = spawn,
                SpawnAtSeconds = 100,
                HeldForRelease = DepartureSpawnClassifier.IsHeldSpawnCandidate(spawn),
            }
        );
        HeldReleaseService.Arm(scenario, world, "SJC");

        Assert.True(HeldReleaseService.IsSpawnHeld(scenario, scenario.DelayedQueue[0]));
        var rundown = HeldReleaseService.BuildRundown(scenario, world);
        Assert.Contains(rundown, h => h.Callsign == "N5" && !h.IsGroundDeparture);
    }

    [Fact]
    public void Release_HeldRunwaySpawn_ReschedulesSpawnWithAirborneJitter()
    {
        var scenario = NewScenario(elapsed: 200);
        var world = new SimulationWorld();
        var spawn = RunwaySpawn("N5", "KSJC");
        scenario.DelayedQueue.Add(
            new DelayedSpawn
            {
                Aircraft = spawn,
                SpawnAtSeconds = 100,
                HeldForRelease = true,
            }
        );
        HeldReleaseService.Arm(scenario, world, "SJC");

        var result = HeldReleaseService.Release(scenario, world, new SerializableRandom(0), "N5", null);

        Assert.True(result.Success);
        var entry = scenario.DelayedQueue.Single();
        Assert.False(entry.HeldForRelease);
        Assert.InRange(
            entry.SpawnAtSeconds,
            (int)(200 + HeldReleaseService.MinSpawnReleaseDelaySeconds),
            (int)(200 + HeldReleaseService.MaxSpawnReleaseDelaySeconds)
        );
    }

    [Fact]
    public void Release_WholeQueueWithInterval_EnqueuesSpacedReleases()
    {
        var scenario = NewScenario(elapsed: 0);
        var world = new SimulationWorld();
        world.AddAircraft(ParkedDeparture("N1", "KSJC", spawnedAt: 1));
        world.AddAircraft(ParkedDeparture("N2", "KSJC", spawnedAt: 2));
        world.AddAircraft(ParkedDeparture("N3", "KSJC", spawnedAt: 3));
        HeldReleaseService.Arm(scenario, world, "SJC");

        var result = HeldReleaseService.Release(scenario, world, new SerializableRandom(0), "SJC", 120);

        Assert.True(result.Success);
        Assert.Equal(3, scenario.ReleaseQueue.Count);
        var fireTimes = scenario.ReleaseQueue.OrderBy(r => r.FireAtSeconds).Select(r => r.FireAtSeconds).ToList();
        Assert.Equal([0.0, 120.0, 240.0], fireTimes);
    }

    [Fact]
    public void Release_UnknownAirport_Fails()
    {
        var scenario = NewScenario();
        var world = new SimulationWorld();

        var result = HeldReleaseService.Release(scenario, world, new SerializableRandom(0), "SJC", null);

        Assert.False(result.Success);
    }
}
