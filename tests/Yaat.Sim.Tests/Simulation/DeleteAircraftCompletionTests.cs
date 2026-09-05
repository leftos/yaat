using Xunit;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;
using Yaat.Sim.Training;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// <see cref="SimulationEngine.DeleteAircraft"/> — the sim-side half of a <c>DEL</c>. A deleted aircraft that never
/// landed, handed off, or transited must still leave a debrief row (stamped <see cref="CompletionReason.Dropped"/>)
/// instead of vanishing from <see cref="SimulationWorld.GetCompletedAircraft"/>; a prior stamp is preserved.
/// </summary>
public class DeleteAircraftCompletionTests
{
    [Fact]
    public void DeleteAircraft_ActiveAircraft_IsStampedDropped_AndRecorded()
    {
        var engine = Engine();
        engine.Scenario!.ElapsedSeconds = 600;
        var ac = Aircraft("N1");
        ac.SpawnedAtSeconds = 100;
        engine.World.AddAircraft(ac);

        engine.DeleteAircraft("N1");

        Assert.Null(engine.World.FindAircraft("N1"));
        Assert.Equal(CompletionReason.Dropped, ac.CompletionReason);
        Assert.Equal(600, ac.CompletedAtSeconds);
        var record = Assert.Single(engine.World.GetCompletedAircraft());
        Assert.Equal("N1", record.Callsign);
        Assert.Equal(CompletionReason.Dropped, record.Reason);
        Assert.Equal(600, record.CompletedAtSeconds);
        Assert.Equal("DEL", record.Detail);
    }

    [Fact]
    public void DeleteAircraft_AlreadyLanded_KeepsLandedStamp()
    {
        var engine = Engine();
        engine.Scenario!.ElapsedSeconds = 600;
        var ac = Aircraft("N1");
        ac.CompletionReason = CompletionReason.Landed;
        ac.CompletedAtSeconds = 450;
        ac.CompletionDetail = "28R";
        engine.World.AddAircraft(ac);

        engine.DeleteAircraft("N1");

        var record = Assert.Single(engine.World.GetCompletedAircraft());
        Assert.Equal(CompletionReason.Landed, record.Reason);
        Assert.Equal(450, record.CompletedAtSeconds);
        Assert.Equal("28R", record.Detail);
    }

    [Fact]
    public void DeleteAircraft_OnlyInDelayedQueue_ClearsQueue_NoRecord()
    {
        var engine = Engine();
        engine.Scenario!.DelayedQueue.Add(
            new DelayedSpawn
            {
                Aircraft = new LoadedAircraft { State = Aircraft("N2") },
                SpawnAtSeconds = 60,
            }
        );

        engine.DeleteAircraft("N2");

        Assert.Empty(engine.Scenario.DelayedQueue);
        Assert.Empty(engine.World.GetCompletedAircraft());
    }

    [Fact]
    public void ReplayedDelete_StampsDropped()
    {
        var engine = Engine();
        engine.Scenario!.ElapsedSeconds = 600;
        var ac = Aircraft("N1");
        engine.World.AddAircraft(ac);

        engine.Actions.Apply(new RecordedCommand(600, "N1", "DEL", "XX", "conn"));

        Assert.Null(engine.World.FindAircraft("N1"));
        var record = Assert.Single(engine.World.GetCompletedAircraft());
        Assert.Equal(CompletionReason.Dropped, record.Reason);
        Assert.Equal("DEL", record.Detail);
    }

    private static AircraftState Aircraft(string callsign) =>
        new()
        {
            Callsign = callsign,
            AircraftType = "C172",
            Position = new LatLon(37.72, -122.22),
            FlightPlan = new AircraftFlightPlan(),
        };

    private static SimulationEngine Engine() =>
        new(new TestAirportGroundData())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "test",
                ScenarioName = "Test",
                RngSeed = 1,
                OriginalScenarioJson = "{}",
            },
        };
}
