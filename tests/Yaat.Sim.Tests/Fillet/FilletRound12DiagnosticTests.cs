using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Fillet;

/// <summary>Round-12 decode-first tests (only-v2 arm-tail, FLL node loss, SFO stable-stable).</summary>
public class FilletRound12DiagnosticTests
{
    private readonly ITestOutputHelper _output;

    public FilletRound12DiagnosticTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Oak_OnlyV2_DecodeArmTailAtJ193()
    {
        var pre = LoadPreFillet("oak");
        if (pre is null)
        {
            return;
        }

        var legacy = LayoutCloner.DeepClone(pre);
        _ = new LegacyFilletArcGenerator().Apply(legacy);
        var v2 = LayoutCloner.DeepClone(pre);
        _ = new FilletArcGeneratorV2().Apply(v2);

        var analyses = FilletReachabilityDiagnostics.AnalyzeOnlyV2StableNodes(pre, legacy, v2, maxSamples: 8);
        _output.WriteLine(FilletReachabilityDiagnostics.FormatOnlyV2Analysis("oak", analyses, [217, 204, 222]));

        foreach (int id in new[] { 188, 810, 217 })
        {
            _output.WriteLine($"node {id}: pre={NodeSummary(pre, id)} legacy={NodeSummary(legacy, id)} v2={NodeSummary(v2, id)}");
        }
    }

    [Theory]
    [InlineData(342)]
    [InlineData(357)]
    [InlineData(501)]
    [InlineData(468)]
    [InlineData(105)]
    [InlineData(106)]
    public void Fll_NodeEdgesAcrossModes(int nodeId)
    {
        if (!File.Exists(Path.Combine("TestData", "fll.geojson")))
        {
            return;
        }

        string geo = File.ReadAllText(Path.Combine("TestData", "fll.geojson"));
        var none = GeoJsonParser.Parse("fll", geo, null, FilletMode.None);
        var legacy = GeoJsonParser.Parse("fll", geo, null, FilletMode.Legacy);
        var v2 = GeoJsonParser.Parse("fll", geo, null, FilletMode.V2);

        _output.WriteLine($"node {nodeId}:");
        _output.WriteLine($"  None:   {NodeSummary(none, nodeId)}");
        _output.WriteLine($"  Legacy: {NodeSummary(legacy, nodeId)}");
        _output.WriteLine($"  V2:     {NodeSummary(v2, nodeId)}");
    }

    [Fact]
    public void Sfo_Coincident359and887_Decode()
    {
        var pre = LoadPreFillet("sfo");
        if (pre is null)
        {
            return;
        }

        _output.WriteLine($"pre-fillet 359: {NodeSummary(pre, 359)}");
        _output.WriteLine($"pre-fillet 887: {NodeSummary(pre, 887)}");
        if (pre.Nodes.TryGetValue(359, out var n359) && pre.Nodes.TryGetValue(887, out var n887))
        {
            double distFt = GeoMath.DistanceNm(n359.Position, n887.Position) * GeoMath.FeetPerNm;
            _output.WriteLine($"distance 359-887: {distFt:F2} ft");
        }

        var v2 = LayoutCloner.DeepClone(pre);
        _ = new FilletArcGeneratorV2().Apply(v2);
        _output.WriteLine($"post-v2 359: {NodeSummary(v2, 359)}");
        _output.WriteLine($"post-v2 887: {NodeSummary(v2, 887)}");

        var edge359_887 = v2
            .Edges.OfType<GroundEdge>()
            .FirstOrDefault(e => ((e.Nodes[0].Id == 359) && (e.Nodes[1].Id == 887)) || ((e.Nodes[0].Id == 887) && (e.Nodes[1].Id == 359)));
        _output.WriteLine($"V2 edge 359-887: {edge359_887?.Origin ?? "none"}");
    }

    private static AirportGroundLayout? LoadPreFillet(string shortId)
    {
        string path = Path.Combine("TestData", $"{shortId}.geojson");
        if (!File.Exists(path))
        {
            return null;
        }

        return GeoJsonParser.Parse(shortId, File.ReadAllText(path), null, FilletMode.None);
    }

    private static string NodeSummary(AirportGroundLayout layout, int nodeId)
    {
        if (!layout.Nodes.TryGetValue(nodeId, out var node))
        {
            return "missing";
        }

        var edges = layout
            .Edges.OfType<GroundEdge>()
            .Where(e => (e.Nodes[0].Id == nodeId) || (e.Nodes[1].Id == nodeId))
            .Select(e =>
            {
                int other = e.Nodes[0].Id == nodeId ? e.Nodes[1].Id : e.Nodes[0].Id;
                return $"{other}({e.TaxiwayName}) {e.Origin ?? "?"}";
            });
        return $"{node.Type} origin={node.Origin ?? "?"} edges=[{string.Join("; ", edges)}]";
    }
}
