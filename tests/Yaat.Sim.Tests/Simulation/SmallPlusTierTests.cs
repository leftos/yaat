using Xunit;
using Xunit.Abstractions;
using Yaat.Sim;
using Yaat.Sim.Data;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// The SmallPlus weight tier is the regional feed: CWT G regional jets + commuter turboprops
/// (plus CWT H upper-small) sit between Small and Large, and Large becomes mainline-only. The
/// pools were aviation-reviewed against OAK 28R (5,448 ft) landing feasibility — CRJ9 is excluded
/// as marginal and "E175" is spelled "E75L" (the resolvable ICAO/CWT designator).
/// </summary>
public class SmallPlusTierTests
{
    private readonly ITestOutputHelper _output;

    public SmallPlusTierTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void SmallPlusJet_IsRegionalFeed_NeverMainlineOrCrj9()
    {
        var pool = AircraftGenerator.GetTypesForCombo(WeightClass.SmallPlus, EngineKind.Jet);
        Assert.NotNull(pool);

        foreach (var regional in new[] { "CRJ7", "E170", "E75L", "E145", "E135" })
        {
            Assert.Contains(regional, pool!);
        }

        // Mainline narrowbodies stay in Large; CRJ9 is dropped (marginal on a 5,448 ft runway).
        foreach (var excluded in new[] { "CRJ9", "A319", "A320", "A321", "B737", "B738", "B739" })
        {
            Assert.DoesNotContain(excluded, pool!);
        }
    }

    [Fact]
    public void LargeJet_IsMainlineOnly_NoRegionals()
    {
        var pool = AircraftGenerator.GetTypesForCombo(WeightClass.Large, EngineKind.Jet);
        Assert.NotNull(pool);

        foreach (var mainline in new[] { "B737", "B738", "B739", "A319", "A320", "A321" })
        {
            Assert.Contains(mainline, pool!);
        }

        foreach (var regional in new[] { "CRJ7", "CRJ9", "E170", "E145", "E135", "E75L" })
        {
            Assert.DoesNotContain(regional, pool!);
        }
    }

    [Fact]
    public void SmallPlusTurboprop_AbsorbsTheRegionalTurboprops_AndLargeTurbopropBucketIsGone()
    {
        var pool = AircraftGenerator.GetTypesForCombo(WeightClass.SmallPlus, EngineKind.Turboprop);
        Assert.NotNull(pool);
        foreach (var t in new[] { "AT72", "DH8C", "SF34", "B190", "B350" })
        {
            Assert.Contains(t, pool!);
        }

        // There is no CWT "Large" turboprop — the bucket is removed entirely.
        Assert.Null(AircraftGenerator.GetTypesForCombo(WeightClass.Large, EngineKind.Turboprop));
    }

    [Fact]
    public void EveryJetPoolType_ResolvesToACwtCategory()
    {
        // Guards the E175-vs-E75L class of bug: AssertEveryTypeResolves checks profile + engine
        // category but not CWT, so a non-resolvable designator would silently be wake-unknown.
        foreach (var weight in new[] { WeightClass.SmallPlus, WeightClass.Large, WeightClass.Heavy })
        {
            var pool = AircraftGenerator.GetTypesForCombo(weight, EngineKind.Jet);
            if (pool is null)
            {
                continue;
            }
            foreach (var type in pool)
            {
                Assert.True(WakeTurbulenceData.GetCwt(type) is not null, $"{weight}+Jet type '{type}' has no CWT category");
            }
        }
    }

    [Fact]
    public void SmallPlusPiston_HasNoBucket_ButStillSpawnsViaFallback()
    {
        Assert.Null(AircraftGenerator.GetTypesForCombo(WeightClass.SmallPlus, EngineKind.Piston));

        if (!AircraftProfileDatabase.IsInitialized)
        {
            _output.WriteLine("AircraftProfileDatabase not initialized; skipping spawn (test data missing)");
            return;
        }

        var rng = new Random(7);
        var request = new SpawnRequest
        {
            Rules = FlightRulesKind.Vfr,
            Weight = WeightClass.SmallPlus,
            Engine = EngineKind.Piston,
            PositionType = SpawnPositionType.Bearing,
            Bearing = 90,
            DistanceNm = 5,
            Altitude = 3000,
        };
        var (state, error) = AircraftGenerator.Generate(request, "KOAK", Array.Empty<AircraftState>(), groundLayout: null, rng);
        Assert.True(state is not null, $"SmallPlus+Piston spawn failed: {error}");
        Assert.Equal(AircraftCategory.Piston, AircraftCategorization.Categorize(state!.AircraftType));
    }

    [Fact]
    public void SmallPlusGenerator_RandomizeOff_SpawnsOnlyRegionalJets()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            _output.WriteLine("NavData not available; skipping");
            return;
        }
        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("SFO") is null)
        {
            _output.WriteLine("SFO layout not available; skipping");
            return;
        }

        var engine = new SimulationEngine(groundData);
        engine.LoadScenario(SmallPlusScenarioJson(), rngSeed: 42);

        for (int t = 0; t < 1200; t++)
        {
            engine.TickOneSecond();
        }

        var regionalPool = AircraftGenerator.GetTypesForCombo(WeightClass.SmallPlus, EngineKind.Jet)!;
        var spawnedTypes = engine.World.GetSnapshot().Select(a => AircraftState.StripTypePrefix(a.AircraftType)).Distinct().ToList();
        Assert.NotEmpty(spawnedTypes);
        foreach (var type in spawnedTypes)
        {
            _output.WriteLine($"spawned {type}");
            Assert.True(
                regionalPool.Contains(type),
                $"SmallPlus generator spawned '{type}', which is not in the regional jet pool [{string.Join(", ", regionalPool)}]"
            );
        }
    }

    private static string SmallPlusScenarioJson() =>
        """
            {
              "id": "01TESTSMALLPLUS00000000000",
              "name": "SmallPlusTierTests",
              "artccId": "ZOA",
              "primaryAirportId": "SFO",
              "aircraft": [],
              "initializationTriggers": [],
              "aircraftGenerators": [
                {
                  "id": "gen-smallplus",
                  "runway": "28R",
                  "engineType": "Jet",
                  "weightCategory": "SmallPlus",
                  "initialDistance": 15,
                  "maxDistance": 50,
                  "intervalDistance": 5,
                  "startTimeOffset": 0,
                  "maxTime": 3600,
                  "intervalTime": 180,
                  "randomizeInterval": false,
                  "randomizeWeightCategory": false
                }
              ]
            }
            """;
}
