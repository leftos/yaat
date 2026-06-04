using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// The arrival generator's randomize-weight roll is bounded to a band around the generator's configured
/// base weight (Small/SmallPlus → {Small, SmallPlus}; Large → {SmallPlus, Large, Heavy}; Heavy →
/// {Large, Heavy}) and then intersected with the classes that have a real type pool for the generator's
/// fixed engine. Bounding keeps a short-runway generator from spawning a mainline jet; the engine
/// intersection keeps a turboprop/piston generator from rolling a class that has no pool. Testing the
/// roll directly (not the spawned type) is deliberate — the fallback masks a bad roll at the type level,
/// so only the weight distribution reveals the bug.
/// </summary>
public class RandomizeWeightDistributionTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(WeightClass.Small)]
    [InlineData(WeightClass.SmallPlus)]
    [InlineData(WeightClass.Large)]
    [InlineData(WeightClass.Heavy)]
    public void Piston_OnlyRollsClassesWithAPistonPool(WeightClass baseWeight)
    {
        // Piston pools exist only for Small (light singles) and Large (twins) — nothing between. Whatever
        // the base, the roll must land on one of those, never SmallPlus/Heavy (which have no piston pool).
        var rng = new Random(1);
        for (int i = 0; i < 2000; i++)
        {
            var w = SimulationEngine.RandomWeightForEngine(EngineKind.Piston, baseWeight, rng);
            Assert.True(w is WeightClass.Small or WeightClass.Large, $"piston base {baseWeight} rolled {w}, which has no piston pool");
        }
    }

    [Fact]
    public void Piston_SmallBase_AlwaysSmall_LargeBase_AlwaysLarge()
    {
        var rng = new Random(7);
        for (int i = 0; i < 1000; i++)
        {
            Assert.Equal(WeightClass.Small, SimulationEngine.RandomWeightForEngine(EngineKind.Piston, WeightClass.Small, rng));
            Assert.Equal(WeightClass.Large, SimulationEngine.RandomWeightForEngine(EngineKind.Piston, WeightClass.Large, rng));
        }
    }

    [Theory]
    [InlineData(WeightClass.Small)]
    [InlineData(WeightClass.SmallPlus)]
    [InlineData(WeightClass.Large)]
    [InlineData(WeightClass.Heavy)]
    public void Turboprop_NeverLargeOrHeavy(WeightClass baseWeight)
    {
        // No Large/Heavy turboprop pool exists, so a turboprop generator must never roll those regardless
        // of base — the band is intersected down to the turboprop-available classes {Small, SmallPlus}.
        var rng = new Random(2);
        for (int i = 0; i < 2000; i++)
        {
            var w = SimulationEngine.RandomWeightForEngine(EngineKind.Turboprop, baseWeight, rng);
            Assert.True(w is WeightClass.Small or WeightClass.SmallPlus, $"turboprop base {baseWeight} rolled {w}, which has no turboprop pool");
        }
    }

    [Fact]
    public void Turboprop_SmallBand_ProducesBothLightAndCommuter()
    {
        var rng = new Random(2);
        var counts = new Dictionary<WeightClass, int>();
        for (int i = 0; i < 4000; i++)
        {
            var w = SimulationEngine.RandomWeightForEngine(EngineKind.Turboprop, WeightClass.SmallPlus, rng);
            counts[w] = counts.GetValueOrDefault(w) + 1;
        }
        Assert.True(counts.GetValueOrDefault(WeightClass.Small) > 0, "no light turboprop produced");
        Assert.True(counts.GetValueOrDefault(WeightClass.SmallPlus) > 0, "no commuter turboprop produced");
        // SmallPlus (commuter) is the configured base, so it carries the plurality.
        Assert.True(counts[WeightClass.SmallPlus] > counts[WeightClass.Small]);
    }

    [Theory]
    [InlineData(WeightClass.Small)]
    [InlineData(WeightClass.SmallPlus)]
    public void Jet_LightBase_OnlySmallAndSmallPlus_NeverMainline(WeightClass baseWeight)
    {
        // The whole point of the change: a Small/SmallPlus jet generator (short runway) must never roll a
        // mainline narrow-body or widebody.
        var rng = new Random(3);
        var counts = new Dictionary<WeightClass, int>();
        for (int i = 0; i < 8000; i++)
        {
            var w = SimulationEngine.RandomWeightForEngine(EngineKind.Jet, baseWeight, rng);
            counts[w] = counts.GetValueOrDefault(w) + 1;
            Assert.True(w is WeightClass.Small or WeightClass.SmallPlus, $"light jet base {baseWeight} rolled {w}");
        }
        // Both reachable classes appear, including the Small (general-aviation bizjet) share.
        Assert.True(counts.GetValueOrDefault(WeightClass.Small) > 0, "light jet band never produced Small (GA bizjet)");
        Assert.True(counts.GetValueOrDefault(WeightClass.SmallPlus) > 0);
    }

    [Fact]
    public void Jet_LargeBase_RollsRegionalToHeavy_PluralityLarge_NeverGa()
    {
        var rng = new Random(4);
        const int n = 20000;
        var counts = new Dictionary<WeightClass, int>();
        for (int i = 0; i < n; i++)
        {
            var w = SimulationEngine.RandomWeightForEngine(EngineKind.Jet, WeightClass.Large, rng);
            counts[w] = counts.GetValueOrDefault(w) + 1;
        }
        foreach (var (w, c) in counts.OrderBy(kv => kv.Key))
        {
            output.WriteLine($"{w}: {c / (double)n:P1}");
        }

        // A mainline generator never drops to the light-GA class and never exceeds the band.
        Assert.Equal(0, counts.GetValueOrDefault(WeightClass.Small));
        foreach (var w in new[] { WeightClass.SmallPlus, WeightClass.Large, WeightClass.Heavy })
        {
            Assert.True(counts.GetValueOrDefault(w) > 0, $"large-base jet never produced {w}");
        }
        // Mainline narrow-body (the configured base) is the plurality.
        Assert.True(counts[WeightClass.Large] > counts[WeightClass.SmallPlus]);
        Assert.True(counts[WeightClass.Large] > counts[WeightClass.Heavy]);
    }

    [Fact]
    public void Jet_HeavyBase_OnlyLargeAndHeavy_PluralityHeavy()
    {
        var rng = new Random(5);
        const int n = 20000;
        var counts = new Dictionary<WeightClass, int>();
        for (int i = 0; i < n; i++)
        {
            var w = SimulationEngine.RandomWeightForEngine(EngineKind.Jet, WeightClass.Heavy, rng);
            counts[w] = counts.GetValueOrDefault(w) + 1;
            Assert.True(w is WeightClass.Large or WeightClass.Heavy, $"heavy-base jet rolled {w}");
        }
        Assert.True(counts.GetValueOrDefault(WeightClass.Large) > 0);
        Assert.True(counts.GetValueOrDefault(WeightClass.Heavy) > 0);
        // Heavy (the configured base) carries the plurality.
        Assert.True(counts[WeightClass.Heavy] > counts[WeightClass.Large]);
    }
}
