using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Generator-config parity with the vNAS model (<c>ScenarioAircraftGenerator</c>): <c>maxTime</c> and
/// <c>intervalDistance</c> are nullable upstream. When omitted, <c>maxTime</c> means "no time-based
/// exhaustion" (the stream runs for the whole session) and <c>intervalDistance</c> means "no author
/// distance floor" (spacing falls back to the radar/wake minimum), NOT YAAT's old injected 3600 s /
/// 5 NM defaults.
/// </summary>
public class GeneratorConfigParityTests(ITestOutputHelper output)
{
    private static string ScenarioJson(string generatorFields) =>
        $$"""
            {
              "id": "01TEST00000000000000000000",
              "name": "GeneratorConfigParityTests",
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
                  "startTimeOffset": 0,
                  "randomizeInterval": false,
                  "randomizeWeightCategory": false,
                  {{generatorFields}}
                }
              ]
            }
            """;

    private SimulationEngine? BuildLoadedEngine(string generatorFields)
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
        var warnings = engine.LoadScenario(ScenarioJson(generatorFields), rngSeed: 42);
        foreach (var w in warnings)
        {
            output.WriteLine($"[load-warn] {w}");
        }
        return engine;
    }

    [Fact]
    public void MaxTimeOmitted_DeserializesToNull()
    {
        var engine = BuildLoadedEngine(""" "maxDistance": 50, "intervalDistance": 5, "intervalTime": 120 """);
        if (engine?.Scenario is null)
        {
            return;
        }

        Assert.Null(engine.Scenario.Generators[0].Config.MaxTime);
    }

    [Fact]
    public void MaxTimePresent_DeserializesToValue()
    {
        var engine = BuildLoadedEngine(""" "maxDistance": 50, "intervalDistance": 5, "intervalTime": 120, "maxTime": 1200 """);
        if (engine?.Scenario is null)
        {
            return;
        }

        Assert.Equal(1200, engine.Scenario.Generators[0].Config.MaxTime);
    }

    [Fact]
    public void IntervalDistanceOmitted_DeserializesToZero_NoPhantomFloor()
    {
        var engine = BuildLoadedEngine(""" "maxDistance": 50, "intervalTime": 120 """);
        if (engine?.Scenario is null)
        {
            return;
        }

        // vNAS leaves intervalDistance null when absent; YAAT must resolve that to 0 (no author floor),
        // not silently inject a 5 NM spacing minimum.
        Assert.Equal(0, engine.Scenario.Generators[0].Config.IntervalDistance);
    }

    [Fact]
    public void MaxTimeOmitted_GeneratorDoesNotExhaustPastOldDefault()
    {
        var engine = BuildLoadedEngine(""" "maxDistance": 50, "intervalDistance": 5, "intervalTime": 120 """);
        if (engine?.Scenario is null)
        {
            return;
        }

        // Jump well past the old hard-coded 3600 s exhaustion default and run one pre-physics pass.
        engine.Scenario.ElapsedSeconds = 4000;
        engine.TickOneSecond();

        Assert.True(engine.Scenario.Generators[0].WasActive, "a generator with no maxTime must keep producing traffic");
    }

    [Fact]
    public void MaxTimePresent_GeneratorExhaustsPastMaxTime()
    {
        var engine = BuildLoadedEngine(""" "maxDistance": 50, "intervalDistance": 5, "intervalTime": 120, "maxTime": 1000 """);
        if (engine?.Scenario is null)
        {
            return;
        }

        engine.Scenario.ElapsedSeconds = 1500;
        engine.TickOneSecond();

        Assert.False(engine.Scenario.Generators[0].WasActive, "a generator past its maxTime must stop producing traffic");
    }

    [Fact]
    public void IntervalDistanceOmitted_PackedStreamSpacesAtRadarFloor_NotFiveNm()
    {
        // Short interval packs the stream so each new arrival is placed behind a leader. With no author
        // distance floor, the binding gap is the 3 NM terminal radar floor (or wake), never the old 5 NM.
        var engine = BuildLoadedEngine(""" "maxDistance": 50, "intervalTime": 20 """);
        if (engine?.Scenario is null)
        {
            return;
        }

        for (int t = 0; t < 400; t++)
        {
            engine.TickOneSecond();
        }

        var behindLeader = engine.GeneratorSpawnLog.Where(s => s.RearmostAtSpawnNm is not null).ToList();
        foreach (var s in behindLeader.OrderBy(s => s.ElapsedSeconds))
        {
            output.WriteLine($"t={s.ElapsedSeconds} d={s.SpawnDistanceNm:F1} rearmost={s.RearmostAtSpawnNm:F1} gap={s.RequiredGapNm:F2}");
        }

        Assert.NotEmpty(behindLeader);
        // Every in-trail gap behind a leader is the radar/wake floor (~3 NM), strictly below the old 5 NM
        // author default that absent intervalDistance used to inject.
        Assert.All(behindLeader, s => Assert.True(s.RequiredGapNm < 4.5, $"in-trail gap {s.RequiredGapNm:F2} NM implies a phantom author floor"));
    }
}
