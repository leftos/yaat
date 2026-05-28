namespace Yaat.Sim.Data.Airport.Fillet.V2;

/// <summary>Plan-time connectivity ops (arm bypass, non-arm edge reconnect) per pass-6 contract.</summary>
internal static class FilletConnectivityPlanner
{
    public static void AppendArmBypasses(
        JunctionPlan junction,
        ArmCutResolver.JunctionCutResult cutResult,
        IReadOnlySet<int> junctionNodesToRemove,
        List<ArmBypassOp> armBypasses
    )
    {
        bool junctionActive = (cutResult.Cuts.Count > 0) || (cutResult.CornerArcs.Count > 0) || (junction.CollinearPairs.Count > 0);

        if ((!junction.PreserveNode) && junctionActive)
        {
            var junctionNode = junction.JunctionNode;
            var armsWithCuts = cutResult.Cuts.Values.Select(c => c.ArmId).ToHashSet();
            foreach (var arm in junction.Arms)
            {
                if (armsWithCuts.Contains(arm.Id))
                {
                    continue;
                }

                if (junctionNodesToRemove.Contains(arm.TerminalNode.Id))
                {
                    continue;
                }

                var remote = arm.RootEdge.OtherNode(junctionNode);
                if (junctionNodesToRemove.Contains(remote.Id))
                {
                    continue;
                }

                armBypasses.Add(
                    new ArmBypassOp(junction.JunctionNodeId, arm.Id, remote.Id, arm.TerminalNode.Id, arm.TaxiwayName, arm.RootEdge.IsRunwayCenterline)
                );
            }
        }
    }

    public static void AppendReconnectEdges(
        AirportGroundLayout layout,
        JunctionPlan junction,
        IReadOnlyDictionary<int, ResolvedArmCut> cuts,
        HashSet<GroundEdge> edgesToRemove,
        List<ReconnectEdgeOp> reconnectEdges,
        List<PlanWarning> warnings
    )
    {
        var junctionNode = layout.Nodes[junction.JunctionNodeId];

        foreach (var edge in layout.Edges)
        {
            if (edgesToRemove.Contains(edge))
            {
                continue;
            }

            if (junction.Arms.Any(a => ReferenceEquals(a.RootEdge, edge) && cuts.Values.Any(c => c.ArmId == a.Id)))
            {
                continue;
            }

            bool touchesJunction = (edge.Nodes[0].Id == junctionNode.Id) || (edge.Nodes[1].Id == junctionNode.Id);
            if (!touchesJunction)
            {
                continue;
            }

            var other = edge.OtherNode(junctionNode);
            int? targetCutId = SelectTargetCut(junction, cuts, edge.TaxiwayName, other.Position);
            if (targetCutId is null)
            {
                if (junction.PreserveNode)
                {
                    reconnectEdges.Add(new ReconnectEdgeOp(junction.JunctionNodeId, other.Id, null, edge.TaxiwayName, edge.IsRunwayCenterline));
                    edgesToRemove.Add(edge);
                }
                else
                {
                    warnings.Add(
                        new PlanWarning(
                            junction.JunctionNodeId,
                            null,
                            PlanWarning.NoOwningCut,
                            $"No arm taxiway match for reconnect of edge {edge.TaxiwayName} from node {other.Id}"
                        )
                    );
                }

                continue;
            }

            reconnectEdges.Add(new ReconnectEdgeOp(junction.JunctionNodeId, other.Id, targetCutId, edge.TaxiwayName, edge.IsRunwayCenterline));
            edgesToRemove.Add(edge);
        }
    }

    private static int? SelectTargetCut(
        JunctionPlan junction,
        IReadOnlyDictionary<int, ResolvedArmCut> cuts,
        string edgeTaxiway,
        LatLon otherPosition
    )
    {
        var armsById = junction.Arms.ToDictionary(a => a.Id);
        string edgeTwy = NormalizeTaxiway(edgeTaxiway);

        var matchingCuts = cuts
            .Values.Where(c =>
            {
                if (!armsById.TryGetValue(c.ArmId, out var arm))
                {
                    return false;
                }

                return string.Equals(NormalizeTaxiway(arm.TaxiwayName), edgeTwy, StringComparison.OrdinalIgnoreCase);
            })
            .ToList();

        if (matchingCuts.Count == 0)
        {
            return null;
        }

        return matchingCuts.OrderBy(c => GeoMath.DistanceNm(otherPosition, c.Position)).First().CutId;
    }

    /// <summary>
    /// Safety net for incident edges touching a removed junction that per-junction <see cref="AppendReconnectEdges"/> missed
    /// (e.g. junction not in the active fillet set but still listed in <paramref name="junctionNodesToRemove"/>).
    /// </summary>
    public static void AppendUnconsumedReconnects(
        AirportGroundLayout layout,
        IReadOnlyDictionary<int, JunctionPlan> junctionById,
        IReadOnlyDictionary<int, ResolvedArmCut> cuts,
        IReadOnlySet<int> junctionNodesToRemove,
        HashSet<GroundEdge> edgesToRemove,
        List<ReconnectEdgeOp> reconnectEdges,
        List<PlanWarning> warnings
    )
    {
        var planned = reconnectEdges.Select(r => (r.JunctionNodeId, r.OtherNodeId)).ToHashSet();

        foreach (var edge in layout.Edges)
        {
            if (edgesToRemove.Contains(edge))
            {
                continue;
            }

            int? junctionId = null;
            int? otherId = null;
            if (junctionNodesToRemove.Contains(edge.Nodes[0].Id))
            {
                junctionId = edge.Nodes[0].Id;
                otherId = edge.Nodes[1].Id;
            }
            else if (junctionNodesToRemove.Contains(edge.Nodes[1].Id))
            {
                junctionId = edge.Nodes[1].Id;
                otherId = edge.Nodes[0].Id;
            }

            if (junctionId is not int jId || otherId is not int oId)
            {
                continue;
            }

            if (planned.Contains((jId, oId)))
            {
                continue;
            }

            if (!junctionById.TryGetValue(jId, out var junction))
            {
                warnings.Add(
                    new PlanWarning(
                        jId,
                        null,
                        PlanWarning.UnconsumedIncidentEdge,
                        $"Unconsumed edge {edge.TaxiwayName} {oId}->{jId} touches removed junction without junction plan"
                    )
                );
                continue;
            }

            if (junction.Arms.Any(a => ReferenceEquals(a.RootEdge, edge) && cuts.Values.Any(c => c.ArmId == a.Id)))
            {
                continue;
            }

            var otherNode = layout.Nodes[oId];
            int? targetCutId = SelectTargetCut(junction, cuts, edge.TaxiwayName, otherNode.Position);
            if (targetCutId is null)
            {
                if (junction.PreserveNode)
                {
                    reconnectEdges.Add(new ReconnectEdgeOp(jId, oId, null, edge.TaxiwayName, edge.IsRunwayCenterline));
                    edgesToRemove.Add(edge);
                    planned.Add((jId, oId));
                    warnings.Add(
                        new PlanWarning(
                            jId,
                            null,
                            PlanWarning.UnconsumedReconnectSafetyNet,
                            $"Safety net preserve-reconnect {oId}->{jId} twy={edge.TaxiwayName} (upstream AppendReconnectEdges missed)"
                        )
                    );
                }
                else
                {
                    warnings.Add(
                        new PlanWarning(
                            jId,
                            null,
                            PlanWarning.NoOwningCut,
                            $"Unconsumed reconnect: no arm taxiway match for edge {edge.TaxiwayName} from node {oId}"
                        )
                    );
                }

                continue;
            }

            reconnectEdges.Add(new ReconnectEdgeOp(jId, oId, targetCutId, edge.TaxiwayName, edge.IsRunwayCenterline));
            edgesToRemove.Add(edge);
            planned.Add((jId, oId));
            warnings.Add(
                new PlanWarning(
                    jId,
                    null,
                    PlanWarning.UnconsumedReconnectSafetyNet,
                    $"Safety net reconnect {oId}->cut {targetCutId} twy={edge.TaxiwayName} (upstream AppendReconnectEdges missed)"
                )
            );
        }
    }

    private static string NormalizeTaxiway(string taxiwayName) =>
        taxiwayName.StartsWith("RWY:", StringComparison.OrdinalIgnoreCase) ? taxiwayName["RWY:".Length..] : taxiwayName;
}
