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
        var cuts = new Dictionary<int, ResolvedArmCut>();
        var armCuts = new List<ArmCutOp>();
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

            armCuts.AddRange(r.ArmCuts);
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
        FilletPlanCutRedirect.ExtendWithStableAnchors(redirect, cuts, preFilletStableNodes, FilletConstants.CoincidentNodeThresholdFt);
        var prunedCuts = FilletPlanCutRedirect.PruneCuts(cuts, redirect);
        var redirectedCornerArcs = FilletPlanCutRedirect.RedirectCornerArcs(cornerArcs, redirect).ToList();
        var redirectedStraightConnectors = FilletPlanCutRedirect.RedirectStraightConnectors(straightConnectors, redirect).ToList();

        var nodesToRemoveSet = nodesToRemove.ToHashSet();
        var split = FilletEdgeSplitPlanner.Plan(layout, junctions, prunedCuts, redirect, nodesToRemoveSet);
        warnings.AddRange(split.Warnings);

        var stableAnchoredEndpoints = CollectStableAnchoredEndpoints(prunedCuts, redirectedCornerArcs, redirectedStraightConnectors);

        var built = new FilletPlan(
            prunedCuts,
            armCuts,
            merges,
            ArmChainEdges: [],
            redirectedCornerArcs,
            redirectedStraightConnectors,
            ArmBypasses: [],
            ReconnectEdges: [],
            HoldShortReconnects: [],
            PreserveStubs: [],
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

    private static HashSet<int> CollectStableAnchoredEndpoints(
        IReadOnlyDictionary<int, ResolvedArmCut> prunedCuts,
        IReadOnlyList<CornerArcOp> cornerArcs,
        IReadOnlyList<StraightConnectorOp> straightConnectors
    )
    {
        var refs = new HashSet<int>();
        foreach (var op in cornerArcs)
        {
            refs.Add(op.CutIdAtArmA);
            refs.Add(op.CutIdAtArmB);
        }

        foreach (var op in straightConnectors)
        {
            refs.Add(op.CutIdAtArmA);
            refs.Add(op.CutIdAtArmB);
        }

        refs.RemoveWhere(id => prunedCuts.ContainsKey(id));
        return refs;
    }
}
