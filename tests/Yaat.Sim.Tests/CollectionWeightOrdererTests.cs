using System.Text;
using Xunit;
using Yaat.Sim.Tests.Simulation;
using Yaat.Sim.Tests.Weather;

namespace Yaat.Sim.Tests;

public class CollectionWeightOrdererTests
{
    private const string TableResource = "Yaat.Sim.Tests.collection-weights.json";

    [Fact]
    public void HeavierCollectionsComeFirst()
    {
        Dictionary<string, int> weights = new(StringComparer.Ordinal)
        {
            ["Light"] = 5,
            ["Heavy"] = 100,
            ["Medium"] = 40,
        };

        IReadOnlyList<string> ordered = CollectionWeightOrderer.Order(["Light", "Heavy", "Medium"], key => key, weights);

        Assert.Equal(["Heavy", "Medium", "Light"], ordered);
    }

    [Fact]
    public void TiesAndUnknownCollectionsSortByKeyOrdinal()
    {
        Dictionary<string, int> weights = new(StringComparer.Ordinal)
        {
            ["alpha"] = 10,
            ["Beta"] = 10,
            ["Gamma"] = 0,
        };

        IReadOnlyList<string> ordered = CollectionWeightOrderer.Order(["Gamma", "alpha", "Beta", "Delta"], key => key, weights);

        // Ordinal, not culture: uppercase sorts ahead of lowercase. The two 10 s collections tie on
        // weight and the two unknowns tie on zero, so each pair falls back to its key.
        Assert.Equal(["Beta", "alpha", "Delta", "Gamma"], ordered);
    }

    [Fact]
    public void DefaultAndNamedCollectionsMapToTheTableTheScriptWrote()
    {
        // xunit's own generator builds a per-class collection's display name, so the assertion does
        // not restate the orderer's assumption about its format.
        string defaultCollection = CollectionAttribute.GetCollectionNameForType(typeof(CollectionWeightOrdererTests));
        Assert.Equal(typeof(CollectionWeightOrdererTests).FullName, CollectionWeightOrderer.CollectionKeyFromDisplayName(defaultCollection));
        Assert.Equal("NavDbMutator", CollectionWeightOrderer.CollectionKeyFromDisplayName("NavDbMutator"));

        IReadOnlyDictionary<string, int> weights = CollectionWeightOrderer.Weights;
        Assert.Contains("NavDbMutator", weights.Keys);
        Assert.Contains(WindGridRegistryCollection.Name, weights.Keys);
        Assert.Contains(typeof(Issue153S2Oak5GeneratorsE2ETests).FullName!, weights.Keys);

        const string unknownCollection = "test collection for X";
        IReadOnlyList<string> ordered = CollectionWeightOrderer.Order(
            [unknownCollection, "NavDbMutator", CollectionAttribute.GetCollectionNameForType(typeof(Issue153S2Oak5GeneratorsE2ETests))],
            CollectionWeightOrderer.CollectionKeyFromDisplayName,
            weights
        );

        // Both real keys come out of the committed table and beat the collection no key matches.
        Assert.Equal(unknownCollection, ordered[^1]);
    }

    [Fact]
    public void MalformedTableFailsNamingTheRefreshScript()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("""{ "collections": { "Yaat.Sim.Tests.FooTests": 12.5 } }"""));

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => CollectionWeightOrderer.ParseWeights(stream, TableResource));

        Assert.Contains(TableResource, error.Message, StringComparison.Ordinal);
        Assert.Contains("tools/refresh-sim-collection-weights.py", error.Message, StringComparison.Ordinal);
        Assert.Contains("\"collections\"", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EmbeddedTableLoadsAndIsNotEmpty()
    {
        IReadOnlyDictionary<string, int> weights = CollectionWeightOrderer.Weights;

        Assert.NotEmpty(weights);
        Assert.All(weights, entry => Assert.False(string.IsNullOrWhiteSpace(entry.Key)));
        Assert.All(weights, entry => Assert.True(entry.Value >= 0));
    }
}
