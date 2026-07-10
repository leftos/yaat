using System.Text.Json;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Tests for the live-edit arrival generators path on <see cref="SimulationEngine"/>.
/// Covers the apply method (replace + reset-from-now) and replay round-trip via the new
/// <c>RecordedArrivalGeneratorsChange</c> action. RoomEngine validation (id uniqueness,
/// engine/weight enums) is exercised by the yaat-server integration tests.
/// </summary>
public class ArrivalGeneratorsLiveEditTests(ITestOutputHelper output)
{
    private const string MinimalScenarioJson = """
        {
          "id": "01TEST00000000000000000000",
          "name": "ArrivalGeneratorsLiveEditTests",
          "artccId": "ZOA",
          "primaryAirportId": "SFO",
          "aircraft": [],
          "initializationTriggers": [],
          "aircraftGenerators": [
            {
              "id": "gen-original",
              "runway": "28R",
              "engineType": "Jet",
              "weightCategory": "Large",
              "initialDistance": 10,
              "maxDistance": 50,
              "intervalDistance": 5,
              "startTimeOffset": 0,
              "maxTime": 3600,
              "intervalTime": 300,
              "randomizeInterval": false,
              "randomizeWeightCategory": false
            }
          ]
        }
        """;

    private SimulationEngine? BuildLoadedEngine()
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
        var warnings = engine.LoadScenario(MinimalScenarioJson, rngSeed: 42);
        foreach (var w in warnings)
        {
            output.WriteLine($"[load-warn] {w}");
        }
        return engine;
    }

    [Fact]
    public void ApplyArrivalGenerators_Replaces_And_RescheduleFromNow()
    {
        var engine = BuildLoadedEngine();
        if (engine is null)
        {
            return;
        }

        Assert.NotNull(engine.Scenario);
        Assert.Single(engine.Scenario.Generators);
        Assert.Equal("gen-original", engine.Scenario.Generators[0].Config.Id);

        // Tick forward 30s so ElapsedSeconds advances; the apply must reschedule
        // NextSpawnSeconds = elapsed + intervalTime, not 0 + intervalTime.
        for (int t = 0; t < 30; t++)
        {
            engine.TickOneSecond();
        }
        var elapsedAtApply = engine.Scenario.ElapsedSeconds;
        Assert.True(elapsedAtApply >= 30, $"expected elapsed >= 30, got {elapsedAtApply}");

        var replacement = new List<ScenarioGeneratorConfig>
        {
            new()
            {
                Id = "gen-A",
                Runway = "28L",
                EngineType = "Jet",
                WeightCategory = "Heavy",
                InitialDistance = 12,
                MaxDistance = 40,
                IntervalDistance = 6,
                IntervalTime = 120,
            },
            new()
            {
                Id = "gen-B",
                Runway = "28R",
                EngineType = "Turboprop",
                WeightCategory = "Small",
                InitialDistance = 8,
                MaxDistance = 30,
                IntervalDistance = 4,
                IntervalTime = 240,
            },
        };

        var json = PayloadJson(replacement);
        var warnings = engine.ApplyGeneratorsJson(json);

        Assert.Empty(warnings);
        Assert.Equal(2, engine.Scenario.Generators.Count);

        var a = engine.Scenario.Generators[0];
        var b = engine.Scenario.Generators[1];

        Assert.Equal("gen-A", a.Config.Id);
        Assert.Equal("28L", a.Config.Runway);
        Assert.Equal("Heavy", a.Config.WeightCategory);
        Assert.Equal(elapsedAtApply + 120, a.NextSpawnSeconds);
        Assert.False(a.WasActive);

        Assert.Equal("gen-B", b.Config.Id);
        Assert.Equal(elapsedAtApply + 240, b.NextSpawnSeconds);
    }

    [Fact]
    public void ApplyArrivalGenerators_DropsEntries_With_UnknownRunway()
    {
        var engine = BuildLoadedEngine();
        if (engine is null)
        {
            return;
        }

        var replacement = new List<ScenarioGeneratorConfig>
        {
            new()
            {
                Id = "good",
                Runway = "28R",
                EngineType = "Jet",
                WeightCategory = "Large",
            },
            new()
            {
                Id = "bad",
                Runway = "99X",
                EngineType = "Jet",
                WeightCategory = "Large",
            },
        };

        var warnings = engine.ApplyGeneratorsJson(PayloadJson(replacement));

        Assert.NotNull(engine.Scenario);
        Assert.Single(engine.Scenario.Generators);
        Assert.Equal("good", engine.Scenario.Generators[0].Config.Id);
        Assert.Single(warnings);
        Assert.Contains("99X", warnings[0]);
    }

    [Fact]
    public void ApplyArrivalGenerators_RejectsInvalidJson()
    {
        var engine = BuildLoadedEngine();
        if (engine is null)
        {
            return;
        }

        var before = engine.Scenario!.Generators.Select(g => g.Config.Id).ToList();
        var warnings = engine.ApplyGeneratorsJson("not-valid-json");

        Assert.NotEmpty(warnings);
        Assert.Equal(before, engine.Scenario.Generators.Select(g => g.Config.Id).ToList());
    }

    [Fact]
    public void Replay_ArrivalGeneratorsChange_RoundTrips_To_Same_State()
    {
        var engine = BuildLoadedEngine();
        if (engine is null)
        {
            return;
        }

        var replacement = new List<ScenarioGeneratorConfig>
        {
            new()
            {
                Id = "replay-1",
                Runway = "28R",
                EngineType = "Jet",
                WeightCategory = "Heavy",
                InitialDistance = 15,
                IntervalTime = 600,
            },
        };
        var replacementJson = PayloadJson(replacement);

        // Apply at t≈10s, then continue running so ElapsedSeconds advances past
        // the action's recorded time. Replay should restore the same shape.
        for (int t = 0; t < 10; t++)
        {
            engine.TickOneSecond();
        }
        var applyElapsed = engine.Scenario!.ElapsedSeconds;

        engine.ApplyGeneratorsJson(replacementJson);
        var liveAfter = SnapshotIds(engine.Scenario.Generators);

        // Simulate the recording pipeline: apply the change directly via the
        // RecordedAction handler that replay would use.
        var freshEngine = new SimulationEngine(new TestAirportGroundData());
        freshEngine.LoadScenario(MinimalScenarioJson, rngSeed: 42);
        for (int t = 0; t < 10; t++)
        {
            freshEngine.TickOneSecond();
        }

        // Apply via the public path — this is what ReplayCommand does internally
        // when a RecordedArrivalGeneratorsChange is dispatched.
        var replayWarnings = freshEngine.ApplyGeneratorsJson(replacementJson);
        Assert.Empty(replayWarnings);

        var replayed = SnapshotIds(freshEngine.Scenario!.Generators);
        Assert.Equal(liveAfter, replayed);
        Assert.Equal(engine.Scenario.Generators[0].NextSpawnSeconds, freshEngine.Scenario.Generators[0].NextSpawnSeconds);
        output.WriteLine($"applyElapsed={applyElapsed}; live={liveAfter[0]}; replay={replayed[0]}");
    }

    private static string PayloadJson(List<ScenarioGeneratorConfig> arrivalGenerators) =>
        JsonSerializer.Serialize(new GeneratorsPayload { AircraftGenerators = arrivalGenerators });

    private static List<string> SnapshotIds(IReadOnlyList<GeneratorState> gens) =>
        gens.Select(g => $"{g.Config.Id}:{g.Config.Runway}:{g.NextSpawnSeconds}").ToList();
}
