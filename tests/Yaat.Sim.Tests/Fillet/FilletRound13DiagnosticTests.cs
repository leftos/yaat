using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Fillet;

/// <summary>Round-13 decode: FLL 105/106/342, SFO gate artifact 359/887, OAK arm-tail.</summary>
public class FilletRound13DiagnosticTests
{
    private readonly ITestOutputHelper _output;

    public FilletRound13DiagnosticTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Fll_PlanTrace_J107Area_105_106()
    {
        var artifacts = FilletPlanDumpDiagnostics.TryBuild("fll");
        if (artifacts is null)
        {
            return;
        }

        _output.WriteLine(FilletPlanDumpDiagnostics.FormatJunctionDump("fll", 107, probeNodeId: 105, artifacts));
        _output.WriteLine(FilletPlanDumpDiagnostics.FormatPlanNodeTrace(artifacts, 105, 106, 107));
    }

    [Fact]
    public void Fll_PlanTrace_HoldShort342()
    {
        var artifacts = FilletPlanDumpDiagnostics.TryBuild("fll");
        if (artifacts is null)
        {
            return;
        }

        _output.WriteLine(FilletPlanDumpDiagnostics.FormatPlanNodeTrace(artifacts, 342));
        _output.WriteLine(FilletPlanDumpDiagnostics.FormatProbePreFilletEdges(artifacts.PreLayout, 342));
    }

    [Fact]
    public void Sfo_Merged887_AliasReachability()
    {
        var pre = LoadPreFillet("sfo");
        if (pre is null)
        {
            return;
        }

        var v2 = LayoutCloner.DeepClone(pre);
        _ = new FilletArcGeneratorV2().Apply(v2);

        var reachable = FilletComparisonGates.ReachableStableIdsFromHoldShorts(pre, v2);
        _output.WriteLine($"359 in layout={v2.Nodes.ContainsKey(359)} reachable={reachable.Contains(359)}");
        _output.WriteLine($"887 in layout={v2.Nodes.ContainsKey(887)} reachable={reachable.Contains(887)}");
    }

    [Fact]
    public void Oak_ArmTailDirectAdjacency_J193()
    {
        var artifacts = FilletPlanDumpDiagnostics.TryBuild("oak");
        if (artifacts is null)
        {
            return;
        }

        _output.WriteLine(FilletPlanDumpDiagnostics.FormatJunctionDump("oak", 193, probeNodeId: 188, artifacts));
        _output.WriteLine(FilletPlanDumpDiagnostics.FormatPlanNodeTrace(artifacts, 188, 217, 204, 810));
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
