using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Tests for the time-first arrival-generator spawn model. <c>IntervalTime</c> drives the spawn cadence;
/// when an arrival is due it is placed at the back of the stream at
/// <c>D = max(InitialDistance, rearmost + gap)</c>, where <c>gap = max(IntervalDistance, wakeMinimum)</c>,
/// capped at <c>MaxDistance</c> (the generator waits when no room exists rather than exceeding the cap).
/// An empty corridor has no leader, so the arrival spawns exactly at <c>InitialDistance</c>.
/// </summary>
public class ArrivalGeneratorSpawnModelTests(ITestOutputHelper output)
{
    private const int InitialDistance = 15;
    private const int MaxDistance = 50;
    private const int IntervalDistance = 5;

    private static string ScenarioJson(int intervalTime, bool randomizeInterval) =>
        $$"""
            {
              "id": "01TEST00000000000000000000",
              "name": "ArrivalGeneratorSpawnModelTests",
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
                  "initialDistance": {{InitialDistance}},
                  "maxDistance": {{MaxDistance}},
                  "intervalDistance": {{IntervalDistance}},
                  "startTimeOffset": 0,
                  "maxTime": 3600,
                  "intervalTime": {{intervalTime}},
                  "randomizeInterval": {{(randomizeInterval ? "true" : "false")}},
                  "randomizeWeightCategory": false
                }
              ]
            }
            """;

    private SimulationEngine? BuildLoadedEngine(int intervalTime, bool randomizeInterval)
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
        var warnings = engine.LoadScenario(ScenarioJson(intervalTime, randomizeInterval), rngSeed: 42);
        foreach (var w in warnings)
        {
            output.WriteLine($"[load-warn] {w}");
        }
        return engine;
    }

    private void Dump(SimulationEngine engine)
    {
        foreach (var s in engine.GeneratorSpawnLog.OrderBy(s => s.ElapsedSeconds))
        {
            output.WriteLine(
                $"t={s.ElapsedSeconds} d={s.SpawnDistanceNm:F1} rearmost={s.RearmostAtSpawnNm?.ToString("F1") ?? "none"} gap={s.RequiredGapNm:F1}"
            );
        }
    }

    [Fact]
    public void LongInterval_KeepsArrivalsNearInitialDistance_PacedByTime()
    {
        var engine = BuildLoadedEngine(intervalTime: 180, randomizeInterval: false);
        if (engine is null)
        {
            return;
        }

        for (int t = 0; t < 600; t++)
        {
            engine.TickOneSecond();
        }
        Dump(engine);

        var spawns = engine.GeneratorSpawnLog.OrderBy(s => s.ElapsedSeconds).ToList();
        Assert.True(spawns.Count >= 3, $"expected a sustained stream, got {spawns.Count} spawns");

        // Empty corridor at activation -> first arrival at InitialDistance, with no leader.
        Assert.Null(spawns[0].RearmostAtSpawnNm);
        Assert.Equal(InitialDistance, spawns[0].SpawnDistanceNm, precision: 1);

        // A long interval drains the corridor between spawns, so every arrival keeps entering near
        // InitialDistance -- NOT at the back of the corridor (MaxDistance). This is the time-first signature.
        foreach (var s in spawns)
        {
            Assert.InRange(s.SpawnDistanceNm, InitialDistance - 0.5, InitialDistance + IntervalDistance);
            Assert.True(s.SpawnDistanceNm <= MaxDistance + 1e-6, "no spawn may exceed MaxDistance");
            if (s.RearmostAtSpawnNm is double rear)
            {
                Assert.True(
                    s.SpawnDistanceNm - rear >= s.RequiredGapNm - 1e-6,
                    $"spawn at {s.SpawnDistanceNm:F2} is closer than the {s.RequiredGapNm:F2}nm gap behind rearmost {rear:F2}"
                );
            }
        }

        // IntervalTime (180s @ 100%) sets the cadence between consecutive arrivals; with the corridor
        // never backed up, randomize off gives an exact, defer-free cadence.
        var expected = ScenarioPacing.EffectiveArrivalGeneratorIntervalSeconds(180, 100);
        for (int i = 1; i < spawns.Count; i++)
        {
            Assert.Equal(expected, spawns[i].ElapsedSeconds - spawns[i - 1].ElapsedSeconds, precision: 6);
        }
    }

    [Fact]
    public void ShortInterval_PacksStreamTowardMaxDistance_ThenWaits()
    {
        var engine = BuildLoadedEngine(intervalTime: 15, randomizeInterval: false);
        if (engine is null)
        {
            return;
        }

        for (int t = 0; t < 600; t++)
        {
            engine.TickOneSecond();
        }
        Dump(engine);

        var spawns = engine.GeneratorSpawnLog.OrderBy(s => s.ElapsedSeconds).ToList();
        Assert.NotEmpty(spawns);

        // The cap is hard: no arrival is ever placed beyond MaxDistance.
        foreach (var s in spawns)
        {
            Assert.True(s.SpawnDistanceNm <= MaxDistance + 1e-6, $"spawn at {s.SpawnDistanceNm:F2} exceeds MaxDistance {MaxDistance}");
        }

        // First arrival enters an empty corridor at InitialDistance; a short interval then fills the
        // corridor, stacking subsequent arrivals back from InitialDistance toward the MaxDistance cap.
        Assert.Equal(InitialDistance, spawns[0].SpawnDistanceNm, precision: 1);
        Assert.True(
            spawns.Max(s => s.SpawnDistanceNm) >= MaxDistance - IntervalDistance,
            $"stream did not pack toward the cap; deepest spawn was {spawns.Max(s => s.SpawnDistanceNm):F1}nm"
        );

        // Once packed, the generator throttles on 'no room' rather than firing every IntervalTime, so the
        // realised count is far below the naive 600/15 the timer alone would produce.
        Assert.True(spawns.Count < 40, $"expected throttling near the cap, got {spawns.Count} spawns");
    }

    [Fact]
    public void RandomizeInterval_JittersCadence_AroundTheBaseInterval()
    {
        var engine = BuildLoadedEngine(intervalTime: 180, randomizeInterval: true);
        if (engine is null)
        {
            return;
        }

        for (int t = 0; t < 1800; t++)
        {
            engine.TickOneSecond();
        }
        Dump(engine);

        var spawns = engine.GeneratorSpawnLog.OrderBy(s => s.ElapsedSeconds).ToList();
        Assert.True(spawns.Count >= 6, $"expected several spawns over 1800s, got {spawns.Count}");

        // The long interval keeps the corridor drained, so each cadence equals the jittered interval
        // (plus up to ~1s tick rounding). Jitter is +-25% of the 180s base, so every gap stays bounded.
        var diffs = new List<double>();
        for (int i = 1; i < spawns.Count; i++)
        {
            var diff = spawns[i].ElapsedSeconds - spawns[i - 1].ElapsedSeconds;
            diffs.Add(diff);
            Assert.InRange(diff, (180 * 0.75) - 1.5, (180 * 1.25) + 1.5);
        }

        // Randomize must actually vary the cadence -- not a metronome.
        Assert.True(diffs.Distinct().Count() > 1, "randomizeInterval produced a constant cadence");

        // ...but it stays centred on the base interval rather than drifting.
        Assert.InRange(diffs.Average(), 150, 210);
    }
}
