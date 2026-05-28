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
                edgesToRemove.Add(arm.RootEdge);
            }

            if (!jp.PreserveNode && ((r.CornerArcs.Count > 0) || (jp.CollinearPairs.Count > 0) || (r.Cuts.Count > 0)))
            {
                nodesToRemove.Add(jp.JunctionNodeId);
            }

            FilletConnectivityPlanner.AppendForJunction(layout, jp, r, edgesToRemove, armBypasses, reconnectEdges, warnings);
        }

        merges.AddRange(SharedArmTangentPass.ApplyCrossJunction(junctions, results, cuts, warnings));

        return new FilletPlan(
            cuts,
            armCuts,
            merges,
            cornerArcs,
            straightConnectors,
            armBypasses,
            reconnectEdges,
            stubs,
            nodesToRemove,
            edgesToRemove,
            warnings
        );
    }
}
