using Microsoft.Extensions.Logging;
using Yaat.Sim.Data.Airport.Fillet;

namespace Yaat.Sim.Data.Airport;

/// <summary>Plan-then-execute fillet generator.</summary>
public sealed class FilletArcGenerator : IFilletArcGenerator
{
    private static readonly ILogger Log = SimLog.CreateLogger("FilletArcGenerator");

    public string Id => "standard";

    public string DisplayName => "Standard (plan-then-execute)";

    /// <summary>
    /// Fillets every eligible corner of <paramref name="layout"/> and reports each corner's outcome and how each of
    /// <paramref name="overrides"/> matched the pre-fillet layout (<see cref="FilletStatistics.Report"/>); the overrides do
    /// not change the result.
    /// </summary>
    public FilletStatistics Apply(AirportGroundLayout layout, CornerFilletOverrides overrides)
    {
        HashSet<int> manualArcNodes = ManualArcDetector.Detect(layout);
        int maxNodeId = layout.Nodes.Keys.DefaultIfEmpty(0).Max();
        var idCounter = new FilletPlanExecutor.NextNodeIdCounter { Next = maxNodeId + 1 };

        layout.RebuildAdjacencyLists();
        IReadOnlyList<FilletOverrideReport> overrideRows = FilletOverrideResolver.Resolve(layout, manualArcNodes, overrides);
        var report = new FilletReportCollector();

        var junctionPlans = new List<JunctionPlan>();
        var cutResults = new List<ArmCutResolver.JunctionCutResult>();

        // Start cut IDs well above the maximum possible node ID (both pre-existing and new
        // tangent-cut nodes) so CutId values never collide with node IDs. Pre-existing nodes are
        // 0..maxNodeId; new tangent-cut nodes start at maxNodeId+1 and there are at most one
        // per cut, so the largest new-node ID is ~maxNodeId + maxCuts. Using a 1_000_000
        // offset guarantees no overlap for any airport that fits in int32. The CutId type wrapper
        // enforces this namespace separation at compile time.
        var nextCutId = new CutId(maxNodeId + 1_000_000);

        foreach (GroundNode? node in layout.Nodes.Values.OrderBy(n => n.Id))
        {
            if (manualArcNodes.Contains(node.Id))
            {
                report.JunctionSkipped(node, FilletSkipReason.ShapePoint);
                continue;
            }

            if (!FilletEligibility.IsEligible(node, out bool preserve))
            {
                report.JunctionSkipped(node, FilletSkipReason.Ineligible);
                continue;
            }

            if (!layout.Nodes.TryGetValue(node.Id, out GroundNode? current) || (current.Edges.Count < 2))
            {
                continue;
            }

            JunctionPlan junction = JunctionClassifier.Classify(current, preserve, manualArcNodes, report);
            if (junction.Kind == JunctionKind.Skip)
            {
                report.JunctionSkipped(current, FilletSkipReason.JunctionSkipped);
                continue;
            }

            ArmCutResolver.JunctionCutResult cutResult = ArmCutResolver.Resolve(junction, ref nextCutId, report);
            if ((cutResult.CornerArcs.Count == 0) && (junction.CollinearPairs.Count == 0))
            {
                report.JunctionSkipped(current, FilletSkipReason.NoCornerPairs);
                continue;
            }

            junctionPlans.Add(junction);
            cutResults.Add(cutResult);
        }

        FilletPlan plan = FilletPlanBuilder.Build(layout, junctionPlans, cutResults, report);
        FilletPlanExecutor.ExecuteResult exec = FilletPlanExecutor.Execute(layout, plan, junctionPlans, idCounter, report);
        int structuralCleanups = FilletGraphNormalizer.Normalize(layout);

        var stats = new FilletStatistics(
            FilletedNodes: exec.FilletedNodes,
            ArcsCreated: exec.ArcsCreated,
            CollinearMerges: exec.CollinearMerges,
            CoincidentNodesMerged: 0,
            OrphansRescued: 0,
            RedundantPreserveEdgesRemoved: 0,
            DuplicateCornerArcsRemoved: 0,
            ParallelBypassEdgesRemoved: 0,
            DirectShortensAdded: 0
        )
        {
            Warnings = plan.Warnings,
            Report = report.Build(plan.Warnings, overrideRows),
        };

        if (Log.IsEnabled(LogLevel.Information))
        {
            Log.LogInformation(
                "Fillet: {Nodes} filleted, {Arcs} arcs, {Merged} collinear, {Cleanups} structural cleanups, {Warnings} warnings",
                stats.FilletedNodes,
                stats.ArcsCreated,
                stats.CollinearMerges,
                structuralCleanups,
                stats.Warnings.Count
            );
        }

        return stats;
    }
}
