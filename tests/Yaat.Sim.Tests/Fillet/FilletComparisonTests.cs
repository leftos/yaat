using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Fillet;

public class FilletComparisonTests
{
    private readonly ITestOutputHelper _output;

    public FilletComparisonTests(ITestOutputHelper output) => _output = output;

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

    [Fact]
    public void Compare_Legacy_MatchesDirectApply()
    {
        var preFillet = LoadPreFilletLayout("oak");
        if (preFillet is null)
        {
            return;
        }

        var direct = LayoutCloner.DeepClone(preFillet);
        var legacy = new LegacyFilletArcGenerator();
        var directStats = legacy.Apply(direct);

        var report = FilletComparison.Compare(preFillet, [legacy]);
        var run = Assert.Single(report.Runs);
        Assert.Equal(directStats, run.Stats);
        Assert.Equal(direct.Arcs.Count, run.ArcCount);
        Assert.Equal(direct.Nodes.Count, run.NodeCount);
        Assert.True(report.ConnectivityMatch);
    }

    [Theory]
    [InlineData("oak")]
    [InlineData("sfo")]
    public void Compare_Legacy_ProducesFillets(string shortId)
    {
        var preFillet = LoadPreFilletLayout(shortId);
        if (preFillet is null)
        {
            return;
        }

        var report = FilletComparison.Compare(preFillet, [new LegacyFilletArcGenerator()]);
        var run = Assert.Single(report.Runs);
        Assert.True(run.ArcCount > 0);
        Assert.True(run.Stats.ArcsCreated > 0);
        Assert.Equal(0, report.ArcCountDelta);
    }

    [Theory]
    [InlineData("oak")]
    [InlineData("sfo")]
    [InlineData("fll")]
    public void Compare_LegacyVsV2_MeetsHardGates(string shortId)
    {
        var preFillet = LoadPreFilletLayout(shortId);
        if (preFillet is null)
        {
            return;
        }

        var report = FilletComparison.Compare(preFillet, [new LegacyFilletArcGenerator(), new FilletArcGeneratorV2()]);
        _output.WriteLine(FilletComparison.FormatReport(report));

        Assert.True(FilletComparison.V2MeetsHardGates(report));
        var v2 = report.Runs.First(r => r.GeneratorId == "v2");
        Assert.True(v2.ArcCount > 0);
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
