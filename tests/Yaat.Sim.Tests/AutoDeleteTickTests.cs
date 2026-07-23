using Xunit;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests;

/// <summary>
/// <see cref="SimulationEngine.TickAutoDelete"/> — the engine-owned auto-delete pass that decides which
/// aircraft to remove, removes them, and returns the removed states so a host can fan out delete
/// broadcasts. The server calls it in place of running the delete decision + removal itself.
/// </summary>
public class AutoDeleteTickTests
{
    [Fact]
    public void TickAutoDelete_PendingAutoDelete_RemovesAndReturnsState()
    {
        var engine = EngineWithMode(null);
        var ac = Aircraft("N1");
        ac.Ground.PendingAutoDelete = true;
        engine.World.AddAircraft(ac);

        var removed = engine.TickAutoDelete();

        Assert.Same(ac, Assert.Single(removed));
        Assert.Null(engine.World.FindAircraft("N1"));
    }

    [Fact]
    public void TickAutoDelete_AutoDeleteExemptWithoutPending_IsNotRemoved()
    {
        var engine = EngineWithMode("OnLanding");
        var ac = Aircraft("N1");
        ac.Ground.AutoDeleteExempt = true;
        engine.World.AddAircraft(ac);

        Assert.Empty(engine.TickAutoDelete());
        Assert.NotNull(engine.World.FindAircraft("N1"));
    }

    [Fact]
    public void TickAutoDelete_PendingAutoDelete_BypassesExempt()
    {
        var engine = EngineWithMode("None");
        var ac = Aircraft("N1");
        ac.Ground.AutoDeleteExempt = true;
        ac.Ground.PendingAutoDelete = true;
        engine.World.AddAircraft(ac);

        Assert.Same(ac, Assert.Single(engine.TickAutoDelete()));
    }

    private static AircraftState Aircraft(string callsign) =>
        new()
        {
            Callsign = callsign,
            AircraftType = "C172",
            Position = new LatLon(37.72, -122.22),
            FlightPlan = new AircraftFlightPlan(),
        };

    private static SimulationEngine EngineWithMode(string? autoDeleteMode) =>
        new(new TestAirportGroundData())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "test",
                ScenarioName = "Test",
                RngSeed = 1,
                OriginalScenarioJson = "{}",
                ScenarioAutoDeleteMode = autoDeleteMode,
            },
        };
}
