using Yaat.Sim.Data.Airport.Fillet;

namespace Yaat.Sim.Data.Airport.Fillet.V2;

internal static class FilletArmChainPlanner
{
    private enum AnchorKind
    {
        Remote,
        Cut,
        Stable,
    }

    private readonly record struct ChainAnchor(double DistFt, AnchorKind Kind, int Id);

    public static List<ArmChainEdgeOp> BuildChainEdges(
        AirportGroundLayout layout,
        IReadOnlyList<JunctionPlan> junctions,
        IReadOnlyDictionary<int, ResolvedArmCut> cuts,
        IReadOnlyDictionary<int, int> redirect,
        IReadOnlySet<int> junctionNodesToRemove,
        IReadOnlySet<int> preFilletNodeIds
    )
    {
        var ops = new List<ArmChainEdgeOp>();
        var junctionById = junctions.ToDictionary(j => j.JunctionNodeId);

        foreach (var jp in junctions)
        {
            foreach (var arm in jp.Arms)
            {
                var survivorCutIds = cuts
                    .Values.Where(c => (c.JunctionNodeId == jp.JunctionNodeId) && (c.ArmId == arm.Id))
                    .Select(c => FilletPlanCutRedirect.Resolve(c.CutId, redirect))
                    .Distinct()
                    .OrderBy(id => cuts[id].DistanceAlongArmFt)
                    .ToList();

                if (survivorCutIds.Count == 0)
                {
                    continue;
                }

                var remote = arm.RootEdge.OtherNode(jp.JunctionNode);
                var anchors = BuildOrderedAnchors(layout, jp, arm, survivorCutIds, cuts, junctionNodesToRemove);

                for (int i = 0; i < anchors.Count - 1; i++)
                {
                    var from = anchors[i];
                    var to = anchors[i + 1];
                    if (TryMakeChainOp(layout, jp, arm, remote, cuts, from, to, preFilletNodeIds, out var op))
                    {
                        ops.Add(op);
                    }
                }

                AppendStableNeighborChainOps(layout, jp, arm, survivorCutIds, cuts, anchors, junctionNodesToRemove, ops);

                if (arm.IsRunwayCenterline)
                {
                    continue;
                }

                int lastId = survivorCutIds[^1];
                int? farCutId = TryResolveSharedJunctionFarCut(jp, arm, junctionById, cuts, redirect);
                if (farCutId is null)
                {
                    farCutId = TryResolveNearestCutAtFilletedJunction(jp, arm, junctionById, cuts, redirect);
                }

                var tailFrom = anchors[^1];
                if ((arm.Terminus != TaxiwayArmTerminus.HoldShort) && (farCutId is int farId))
                {
                    if ((farId != lastId) && (!AreCutsCoincident(cuts[lastId], cuts[farId])))
                    {
                        int tailNodeId = ChainAnchorNodeId(tailFrom);
                        int farJunctionNodeId = cuts[farId].JunctionNodeId;
                        if (
                            (tailNodeId >= 0)
                            && (!TailAnchorTouchesRunwayHoldShort(layout, cuts, tailFrom))
                            && (!junctionNodesToRemove.Contains(farJunctionNodeId))
                            && HasDirectTaxiwayEdge(layout, tailNodeId, farJunctionNodeId, arm.TaxiwayName)
                            && ((tailFrom.Kind != AnchorKind.Cut) || (tailFrom.Id != farId))
                            && TryMakeChainOp(
                                layout,
                                jp,
                                arm,
                                remote,
                                cuts,
                                tailFrom,
                                new ChainAnchor(double.MaxValue, AnchorKind.Cut, farId),
                                preFilletNodeIds,
                                out var op
                            )
                        )
                        {
                            ops.Add(op);
                        }
                    }
                }
                else if (
                    (arm.Terminus != TaxiwayArmTerminus.HoldShort)
                    && (arm.Terminus == TaxiwayArmTerminus.OtherIntersection)
                    && junctionById.TryGetValue(arm.TerminalNode.Id, out var farJunction)
                    && (!farJunction.PreserveNode)
                    && junctionNodesToRemove.Contains(arm.TerminalNode.Id)
                )
                {
                    // Far junction filleted but has no surviving cuts — do not chain to a node id execute will delete.
                }
                else if (
                    (arm.Terminus != TaxiwayArmTerminus.HoldShort)
                    && (!IsSubThresholdCut(cuts[lastId]))
                    && (!IsCoincidentWithNode(cuts[lastId], arm.TerminalNode))
                    && (!IsCoincidentWithNode(cuts[lastId], jp.JunctionNode))
                    && (!AreNodesCoincident(jp.JunctionNode, arm.TerminalNode))
                )
                {
                    int terminalChainFromId = ChainAnchorNodeId(tailFrom);
                    bool terminalDirectOk =
                        (tailFrom.Kind == AnchorKind.Cut) || HasDirectTaxiwayEdge(layout, terminalChainFromId, arm.TerminalNode.Id, arm.TaxiwayName);
                    if (
                        ((tailFrom.Kind != AnchorKind.Stable) || (tailFrom.Id != arm.TerminalNode.Id))
                        && terminalDirectOk
                        && TryMakeChainOp(
                            layout,
                            jp,
                            arm,
                            remote,
                            cuts,
                            tailFrom,
                            new ChainAnchor(arm.Walk.AvailableLengthFt, AnchorKind.Stable, arm.TerminalNode.Id),
                            preFilletNodeIds,
                            out var op
                        )
                    )
                    {
                        ops.Add(op);
                    }
                }
                else if (
                    (!junctionNodesToRemove.Contains(arm.TerminalNode.Id))
                    && (!junctionNodesToRemove.Contains(remote.Id))
                    && (remote.Id != arm.TerminalNode.Id)
                )
                {
                    ops.Add(new ArmChainEdgeOp(jp.JunctionNodeId, arm.Id, null, null, arm.TerminalNode.Id, null, arm.TaxiwayName, false));
                }
            }
        }

        return ops;
    }

    /// <summary>
    /// After per-arm chains are built, reconnect pre-fillet side branches (e.g. W6 58→57) whose near endpoint
    /// appears on an arm chain but is not on the arm walk polyline.
    /// </summary>
    public static List<ArmChainEdgeOp> AppendInputStableSideBranches(
        AirportGroundLayout layout,
        IReadOnlyList<JunctionPlan> junctions,
        IReadOnlySet<int> junctionNodesToRemove,
        IReadOnlyList<ArmChainEdgeOp> existing
    )
    {
        var added = new List<ArmChainEdgeOp>();
        var existingKeys = new HashSet<(string Taxiway, int FromStable, int Terminal)>();
        foreach (var op in existing)
        {
            if (op.FromStableNodeId is int from && op.TerminalNodeId is int terminal)
            {
                existingKeys.Add((op.TaxiwayName, from, terminal));
            }
        }

        foreach (var jp in junctions)
        {
            foreach (var arm in jp.Arms)
            {
                if (arm.IsRunwayCenterline)
                {
                    continue;
                }

                var armNodeIds = new HashSet<int> { jp.JunctionNodeId, arm.TerminalNode.Id, arm.RootEdge.OtherNode(jp.JunctionNode).Id };
                foreach (var step in arm.Walk.Steps)
                {
                    armNodeIds.Add(step.FarNode.Id);
                }

                foreach (var op in existing.Where(o => (o.JunctionNodeId == jp.JunctionNodeId) && (o.ArmId == arm.Id)))
                {
                    if (op.TerminalNodeId is int terminal)
                    {
                        armNodeIds.Add(terminal);
                    }

                    if (op.FromStableNodeId is int fromStable)
                    {
                        armNodeIds.Add(fromStable);
                    }
                }

                foreach (var edge in layout.Edges.OfType<GroundEdge>())
                {
                    if (edge.TaxiwayName.StartsWith("RWY:", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!string.Equals(edge.TaxiwayName, arm.TaxiwayName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    int a = edge.Nodes[0].Id;
                    int b = edge.Nodes[1].Id;
                    if (junctionNodesToRemove.Contains(a) || junctionNodesToRemove.Contains(b))
                    {
                        continue;
                    }

                    if (!layout.Nodes.TryGetValue(a, out var nodeA) || !layout.Nodes.TryGetValue(b, out var nodeB))
                    {
                        continue;
                    }

                    if ((nodeA.Type != GroundNodeType.TaxiwayIntersection) || (nodeB.Type != GroundNodeType.TaxiwayIntersection))
                    {
                        continue;
                    }

                    if (armNodeIds.Contains(a) && !armNodeIds.Contains(b))
                    {
                        TryAddSideBranch(jp, arm, a, b, edge, existingKeys, added);
                    }

                    if (armNodeIds.Contains(b) && !armNodeIds.Contains(a))
                    {
                        TryAddSideBranch(jp, arm, b, a, edge, existingKeys, added);
                    }
                }
            }
        }

        return added;
    }

    private static void TryAddSideBranch(
        JunctionPlan jp,
        TaxiwayArm arm,
        int fromStable,
        int terminal,
        GroundEdge inputEdge,
        HashSet<(string Taxiway, int FromStable, int Terminal)> existingKeys,
        List<ArmChainEdgeOp> added
    )
    {
        var key = (inputEdge.TaxiwayName, fromStable, terminal);
        if (!existingKeys.Add(key))
        {
            return;
        }

        added.Add(
            new ArmChainEdgeOp(jp.JunctionNodeId, arm.Id, null, null, terminal, fromStable, inputEdge.TaxiwayName, arm.RootEdge.IsRunwayCenterline)
        );
    }

    private static List<ChainAnchor> BuildOrderedAnchors(
        AirportGroundLayout layout,
        JunctionPlan jp,
        TaxiwayArm arm,
        IReadOnlyList<int> survivorCutIds,
        IReadOnlyDictionary<int, ResolvedArmCut> cuts,
        IReadOnlySet<int> junctionNodesToRemove
    )
    {
        var remote = arm.RootEdge.OtherNode(jp.JunctionNode);
        var anchors = new List<ChainAnchor>();

        if (!junctionNodesToRemove.Contains(remote.Id))
        {
            anchors.Add(new ChainAnchor(0, AnchorKind.Remote, remote.Id));
        }

        foreach (int cutId in survivorCutIds)
        {
            if (!IsSubThresholdCut(cuts[cutId]) && (!IsCoincidentWithNode(cuts[cutId], remote)))
            {
                anchors.Add(new ChainAnchor(cuts[cutId].DistanceAlongArmFt, AnchorKind.Cut, cutId));
            }
        }

        if (!arm.IsRunwayCenterline)
        {
            foreach (var step in arm.Walk.Steps)
            {
                int nodeId = step.FarNode.Id;
                if (nodeId == jp.JunctionNodeId)
                {
                    continue;
                }

                if (!layout.Nodes.ContainsKey(nodeId))
                {
                    continue;
                }

                if (junctionNodesToRemove.Contains(nodeId))
                {
                    continue;
                }

                if (IsCoincidentWithAnyCut(cuts, survivorCutIds, step.FarNode))
                {
                    continue;
                }

                anchors.Add(new ChainAnchor(step.CumulativeDistFt, AnchorKind.Stable, nodeId));
            }
        }

        anchors.Sort((a, b) => a.DistFt.CompareTo(b.DistFt));
        return DeduplicateAnchors(anchors);
    }

    /// <summary>
    /// Pre-fillet edges from a chained stable node to a side-branch neighbor (e.g. W6 58→57) are not on the arm walk
    /// but must survive execute — emit explicit chain segments so they are not orphaned.
    /// </summary>
    private static void AppendStableNeighborChainOps(
        AirportGroundLayout layout,
        JunctionPlan jp,
        TaxiwayArm arm,
        IReadOnlyList<int> survivorCutIds,
        IReadOnlyDictionary<int, ResolvedArmCut> cuts,
        List<ChainAnchor> anchors,
        IReadOnlySet<int> junctionNodesToRemove,
        List<ArmChainEdgeOp> ops
    )
    {
        if (arm.IsRunwayCenterline)
        {
            return;
        }

        var chainNodeIds = new HashSet<int> { jp.JunctionNodeId, arm.TerminalNode.Id };
        foreach (var step in arm.Walk.Steps)
        {
            chainNodeIds.Add(step.FarNode.Id);
        }

        foreach (int cutId in survivorCutIds)
        {
            if (!cuts.TryGetValue(cutId, out var cut))
            {
                continue;
            }

            foreach (var step in arm.Walk.Steps)
            {
                if (IsCoincidentWithNode(cut, step.FarNode))
                {
                    chainNodeIds.Add(step.FarNode.Id);
                }
            }
        }

        foreach (int nodeId in chainNodeIds)
        {
            if ((nodeId == jp.JunctionNodeId) || junctionNodesToRemove.Contains(nodeId) || !layout.Nodes.TryGetValue(nodeId, out var node))
            {
                continue;
            }

            foreach (var edge in node.Edges.OfType<GroundEdge>())
            {
                int other = edge.OtherNodeId(nodeId);
                if (chainNodeIds.Contains(other) || junctionNodesToRemove.Contains(other) || !layout.Nodes.ContainsKey(other))
                {
                    continue;
                }

                if (
                    ops.Any(o =>
                        (o.ArmId == arm.Id)
                        && (o.JunctionNodeId == jp.JunctionNodeId)
                        && (
                            ((o.FromStableNodeId == nodeId) && (o.TerminalNodeId == other))
                            || ((o.FromStableNodeId == other) && (o.TerminalNodeId == nodeId))
                        )
                    )
                )
                {
                    continue;
                }

                ops.Add(new ArmChainEdgeOp(jp.JunctionNodeId, arm.Id, null, null, other, nodeId, edge.TaxiwayName, arm.RootEdge.IsRunwayCenterline));
            }
        }
    }

    private static List<ChainAnchor> DeduplicateAnchors(List<ChainAnchor> sorted)
    {
        if (sorted.Count == 0)
        {
            return sorted;
        }

        var deduped = new List<ChainAnchor> { sorted[0] };
        for (int i = 1; i < sorted.Count; i++)
        {
            var prev = deduped[^1];
            var cur = sorted[i];
            if ((prev.Kind == cur.Kind) && (prev.Id == cur.Id))
            {
                continue;
            }

            if (
                (Math.Abs(prev.DistFt - cur.DistFt) <= FilletConstants.CoincidentNodeThresholdFt)
                && (prev.Kind == AnchorKind.Cut || cur.Kind == AnchorKind.Cut)
            )
            {
                if (cur.Kind == AnchorKind.Cut)
                {
                    deduped[^1] = cur;
                }

                continue;
            }

            deduped.Add(cur);
        }

        return deduped;
    }

    private static bool TryMakeChainOp(
        AirportGroundLayout layout,
        JunctionPlan jp,
        TaxiwayArm arm,
        GroundNode remote,
        IReadOnlyDictionary<int, ResolvedArmCut> cuts,
        ChainAnchor from,
        ChainAnchor to,
        IReadOnlySet<int> preFilletNodeIds,
        out ArmChainEdgeOp op
    )
    {
        op = null!;

        if ((from.Kind == to.Kind) && (from.Id == to.Id))
        {
            return false;
        }

        if ((from.Kind == AnchorKind.Cut) && (to.Kind == AnchorKind.Cut) && AreCutsCoincident(cuts[from.Id], cuts[to.Id]))
        {
            return false;
        }

        if (
            (from.Kind == AnchorKind.Cut)
            && (to.Kind == AnchorKind.Stable)
            && layout.Nodes.TryGetValue(to.Id, out var toStable)
            && IsCoincidentWithNode(cuts[from.Id], toStable)
        )
        {
            return false;
        }

        if (
            (from.Kind == AnchorKind.Cut)
            && (to.Kind == AnchorKind.Stable)
            && layout.Nodes.TryGetValue(to.Id, out var toHoldShort)
            && (toHoldShort.Type == GroundNodeType.RunwayHoldShort)
            && (!IsCoincidentWithNode(cuts[from.Id], toHoldShort))
        )
        {
            return false;
        }

        if (
            (from.Kind == AnchorKind.Stable)
            && (to.Kind == AnchorKind.Cut)
            && layout.Nodes.TryGetValue(from.Id, out var fromStableNode)
            && IsCoincidentWithNode(cuts[to.Id], fromStableNode)
        )
        {
            return false;
        }

        if (
            (from.Kind == AnchorKind.Stable)
            && (to.Kind == AnchorKind.Cut)
            && (!HasDirectTaxiwayEdge(layout, from.Id, cuts[to.Id].JunctionNodeId, arm.TaxiwayName))
        )
        {
            return false;
        }

        if ((from.Kind == AnchorKind.Cut) && (to.Kind == AnchorKind.Cut) && CutIsCoincidentWithHoldShort(layout, cuts, from.Id))
        {
            return false;
        }

        if ((from.Kind == AnchorKind.Remote) && (to.Kind == AnchorKind.Cut) && IsCoincidentWithNode(cuts[to.Id], remote))
        {
            return false;
        }

        if (
            (from.Kind == AnchorKind.Stable)
            && (to.Kind == AnchorKind.Stable)
            && layout.Nodes.TryGetValue(from.Id, out var fromStableForPair)
            && layout.Nodes.TryGetValue(to.Id, out var toStableForPair)
            && AreNodesCoincident(fromStableForPair, toStableForPair)
        )
        {
            return false;
        }

        int? fromCut = from.Kind == AnchorKind.Cut ? from.Id : null;
        int? fromStable = from.Kind == AnchorKind.Stable ? from.Id : null;
        int? toCut = to.Kind == AnchorKind.Cut ? to.Id : null;
        int? terminal = to.Kind is AnchorKind.Stable or AnchorKind.Remote ? to.Id : null;

        if ((fromStable is null) && (fromCut is null) && (from.Kind != AnchorKind.Remote))
        {
            return false;
        }

        if (
            (to.Kind == AnchorKind.Stable)
            && layout.Nodes.TryGetValue(to.Id, out var terminalStable)
            && ShouldSkipTerminalStableWithoutDirectPreFilletEdge(layout, preFilletNodeIds, arm, from, remote, cuts, terminalStable)
        )
        {
            return false;
        }

        op = new ArmChainEdgeOp(jp.JunctionNodeId, arm.Id, fromCut, toCut, terminal, fromStable, arm.TaxiwayName, arm.RootEdge.IsRunwayCenterline);
        return true;
    }

    private static bool IsCoincidentWithAnyCut(IReadOnlyDictionary<int, ResolvedArmCut> cuts, IReadOnlyList<int> survivorCutIds, GroundNode node)
    {
        foreach (int cutId in survivorCutIds)
        {
            if (IsCoincidentWithNode(cuts[cutId], node))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TailAnchorTouchesRunwayHoldShort(
        AirportGroundLayout layout,
        IReadOnlyDictionary<int, ResolvedArmCut> cuts,
        ChainAnchor tailFrom
    ) => (tailFrom.Kind == AnchorKind.Cut) && CutIsCoincidentWithHoldShort(layout, cuts, tailFrom.Id);

    private static bool CutIsCoincidentWithHoldShort(AirportGroundLayout layout, IReadOnlyDictionary<int, ResolvedArmCut> cuts, int cutId)
    {
        if (!cuts.TryGetValue(cutId, out var cut))
        {
            return false;
        }

        foreach (var node in layout.Nodes.Values)
        {
            if ((node.Type == GroundNodeType.RunwayHoldShort) && IsCoincidentWithNode(cut, node))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsSubThresholdCut(ResolvedArmCut cut) => cut.DistanceAlongArmFt <= FilletConstants.CoincidentNodeThresholdFt;

    private static bool IsCoincidentWithNode(ResolvedArmCut cut, GroundNode node)
    {
        double gapFt = GeoMath.DistanceNm(cut.Position, node.Position) * GeoMath.FeetPerNm;
        return gapFt <= FilletConstants.CoincidentNodeThresholdFt;
    }

    private static bool AreCutsCoincident(ResolvedArmCut a, ResolvedArmCut b)
    {
        double gapFt = GeoMath.DistanceNm(a.Position, b.Position) * GeoMath.FeetPerNm;
        return gapFt <= FilletConstants.CoincidentNodeThresholdFt;
    }

    private static bool AreNodesCoincident(GroundNode a, GroundNode b)
    {
        double gapFt = GeoMath.DistanceNm(a.Position, b.Position) * GeoMath.FeetPerNm;
        return gapFt <= FilletConstants.CoincidentNodeThresholdFt;
    }

    private static int ChainAnchorNodeId(ChainAnchor anchor) =>
        anchor.Kind switch
        {
            AnchorKind.Remote => anchor.Id,
            AnchorKind.Stable => anchor.Id,
            _ => -1,
        };

    /// <summary>True when pre-fillet layout has a one-hop same-taxiway edge (no invented shortcuts).</summary>
    private static bool HasDirectTaxiwayEdge(AirportGroundLayout layout, int fromNodeId, int toNodeId, string taxiwayName)
    {
        if ((fromNodeId < 0) || (fromNodeId == toNodeId))
        {
            return fromNodeId == toNodeId;
        }

        if (!layout.Nodes.TryGetValue(fromNodeId, out var fromNode))
        {
            return false;
        }

        foreach (var edge in fromNode.Edges.OfType<GroundEdge>())
        {
            if (!string.Equals(edge.TaxiwayName, taxiwayName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (edge.OtherNodeId(fromNodeId) == toNodeId)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Do not synthesize a chain to a pre-fillet stable that is coincident with another pre-fillet intersection
    /// unless the pre-fillet graph already had a direct same-taxiway edge from the chain source.
    /// </summary>
    private static bool ShouldSkipTerminalStableWithoutDirectPreFilletEdge(
        AirportGroundLayout layout,
        IReadOnlySet<int> preFilletNodeIds,
        TaxiwayArm arm,
        ChainAnchor from,
        GroundNode remote,
        IReadOnlyDictionary<int, ResolvedArmCut> cuts,
        GroundNode terminalStable
    )
    {
        if (!preFilletNodeIds.Contains(terminalStable.Id))
        {
            return false;
        }

        int fromNodeId = ChainAnchorNodeId(from);
        if (fromNodeId < 0)
        {
            fromNodeId = remote.Id;
        }

        if (HasDirectTaxiwayEdge(layout, fromNodeId, terminalStable.Id, arm.TaxiwayName))
        {
            return false;
        }

        foreach (var step in arm.Walk.Steps)
        {
            if (!preFilletNodeIds.Contains(step.FarNode.Id))
            {
                continue;
            }

            if (!AreNodesCoincident(terminalStable, step.FarNode))
            {
                continue;
            }

            if (step.FarNode.Id == terminalStable.Id)
            {
                continue;
            }

            return true;
        }

        foreach (var kv in layout.Nodes)
        {
            if (!preFilletNodeIds.Contains(kv.Key))
            {
                continue;
            }

            if (kv.Key == terminalStable.Id)
            {
                continue;
            }

            if (kv.Value.Type != GroundNodeType.TaxiwayIntersection)
            {
                continue;
            }

            if (!AreNodesCoincident(terminalStable, kv.Value))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    /// <summary>When the arm ends at another filleted junction, chain to that junction's near cut instead of a node id that execute will delete.</summary>
    private static int? TryResolveSharedJunctionFarCut(
        JunctionPlan junction,
        TaxiwayArm arm,
        IReadOnlyDictionary<int, JunctionPlan> junctionById,
        IReadOnlyDictionary<int, ResolvedArmCut> cuts,
        IReadOnlyDictionary<int, int> redirect
    )
    {
        if (arm.Terminus != TaxiwayArmTerminus.OtherIntersection)
        {
            return null;
        }

        int farNodeId = arm.TerminalNode.Id;
        if (!junctionById.TryGetValue(farNodeId, out var farJunction))
        {
            return null;
        }

        if (farJunction.PreserveNode)
        {
            return null;
        }

        var returnArm = FindReturnArmOnSharedTaxiway(farJunction, junction.JunctionNodeId, arm.TaxiwayName);
        if (returnArm is null)
        {
            return null;
        }

        var farCutIds = cuts
            .Values.Where(c => (c.JunctionNodeId == farJunction.JunctionNodeId) && (c.ArmId == returnArm.Id))
            .Select(c => FilletPlanCutRedirect.Resolve(c.CutId, redirect))
            .Distinct()
            .OrderBy(id => cuts[id].DistanceAlongArmFt)
            .ToList();

        return farCutIds.Count == 0 ? null : farCutIds[0];
    }

    /// <summary>
    /// When the far junction is filleted, chain to the nearest surviving cut there (by distance from the near junction).
    /// Used when taxiway-matched return-arm lookup fails.
    /// </summary>
    private static int? TryResolveNearestCutAtFilletedJunction(
        JunctionPlan nearJunction,
        TaxiwayArm arm,
        IReadOnlyDictionary<int, JunctionPlan> junctionById,
        IReadOnlyDictionary<int, ResolvedArmCut> cuts,
        IReadOnlyDictionary<int, int> redirect
    )
    {
        if (arm.Terminus != TaxiwayArmTerminus.OtherIntersection)
        {
            return null;
        }

        int farNodeId = arm.TerminalNode.Id;
        if (!junctionById.TryGetValue(farNodeId, out var farJunction) || farJunction.PreserveNode)
        {
            return null;
        }

        var nearPos = nearJunction.JunctionNode.Position;
        var nearest = cuts
            .Values.Where(c => c.JunctionNodeId == farJunction.JunctionNodeId)
            .Select(c => FilletPlanCutRedirect.Resolve(c.CutId, redirect))
            .Distinct()
            .Select(id => cuts[id])
            .OrderBy(c => GeoMath.DistanceNm(nearPos, c.Position))
            .FirstOrDefault();

        return nearest?.CutId;
    }

    private static TaxiwayArm? FindReturnArmOnSharedTaxiway(JunctionPlan farJunction, int nearJunctionNodeId, string taxiwayName)
    {
        var junctionNode = farJunction.JunctionNode;
        return farJunction.Arms.FirstOrDefault(a =>
        {
            if (!string.Equals(a.TaxiwayName, taxiwayName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (ArmWalkIncludesNode(a, nearJunctionNodeId))
            {
                return true;
            }

            if (a.RootEdge.OtherNode(junctionNode).Id == nearJunctionNodeId)
            {
                return true;
            }

            return a.TerminalNode.Id == nearJunctionNodeId;
        });
    }

    private static bool ArmWalkIncludesNode(TaxiwayArm arm, int nodeId) =>
        (arm.TerminalNode.Id == nodeId) || arm.Walk.Steps.Any(s => s.FarNode.Id == nodeId);
}
