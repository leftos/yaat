using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Fillet;

/// <summary>Round-9 per-node decodes (FLL 83, OAK 57, SFO 224), only-v2 OAK, J224 preserve check.</summary>
public class FilletRound9DiagnosticTests
{
    private readonly ITestOutputHelper _output;

    public FilletRound9DiagnosticTests(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData("fll", 83, 340, 83)]
    [InlineData("oak", 57, 58, 57)]
    [InlineData("sfo", 224, 830, 1682)]
    public void Round9_TargetNode_NeighborhoodAndPlanDecode(string shortId, int targetId, int probeNodeId, int gapNextNodeId)
    {
        var pre = Load(shortId);
        if (pre is null)
        {
            return;
        }

        var artifacts = FilletPlanDumpDiagnostics.TryBuild(shortId);
        Assert.NotNull(artifacts);

        _output.WriteLine(FilletReachabilityDiagnostics.FormatReachabilityDiffSummary(shortId, pre, CloneAndLegacy(pre), CloneAndV2(pre)));

        var analyses = FilletReachabilityDiagnostics.AnalyzeOnlyLegacyStableNodes(pre, CloneAndLegacy(pre), CloneAndV2(pre), maxSamples: 20);
        var targetAnalysis = analyses.FirstOrDefault(a => a.TargetStableNodeId == targetId);
        if (targetAnalysis is not null)
        {
            _output.WriteLine(
                $"target {targetId}: last-v2={targetAnalysis.LastV2ReachableNodeId} next={targetAnalysis.NextNodeId} "
                    + $"origin={targetAnalysis.LegacyEdgeOrigin ?? "?"}"
            );
        }

        _output.WriteLine(FilletPlanDumpDiagnostics.FormatProbePreFilletEdges(pre, targetId));
        _output.WriteLine(FilletPlanDumpDiagnostics.FormatProbePreFilletEdges(pre, probeNodeId));
        _output.WriteLine(FilletPlanDumpDiagnostics.FormatGapTargetNodeTypes(pre, targetId, probeNodeId, gapNextNodeId));
        _output.WriteLine(FilletPlanDumpDiagnostics.FormatPreFilletNeighborhoodTrace(artifacts, probeNodeId, maxDepth: 3));
        _output.WriteLine(FilletPlanDumpDiagnostics.FormatPreFilletNeighborhoodTrace(artifacts, targetId, maxDepth: 2));

        var gapResolution = FilletPlanDumpDiagnostics.ResolveJunctionForGapFromPreFillet(artifacts, probeNodeId, gapNextNodeId);
        _output.WriteLine(gapResolution.Report);

        int? junctionId = gapResolution.ConsumingJunctionId ?? FilletPlanDumpDiagnostics.ResolveJunctionForGap(artifacts, probeNodeId);
        if (junctionId is int jId)
        {
            _output.WriteLine(FilletPlanDumpDiagnostics.FormatJunctionDump(shortId, jId, probeNodeId, artifacts));
        }
    }

    [Fact]
    public void Oak_OnlyV2Nodes_DecodeExtraPaths()
    {
        const string shortId = "oak";
        var pre = Load(shortId);
        if (pre is null)
        {
            return;
        }

        var legacy = CloneAndLegacy(pre);
        var v2 = CloneAndV2(pre);
        var allOnlyV2 = FilletReachabilityDiagnostics.GetOnlyV2StableNodeIds(pre, legacy, v2);
        var analyses = FilletReachabilityDiagnostics.AnalyzeOnlyV2StableNodes(pre, legacy, v2, maxSamples: 10);
        _output.WriteLine(FilletReachabilityDiagnostics.FormatOnlyV2Analysis(shortId, analyses, allOnlyV2));

        var artifacts = FilletPlanDumpDiagnostics.TryBuild(shortId);
        if (artifacts is null)
        {
            return;
        }

        foreach (int nodeId in allOnlyV2)
        {
            _output.WriteLine(FilletPlanDumpDiagnostics.FormatProbePreFilletEdges(pre, nodeId));
            _output.WriteLine(FilletPlanDumpDiagnostics.FormatPreFilletNeighborhoodTrace(artifacts, nodeId, maxDepth: 2));
        }
    }

    [Fact]
    public void Sfo_J224_PreservedJunctionPresentAfterV2Apply_DecodeOnly()
    {
        const string shortId = "sfo";
        if (Load(shortId) is null)
        {
            return;
        }

        _output.WriteLine(FilletPlanDumpDiagnostics.FormatPreservedJunctionPostExecuteCheck(shortId, junctionNodeId: 224));
        _output.WriteLine(FilletPlanDumpDiagnostics.FormatJunctionDump(shortId, 224, probeNodeId: 830, FilletPlanDumpDiagnostics.TryBuild(shortId)!));
    }

    private static AirportGroundLayout CloneAndLegacy(AirportGroundLayout pre)
    {
        var legacy = LayoutCloner.DeepClone(pre);
        _ = new LegacyFilletArcGenerator().Apply(legacy);
        return legacy;
    }

    private static AirportGroundLayout CloneAndV2(AirportGroundLayout pre)
    {
        var v2 = LayoutCloner.DeepClone(pre);
        _ = new FilletArcGeneratorV2().Apply(v2);
        return v2;
    }

    private static AirportGroundLayout? Load(string shortId)
    {
        string path = Path.Combine("TestData", $"{shortId}.geojson");
        return !File.Exists(path) ? null : GeoJsonParser.Parse(shortId, File.ReadAllText(path), null, FilletMode.None);
    }
}
