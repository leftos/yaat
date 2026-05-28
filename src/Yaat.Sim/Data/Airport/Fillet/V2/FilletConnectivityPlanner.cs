namespace Yaat.Sim.Data.Airport.Fillet.V2;

/// <summary>Plan-time connectivity ops (arm bypass, non-arm edge reconnect) per pass-6 contract.</summary>
internal static class FilletConnectivityPlanner
{
    public static void AppendForJunction(
        AirportGroundLayout layout,
        JunctionPlan junction,
        ArmCutResolver.JunctionCutResult cutResult,
        HashSet<GroundEdge> edgesToRemove,
        List<ArmBypassOp> armBypasses,
        List<ReconnectEdgeOp> reconnectEdges,
        List<PlanWarning> warnings
    )
    {
        var junctionNode = layout.Nodes[junction.JunctionNodeId];
        bool junctionActive = (cutResult.Cuts.Count > 0) || (cutResult.CornerArcs.Count > 0) || (junction.CollinearPairs.Count > 0);

        if ((!junction.PreserveNode) && junctionActive)
        {
            var armsWithCuts = cutResult.Cuts.Values.Select(c => c.ArmId).ToHashSet();
            foreach (var arm in junction.Arms)
            {
                if (armsWithCuts.Contains(arm.Id))
                {
                    continue;
                }

                var remote = arm.RootEdge.OtherNode(junctionNode);
                armBypasses.Add(
                    new ArmBypassOp(junction.JunctionNodeId, arm.Id, remote.Id, arm.TerminalNode.Id, arm.TaxiwayName, arm.RootEdge.IsRunwayCenterline)
                );
            }
        }

        foreach (var edge in layout.Edges)
        {
            if (edgesToRemove.Contains(edge))
            {
                continue;
            }

            bool touchesJunction = (edge.Nodes[0].Id == junctionNode.Id) || (edge.Nodes[1].Id == junctionNode.Id);
            if (!touchesJunction)
            {
                continue;
            }

            var other = edge.OtherNode(junctionNode);
            int? targetCutId = SelectTargetCut(junction, cutResult, edge.TaxiwayName, other.Position);
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
                            $"No cut for reconnect of edge {edge.TaxiwayName} from node {other.Id}"
                        )
                    );
                }

                continue;
            }

            reconnectEdges.Add(new ReconnectEdgeOp(junction.JunctionNodeId, other.Id, targetCutId, edge.TaxiwayName, edge.IsRunwayCenterline));
            edgesToRemove.Add(edge);
        }
    }

    private static int? SelectTargetCut(JunctionPlan junction, ArmCutResolver.JunctionCutResult cutResult, string edgeTaxiway, LatLon otherPosition)
    {
        var armsById = junction.Arms.ToDictionary(a => a.Id);
        var matchingCuts = cutResult
            .Cuts.Values.Where(c =>
            {
                if (!armsById.TryGetValue(c.ArmId, out var arm))
                {
                    return false;
                }

                return string.Equals(arm.TaxiwayName, edgeTaxiway, StringComparison.OrdinalIgnoreCase)
                    || edgeTaxiway.StartsWith("RWY:", StringComparison.OrdinalIgnoreCase);
            })
            .ToList();

        if (matchingCuts.Count == 0)
        {
            matchingCuts = cutResult.Cuts.Values.ToList();
        }

        if (matchingCuts.Count == 0)
        {
            return null;
        }

        return matchingCuts.OrderBy(c => GeoMath.DistanceNm(otherPosition, c.Position)).First().CutId;
    }
}
