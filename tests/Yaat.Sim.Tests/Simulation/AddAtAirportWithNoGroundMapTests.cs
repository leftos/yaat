using Xunit;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// <c>ADD</c> at a primary airport with no ground map (no vNAS layout, or first named mid-session): the only ground spawn
/// allowed is the one on the runway, ready for departure. A parking or spot spawn is refused with a message saying the
/// airport has no ground map, where the generator has always refused it, so recorded spawns replay with the same draws.
/// </summary>
public class AddAtAirportWithNoGroundMapTests
{
    private const string NoGroundMap = "OAK has no ground map: only a runway spawn ready for departure is allowed";

    public AddAtAirportWithNoGroundMapTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void AddParking_AtAPrimaryWithNoGroundMap_IsRefusedWithTheNoGroundMapMessage()
    {
        SimulationEngine engine = NewEngine();

        AddAircraftOutcome outcome = engine.AddAircraft("I L J @S7", recorded: null);

        Assert.Null(outcome.Aircraft);
        Assert.Equal(NoGroundMap, outcome.Error);
    }

    [Fact]
    public void AddRunway_AtAPrimaryWithNoGroundMap_SpawnsLinedUp()
    {
        SimulationEngine engine = NewEngine();

        AddAircraftOutcome outcome = engine.AddAircraft("I L J 28R", recorded: null);

        Assert.Null(outcome.Error);
        AircraftState aircraft = outcome.Aircraft ?? throw new InvalidOperationException("the runway spawn added no aircraft");
        Assert.True(aircraft.IsOnGround);
        Assert.IsType<LinedUpAndWaitingPhase>(aircraft.Phases?.CurrentPhase);
        Assert.Null(aircraft.Ground.Layout);
    }

    private static SimulationEngine NewEngine()
    {
        var engine = new SimulationEngine(new NullGroundData(), PushPlanStore.CreateShipped())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "test-add-no-ground-map",
                ScenarioName = "ADD at an airport with no ground map",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = "OAK",
            },
        };
        engine.World.Rng = new SerializableRandom(1, 2, 3, 4);
        return engine;
    }
}
