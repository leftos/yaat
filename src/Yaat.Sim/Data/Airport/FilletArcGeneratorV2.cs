using Microsoft.Extensions.Logging;
using Yaat.Sim.Data.Airport.Fillet;
using Yaat.Sim.Data.Airport.Fillet.V2;

namespace Yaat.Sim.Data.Airport;

/// <summary>Plan-then-execute fillet generator (V2).</summary>
public sealed class FilletArcGeneratorV2 : IFilletArcGenerator
{
    private static readonly ILogger Log = SimLog.CreateLogger("FilletArcGeneratorV2");

    public string Id => "v2";

    public string DisplayName => "V2 (plan-then-execute)";

    public FilletStatistics Apply(AirportGroundLayout layout)
    {
        var manualArcNodes = ManualArcDetector.Detect(layout);
        var idCounter = new FilletPlanExecutor.NextNodeIdCounter { Next = layout.Nodes.Keys.DefaultIfEmpty(0).Max() + 1 };

        layout.RebuildAdjacencyLists();

        var junctionPlans = new List<JunctionPlan>();
        var cutResults = new List<ArmCutResolver.JunctionCutResult>();
        int nextCutId = 1;

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

        var plan = FilletPlanBuilder.Build(junctionPlans, cutResults);
        var exec = FilletPlanExecutor.Execute(layout, plan, junctionPlans, idCounter);
        int coincidentMerged = FilletGraphNormalizer.Normalize(layout);

        var stats = new FilletStatistics(
            FilletedNodes: exec.FilletedNodes,
            ArcsCreated: exec.ArcsCreated,
            CollinearMerges: exec.CollinearMerges,
            CoincidentNodesMerged: coincidentMerged,
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
            "V2 fillet: {Nodes} filleted, {Arcs} arcs, {Merged} collinear, {Coincident} coincident merged, {Warnings} warnings",
            stats.FilletedNodes,
            stats.ArcsCreated,
            stats.CollinearMerges,
            stats.CoincidentNodesMerged,
            stats.Warnings.Count
        );

        return stats;
    }
}
