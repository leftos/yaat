using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Data.Airport.Fillet.V2;

internal static class FilletPlanExecutor
{
    private static readonly ILogger Log = SimLog.CreateLogger("FilletPlanExecutor");

    public sealed record ExecuteResult(int ArcsCreated, int CollinearMerges, int FilletedNodes);

    public static ExecuteResult Execute(
        AirportGroundLayout layout,
        FilletPlan plan,
        IReadOnlyList<JunctionPlan> junctionPlans,
        NextNodeIdCounter idCounter
    )
    {
        int arcsCreated = 0;
        int collinearMerges = 0;
        int filletedNodes = 0;

        var cutToNode = new Dictionary<int, GroundNode>();
        var mergeRedirect = BuildMergeRedirect(plan.TangentMerges);
        var cornerToJunction = new Dictionary<int, int>();
        foreach (var jp in junctionPlans)
        {
            foreach (var corner in jp.Corners)
            {
                cornerToJunction[corner.CornerId] = jp.JunctionNodeId;
            }
        }

        var cornerArcsByJunction = plan
            .CornerArcs.Where(a => cornerToJunction.ContainsKey(a.CornerId))
            .GroupBy(a => cornerToJunction[a.CornerId])
            .ToDictionary(g => g.Key, g => g.ToList());

        var straightByJunction = plan.StraightConnectors.GroupBy(s => s.JunctionNodeId).ToDictionary(g => g.Key, g => g.ToList());
        var bypassByJunction = plan.ArmBypasses.GroupBy(b => b.JunctionNodeId).ToDictionary(g => g.Key, g => g.ToList());
        var reconnectByJunction = plan.ReconnectEdges.GroupBy(r => r.JunctionNodeId).ToDictionary(g => g.Key, g => g.ToList());

        foreach (var junction in junctionPlans)
        {
            if (!layout.Nodes.ContainsKey(junction.JunctionNodeId))
            {
                continue;
            }

            var node = layout.Nodes[junction.JunctionNodeId];
            var junctionCuts = plan.Cuts.Values.Where(c => c.JunctionNodeId == junction.JunctionNodeId).ToList();
            if ((junctionCuts.Count == 0) && (junction.CollinearPairs.Count == 0))
            {
                continue;
            }

            filletedNodes++;

            foreach (var cut in junctionCuts)
            {
                int effectiveCutId = ResolveCutId(cut.CutId, mergeRedirect);
                if (cutToNode.ContainsKey(effectiveCutId))
                {
                    continue;
                }

                int id = idCounter.Next++;
                var tanNode = new GroundNode
                {
                    Id = id,
                    Position = cut.Position,
                    Type = GroundNodeType.TaxiwayIntersection,
                    SourceIntersectionPosition = (node.Position.Lat, node.Position.Lon),
                    Origin = $"V2:tangent-cut@J{junction.JunctionNodeId}/{GetTaxiwayName(junction, cut.ArmId)}",
                };
                layout.Nodes[id] = tanNode;
                cutToNode[effectiveCutId] = tanNode;
            }

            var consumed = new HashSet<GroundEdge>();
            foreach (var arm in junction.Arms)
            {
                var armCuts = junctionCuts.Where(c => c.ArmId == arm.Id).OrderBy(c => c.DistanceAlongArmFt).ToList();
                if (armCuts.Count == 0)
                {
                    continue;
                }

                var root = arm.RootEdge;
                var other = root.OtherNode(node);
                consumed.Add(root);

                if (armCuts.Count == 1)
                {
                    var tan = cutToNode[ResolveCutId(armCuts[0].CutId, mergeRedirect)];
                    AddEdge(layout, other, tan, root.TaxiwayName, root.IsRunwayCenterline, junction.JunctionNodeId, "shorten");
                }
                else
                {
                    GroundNode prev = other;
                    foreach (var cut in armCuts)
                    {
                        var tan = cutToNode[ResolveCutId(cut.CutId, mergeRedirect)];
                        if (prev.Id != tan.Id)
                        {
                            AddEdge(layout, prev, tan, root.TaxiwayName, root.IsRunwayCenterline, junction.JunctionNodeId, "arm-sub");
                        }

                        prev = tan;
                    }

                    if (!arm.IsRunwayCenterline)
                    {
                        AddEdge(layout, prev, arm.TerminalNode, root.TaxiwayName, false, junction.JunctionNodeId, "arm-tail");
                    }
                }
            }

            if (!cornerArcsByJunction.TryGetValue(junction.JunctionNodeId, out var junctionArcOps))
            {
                junctionArcOps = [];
            }

            foreach (var arcOp in junctionArcOps)
            {
                var corner = junction.Corners.First(c => c.CornerId == arcOp.CornerId);
                var tanA = cutToNode[ResolveCutId(arcOp.CutIdAtArmA, mergeRedirect)];
                var tanB = cutToNode[ResolveCutId(arcOp.CutIdAtArmB, mergeRedirect)];
                if (tanA.Id == tanB.Id)
                {
                    continue;
                }

                var bez = FilletGeometry.BuildBezier(
                    tanA.Position,
                    tanB.Position,
                    corner.BearingAToJunctionDeg,
                    corner.BearingBToJunctionDeg,
                    corner.RequestedRadiusFt
                );

                bool sameTaxiway = corner.EdgeA.SharesTaxiway(corner.EdgeB);
                layout.Arcs.Add(
                    new GroundArc
                    {
                        Nodes = [tanA, tanB],
                        TaxiwayNames = sameTaxiway ? [corner.EdgeA.TaxiwayName] : [corner.EdgeA.TaxiwayName, corner.EdgeB.TaxiwayName],
                        P1Lat = bez.P1Lat,
                        P1Lon = bez.P1Lon,
                        P2Lat = bez.P2Lat,
                        P2Lon = bez.P2Lon,
                        MinRadiusOfCurvatureFt = bez.MinRadiusFt,
                        DistanceNm = bez.ArcLengthNm,
                        EdgeBearingAtNode0Deg = bez.BearingAFromTangentDeg,
                        EdgeBearingAtNode1Deg = bez.BearingBFromTangentDeg,
                        TurnAngleDeg = bez.EffectiveTurnDeg,
                        Origin = $"V2:corner@J{junction.JunctionNodeId}/{corner.EdgeA.TaxiwayName}/{corner.EdgeB.TaxiwayName}",
                    }
                );
                arcsCreated++;
            }

            if (straightByJunction.TryGetValue(junction.JunctionNodeId, out var straightOps))
            {
                foreach (var op in straightOps)
                {
                    var tanA = cutToNode[ResolveCutId(op.CutIdAtArmA, mergeRedirect)];
                    var tanB = cutToNode[ResolveCutId(op.CutIdAtArmB, mergeRedirect)];
                    if (tanA.Id == tanB.Id)
                    {
                        continue;
                    }

                    AddEdge(layout, tanA, tanB, op.TaxiwayName, false, junction.JunctionNodeId, "straight-connector");
                }
            }

            if (junction.PreserveNode)
            {
                var armsWithCuts = junctionCuts.Select(c => c.ArmId).ToHashSet();
                foreach (var (armIdA, armIdB) in junction.CollinearPairs)
                {
                    var armA = junction.Arms.First(a => a.Id == armIdA);
                    var armB = junction.Arms.First(a => a.Id == armIdB);
                    consumed.Add(armA.RootEdge);
                    consumed.Add(armB.RootEdge);

                    bool cutOnA = armsWithCuts.Contains(armIdA);
                    bool cutOnB = armsWithCuts.Contains(armIdB);

                    if (cutOnA && cutOnB)
                    {
                        var farCutA = FarthestCutOnArm(junctionCuts, armIdA);
                        var farCutB = FarthestCutOnArm(junctionCuts, armIdB);
                        var tanA = cutToNode[ResolveCutId(farCutA.CutId, mergeRedirect)];
                        var tanB = cutToNode[ResolveCutId(farCutB.CutId, mergeRedirect)];
                        string twy = armA.TaxiwayName;
                        AddEdge(layout, tanA, tanB, twy, false, junction.JunctionNodeId, "collinear-through");
                    }
                    else
                    {
                        if (!cutOnA)
                        {
                            var otherA = armA.RootEdge.OtherNode(node);
                            AddEdge(layout, node, otherA, armA.TaxiwayName, false, junction.JunctionNodeId, "preserve-collinear");
                        }

                        if (!cutOnB)
                        {
                            var otherB = armB.RootEdge.OtherNode(node);
                            AddEdge(layout, node, otherB, armB.TaxiwayName, false, junction.JunctionNodeId, "preserve-collinear");
                        }
                    }

                    collinearMerges++;
                }

                foreach (var arm in junction.Arms)
                {
                    bool hasCut = junctionCuts.Any(c => c.ArmId == arm.Id);
                    if (!hasCut && !consumed.Contains(arm.RootEdge))
                    {
                        var other = arm.RootEdge.OtherNode(node);
                        AddEdge(layout, node, other, arm.TaxiwayName, false, junction.JunctionNodeId, "preserve");
                        consumed.Add(arm.RootEdge);
                    }
                }
            }

            if (bypassByJunction.TryGetValue(junction.JunctionNodeId, out var bypassOps))
            {
                foreach (var op in bypassOps)
                {
                    var arm = junction.Arms.First(a => a.Id == op.ArmId);
                    consumed.Add(arm.RootEdge);
                    var remote = layout.Nodes[op.RemoteNodeId];
                    var terminal = layout.Nodes[op.TerminalNodeId];
                    AddEdge(layout, remote, terminal, op.TaxiwayName, op.IsRunwayCenterline, junction.JunctionNodeId, "arm-bypass");
                }
            }

            if (reconnectByJunction.TryGetValue(junction.JunctionNodeId, out var reconnectOps))
            {
                foreach (var op in reconnectOps)
                {
                    var other = layout.Nodes[op.OtherNodeId];
                    GroundNode target = op.TargetCutId is int cutId ? cutToNode[ResolveCutId(cutId, mergeRedirect)] : node;

                    AddEdge(layout, other, target, op.TaxiwayName, op.IsRunwayCenterline, junction.JunctionNodeId, "reconnect");

                    foreach (var edge in layout.Edges)
                    {
                        if (consumed.Contains(edge))
                        {
                            continue;
                        }

                        bool touches =
                            (edge.Nodes[0].Id == node.Id && edge.Nodes[1].Id == other.Id)
                            || (edge.Nodes[1].Id == node.Id && edge.Nodes[0].Id == other.Id);
                        if (touches)
                        {
                            consumed.Add(edge);
                            break;
                        }
                    }
                }
            }

            foreach (var edge in layout.Edges)
            {
                if (plan.EdgesToRemove.Contains(edge))
                {
                    bool touches = (edge.Nodes[0].Id == node.Id) || (edge.Nodes[1].Id == node.Id);
                    if (touches)
                    {
                        consumed.Add(edge);
                    }
                }
            }

            layout.Edges.RemoveAll(e => consumed.Contains(e));

            if (!junction.PreserveNode)
            {
                int intId = junction.JunctionNodeId;
                layout.Edges.RemoveAll(e => (e.Nodes[0].Id == intId) || (e.Nodes[1].Id == intId));
                layout.Arcs.RemoveAll(a => (a.Nodes[0].Id == intId) || (a.Nodes[1].Id == intId));
                layout.Nodes.Remove(intId);
            }
        }

        if (Log.IsEnabled(LogLevel.Debug))
        {
            Log.LogDebug("V2 executor: {Arcs} arcs, {Collinear} collinear, {Nodes} junctions", arcsCreated, collinearMerges, filletedNodes);
        }

        return new ExecuteResult(arcsCreated, collinearMerges, filletedNodes);
    }

    private static void AddEdge(AirportGroundLayout layout, GroundNode a, GroundNode b, string taxiway, bool isRunway, int junctionId, string kind)
    {
        if (a.Id == b.Id)
        {
            return;
        }

        double dist = GeoMath.DistanceNm(a.Position, b.Position);
        layout.Edges.Add(
            new GroundEdge
            {
                Nodes = [a, b],
                TaxiwayName = isRunway ? $"RWY:{taxiway}" : taxiway,
                DistanceNm = dist,
                Origin = $"V2:{kind}@J{junctionId}/{taxiway}",
            }
        );
    }

    private static Dictionary<int, int> BuildMergeRedirect(IReadOnlyList<TangentMergeOp> merges)
    {
        var parent = new Dictionary<int, int>();
        int Find(int x)
        {
            if (!parent.TryGetValue(x, out int p))
            {
                parent[x] = x;
                return x;
            }

            if (p != x)
            {
                parent[x] = Find(p);
            }

            return parent[x];
        }

        void Union(int a, int b)
        {
            int ra = Find(a);
            int rb = Find(b);
            int survivor = Math.Min(ra, rb);
            int child = Math.Max(ra, rb);
            parent[child] = survivor;
        }

        foreach (var m in merges)
        {
            Union(m.CutIdA, m.CutIdB);
        }

        var redirect = new Dictionary<int, int>();
        foreach (var id in parent.Keys)
        {
            redirect[id] = Find(id);
        }

        return redirect;
    }

    private static int ResolveCutId(int cutId, Dictionary<int, int> mergeRedirect) =>
        mergeRedirect.TryGetValue(cutId, out int resolved) ? resolved : cutId;

    private static string GetTaxiwayName(JunctionPlan junction, int armId) => junction.Arms.FirstOrDefault(a => a.Id == armId)?.TaxiwayName ?? "?";

    private static ResolvedArmCut FarthestCutOnArm(IReadOnlyList<ResolvedArmCut> junctionCuts, int armId) =>
        junctionCuts.Where(c => c.ArmId == armId).OrderByDescending(c => c.DistanceAlongArmFt).First();

    internal sealed class NextNodeIdCounter
    {
        public int Next { get; set; }
    }
}
