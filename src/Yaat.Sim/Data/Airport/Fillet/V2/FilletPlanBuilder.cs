using Yaat.Sim.Data.Airport.Fillet;

namespace Yaat.Sim.Data.Airport.Fillet.V2;

internal static class FilletPlanBuilder
{
    public static FilletPlan Build(
        AirportGroundLayout layout,
        IReadOnlyList<JunctionPlan> junctions,
        IReadOnlyList<ArmCutResolver.JunctionCutResult> results
    )
    {
        var cuts = new Dictionary<CutId, ResolvedArmCut>();
        var merges = new List<TangentMergeOp>();
        var cornerArcs = new List<CornerArcOp>();
        var straightConnectors = new List<StraightConnectorOp>();
        var warnings = new List<PlanWarning>();
        var nodesToRemove = new List<int>();

        for (int i = 0; i < junctions.Count; i++)
        {
            var jp = junctions[i];
            var r = results[i];

            foreach (var (id, cut) in r.Cuts)
            {
                cuts[id] = cut;
            }

            merges.AddRange(r.TangentMerges);
            cornerArcs.AddRange(r.CornerArcs);
            straightConnectors.AddRange(r.StraightConnectors);
            warnings.AddRange(r.Warnings);

            if ((!jp.PreserveNode) && (jp.CollinearPairs.Count == 0) && ((r.CornerArcs.Count > 0) || (r.Cuts.Count > 0)))
            {
                nodesToRemove.Add(jp.JunctionNodeId);
            }
        }

        merges.AddRange(SharedArmTangentPass.ApplyCrossJunction(junctions, results, cuts, warnings));

        var redirect = FilletPlanCutRedirect.BuildSurvivorMap(merges);
        var preFilletStableNodes = layout
            .Nodes.Where(kv => FilletPlanCutRedirect.IsStableAnchorTarget(kv.Value))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        var stableAnchorNodeIds = FilletPlanCutRedirect.ExtendWithStableAnchors(
            redirect,
            cuts,
            preFilletStableNodes,
            FilletConstants.CoincidentNodeThresholdFt
        );
        var prunedCuts = FilletPlanCutRedirect.PruneCuts(cuts, redirect);
        var redirectedCornerArcs = FilletPlanCutRedirect.RedirectCornerArcs(cornerArcs, redirect).ToList();
        var redirectedStraightConnectors = FilletPlanCutRedirect.RedirectStraightConnectors(straightConnectors, redirect).ToList();

        var nodesToRemoveSet = nodesToRemove.ToHashSet();
        var split = FilletEdgeSplitPlanner.Plan(layout, junctions, prunedCuts, redirect, nodesToRemoveSet);
        warnings.AddRange(split.Warnings);

        // Use the anchor node IDs returned directly by ExtendWithStableAnchors rather than
        // inferring them from arc ops post-hoc. The inferred approach was broken: when an
        // anchor node ID coincides with a surviving cut ID, the inference incorrectly removed
        // the anchor from the set, causing the executor to resolve via cutNode instead of
        // layout.Nodes and pick up the wrong tangent-cut from a different junction.
        var stableAnchoredEndpoints = stableAnchorNodeIds;

        var built = new FilletPlan(
            prunedCuts,
            merges,
            redirectedCornerArcs,
            redirectedStraightConnectors,
            split.SurvivingEdges,
            nodesToRemove,
            split.ConsumedEdges,
            warnings,
            stableAnchoredEndpoints
        );
        FilletPlanConsistency.ValidateCutReferences(built);
        FilletPlanConsistency.ValidateNodeReferences(built);
        return built;
    }
}
