using Xunit;
using Yaat.Sim;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Tests;

public class HoldForReleaseSnapshotTests
{
    [Fact]
    public void GroundOps_HoldForReleaseFields_RoundTrip()
    {
        var ground = new AircraftGroundOps
        {
            HeldForRelease = true,
            ReleasedForDeparture = true,
            ReleasedAtSeconds = 123.5,
        };

        var restored = AircraftGroundOps.FromSnapshot(ground.ToSnapshot(), layout: null);

        Assert.True(restored.HeldForRelease);
        Assert.True(restored.ReleasedForDeparture);
        Assert.Equal(123.5, restored.ReleasedAtSeconds);
    }

    [Fact]
    public void GroundOps_LeftStudentFrequencyAtSeconds_RoundTrips()
    {
        var stamped = new AircraftGroundOps { LeftStudentFrequencyAtSeconds = 87.0 };
        var unstamped = new AircraftGroundOps();

        Assert.Equal(87.0, AircraftGroundOps.FromSnapshot(stamped.ToSnapshot(), layout: null).LeftStudentFrequencyAtSeconds);
        Assert.Null(AircraftGroundOps.FromSnapshot(unstamped.ToSnapshot(), layout: null).LeftStudentFrequencyAtSeconds);
    }

    [Fact]
    public void ScenarioState_HoldForReleaseState_Serializes()
    {
        var scenario = new SimScenarioState
        {
            ScenarioId = "s",
            ScenarioName = "s",
            RngSeed = 0,
            OriginalScenarioJson = "{}",
        };
        scenario.HeldDepartureAirports.Add("SJC");
        scenario.ReleaseQueue.Add(
            new ScheduledRelease
            {
                Airport = "SJC",
                Callsign = "N1",
                FireAtSeconds = 120,
            }
        );
        var spawn = new LoadedAircraft
        {
            State = new AircraftState { Callsign = "N5", AircraftType = "B738" },
        };
        scenario.DelayedQueue.Add(
            new DelayedSpawn
            {
                Aircraft = spawn,
                SpawnAtSeconds = 100,
                HeldForRelease = true,
            }
        );

        ScenarioSnapshotDto dto = scenario.ToSnapshot();

        Assert.NotNull(dto.HeldDepartureAirports);
        Assert.Contains("SJC", dto.HeldDepartureAirports!);
        Assert.NotNull(dto.ReleaseQueue);
        Assert.Single(dto.ReleaseQueue!);
        Assert.Equal("N1", dto.ReleaseQueue![0].Callsign);
        Assert.Equal(120, dto.ReleaseQueue![0].FireAtSeconds);
        Assert.NotNull(dto.DelayedQueue);
        Assert.True(dto.DelayedQueue![0].HeldForRelease);
    }
}
