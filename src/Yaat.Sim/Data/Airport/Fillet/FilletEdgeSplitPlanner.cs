namespace Yaat.Sim.Data.Airport.Fillet;

/// <summary>
/// Order-independent connectivity planner. Splits each ORIGINAL edge exactly once by the cuts
/// that land on it, dropping only the stub incident to a removed junction and keeping every other
/// sub-segment. Replaces the per-arm chain reconstruction + reconnect/bypass/side-branch passes.
/// Pure: reads the pre-fillet layout, emits <see cref="SurvivingEdgeOp"/>s the executor materializes
/// after removing the consumed originals. A removed junction never appears as a surviving endpoint
/// because its endpoints are replaced by cut nodes before any mutation.
/// </summary>
internal static class FilletEdgeSplitPlanner
{
    public sealed record Result(
        IReadOnlySet<GroundEdge> ConsumedEdges,
        IReadOnlyList<SurvivingEdgeOp> SurvivingEdges,
        IReadOnlyList<PlanWarning> Warnings
    );

    private readonly record struct CutOnEdge(double Frac, FilletEndpoint Endpoint);

    public static Result Plan(
        AirportGroundLayout layout,
        IReadOnlyList<JunctionPlan> junctions,
        IReadOnlyDictionary<CutId, ResolvedArmCut> prunedCuts,
        IReadOnlyDictionary<CutId, FilletEndpoint> redirect,
        IReadOnlySet<int> removedJunctionIds
    )
    {
        var junctionById = junctions.ToDictionary(j => j.JunctionNodeId);
        var warnings = new List<PlanWarning>();
        var consumed = new HashSet<GroundEdge>();
        var edgeCuts = new Dictionary<GroundEdge, List<CutOnEdge>>();
        var armHasCut = new HashSet<(int Junction, int Arm)>();

        FilletEndpoint ResolveCut(CutId cutId)
        {
            var ep = FilletPlanCutRedirect.Resolve(cutId, redirect);
            return ep switch
            {
                FilletEndpoint.Cut cut => prunedCuts.ContainsKey(cut.Id) ? new FilletEndpoint.Cut(cut.Id) : new FilletEndpoint.Node(cut.Id.Value),
                FilletEndpoint.Node node => node,
                _ => throw new InvalidOperationException($"Unknown FilletEndpoint subtype: {ep.GetType().Name}"),
            };
        }

        // Map every surviving cut to the original edge it lands on, with the fraction normalized
        // to the edge's own (Nodes[0]->Nodes[1]) orientation so cuts arriving from opposite arm
        // walks on a shared edge sort consistently.
        foreach (var (cutId, cut) in prunedCuts)
        {
            if (!junctionById.TryGetValue(cut.JunctionNodeId, out var jp))
            {
                continue;
            }

            var arm = jp.Arms.FirstOrDefault(a => a.Id == cut.ArmId);
            if (arm is null)
            {
                continue;
            }

            armHasCut.Add((cut.JunctionNodeId, cut.ArmId));
            var loc = TaxiwayWalk.LocateDistanceFt(arm.Walk, jp.JunctionNode, cut.DistanceAlongArmFt);
            double frac = loc.Edge.Nodes[0].Id == loc.StepFromNode.Id ? loc.FractionFromStepStart : 1.0 - loc.FractionFromStepStart;
            if (!edgeCuts.TryGetValue(loc.Edge, out var list))
            {
                list = [];
                edgeCuts[loc.Edge] = list;
            }

            list.Add(new CutOnEdge(frac, ResolveCut(cutId)));
        }

        // Determine consumed (split or dropped) edges: every walk-step edge from the root up to and
        // including the farthest cut's step. A removed junction's cutless arm consumes its root stub.
        foreach (var jp in junctions)
        {
            bool removed = removedJunctionIds.Contains(jp.JunctionNodeId);
            foreach (var arm in jp.Arms)
            {
                var armCuts = prunedCuts.Values.Where(c => (c.JunctionNodeId == jp.JunctionNodeId) && (c.ArmId == arm.Id)).ToList();
                if (armCuts.Count == 0)
                {
                    if (removed)
                    {
                        consumed.Add(arm.RootEdge);
                    }

                    continue;
                }

                double farthest = armCuts.Max(c => c.DistanceAlongArmFt);
                var farLoc = TaxiwayWalk.LocateDistanceFt(arm.Walk, jp.JunctionNode, farthest);
                for (int i = 0; (i <= farLoc.StepIndex) && (i < arm.Walk.Steps.Count); i++)
                {
                    consumed.Add(arm.Walk.Steps[i].Edge);
                }
            }
        }

        // Split each consumed edge once, using both endpoints' removed status. Drop only the
        // stub incident to a removed junction; keep every other sub-segment.
        var surviving = new List<SurvivingEdgeOp>();
        foreach (var edge in consumed)
        {
            var sorted = edgeCuts.TryGetValue(edge, out var cutsOnE) ? cutsOnE.OrderBy(c => c.Frac).ToList() : [];
            bool aRemoved = removedJunctionIds.Contains(edge.Nodes[0].Id);
            bool bRemoved = removedJunctionIds.Contains(edge.Nodes[1].Id);

            var seq = new List<FilletEndpoint>();
            if (!aRemoved)
            {
                seq.Add(new FilletEndpoint.Node(edge.Nodes[0].Id));
            }

            foreach (var c in sorted)
            {
                seq.Add(c.Endpoint);
            }

            if (!bRemoved)
            {
                seq.Add(new FilletEndpoint.Node(edge.Nodes[1].Id));
            }

            for (int i = 0; (i + 1) < seq.Count; i++)
            {
                surviving.Add(MakeEdge(seq[i], seq[i + 1], edge, "edge-split"));
            }
        }

        // Cutless arms at removed junctions (distorted/demoted): redirect the root edge's far node
        // to the junction's nearest cut so the arm never severs and never names the removed junction.
        foreach (var jp in junctions)
        {
            if (!removedJunctionIds.Contains(jp.JunctionNodeId))
            {
                continue;
            }

            var junctionCuts = prunedCuts.Values.Where(c => c.JunctionNodeId == jp.JunctionNodeId).ToList();
            if (junctionCuts.Count == 0)
            {
                continue;
            }

            foreach (var arm in jp.Arms)
            {
                if (armHasCut.Contains((jp.JunctionNodeId, arm.Id)))
                {
                    continue;
                }

                var farNode = arm.RootEdge.OtherNode(jp.JunctionNode);
                if (removedJunctionIds.Contains(farNode.Id))
                {
                    warnings.Add(
                        new PlanWarning(
                            jp.JunctionNodeId,
                            null,
                            PlanWarning.NoOwningCut,
                            $"Cutless arm {arm.Id} ({arm.TaxiwayName}) far node {farNode.Id} is a removed junction; left to its own redirect"
                        )
                    );
                    continue;
                }

                var nearest = junctionCuts.OrderBy(c => GeoMath.DistanceNm(c.Position, farNode.Position)).First();
                surviving.Add(MakeEdge(new FilletEndpoint.Node(farNode.Id), ResolveCut(nearest.CutId), arm.RootEdge, "edge-split-redirect"));
            }
        }

        return new Result(consumed, surviving, warnings);
    }

    private static SurvivingEdgeOp MakeEdge(FilletEndpoint from, FilletEndpoint to, GroundEdge source, string kind) =>
        new(from, to, source.TaxiwayName, source.IsRunwayCenterline, $"V2:{kind}/{source.TaxiwayName}");
}
