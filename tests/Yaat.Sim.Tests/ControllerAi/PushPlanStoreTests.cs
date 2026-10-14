using Xunit;
using Yaat.Sim.ControllerAi;
using Yaat.Sim.ControllerAi.Brains;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Actions;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.ControllerAi;

/// <summary>
/// When the push plan of a push-back stand is made (<see cref="PushPlanStore"/>): an aircraft spawned at the stand
/// after the load plans it as it spawns, and a seeded run pushes the same way whether its stands were planned before the
/// tick or by the push rule on the tick.
/// </summary>
public class PushPlanStoreTests
{
    private readonly ArtccConfigRoot? _zoa = TestArtccConfig.LoadZoa();

    public PushPlanStoreTests()
    {
        TestVnasData.EnsureInitialized();
    }

    /// <summary>A B738 spawned on push-back stand S7 after the load has the stand's plan stored as it spawns, before any brain runs.</summary>
    [Fact]
    public void DelayedSpawnAtAPushBackStand_PlansTheStandAsItSpawns()
    {
        if (_zoa is null)
        {
            return;
        }

        AiPositionConfig ground = TestAiPositions.OakGround(_zoa);
        SimulationEngine engine = AiTestFixture.LoadWith(Scenario(("N1SP", "S7", 5)), _zoa, 7, [ground], "30", p => new GroundBrain(p));
        Assert.Null(engine.FindAircraft("N1SP"));
        AircraftState? spawned = null;
        for (int second = 0; (spawned is null) && (second < 15); second++)
        {
            AiTestFixture.Tick(engine, 1);
            spawned = engine.FindAircraft("N1SP");
        }

        Assert.NotNull(spawned);
        PushPlanStore store = engine.World.PushPlans;
        AirportGroundLayout layout = engine.ResolveGroundLayout(spawned)!;
        Assert.True(store.TryGet(layout, "S7", AirplaneDesignGroups.GroupForType("B738"), out PushTargetEntry? plan));
        Assert.NotNull(plan);
        Assert.Equal(0, store.Misses);
    }

    /// <summary>An RPO <c>ADD</c> of a B738 at push-back stand S7 while no AI Ground staffs the airport plans nothing at spawn.</summary>
    [Fact]
    public void AddAtAPushBackStand_WithAiGroundOff_PlansNothing()
    {
        if (_zoa is null)
        {
            return;
        }

        SimulationEngine engine = AiTestFixture.Load(AiTestFixture.ParkedAtOak, _zoa, 7, []);
        AircraftState added = AddB738AtS7(engine);

        Assert.False(engine.World.PushPlans.TryGet(engine.ResolveGroundLayout(added)!, "S7", B738Group, out _));
    }

    /// <summary>The same <c>ADD</c> with AI Ground active plans stand S7 as the aircraft spawns.</summary>
    [Fact]
    public void AddAtAPushBackStand_WithAiGroundOn_PlansTheStand()
    {
        if (_zoa is null)
        {
            return;
        }

        AiPositionConfig ground = TestAiPositions.OakGround(_zoa);
        SimulationEngine engine = AiTestFixture.LoadWith(AiTestFixture.ParkedAtOak, _zoa, 7, [ground], "30", p => new GroundBrain(p));
        AircraftState added = AddB738AtS7(engine);

        Assert.True(engine.World.PushPlans.TryGet(engine.ResolveGroundLayout(added)!, "S7", B738Group, out PushTargetEntry? plan));
        Assert.NotNull(plan);
    }

    /// <summary>
    /// A replay never plans at spawn, even with AI Ground configured: neither the load-time aircraft the replay's first
    /// second runs the spawn hooks for, nor one spawned later in the replay.
    /// </summary>
    [Fact]
    public void Replay_OfJetsAtPushBackStands_PlansNothing()
    {
        if (_zoa is null)
        {
            return;
        }

        AiPositionConfig ground = TestAiPositions.OakGround(_zoa);
        string json = Scenario(("N1SP", "S7", 0), ("N2SP", "RON9", 5));
        SimulationEngine engine = AiTestFixture.LoadWith(json, _zoa, 7, [ground], "30", p => new GroundBrain(p));

        engine.ReplayRange(0, 10, []);

        foreach ((string callsign, string stand) in new[] { ("N1SP", "S7"), ("N2SP", "RON9") })
        {
            AircraftState jet = engine.FindAircraft(callsign) ?? throw new InvalidOperationException($"{callsign} never spawned in the replay");
            Assert.False(engine.World.PushPlans.TryGet(engine.ResolveGroundLayout(jet)!, stand, B738Group, out _), $"{stand} was planned");
        }
    }

    /// <summary>
    /// One stand whose planning throws is logged and left unstored, at load and at spawn; every other stand is still
    /// planned, and neither call throws.
    /// </summary>
    [Fact]
    public void PlanningThatThrowsForOneStand_LeavesItUnstored_AndPlansTheOthers()
    {
        if (_zoa is null)
        {
            return;
        }

        SimulationEngine engine = AiTestFixture.Load(Scenario(("N1SP", "S7", 0), ("N2SP", "RON9", 0), ("N3SP", "RON10", 0)), _zoa, 7, []);
        var store = new PushPlanStore(
            (_, stand, _, _) => (stand == "RON9") ? throw new InvalidOperationException("injected planner failure") : (PushTargetEntry?)null
        );

        Assert.Equal(2, store.PlanParked(engine.World.GetSnapshot(), engine.ResolveGroundLayout, 2));
        AircraftState failing = engine.FindAircraft("N2SP")!;
        store.PlanSpawned(failing, engine.ResolveGroundLayout(failing));

        AirportGroundLayout layout = engine.ResolveGroundLayout(failing)!;
        Assert.True(store.TryGet(layout, "S7", B738Group, out _));
        Assert.True(store.TryGet(layout, "RON10", B738Group, out _));
        Assert.False(store.TryGet(layout, "RON9", B738Group, out _));
    }

    /// <summary>
    /// One aircraft whose stand cannot be classified (its layout lookup throws) is logged and skipped; every other stand
    /// is still planned, and the call does not throw.
    /// </summary>
    [Fact]
    public void ClassificationThatThrowsForOneAircraft_SkipsIt_AndPlansTheOthers()
    {
        if (_zoa is null)
        {
            return;
        }

        SimulationEngine engine = AiTestFixture.Load(Scenario(("N1SP", "S7", 0), ("N2SP", "RON9", 0), ("N3SP", "RON10", 0)), _zoa, 7, []);
        var store = new PushPlanStore((_, _, _, _) => null);
        AirportGroundLayout? LayoutFor(AircraftState ac) =>
            (ac.Callsign == "N2SP") ? throw new InvalidOperationException("injected classification failure") : engine.ResolveGroundLayout(ac);

        Assert.Equal(2, store.PlanParked(engine.World.GetSnapshot(), LayoutFor, 2));

        AirportGroundLayout layout = engine.ResolveGroundLayout(engine.FindAircraft("N2SP")!)!;
        Assert.True(store.TryGet(layout, "S7", B738Group, out _));
        Assert.True(store.TryGet(layout, "RON10", B738Group, out _));
        Assert.False(store.TryGet(layout, "RON9", B738Group, out _));
    }

    /// <summary>
    /// The spawn hooks the server's placement of a load's aircraft runs plan nothing, even with AI Ground active: the
    /// load's prepare plans those stands. The same aircraft through the live spawn hooks does plan, so AI Ground is on.
    /// </summary>
    [Fact]
    public void LoadedAircraftPlacement_WithAiGroundOn_PlansNothing()
    {
        if (_zoa is null)
        {
            return;
        }

        AiPositionConfig ground = TestAiPositions.OakGround(_zoa);
        SimulationEngine engine = AiTestFixture.LoadWith(Scenario(("N1SP", "S7", 0)), _zoa, 7, [ground], "30", p => new GroundBrain(p));
        AircraftState jet = engine.FindAircraft("N1SP")!;
        AirportGroundLayout layout = engine.ResolveGroundLayout(jet)!;

        engine.AfterLoadedAircraftPlaced(jet);
        Assert.False(engine.World.PushPlans.TryGet(layout, "S7", B738Group, out _), "placement planned S7");

        engine.AfterAircraftSpawned(jet);
        Assert.True(engine.World.PushPlans.TryGet(layout, "S7", B738Group, out _), "AI Ground is not active, so the test proves nothing");
    }

    private static AirplaneDesignGroup B738Group => AirplaneDesignGroups.GroupForType("B738");

    /// <summary>Adds a B738 at stand S7 through the RPO <c>ADD</c> and returns it.</summary>
    private static AircraftState AddB738AtS7(SimulationEngine engine)
    {
        ActionOutcome outcome = engine.Actions.Issue(new ActionInput("", "ADD I L J @S7 B738", "conn-1", "XX", Baked: null));
        Assert.True(outcome.Result.Success, outcome.Result.Message);
        return Assert.Single(engine.World.GetSnapshot(), ac => ac.Callsign != AiTestFixture.Callsign);
    }

    /// <summary>
    /// Three B738s on OAK push-back stands under the AI Ground brain, run twice on one seed: once with every stand planned
    /// before the tick, once cold so the push rule plans each on the tick. Both runs issue the same pushes at the same
    /// sim times, because a stand's plan is the same whenever it is made.
    /// </summary>
    [Fact]
    public void SeededRun_PreparedAndCold_IssueTheSamePushesAtTheSameTimes()
    {
        if (_zoa is null)
        {
            return;
        }

        string json = Scenario(("N1SP", "S7", 0), ("N2SP", "RON9", 0), ("N3SP", "RON10", 0));
        (List<string> Pushes, PushPlanStore Store) prepared = Run(json, prepare: true);
        (List<string> Pushes, PushPlanStore Store) cold = Run(json, prepare: false);

        Assert.Equal(0, prepared.Store.Misses);
        Assert.True(cold.Store.Misses > 0, "the cold run never planned a stand on the tick");
        Assert.True(prepared.Pushes.Count >= 2, string.Join("; ", prepared.Pushes));
        Assert.Equal(prepared.Pushes, cold.Pushes);
    }

    /// <summary>The run's PUSH commands as "time callsign command", and the world's plan store.</summary>
    private (List<string> Pushes, PushPlanStore Store) Run(string json, bool prepare)
    {
        AiPositionConfig ground = TestAiPositions.OakGround(_zoa!);
        SimulationEngine engine = AiTestFixture.LoadWith(json, _zoa!, 42, [ground], "30", p => new GroundBrain(p));
        if (prepare)
        {
            Assert.Equal(3, engine.World.PushPlans.PlanParked(engine.World.GetSnapshot(), engine.ResolveGroundLayout, 2));
        }

        AiTestFixture.Tick(engine, 400);
        List<string> pushes =
        [
            .. engine
                .Scenario!.ActionLog.OfType<RecordedCommand>()
                .Where(c => c.Command.StartsWith("PUSH", StringComparison.Ordinal))
                .Select(c => $"{c.ElapsedSeconds:F1} {c.Callsign} {c.Command}"),
        ];
        return (pushes, engine.World.PushPlans);
    }

    /// <summary>A scenario of B738s parked at OAK, each on its stand and spawning after its delay in seconds.</summary>
    private static string Scenario(params (string Callsign, string Stand, int SpawnDelay)[] jets)
    {
        IEnumerable<string> aircraft = jets.Select(jet =>
            $$"""
                    {
                      "id": "{{jet.Callsign}}", "aircraftId": "{{jet.Callsign}}", "aircraftType": "B738", "transponderMode": "C",
                      "spawnDelay": {{jet.SpawnDelay}},
                      "startingConditions": { "type": "Parking", "parking": "{{jet.Stand}}" },
                      "flightplan": {
                        "rules": "IFR", "departure": "KOAK", "destination": "KLAX", "cruiseAltitude": 35000, "cruiseSpeed": 430,
                        "route": "", "remarks": "", "aircraftType": "B738"
                      }
                    }
                """
        );
        string aircraftList = string.Join(",", aircraft);
        return $$"""
            {
              "id": "push-plans", "name": "Jets on OAK push-back stands", "artccId": "ZOA", "primaryAirportId": "OAK",
              "aircraft": [{{aircraftList}}]
            }
            """;
    }
}
