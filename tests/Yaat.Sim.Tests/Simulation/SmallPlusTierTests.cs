using Xunit;
using Xunit.Abstractions;
using Yaat.Sim;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// The arrival-generator weight tiers map to RECAT CWT category: <c>Small</c> = CWT I (lower
/// small), <c>SmallPlus</c> = CWT H (upper small). The SmallPlus jet pool is upper-small business
/// jets (Citation Excel/XLS/Sovereign, Learjet 60/45); regional jets (CWT G) live in
/// <c>Large</c> alongside the mainline narrow-bodies, so they only feed long runways. The
/// SmallPlus turboprop pool keeps the commuter turboprops (AT72/DH8C/SF34, CWT G) because their
/// lower landing energy and superior low-speed deceleration keep them short-field appropriate
/// (e.g. OAK 28R, 5,448 ft) — even SF34, whose approach speed alone matches the regional jets.
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
    public void SmallPlusJet_IsUpperSmallBizjets_AllCwtH_NoRegionals()
    {
        var pool = AircraftGenerator.GetTypesForCombo(WeightClass.SmallPlus, EngineKind.Jet);
        Assert.NotNull(pool);

        foreach (var bizjet in new[] { "C560", "C56X", "C680", "LJ60", "LJ45" })
        {
            Assert.Contains(bizjet, pool!);
        }

        // Regional jets are CWT G — they moved to Large. Mainline narrow-bodies stay in Large too.
        foreach (var excluded in new[] { "CRJ7", "CRJ9", "E170", "E75L", "E145", "E135", "A320", "B738" })
        {
            Assert.DoesNotContain(excluded, pool!);
        }

        // Every member is CWT H (upper small).
        foreach (var type in pool!)
        {
            Assert.Equal("H", WakeTurbulenceData.GetCwt(type));
        }
    }

    [Fact]
    public void SmallJet_IsLightBizjets_AllCwtI()
    {
        var pool = AircraftGenerator.GetTypesForCombo(WeightClass.Small, EngineKind.Jet);
        Assert.NotNull(pool);

        // C560 is CWT H — it moved up to SmallPlus; the Small jet pool is CWT I only.
        Assert.DoesNotContain("C560", pool!);
        foreach (var type in pool!)
        {
            Assert.Equal("I", WakeTurbulenceData.GetCwt(type));
        }
    }

    [Fact]
    public void LargeJet_IncludesMainlineAndRegionals()
    {
        var pool = AircraftGenerator.GetTypesForCombo(WeightClass.Large, EngineKind.Jet);
        Assert.NotNull(pool);

        foreach (var mainline in new[] { "B737", "B738", "B739", "A319", "A320", "A321" })
        {
            Assert.Contains(mainline, pool!);
        }

        foreach (var regional in new[] { "CRJ7", "CRJ9", "E170", "E75L", "E145", "E135" })
        {
            Assert.Contains(regional, pool!);
        }
    }

    [Fact]
    public void SmallPlusTurboprop_KeepsLowFasCommuters_AndLargeTurbopropBucketIsGone()
    {
        var pool = AircraftGenerator.GetTypesForCombo(WeightClass.SmallPlus, EngineKind.Turboprop);
        Assert.NotNull(pool);
        foreach (var t in new[] { "AT72", "DH8C", "SF34", "B190", "B350" })
        {
            Assert.Contains(t, pool!);
        }

        // Regional turboprops stay in SmallPlus (low FAS), so no Large turboprop bucket exists.
        Assert.Null(AircraftGenerator.GetTypesForCombo(WeightClass.Large, EngineKind.Turboprop));
    }

    [Fact]
    public void EveryJetPoolType_ResolvesToACwtCategory()
    {
        // Guards the E175-vs-E75L class of bug: AssertEveryTypeResolves checks profile + engine
        // category but not CWT, so a non-resolvable designator would silently be wake-unknown.
        foreach (var weight in new[] { WeightClass.Small, WeightClass.SmallPlus, WeightClass.Large, WeightClass.Heavy })
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
        var (state, error) = AircraftGenerator.Generate(request, "KOAK", Array.Empty<AircraftState>(), groundLayout: null, rng, new BeaconCodePool());
        Assert.True(state is not null, $"SmallPlus+Piston spawn failed: {error}");
        Assert.Equal(AircraftCategory.Piston, AircraftCategorization.Categorize(state!.AircraftType));
    }

    [Fact]
    public void SmallPlusGenerator_RandomizeOff_SpawnsOnlyUpperSmallBizjets()
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

        var bizjetPool = AircraftGenerator.GetTypesForCombo(WeightClass.SmallPlus, EngineKind.Jet)!;
        var spawnedTypes = engine.World.GetSnapshot().Select(a => AircraftState.StripTypePrefix(a.AircraftType)).Distinct().ToList();
        Assert.NotEmpty(spawnedTypes);
        foreach (var type in spawnedTypes)
        {
            _output.WriteLine($"spawned {type}");
            Assert.True(
                bizjetPool.Contains(type),
                $"SmallPlus generator spawned '{type}', which is not in the upper-small bizjet pool [{string.Join(", ", bizjetPool)}]"
            );
            Assert.Equal("H", WakeTurbulenceData.GetCwt(type));
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
