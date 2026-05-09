using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data;
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
public class AircraftGeneratorTypeTableTests(ITestOutputHelper output)
{
    [Fact]
    public void EveryTypeResolvesThroughDataLookups()
    {
        TestVnasData.EnsureInitialized();
        if (!AircraftProfileDatabase.IsInitialized)
        {
            output.WriteLine("AircraftProfileDatabase not initialized; skipping (test data missing)");
            return;
        }

        // AssertEveryTypeResolves throws InvalidOperationException with all problems
        // joined into the message — the test fails with the full list.
        AircraftGenerator.AssertEveryTypeResolves();
    }

    [Theory]
    [InlineData(WeightClass.Small, EngineKind.Piston)]
    [InlineData(WeightClass.Small, EngineKind.Turboprop)]
    [InlineData(WeightClass.Large, EngineKind.Turboprop)]
    [InlineData(WeightClass.Large, EngineKind.Jet)]
    [InlineData(WeightClass.Heavy, EngineKind.Jet)]
    public void EveryBucketHasAtLeastTwoTypes(WeightClass weight, EngineKind engine)
    {
        var pool = AircraftGenerator.GetTypesForCombo(weight, engine);
        Assert.NotNull(pool);
        Assert.True(pool!.Length >= 2, $"{weight}+{engine} has only {pool.Length} types — random pick won't feel varied");
    }
}
