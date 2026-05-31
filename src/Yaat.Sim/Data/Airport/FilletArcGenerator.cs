using Microsoft.Extensions.Logging;
using Yaat.Sim.Data.Airport.Fillet;

namespace Yaat.Sim.Data.Airport;

/// <summary>Plan-then-execute fillet generator (V2).</summary>
public sealed class FilletArcGenerator : IFilletArcGenerator
{
    private static readonly ILogger Log = SimLog.CreateLogger("FilletArcGenerator");

    public string Id => "standard";

    public string DisplayName => "Standard (plan-then-execute)";

    public FilletStatistics Apply(AirportGroundLayout layout)
    {
        var manualArcNodes = ManualArcDetector.Detect(layout);
        int maxNodeId = layout.Nodes.Keys.DefaultIfEmpty(0).Max();
        var idCounter = new FilletPlanExecutor.NextNodeIdCounter { Next = maxNodeId + 1 };

        layout.RebuildAdjacencyLists();

        var junctionPlans = new List<JunctionPlan>();
        var cutResults = new List<ArmCutResolver.JunctionCutResult>();

        // Start cut IDs well above the maximum possible node ID (both pre-existing and new
        // tangent-cut nodes) so CutId values never collide with node IDs. Pre-existing nodes are
        // 0..maxNodeId; new tangent-cut nodes start at maxNodeId+1 and there are at most one
        // per cut, so the largest new-node ID is ~maxNodeId + maxCuts. Using a 1_000_000
        // offset guarantees no overlap for any airport that fits in int32. The CutId type wrapper
        // enforces this namespace separation at compile time.
        var nextCutId = new CutId(maxNodeId + 1_000_000);

        foreach (var node in layout.Nodes.Values.OrderBy(n => n.Id))
        {
            if (manualArcNodes.Contains(node.Id))
            {
                continue;
            }

            if (!FilletEligibility.IsEligible(node, out bool preserve))
            {
                continue;
            }

            if (!layout.Nodes.TryGetValue(node.Id, out var current) || (current.Edges.Count < 2))
            {
                continue;
            }

            var junction = JunctionClassifier.Classify(current, preserve, manualArcNodes);
            if (junction.Kind == JunctionKind.Skip)
            {
                continue;
            }

            var cutResult = ArmCutResolver.Resolve(junction, ref nextCutId);
            if ((cutResult.CornerArcs.Count == 0) && (junction.CollinearPairs.Count == 0))
            {
                continue;
            }

            junctionPlans.Add(junction);
            cutResults.Add(cutResult);
        }

        var plan = FilletPlanBuilder.Build(layout, junctionPlans, cutResults);
        var exec = FilletPlanExecutor.Execute(layout, plan, junctionPlans, idCounter);
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
        };

        Log.LogInformation(
            "V2 fillet: {Nodes} filleted, {Arcs} arcs, {Merged} collinear, {Cleanups} structural cleanups, {Warnings} warnings",
            stats.FilletedNodes,
            stats.ArcsCreated,
            stats.CollinearMerges,
            structuralCleanups,
            stats.Warnings.Count
        );

        return stats;
    }
}
