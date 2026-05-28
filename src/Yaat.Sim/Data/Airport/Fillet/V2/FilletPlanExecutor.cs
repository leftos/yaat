using Microsoft.Extensions.Logging;
using Yaat.Sim.Data.Airport.Fillet;

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
        foreach (int stableId in plan.StableAnchoredEndpointIds)
        {
            if (layout.Nodes.TryGetValue(stableId, out var stableNode))
            {
                cutToNode[stableId] = stableNode;
            }
        }

        var cornerToJunction = new Dictionary<int, int>();
        foreach (var jp in junctionPlans)
        {
            foreach (var corner in jp.Corners)
            {
                cornerToJunction[corner.CornerId] = jp.JunctionNodeId;
            }
        }

        foreach (var (cutId, cut) in plan.Cuts)
        {
            if (cutToNode.ContainsKey(cutId))
            {
                continue;
            }

            if (TryBindCutToCoincidentStable(layout, cutId, cut, cutToNode))
            {
                continue;
            }

            var junctionPlan = junctionPlans.FirstOrDefault(j => j.JunctionNodeId == cut.JunctionNodeId);
            var junctionPos = junctionPlan?.JunctionNode.Position ?? cut.Position;

            int id = idCounter.Next++;
            while (layout.Nodes.ContainsKey(id))
            {
                id = idCounter.Next++;
            }

            var tanNode = new GroundNode
            {
                Id = id,
                Position = cut.Position,
                Type = GroundNodeType.TaxiwayIntersection,
                SourceIntersectionPosition = (junctionPos.Lat, junctionPos.Lon),
                Origin = $"V2:tangent-cut@J{cut.JunctionNodeId}/{(junctionPlan is not null ? GetTaxiwayName(junctionPlan, cut.ArmId) : "?")}",
            };
            layout.Nodes[id] = tanNode;
            cutToNode[cutId] = tanNode;
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

            var consumed = new HashSet<GroundEdge>();
            var chainOpsForJunction = plan.ArmChainEdges.Where(e => e.JunctionNodeId == junction.JunctionNodeId).ToList();
            foreach (var arm in junction.Arms)
            {
                var root = arm.RootEdge;
                var other = root.OtherNode(node);
                var armChains = chainOpsForJunction.Where(e => e.ArmId == arm.Id).ToList();
                if (armChains.Count == 0)
                {
                    continue;
                }

                consumed.Add(root);
                foreach (var op in armChains)
                {
                    if (op.FromStableNodeId is int stableFromId && !layout.Nodes.ContainsKey(stableFromId))
                    {
                        continue;
                    }

                    if (op.TerminalNodeId is int terminalId && !layout.Nodes.ContainsKey(terminalId))
                    {
                        continue;
                    }

                    GroundNode from =
                        op.FromCutId is int fromCut ? ResolveCutEndpoint(fromCut, cutToNode, layout)
                        : op.FromStableNodeId is int stableFrom ? layout.Nodes[stableFrom]
                        : other;
                    GroundNode to = op.ToCutId is int toCut ? ResolveCutEndpoint(toCut, cutToNode, layout) : layout.Nodes[op.TerminalNodeId!.Value];

                    if ((!layout.Nodes.ContainsKey(from.Id)) || (!layout.Nodes.ContainsKey(to.Id)))
                    {
                        continue;
                    }

                    if ((from.Id == to.Id) || (GeoMath.DistanceNm(from.Position, to.Position) * GeoMath.FeetPerNm < 1.0))
                    {
                        continue;
                    }

                    double chainSpanFt = GeoMath.DistanceNm(from.Position, to.Position) * GeoMath.FeetPerNm;
                    bool fromIsV2Tangent = from.Origin?.StartsWith("V2:", StringComparison.Ordinal) == true;
                    bool toIsV2Tangent = to.Origin?.StartsWith("V2:", StringComparison.Ordinal) == true;
                    if (
                        (chainSpanFt > FilletConstants.MaxHoldShortTangentSpanFt)
                        && (
                            ((from.Type == GroundNodeType.RunwayHoldShort) && toIsV2Tangent)
                            || ((to.Type == GroundNodeType.RunwayHoldShort) && fromIsV2Tangent)
                        )
                    )
                    {
                        continue;
                    }

                    string kind = (op.FromCutId is null) ? "shorten" : ((op.ToCutId is null) ? "arm-tail" : "arm-chain");
                    AddEdge(layout, from, to, op.TaxiwayName, op.IsRunwayCenterline, junction.JunctionNodeId, kind);
                }
            }

            if (!cornerArcsByJunction.TryGetValue(junction.JunctionNodeId, out var junctionArcOps))
            {
                junctionArcOps = [];
            }

            foreach (var arcOp in junctionArcOps)
            {
                var corner = junction.Corners.First(c => c.CornerId == arcOp.CornerId);
                var tanA = ResolveCutEndpoint(arcOp.CutIdAtArmA, cutToNode, layout);
                var tanB = ResolveCutEndpoint(arcOp.CutIdAtArmB, cutToNode, layout);
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
                    var tanA = ResolveCutEndpoint(op.CutIdAtArmA, cutToNode, layout);
                    var tanB = ResolveCutEndpoint(op.CutIdAtArmB, cutToNode, layout);
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
                        var tanA = ResolveCutEndpoint(farCutA.CutId, cutToNode, layout);
                        var tanB = ResolveCutEndpoint(farCutB.CutId, cutToNode, layout);
                        string twy = armA.TaxiwayName;
                        AddEdge(layout, tanA, tanB, twy, false, junction.JunctionNodeId, "collinear-through");
                    }
                    else
                    {
                        if (!cutOnA)
                        {
                            AddPreserveCollinearStub(layout, plan, junctionPlans, junction, armA, node, cutToNode);
                        }

                        if (!cutOnB)
                        {
                            AddPreserveCollinearStub(layout, plan, junctionPlans, junction, armB, node, cutToNode);
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

                foreach (var armGroup in junctionCuts.GroupBy(c => c.ArmId))
                {
                    var nearestCut = armGroup.OrderBy(c => c.DistanceAlongArmFt).First();
                    if (!cutToNode.TryGetValue(nearestCut.CutId, out var tan) || (tan.Id == node.Id))
                    {
                        continue;
                    }

                    bool hasTangentEdge = layout.Edges.Any(e =>
                        ((e.Nodes[0].Id == node.Id) && (e.Nodes[1].Id == tan.Id)) || ((e.Nodes[1].Id == node.Id) && (e.Nodes[0].Id == tan.Id))
                    );
                    if (!hasTangentEdge)
                    {
                        var arm = junction.Arms.First(a => a.Id == nearestCut.ArmId);
                        AddEdge(layout, node, tan, arm.TaxiwayName, false, junction.JunctionNodeId, "preserve-to-cut");
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
                    GroundNode target = op.TargetCutId is int cutId ? ResolveCutEndpoint(cutId, cutToNode, layout) : node;

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
                    if (!touches)
                    {
                        continue;
                    }

                    bool touchesHoldShort =
                        (edge.Nodes[0].Type == GroundNodeType.RunwayHoldShort) || (edge.Nodes[1].Type == GroundNodeType.RunwayHoldShort);
                    if (touchesHoldShort)
                    {
                        continue;
                    }

                    consumed.Add(edge);
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

        foreach (var op in plan.HoldShortReconnects)
        {
            if (
                !layout.Nodes.TryGetValue(op.HoldShortNodeId, out var holdShort)
                || !layout.Nodes.TryGetValue(op.IntersectionNodeId, out var intersection)
            )
            {
                continue;
            }

            AddEdge(layout, holdShort, intersection, op.TaxiwayName, op.IsRunwayCenterline, op.HoldShortNodeId, "hold-short-reconnect");
        }

        if (Log.IsEnabled(LogLevel.Debug))
        {
            Log.LogDebug("V2 executor: {Arcs} arcs, {Collinear} collinear, {Nodes} junctions", arcsCreated, collinearMerges, filletedNodes);
        }

        return new ExecuteResult(arcsCreated, collinearMerges, filletedNodes);
    }

    private static void AddEdge(AirportGroundLayout layout, GroundNode a, GroundNode b, string taxiway, bool isRunway, int junctionId, string kind)
    {
        if ((a.Id == b.Id) || (!layout.Nodes.ContainsKey(a.Id)) || (!layout.Nodes.ContainsKey(b.Id)))
        {
            return;
        }

        string twy = isRunway ? $"RWY:{taxiway}" : taxiway;
        bool alreadyExists = layout.Edges.Any(e =>
            string.Equals(e.TaxiwayName, twy, StringComparison.OrdinalIgnoreCase)
            && (((e.Nodes[0].Id == a.Id) && (e.Nodes[1].Id == b.Id)) || ((e.Nodes[0].Id == b.Id) && (e.Nodes[1].Id == a.Id)))
        );
        if (alreadyExists)
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

    private static string GetTaxiwayName(JunctionPlan junction, int armId) => junction.Arms.FirstOrDefault(a => a.Id == armId)?.TaxiwayName ?? "?";

    private static void AddPreserveCollinearStub(
        AirportGroundLayout layout,
        FilletPlan plan,
        IReadOnlyList<JunctionPlan> junctionPlans,
        JunctionPlan junction,
        TaxiwayArm arm,
        GroundNode junctionNode,
        IReadOnlyDictionary<int, GroundNode> cutToNode
    )
    {
        var other = arm.RootEdge.OtherNode(junctionNode);
        var removed = plan.JunctionNodesToRemove.ToHashSet();
        if ((!removed.Contains(other.Id)) && layout.Nodes.ContainsKey(other.Id))
        {
            AddEdge(layout, junctionNode, other, arm.TaxiwayName, false, junction.JunctionNodeId, "preserve-collinear");
            return;
        }

        var farJunction = junctionPlans.FirstOrDefault(j => j.JunctionNodeId == other.Id);
        if (farJunction is null)
        {
            return;
        }

        var returnArm = farJunction.Arms.FirstOrDefault(a =>
            string.Equals(a.TaxiwayName, arm.TaxiwayName, StringComparison.OrdinalIgnoreCase)
            && ((a.TerminalNode.Id == junction.JunctionNodeId) || a.Walk.Steps.Any(s => s.FarNode.Id == junction.JunctionNodeId))
        );
        if (returnArm is null)
        {
            return;
        }

        var partnerCut = plan
            .Cuts.Values.Where(c => (c.JunctionNodeId == other.Id) && (c.ArmId == returnArm.Id))
            .OrderByDescending(c => c.DistanceAlongArmFt)
            .FirstOrDefault();
        if ((partnerCut is not null) && cutToNode.TryGetValue(partnerCut.CutId, out var tan))
        {
            AddEdge(layout, junctionNode, tan, arm.TaxiwayName, false, junction.JunctionNodeId, "preserve-collinear-cut");
        }
    }

    private static ResolvedArmCut FarthestCutOnArm(IReadOnlyList<ResolvedArmCut> junctionCuts, int armId) =>
        junctionCuts.Where(c => c.ArmId == armId).OrderByDescending(c => c.DistanceAlongArmFt).First();

    private static GroundNode ResolveCutEndpoint(int cutOrStableId, IReadOnlyDictionary<int, GroundNode> cutToNode, AirportGroundLayout layout)
    {
        if (cutToNode.TryGetValue(cutOrStableId, out var node))
        {
            return node;
        }

        return layout.Nodes[cutOrStableId];
    }

    private static bool TryBindCutToCoincidentStable(AirportGroundLayout layout, int cutId, ResolvedArmCut cut, Dictionary<int, GroundNode> cutToNode)
    {
        if (!layout.Nodes.TryGetValue(cutId, out var existing) || (existing.Type != GroundNodeType.TaxiwayIntersection))
        {
            return false;
        }

        double distFt = GeoMath.DistanceNm(cut.Position, existing.Position) * GeoMath.FeetPerNm;
        if (distFt > FilletConstants.CoincidentNodeThresholdFt)
        {
            return false;
        }

        cutToNode[cutId] = existing;
        return true;
    }

    internal sealed class NextNodeIdCounter
    {
        public int Next { get; set; }
    }
}
