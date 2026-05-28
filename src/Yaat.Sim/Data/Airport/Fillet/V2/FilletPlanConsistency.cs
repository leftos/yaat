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

        foreach (var op in plan.ArmChainEdges)
        {
            if (op.FromCutId is int from)
            {
                Require(from, $"ArmChainEdge J{op.JunctionNodeId} arm {op.ArmId} FromCutId");
            }

            if (op.ToCutId is int to)
            {
                Require(to, $"ArmChainEdge J{op.JunctionNodeId} arm {op.ArmId} ToCutId");
            }
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

        foreach (var op in plan.ReconnectEdges)
        {
            if (op.TargetCutId is int target)
            {
                Require(target, $"ReconnectEdge J{op.JunctionNodeId} other {op.OtherNodeId}");
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

        foreach (var op in plan.ArmBypasses)
        {
            RequireNotRemoved(op.RemoteNodeId, $"ArmBypass J{op.JunctionNodeId} arm {op.ArmId} RemoteNodeId");
            RequireNotRemoved(op.TerminalNodeId, $"ArmBypass J{op.JunctionNodeId} arm {op.ArmId} TerminalNodeId");
        }

        foreach (var op in plan.ArmChainEdges)
        {
            if (op.TerminalNodeId is int terminal)
            {
                RequireNotRemoved(terminal, $"ArmChainEdge J{op.JunctionNodeId} arm {op.ArmId} TerminalNodeId");
            }

            if (op.FromStableNodeId is int stableFrom)
            {
                RequireNotRemoved(stableFrom, $"ArmChainEdge J{op.JunctionNodeId} arm {op.ArmId} FromStableNodeId");
            }
        }

        foreach (var op in plan.ReconnectEdges)
        {
            if (op.TargetCutId is null)
            {
                RequireNotRemoved(op.JunctionNodeId, $"ReconnectEdge J{op.JunctionNodeId} other {op.OtherNodeId} TargetCutId=null");
            }
        }
    }
}
