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

        // The cross-arm tangent merge collapses coincident cross-arm cuts onto one survivor, so
        // several corners (e.g. A/A and A/A8, or A/RAMP twice) now resolve to the SAME endpoint
        // pair. Keep one op per pair — preferring the single-name corner (requirement ①) — so the
        // executor emits exactly one arc per node pair and the post-execute normalizer has no
        // coincident nodes to merge.
        redirectedCornerArcs = DedupByEndpointPair(
            redirectedCornerArcs,
            op => (op.EndpointAtArmA, op.EndpointAtArmB),
            op => IsSingleNameCorner(op, junctions)
        );
        redirectedStraightConnectors = DedupByEndpointPair(redirectedStraightConnectors, op => (op.EndpointAtArmA, op.EndpointAtArmB), _ => true);

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

    /// <summary>
    /// Keep one op per unordered resolved-endpoint pair, preserving original order. When two ops
    /// share a pair, the one for which <paramref name="prefer"/> is true (e.g. a single-name corner)
    /// wins; otherwise the first-seen op is kept.
    /// </summary>
    private static List<T> DedupByEndpointPair<T>(List<T> ops, Func<T, (FilletEndpoint A, FilletEndpoint B)> endpoints, Func<T, bool> prefer)
    {
        var chosenIndex = new Dictionary<((int, int) A, (int, int) B), int>();
        for (int i = 0; i < ops.Count; i++)
        {
            var key = PairKey(endpoints(ops[i]));
            if (!chosenIndex.TryGetValue(key, out int existing))
            {
                chosenIndex[key] = i;
            }
            else if (!prefer(ops[existing]) && prefer(ops[i]))
            {
                chosenIndex[key] = i;
            }
        }

        var keep = chosenIndex.Values.ToHashSet();
        var result = new List<T>(keep.Count);
        for (int i = 0; i < ops.Count; i++)
        {
            if (keep.Contains(i))
            {
                result.Add(ops[i]);
            }
        }

        return result;
    }

    private static ((int, int) A, (int, int) B) PairKey((FilletEndpoint A, FilletEndpoint B) ep)
    {
        var a = Token(ep.A);
        var b = Token(ep.B);
        return a.CompareTo(b) <= 0 ? (a, b) : (b, a);
    }

    private static (int Kind, int Id) Token(FilletEndpoint ep) =>
        ep switch
        {
            FilletEndpoint.Cut cut => (0, cut.Id.Value),
            FilletEndpoint.Node node => (1, node.NodeId),
            _ => throw new InvalidOperationException($"Unknown FilletEndpoint subtype: {ep.GetType().Name}"),
        };

    private static bool IsSingleNameCorner(CornerArcOp op, IReadOnlyList<JunctionPlan> junctions)
    {
        foreach (var jp in junctions)
        {
            if (jp.JunctionNodeId != op.JunctionNodeId)
            {
                continue;
            }

            foreach (var corner in jp.Corners)
            {
                if (corner.CornerId == op.CornerId)
                {
                    return corner.EdgeA.SharesTaxiway(corner.EdgeB);
                }
            }
        }

        return false;
    }
}
