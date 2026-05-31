namespace Yaat.Sim.Data.Airport.Fillet;

internal static class FilletPlanConsistency
{
    public static void ValidateCutReferences(FilletPlan plan)
    {
        var cutIds = plan.Cuts.Keys.ToHashSet();
        var stableAnchors = plan.StableAnchoredEndpointIds;

        void RequireEndpoint(FilletEndpoint ep, string context)
        {
            switch (ep)
            {
                case FilletEndpoint.Cut cut:
                    if (!cutIds.Contains(cut.Id))
                    {
                        throw new InvalidOperationException($"{context}: cut id {cut.Id.Value} is not in plan.Cuts (keys: {cutIds.Count})");
                    }

                    break;
                case FilletEndpoint.Node node:
                    if (!stableAnchors.Contains(node.NodeId))
                    {
                        throw new InvalidOperationException(
                            $"{context}: stable anchor node id {node.NodeId} is not in StableAnchoredEndpointIds (count: {stableAnchors.Count})"
                        );
                    }

                    break;
            }
        }

        foreach (var op in plan.CornerArcs)
        {
            RequireEndpoint(op.EndpointAtArmA, $"CornerArc corner {op.CornerId} armA");
            RequireEndpoint(op.EndpointAtArmB, $"CornerArc corner {op.CornerId} armB");
        }

        foreach (var op in plan.StraightConnectors)
        {
            RequireEndpoint(op.EndpointAtArmA, $"StraightConnector J{op.JunctionNodeId} corner {op.CornerId} armA");
            RequireEndpoint(op.EndpointAtArmB, $"StraightConnector J{op.JunctionNodeId} corner {op.CornerId} armB");
        }

        foreach (var op in plan.SurvivingEdges)
        {
            if (op.From is FilletEndpoint.Cut fromCut)
            {
                RequireEndpoint(fromCut, $"SurvivingEdge {op.Origin} From");
            }

            if (op.To is FilletEndpoint.Cut toCut)
            {
                RequireEndpoint(toCut, $"SurvivingEdge {op.Origin} To");
            }
        }
    }

    public static void ValidateNodeReferences(FilletPlan plan)
    {
        var removed = plan.JunctionNodesToRemove.ToHashSet();
        if (removed.Count == 0)
        {
            return;
        }

        void RequireNotRemoved(int nodeId, string context)
        {
            if (removed.Contains(nodeId))
            {
                throw new InvalidOperationException($"{context}: node {nodeId} is in JunctionNodesToRemove");
            }
        }

        foreach (var op in plan.SurvivingEdges)
        {
            if (op.From is FilletEndpoint.Node fromNode)
            {
                RequireNotRemoved(fromNode.NodeId, $"SurvivingEdge {op.Origin} From");
            }

            if (op.To is FilletEndpoint.Node toNode)
            {
                RequireNotRemoved(toNode.NodeId, $"SurvivingEdge {op.Origin} To");
            }
        }
    }
}
