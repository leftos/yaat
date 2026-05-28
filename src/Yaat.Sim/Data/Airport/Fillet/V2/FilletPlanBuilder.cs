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
        var armBypasses = new List<ArmBypassOp>();
        var reconnectEdges = new List<ReconnectEdgeOp>();
        var holdShortReconnects = new List<ReconnectHoldShortOp>();
        var stubs = new List<PreserveStubOp>();
        var warnings = new List<PlanWarning>();
        var nodesToRemove = new List<int>();
        var edgesToRemove = new HashSet<GroundEdge>();

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

            foreach (var arm in jp.Arms)
            {
                if (!EdgeTouchesRunwayHoldShort(arm.RootEdge))
                {
                    edgesToRemove.Add(arm.RootEdge);
                }
            }

            if ((!jp.PreserveNode) && (jp.CollinearPairs.Count == 0) && ((r.CornerArcs.Count > 0) || (r.Cuts.Count > 0)))
            {
                nodesToRemove.Add(jp.JunctionNodeId);
            }
        }

        var nodesToRemoveSet = nodesToRemove.ToHashSet();
        for (int i = 0; i < junctions.Count; i++)
        {
            FilletConnectivityPlanner.AppendArmBypasses(junctions[i], results[i], nodesToRemoveSet, armBypasses);
        }

        AuditIncidentEdges(layout, nodesToRemoveSet, edgesToRemove, warnings);

        merges.AddRange(SharedArmTangentPass.ApplyCrossJunction(junctions, results, cuts, warnings));

        var redirect = FilletPlanCutRedirect.BuildSurvivorMap(merges);
        var preFilletStableNodes = layout
            .Nodes.Where(kv => FilletPlanCutRedirect.IsStableAnchorTarget(kv.Value))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        FilletPlanCutRedirect.ExtendWithStableAnchors(redirect, cuts, preFilletStableNodes, FilletConstants.CoincidentNodeThresholdFt);
        var prunedCuts = FilletPlanCutRedirect.PruneCuts(cuts, redirect);
        cornerArcs = FilletPlanCutRedirect.RedirectCornerArcs(cornerArcs, redirect).ToList();
        straightConnectors = FilletPlanCutRedirect.RedirectStraightConnectors(straightConnectors, redirect).ToList();
        var preFilletNodeIds = layout.Nodes.Keys.ToHashSet();
        var rawChainEdges = FilletArmChainPlanner.BuildChainEdges(layout, junctions, prunedCuts, redirect, nodesToRemoveSet, preFilletNodeIds);
        rawChainEdges.AddRange(FilletArmChainPlanner.AppendInputStableSideBranches(layout, junctions, nodesToRemoveSet, rawChainEdges));
        var armChainEdges = FilletPlanCutRedirect
            .RedirectArmChainEdges(rawChainEdges, redirect)
            .Where(op => !((op.FromCutId is int from) && (op.ToCutId is int to) && (from == to)))
            .ToList();

        var junctionById = junctions.ToDictionary(j => j.JunctionNodeId);
        foreach (var jp in junctions)
        {
            FilletConnectivityPlanner.AppendReconnectEdges(layout, jp, prunedCuts, edgesToRemove, reconnectEdges, warnings);
        }

        FilletConnectivityPlanner.AppendUnconsumedReconnects(
            layout,
            junctionById,
            prunedCuts,
            nodesToRemoveSet,
            edgesToRemove,
            reconnectEdges,
            warnings
        );

        reconnectEdges = FilletPlanCutRedirect.RedirectReconnectEdges(reconnectEdges, redirect).ToList();

        AppendHoldShortReconnects(layout, holdShortReconnects);

        var stableAnchoredEndpoints = CollectStableAnchoredEndpoints(
            prunedCuts,
            armChainEdges,
            cornerArcs,
            straightConnectors,
            reconnectEdges,
            stubs
        );

        var built = new FilletPlan(
            prunedCuts,
            armCuts,
            merges,
            armChainEdges,
            cornerArcs,
            straightConnectors,
            armBypasses,
            reconnectEdges,
            holdShortReconnects,
            stubs,
            nodesToRemove,
            edgesToRemove,
            warnings,
            stableAnchoredEndpoints
        );
        FilletPlanConsistency.ValidateCutReferences(built);
        FilletPlanConsistency.ValidateNodeReferences(built);
        return built;
    }

    private static HashSet<int> CollectStableAnchoredEndpoints(
        IReadOnlyDictionary<int, ResolvedArmCut> prunedCuts,
        IReadOnlyList<ArmChainEdgeOp> armChainEdges,
        IReadOnlyList<CornerArcOp> cornerArcs,
        IReadOnlyList<StraightConnectorOp> straightConnectors,
        IReadOnlyList<ReconnectEdgeOp> reconnectEdges,
        IReadOnlyList<PreserveStubOp> stubs
    )
    {
        var refs = new HashSet<int>();
        foreach (var op in armChainEdges)
        {
            if (op.FromCutId is int from)
            {
                refs.Add(from);
            }

            if (op.ToCutId is int to)
            {
                refs.Add(to);
            }
        }

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

        foreach (var op in reconnectEdges)
        {
            if (op.TargetCutId is int target)
            {
                refs.Add(target);
            }
        }

        foreach (var op in stubs)
        {
            refs.Add(op.CutId);
        }

        refs.RemoveWhere(id => prunedCuts.ContainsKey(id));
        return refs;
    }

    private static bool EdgeTouchesRunwayHoldShort(GroundEdge edge) =>
        (edge.Nodes[0].Type == GroundNodeType.RunwayHoldShort) || (edge.Nodes[1].Type == GroundNodeType.RunwayHoldShort);

    private static void AppendHoldShortReconnects(AirportGroundLayout layout, List<ReconnectHoldShortOp> ops)
    {
        var keys = new HashSet<(int HoldShort, int Intersection)>();
        foreach (var edge in layout.Edges.OfType<GroundEdge>())
        {
            if (!EdgeTouchesRunwayHoldShort(edge))
            {
                continue;
            }

            int holdShortId = edge.Nodes[0].Type == GroundNodeType.RunwayHoldShort ? edge.Nodes[0].Id : edge.Nodes[1].Id;
            int otherId = edge.Nodes[0].Id == holdShortId ? edge.Nodes[1].Id : edge.Nodes[0].Id;
            if (!layout.Nodes.TryGetValue(otherId, out var other))
            {
                continue;
            }

            if (other.Type == GroundNodeType.RunwayHoldShort)
            {
                continue;
            }

            if (!keys.Add((holdShortId, otherId)))
            {
                continue;
            }

            ops.Add(new ReconnectHoldShortOp(holdShortId, otherId, edge.TaxiwayName, edge.IsRunwayCenterline));
        }
    }

    private static void AuditIncidentEdges(
        AirportGroundLayout layout,
        IReadOnlySet<int> nodesToRemove,
        HashSet<GroundEdge> edgesToRemove,
        List<PlanWarning> warnings
    )
    {
        foreach (var edge in layout.Edges)
        {
            if (edgesToRemove.Contains(edge))
            {
                continue;
            }

            bool touches = nodesToRemove.Contains(edge.Nodes[0].Id) || nodesToRemove.Contains(edge.Nodes[1].Id);
            if (touches)
            {
                warnings.Add(
                    new PlanWarning(
                        null,
                        null,
                        PlanWarning.UnconsumedIncidentEdge,
                        $"Edge {edge.TaxiwayName} between {edge.Nodes[0].Id} and {edge.Nodes[1].Id} touches removed junction but is not in EdgesToRemove"
                    )
                );
            }
        }
    }
}
