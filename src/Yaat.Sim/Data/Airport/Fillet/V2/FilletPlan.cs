namespace Yaat.Sim.Data.Airport.Fillet.V2;

internal sealed record ArmCutOp(int CutId);

internal sealed record TangentMergeOp(int CutIdA, int CutIdB);

internal sealed record CornerArcOp(int CornerId, int CutIdAtArmA, int CutIdAtArmB);

internal sealed record StraightConnectorOp(int JunctionNodeId, int CornerId, int CutIdAtArmA, int CutIdAtArmB, string TaxiwayName);

internal sealed record ArmBypassOp(int JunctionNodeId, int ArmId, int RemoteNodeId, int TerminalNodeId, string TaxiwayName, bool IsRunwayCenterline);

/// <param name="TargetCutId">Resolved cut at execute time; null connects to preserved junction node.</param>
internal sealed record ReconnectEdgeOp(int JunctionNodeId, int OtherNodeId, int? TargetCutId, string TaxiwayName, bool IsRunwayCenterline);

internal sealed record PreserveStubOp(int JunctionNodeId, int CutId);

internal sealed record FilletPlan(
    IReadOnlyDictionary<int, ResolvedArmCut> Cuts,
    IReadOnlyList<ArmCutOp> ArmCuts,
    IReadOnlyList<TangentMergeOp> TangentMerges,
    IReadOnlyList<CornerArcOp> CornerArcs,
    IReadOnlyList<StraightConnectorOp> StraightConnectors,
    IReadOnlyList<ArmBypassOp> ArmBypasses,
    IReadOnlyList<ReconnectEdgeOp> ReconnectEdges,
    IReadOnlyList<PreserveStubOp> PreserveStubs,
    IReadOnlyList<int> JunctionNodesToRemove,
    IReadOnlySet<GroundEdge> EdgesToRemove,
    IReadOnlyList<PlanWarning> Warnings
)
{
    public static FilletPlan Empty { get; } =
        new(
            Cuts: new Dictionary<int, ResolvedArmCut>(),
            ArmCuts: [],
            TangentMerges: [],
            CornerArcs: [],
            StraightConnectors: [],
            ArmBypasses: [],
            ReconnectEdges: [],
            PreserveStubs: [],
            JunctionNodesToRemove: [],
            EdgesToRemove: new HashSet<GroundEdge>(),
            Warnings: []
        );
}
