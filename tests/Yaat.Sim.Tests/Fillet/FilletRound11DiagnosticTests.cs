using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Fillet;

/// <summary>Round-11 decode: stable-anchor structural gate, only-v2 side branches, FLL parser probe.</summary>
public class FilletRound11DiagnosticTests
{
    private readonly ITestOutputHelper _output;

    public FilletRound11DiagnosticTests(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData("oak")]
    [InlineData("sfo")]
    [InlineData("fll")]
    public void Round11_ReachabilitySummary_AfterStableAnchor(string shortId)
    {
        var pre = LoadPreFillet(shortId);
        if (pre is null)
        {
            return;
        }

        var legacy = LayoutCloner.DeepClone(pre);
        _ = new LegacyFilletArcGenerator().Apply(legacy);
        var v2 = LayoutCloner.DeepClone(pre);
        _ = new FilletArcGeneratorV2().Apply(v2);

        _output.WriteLine(FilletReachabilityDiagnostics.FormatReachabilityDiffSummary(shortId, pre, legacy, v2));
    }

    [Theory]
    [InlineData("oak")]
    [InlineData("sfo")]
    public void Round11_DecodeOnlyV2StableNodes(string shortId)
    {
        var pre = LoadPreFillet(shortId);
        if (pre is null)
        {
            return;
        }

        var legacy = LayoutCloner.DeepClone(pre);
        _ = new LegacyFilletArcGenerator().Apply(legacy);
        var v2 = LayoutCloner.DeepClone(pre);
        _ = new FilletArcGeneratorV2().Apply(v2);

        var allOnlyV2 = FilletReachabilityDiagnostics.GetOnlyV2StableNodeIds(pre, legacy, v2);
        var analyses = FilletReachabilityDiagnostics.AnalyzeOnlyV2StableNodes(pre, legacy, v2, maxSamples: 8);
        _output.WriteLine(FilletReachabilityDiagnostics.FormatOnlyV2Analysis(shortId, analyses, allOnlyV2));

        var sideBranchOrigins = v2
            .Edges.OfType<GroundEdge>()
            .Where(e => e.Origin?.Contains("V2:shorten", StringComparison.Ordinal) == true)
            .Select(e => $"{e.Nodes[0].Id}->{e.Nodes[1].Id} {e.TaxiwayName} {e.Origin}")
            .Take(20);
        _output.WriteLine($"sample V2:shorten edges: {string.Join("; ", sideBranchOrigins)}");
    }

    [Fact]
    public void Fll_ProbeParserEdgesAt105And106_NoneVsLegacyVsV2PreFillet()
    {
        const string shortId = "fll";
        if (!File.Exists(Path.Combine("TestData", "fll.geojson")))
        {
            return;
        }

        string geo = File.ReadAllText(Path.Combine("TestData", "fll.geojson"));
        var none = GeoJsonParser.Parse(shortId, geo, null, FilletMode.None);
        var legacyPre = GeoJsonParser.Parse(shortId, geo, null, FilletMode.Legacy);
        var v2Pre = GeoJsonParser.Parse(shortId, geo, null, FilletMode.V2);

        foreach (int nodeId in new[] { 105, 106, 83, 782, 340 })
        {
            _output.WriteLine($"--- node {nodeId} ---");
            _output.WriteLine($"None:   {FormatIncident(none, nodeId)}");
            _output.WriteLine($"Legacy: {FormatIncident(legacyPre, nodeId)}");
            _output.WriteLine($"V2:     {FormatIncident(v2Pre, nodeId)}");
        }
    }

    [Theory]
    [InlineData("oak", 600)]
    [InlineData("sfo", 1043)]
    [InlineData("fll", 105)]
    [InlineData("fll", 106)]
    public void Round11_DecodeOnlyLegacyStableNode(string shortId, int targetId)
    {
        var pre = LoadPreFillet(shortId);
        if (pre is null)
        {
            return;
        }

        var legacy = LayoutCloner.DeepClone(pre);
        _ = new LegacyFilletArcGenerator().Apply(legacy);
        var v2 = LayoutCloner.DeepClone(pre);
        _ = new FilletArcGeneratorV2().Apply(v2);

        var analyses = FilletReachabilityDiagnostics.AnalyzeOnlyLegacyStableNodes(pre, legacy, v2, maxSamples: 4);
        var match = analyses.FirstOrDefault(a => a.TargetStableNodeId == targetId);
        if (match is not null)
        {
            _output.WriteLine(FilletReachabilityDiagnostics.FormatAnalysis(shortId, [match]));
        }
        else
        {
            _output.WriteLine($"{shortId} target {targetId}: not in only-legacy set (gate may have passed)");
        }
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

    private static string FormatIncident(AirportGroundLayout layout, int nodeId)
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
                return $"{other}({e.TaxiwayName}) origin={e.Origin ?? "?"}";
            })
            .OrderBy(s => s);
        return $"{node.Type} edges=[{string.Join(", ", edges)}]";
    }
}
