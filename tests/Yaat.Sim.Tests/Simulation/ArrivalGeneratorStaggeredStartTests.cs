using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// When a scenario has multiple arrival generators that share a <c>startTimeOffset</c>, they used to
/// all fire their first arrival on the very first tick — a synchronized burst of arrivals. The first
/// generator still spawns on schedule, but each subsequent generator with <c>randomizeInterval</c> on
/// gets a random initial phase within its first interval so the streams desync and don't pile onto the
/// same tick. Generators without <c>randomizeInterval</c> keep their authored deterministic timing.
/// </summary>
public class ArrivalGeneratorStaggeredStartTests(ITestOutputHelper output)
{
    private static string TwoGeneratorScenario(bool randomizeInterval) =>
        $$"""
            {
              "id": "01TEST00000000000000000000",
              "name": "ArrivalGeneratorStaggeredStartTests",
              "artccId": "ZOA",
              "primaryAirportId": "SFO",
              "aircraft": [],
              "initializationTriggers": [],
              "aircraftGenerators": [
                {
                  "id": "gen-28R",
                  "runway": "28R",
                  "engineType": "Jet",
                  "weightCategory": "Large",
                  "initialDistance": 15,
                  "maxDistance": 50,
                  "intervalDistance": 5,
                  "startTimeOffset": 0,
                  "maxTime": 3600,
                  "intervalTime": 240,
                  "randomizeInterval": {{(randomizeInterval ? "true" : "false")}},
                  "randomizeWeightCategory": false
                },
                {
                  "id": "gen-28L",
                  "runway": "28L",
                  "engineType": "Jet",
                  "weightCategory": "Large",
                  "initialDistance": 15,
                  "maxDistance": 50,
                  "intervalDistance": 5,
                  "startTimeOffset": 0,
                  "maxTime": 3600,
                  "intervalTime": 720,
                  "randomizeInterval": {{(randomizeInterval ? "true" : "false")}},
                  "randomizeWeightCategory": false
                }
              ]
            }
            """;

    private SimulationEngine? BuildEngine(bool randomizeInterval)
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("SFO") is null)
        {
            return null;
        }

        var engine = new SimulationEngine(groundData);
        var warnings = engine.LoadScenario(TwoGeneratorScenario(randomizeInterval), rngSeed: 42);
        foreach (var w in warnings)
        {
            output.WriteLine($"[load-warn] {w}");
        }
        return engine;
    }

    [Fact]
    public void MultipleRandomizedGenerators_OnlyFirstSpawnsOnFirstTick()
    {
        var engine = BuildEngine(randomizeInterval: true);
        if (engine is null)
        {
            return;
        }

        var gens = engine.Scenario!.Generators;
        Assert.Equal(2, gens.Count);

        // The first generator keeps its authored schedule and fires on the first tick.
        Assert.Equal(0, gens[0].NextSpawnSeconds);

        // The second randomized generator gets a random initial phase within its first interval, so it
        // does not share the first tick with the first generator.
        output.WriteLine($"gen[1].NextSpawnSeconds = {gens[1].NextSpawnSeconds}");
        Assert.True(gens[1].NextSpawnSeconds > 1, $"second generator must not also fire on the first tick (NextSpawn={gens[1].NextSpawnSeconds})");
        Assert.True(gens[1].NextSpawnSeconds <= gens[1].Config.IntervalTime, "the staggered first spawn stays within the first interval");

        engine.TickOneSecond();

        var firstTickSpawns = engine.GeneratorSpawnLog.Where(s => s.ElapsedSeconds == 1).ToList();
        Assert.Single(firstTickSpawns);
    }

    [Fact]
    public void MultipleDeterministicGenerators_KeepAuthoredStart()
    {
        var engine = BuildEngine(randomizeInterval: false);
        if (engine is null)
        {
            return;
        }

        // Without randomizeInterval the authored deterministic timing is preserved — both generators
        // keep StartTimeOffset (an author who wants a stagger sets distinct offsets or randomizes).
        Assert.All(engine.Scenario!.Generators, g => Assert.Equal(0, g.NextSpawnSeconds));
    }
}
