using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Fillet;

public class FilletComparisonTests
{
    private const string TestDataDir = "TestData";

    [Fact]
    public void LayoutCloner_DeepClone_PreservesPreFilletStructure()
    {
        var source = LoadPreFilletLayout("oak");
        if (source is null)
        {
            return;
        }

        var clone = LayoutCloner.DeepClone(source);
        Assert.Equal(source.Nodes.Count, clone.Nodes.Count);
        Assert.Equal(source.Edges.Count, clone.Edges.Count);
        Assert.Empty(clone.Arcs);
    }

    private static AirportGroundLayout? LoadPreFilletLayout(string shortId)
    {
        string path = Path.Combine(TestDataDir, $"{shortId}.geojson");
        if (!File.Exists(path))
        {
            return null;
        }

        return GeoJsonParser.Parse(shortId, File.ReadAllText(path), null, FilletMode.None);
    }
}
