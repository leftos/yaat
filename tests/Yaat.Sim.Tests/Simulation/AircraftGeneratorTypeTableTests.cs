using Xunit;
using Xunit.Abstractions;
using Yaat.Sim;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// CI guardrail: every type listed in <see cref="AircraftGenerator.TypeTable"/> must
/// resolve through <see cref="AircraftProfileDatabase"/> and
/// <see cref="AircraftCategorization"/>, and must categorize as the engine kind
/// declared in its bucket. This prevents a regression where a developer adds a
/// non-ICAO string ("PA28" vs "P28A") or an unprofiled code that falls through to
/// jet defaults silently.
/// </summary>
public class AircraftGeneratorTypeTableTests
{
    private readonly ITestOutputHelper _output;

    public AircraftGeneratorTypeTableTests(ITestOutputHelper output)
    {
        _output = output;
        // Pin singletons before any test method body runs so that parallel test
        // classes can't observe a mid-Initialize state of AircraftProfileDatabase /
        // AircraftCategorization (see project_static_singleton_test_races in MEMORY.md).
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void EveryTypeResolvesThroughDataLookups()
    {
        if (!AircraftProfileDatabase.IsInitialized)
        {
            _output.WriteLine("AircraftProfileDatabase not initialized; skipping (test data missing)");
            return;
        }

        // AssertEveryTypeResolves throws InvalidOperationException with all problems
        // joined into the message — the test fails with the full list.
        AircraftGenerator.AssertEveryTypeResolves();
    }

    [Theory]
    [InlineData(WeightClass.Small, EngineKind.Piston)]
    [InlineData(WeightClass.Small, EngineKind.Turboprop)]
    [InlineData(WeightClass.Small, EngineKind.Jet)]
    [InlineData(WeightClass.SmallPlus, EngineKind.Turboprop)]
    [InlineData(WeightClass.SmallPlus, EngineKind.Jet)]
    [InlineData(WeightClass.Large, EngineKind.Piston)]
    [InlineData(WeightClass.Large, EngineKind.Jet)]
    [InlineData(WeightClass.Heavy, EngineKind.Jet)]
    [InlineData(WeightClass.Small, EngineKind.Helicopter)]
    public void EveryBucketHasAtLeastTwoTypes(WeightClass weight, EngineKind engine)
    {
        var pool = AircraftGenerator.GetTypesForCombo(weight, engine);
        Assert.NotNull(pool);
        Assert.True(pool!.Length >= 2, $"{weight}+{engine} has only {pool.Length} types — random pick won't feel varied");
    }

    /// <summary>
    /// Every (weight, engine) combination must yield a spawn — even buckets that
    /// have no curated entries (Heavy+Piston, Heavy+Turboprop). The fallback chain
    /// in <see cref="AircraftGenerator"/> guarantees we always pick something.
    /// </summary>
    [Fact]
    public void GenerateNeverFailsForAnyWeightEngineCombo()
    {
        if (!AircraftProfileDatabase.IsInitialized)
        {
            _output.WriteLine("AircraftProfileDatabase not initialized; skipping (test data missing)");
            return;
        }

        var rng = new Random(12345);
        foreach (var weight in Enum.GetValues<WeightClass>())
        {
            foreach (var engine in Enum.GetValues<EngineKind>())
            {
                var request = BuildBearingRequest(weight, engine);
                var (state, error) = AircraftGenerator.Generate(
                    request,
                    primaryAirportId: "KOAK",
                    existingAircraft: Array.Empty<AircraftState>(),
                    groundLayout: null,
                    rng,
                    beaconPool: new BeaconCodePool()
                );
                Assert.True(state is not null, $"{weight}+{engine}: spawn failed with '{error}'");
            }
        }
    }

    [Theory]
    [InlineData(EngineKind.Piston, AircraftCategory.Piston)]
    [InlineData(EngineKind.Turboprop, AircraftCategory.Turboprop)]
    public void HeavyFallsBackPreservingEngineCategory(EngineKind requestedEngine, AircraftCategory expectedCategory)
    {
        if (!AircraftProfileDatabase.IsInitialized)
        {
            _output.WriteLine("AircraftProfileDatabase not initialized; skipping (test data missing)");
            return;
        }

        var rng = new Random(42);
        var request = BuildBearingRequest(WeightClass.Heavy, requestedEngine);

        // Repeat several times so any randomness in bucket-pool pick is exercised.
        for (var i = 0; i < 20; i++)
        {
            var (state, error) = AircraftGenerator.Generate(
                request,
                primaryAirportId: "KOAK",
                existingAircraft: Array.Empty<AircraftState>(),
                groundLayout: null,
                rng,
                beaconPool: new BeaconCodePool()
            );
            Assert.True(state is not null, $"spawn failed: {error}");
            var category = AircraftCategorization.Categorize(state!.AircraftType);
            Assert.Equal(expectedCategory, category);
        }
    }

    [Fact]
    public void FallbackChainOrdersEngineFirstThenSize()
    {
        // Heavy+Piston: today's bucket is empty. The chain must visit other piston
        // buckets (Large+Piston, Small+Piston) before any non-piston bucket.
        var chain = AircraftGenerator.EnumerateBucketFallbackChain(WeightClass.Heavy, EngineKind.Piston).ToArray();

        Assert.Equal((WeightClass.Heavy, EngineKind.Piston), chain[0]);

        var firstNonExact = chain.Skip(1).First();
        Assert.Equal(EngineKind.Piston, firstNonExact.Engine);

        // The first non-piston bucket must appear AFTER every piston bucket.
        var firstNonPistonIndex = Array.FindIndex(chain, c => c.Engine != EngineKind.Piston);
        var lastPistonIndex = Array.FindLastIndex(chain, c => c.Engine == EngineKind.Piston);
        Assert.True(lastPistonIndex < firstNonPistonIndex, "engine priority violated: non-piston appears before all piston buckets exhausted");

        // The chain must cover every (weight, engine) combination exactly once.
        var distinct = chain.Distinct().ToArray();
        Assert.Equal(chain.Length, distinct.Length);
        Assert.Equal(Enum.GetValues<WeightClass>().Length * Enum.GetValues<EngineKind>().Length, chain.Length);
    }

    private static SpawnRequest BuildBearingRequest(WeightClass weight, EngineKind engine) =>
        new()
        {
            Rules = FlightRulesKind.Vfr,
            Weight = weight,
            Engine = engine,
            PositionType = SpawnPositionType.Bearing,
            Bearing = 90,
            DistanceNm = 5,
            Altitude = 3000,
        };
}
