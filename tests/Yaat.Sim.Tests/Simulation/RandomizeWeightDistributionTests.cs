using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// The arrival generator's randomize-weight roll is conditioned on the generator's fixed engine type
/// (aviation-reviewed): a class with no type pool for that engine is never rolled, so a randomized
/// turboprop/piston generator can't pick a class that would only degrade through the fallback chain.
/// Testing the roll directly (not the spawned type) is deliberate — the fallback masks a bad roll at
/// the type level, so only the weight distribution reveals the bug.
/// </summary>
public class RandomizeWeightDistributionTests(ITestOutputHelper output)
{
    [Fact]
    public void Piston_AlwaysSmall()
    {
        var rng = new Random(1);
        for (int i = 0; i < 2000; i++)
        {
            Assert.Equal(WeightClass.Small, SimulationEngine.RandomWeightForEngine(EngineKind.Piston, rng));
        }
    }

    [Fact]
    public void Turboprop_OnlySmallOrSmallPlus_NeverLargeOrHeavy()
    {
        var rng = new Random(2);
        var counts = new Dictionary<WeightClass, int>();
        for (int i = 0; i < 4000; i++)
        {
            var w = SimulationEngine.RandomWeightForEngine(EngineKind.Turboprop, rng);
            counts[w] = counts.GetValueOrDefault(w) + 1;
            Assert.True(w is WeightClass.Small or WeightClass.SmallPlus, $"turboprop rolled {w}, which has no turboprop pool");
        }
        // Both reachable classes must actually appear.
        Assert.True(counts.GetValueOrDefault(WeightClass.Small) > 0);
        Assert.True(counts.GetValueOrDefault(WeightClass.SmallPlus) > 0);
        // SmallPlus (commuter) dominates the turboprop mix.
        Assert.True(counts[WeightClass.SmallPlus] > counts[WeightClass.Small]);
    }

    [Fact]
    public void Jet_ProducesAllFourClasses_WeightedTowardMainline()
    {
        var rng = new Random(3);
        const int n = 20000;
        var counts = new Dictionary<WeightClass, int>();
        for (int i = 0; i < n; i++)
        {
            var w = SimulationEngine.RandomWeightForEngine(EngineKind.Jet, rng);
            counts[w] = counts.GetValueOrDefault(w) + 1;
        }
        foreach (var (w, c) in counts.OrderBy(kv => kv.Key))
        {
            output.WriteLine($"{w}: {c / (double)n:P1}");
        }

        // All four classes are reachable for a jet generator.
        foreach (var w in new[] { WeightClass.Small, WeightClass.SmallPlus, WeightClass.Large, WeightClass.Heavy })
        {
            Assert.True(counts.GetValueOrDefault(w) > 0, $"jet randomize never produced {w}");
        }

        double Frac(WeightClass w) => counts.GetValueOrDefault(w) / (double)n;

        // Target split 3/32/55/10 (Small/SmallPlus/Large/Heavy) — assert each within a generous band.
        Assert.InRange(Frac(WeightClass.Small), 0.01, 0.06);
        Assert.InRange(Frac(WeightClass.SmallPlus), 0.27, 0.37);
        Assert.InRange(Frac(WeightClass.Large), 0.50, 0.60);
        Assert.InRange(Frac(WeightClass.Heavy), 0.06, 0.14);

        // Mainline narrow-body is the plurality.
        Assert.True(counts[WeightClass.Large] > counts[WeightClass.SmallPlus]);
        Assert.True(counts[WeightClass.SmallPlus] > counts[WeightClass.Heavy]);
    }
}
