namespace Yaat.Sim.Data.Airport.Fillet.V2;

internal static class FilletPlanConsistency
{
    public static void ValidateCutReferences(FilletPlan plan)
    {
        var cutIds = plan.Cuts.Keys.ToHashSet();
        var stableAnchors = plan.StableAnchoredEndpointIds;
        void Require(int id, string context)
        {
            if (cutIds.Contains(id) || stableAnchors.Contains(id))
            {
                return;
            }

            throw new InvalidOperationException($"{context}: cut id {id} is not in plan.Cuts (keys: {cutIds.Count})");
        }

        foreach (var op in plan.CornerArcs)
        {
            Require(op.CutIdAtArmA, $"CornerArc corner {op.CornerId} armA");
            Require(op.CutIdAtArmB, $"CornerArc corner {op.CornerId} armB");
        }

        foreach (var op in plan.StraightConnectors)
        {
            Require(op.CutIdAtArmA, $"StraightConnector J{op.JunctionNodeId} corner {op.CornerId} armA");
            Require(op.CutIdAtArmB, $"StraightConnector J{op.JunctionNodeId} corner {op.CornerId} armB");
        }

        foreach (var op in plan.SurvivingEdges)
        {
            if (op.FromCutId is int from)
            {
                Require(from, $"SurvivingEdge {op.Origin} FromCutId");
            }

            if (op.ToCutId is int to)
            {
                Require(to, $"SurvivingEdge {op.Origin} ToCutId");
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
            if (op.FromNodeId is int from)
            {
                RequireNotRemoved(from, $"SurvivingEdge {op.Origin} FromNodeId");
            }

            if (op.ToNodeId is int to)
            {
                RequireNotRemoved(to, $"SurvivingEdge {op.Origin} ToNodeId");
            }
        }
    }
}
