using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Fillet.V2;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Fillet;

/// <summary>Round-14 decode and predicate checks per claude-response.md.</summary>
public class FilletRound14DiagnosticTests
{
    private readonly ITestOutputHelper _output;

    public FilletRound14DiagnosticTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Oak_OnlyV2_DecodeAfterHoldShortCutGuard()
    {
        var pre = LoadPreFillet("oak");
        if (pre is null)
        {
            return;
        }

        _output.WriteLine($"node 188 type={pre.Nodes[188].Type}");
        var legacy = LayoutCloner.DeepClone(pre);
        _ = new LegacyFilletArcGenerator().Apply(legacy);
        var v2 = LayoutCloner.DeepClone(pre);
        _ = new FilletArcGeneratorV2().Apply(v2);

        var allOnlyV2 = FilletReachabilityDiagnostics.GetOnlyV2StableNodeIds(pre, legacy, v2);
        var analyses = FilletReachabilityDiagnostics.AnalyzeOnlyV2StableNodes(pre, legacy, v2, maxSamples: 8);
        _output.WriteLine(FilletReachabilityDiagnostics.FormatOnlyV2Analysis("oak", analyses, allOnlyV2));

        var armTail188 = v2
            .Edges.OfType<GroundEdge>()
            .FirstOrDefault(e => ((e.Nodes[0].Id == 188) && (e.Nodes[1].Id == 810)) || ((e.Nodes[0].Id == 810) && (e.Nodes[1].Id == 188)));
        _output.WriteLine($"edge 188-810 present: {armTail188 is not null} origin={armTail188?.Origin ?? "none"}");
    }

    [Theory]
    [InlineData(105)]
    [InlineData(106)]
    public void Fll_NamedTaxiwayIntersection_ClassifiedPreserve(int junctionId)
    {
        var artifacts = FilletPlanDumpDiagnostics.TryBuild("fll");
        if (artifacts is null)
        {
            return;
        }

        var jp = artifacts.JunctionPlans.FirstOrDefault(j => j.JunctionNodeId == junctionId);
        if (jp is null)
        {
            _output.WriteLine($"J{junctionId} not in active fillet set");
            return;
        }

        var taxiways = jp.Arms.Select(a => a.TaxiwayName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _output.WriteLine(
            $"J{junctionId}: Kind={jp.Kind} PreserveNode={jp.PreserveNode} taxiways=[{string.Join(", ", taxiways)}] "
                + $"inRemoveSet={artifacts.JunctionNodesToRemove.Contains(junctionId)}"
        );
    }

    [Fact]
    public void Fll_HoldShort342_HasEdgesAfterApply()
    {
        var pre = LoadPreFillet("fll");
        if (pre is null)
        {
            return;
        }

        var v2 = LayoutCloner.DeepClone(pre);
        _ = new FilletArcGeneratorV2().Apply(v2);

        if (!v2.Nodes.TryGetValue(342, out var hs))
        {
            _output.WriteLine("node 342 missing");
            return;
        }

        _output.WriteLine($"node 342 edgeCount={hs.Edges.Count}");
        foreach (var edge in hs.Edges.OfType<GroundEdge>())
        {
            int other = edge.OtherNodeId(342);
            _output.WriteLine($"  342->{other} twy={edge.TaxiwayName} origin={edge.Origin ?? "?"}");
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
}
