using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport.Fillet.V2;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Fillet;

/// <summary>Round-5 per-junction plan dump — names chain vs reconnect vs coincident-merge fix.</summary>
public class FilletPlanDumpDiagnosticTests
{
    private readonly ITestOutputHelper _output;

    public FilletPlanDumpDiagnosticTests(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData("oak", 172, 173, 172)]
    [InlineData("fll", 215, 356, 1527)]
    [InlineData("sfo", 224, 830, 1682)]
    public void NamedGapJunction_PlanSliceDump(string shortId, int junctionNodeId, int probeNodeId, int gapNextNodeId)
    {
        var artifacts = FilletPlanDumpDiagnostics.TryBuild(shortId);
        if (artifacts is null)
        {
            return;
        }

        int safetyNetCount = artifacts.Plan.Warnings.Count(w => w.Code == PlanWarning.UnconsumedReconnectSafetyNet);
        _output.WriteLine($"{shortId} UNCONSUMED_RECONNECT_SAFETY_NET warnings (airport total): {safetyNetCount}");

        var gapResolution = FilletPlanDumpDiagnostics.ResolveJunctionForGapFromPreFillet(artifacts, probeNodeId, gapNextNodeId);
        _output.WriteLine(gapResolution.Report);

        if (!artifacts.JunctionPlans.Any(j => j.JunctionNodeId == junctionNodeId))
        {
            int? resolved = gapResolution.ConsumingJunctionId ?? FilletPlanDumpDiagnostics.ResolveJunctionForGap(artifacts, probeNodeId);
            _output.WriteLine($"  note: J{junctionNodeId} not in active fillet set; resolved consuming junction={resolved?.ToString() ?? "none"}");
            if (resolved is int jId)
            {
                junctionNodeId = jId;
            }
        }

        _output.WriteLine(FilletPlanDumpDiagnostics.FormatGapTargetNodeTypes(artifacts.PreLayout, gapNextNodeId));
        _output.WriteLine(FilletPlanDumpDiagnostics.FormatPreFilletNeighborhoodTrace(artifacts, probeNodeId, maxDepth: 2));
        _output.WriteLine(FilletPlanDumpDiagnostics.FormatJunctionDump(shortId, junctionNodeId, probeNodeId, artifacts));
    }
}
