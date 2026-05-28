namespace Yaat.Sim.Data.Airport.Fillet.V2;

internal static class FilletPlanBuilder
{
    public static FilletPlan Build(IReadOnlyList<JunctionPlan> junctions, IReadOnlyList<ArmCutResolver.JunctionCutResult> results)
    {
        var cuts = new Dictionary<int, ResolvedArmCut>();
        var armCuts = new List<ArmCutOp>();
        var merges = new List<TangentMergeOp>();
        var cornerArcs = new List<CornerArcOp>();
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
            warnings.AddRange(r.Warnings);

            if (!jp.PreserveNode && (r.CornerArcs.Count > 0 || jp.CollinearPairs.Count > 0))
            {
                nodesToRemove.Add(jp.JunctionNodeId);
            }

            foreach (var arm in jp.Arms)
            {
                edgesToRemove.Add(arm.RootEdge);
            }
        }

        merges.AddRange(SharedArmTangentPass.ApplyCrossJunction(junctions, results, cuts, warnings));

        return new FilletPlan(cuts, armCuts, merges, cornerArcs, stubs, nodesToRemove, edgesToRemove, warnings);
    }
}
