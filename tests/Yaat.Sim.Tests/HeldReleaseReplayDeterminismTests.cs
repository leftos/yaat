using Xunit;
using Yaat.Sim;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests;

/// <summary>
/// Replaying a <c>REL</c> of a held runway/airborne departure must reproduce the airborne spawn
/// jitter baked into the recorded command without consuming <see cref="SimulationWorld.Rng"/>.
/// Re-sampling on replay (the original <c>ReleaseDeparture</c> arm) drew from the shared RNG at a
/// different stream position than the live draw, diverging every downstream consumer (arrival
/// generators, go-around rolls) from the REL onward.
/// </summary>
public class HeldReleaseReplayDeterminismTests
{
    private static SimScenarioState NewScenario(double elapsed) =>
        new()
        {
            ScenarioId = "test",
            ScenarioName = "test",
            RngSeed = 0,
            OriginalScenarioJson = "{}",
            ElapsedSeconds = elapsed,
        };

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
    public void ReplayRelease_HeldRunwaySpawn_UsesBakedJitter_AndConsumesNoRng()
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

        var before = world.Rng.GetState();

        var result = HeldReleaseService.ReplayRelease(scenario, world, "N5", intervalSeconds: null, bakedJitterSeconds: 42);

        Assert.True(result.Success);
        // Replay reproduces the recorded spawn time exactly — no jitter re-sampling.
        var entry = scenario.DelayedQueue.Single();
        Assert.False(entry.HeldForRelease);
        Assert.Equal(242, entry.SpawnAtSeconds);
        // The shared RNG stream is untouched, so every downstream consumer stays aligned.
        Assert.Equal(before, world.Rng.GetState());
    }

    [Fact]
    public void ReplayRelease_NullBakedJitter_FallsBackDeterministically_NoRng()
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

        var before = world.Rng.GetState();

        // A legacy recording with no baked jitter must still replay without touching the RNG.
        var result = HeldReleaseService.ReplayRelease(scenario, world, "N5", intervalSeconds: null, bakedJitterSeconds: null);

        Assert.True(result.Success);
        var entry = scenario.DelayedQueue.Single();
        Assert.Equal((int)(200 + HeldReleaseService.MinSpawnReleaseDelaySeconds), entry.SpawnAtSeconds);
        Assert.Equal(before, world.Rng.GetState());
    }
}
