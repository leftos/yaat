namespace Yaat.Sim.Data.Airport.Fillet.V2;

internal sealed record ArmCutOp(int CutId);

internal sealed record TangentMergeOp(int CutIdA, int CutIdB);

internal sealed record CornerArcOp(int JunctionNodeId, int CornerId, int CutIdAtArmA, int CutIdAtArmB);

internal sealed record StraightConnectorOp(int JunctionNodeId, int CornerId, int CutIdAtArmA, int CutIdAtArmB, string TaxiwayName);

internal sealed record ArmBypassOp(int JunctionNodeId, int ArmId, int RemoteNodeId, int TerminalNodeId, string TaxiwayName, bool IsRunwayCenterline);

/// <param name="TargetCutId">Resolved cut at execute time; null connects to preserved junction node.</param>
internal sealed record ReconnectEdgeOp(int JunctionNodeId, int OtherNodeId, int? TargetCutId, string TaxiwayName, bool IsRunwayCenterline);

/// <summary>Restore a pre-fillet taxiway link to a hold-short that must never be left isolated after execute.</summary>
internal sealed record ReconnectHoldShortOp(int HoldShortNodeId, int IntersectionNodeId, string TaxiwayName, bool IsRunwayCenterline);

internal sealed record PreserveStubOp(int JunctionNodeId, int CutId);

/// <summary>
/// A surviving sub-segment of exactly one original edge, produced by the global edge-split.
/// Each endpoint is EITHER a resolved cut (<see cref="FromCutId"/>/<see cref="ToCutId"/>) OR a
/// stable graph node (<see cref="FromNodeId"/>/<see cref="ToNodeId"/>) — exactly one of each pair
/// is non-null. Cut endpoints are materialized by the executor; node endpoints already exist.
/// </summary>
internal sealed record SurvivingEdgeOp(
    int? FromCutId,
    int? FromNodeId,
    int? ToCutId,
    int? ToNodeId,
    string TaxiwayName,
    bool IsRunwayCenterline,
    string Origin
);

/// <summary>
/// Planned straight segment on one arm: remote→cut, cut→cut, cut→stable, stable→cut, or stable→terminal.
/// When <see cref="FromCutId"/> is null and <see cref="FromStableNodeId"/> is set, the segment starts at that pre-fillet stable node (not the arm remote).
/// </summary>
internal sealed record ArmChainEdgeOp(
    int JunctionNodeId,
    int ArmId,
    int? FromCutId,
    int? ToCutId,
    int? TerminalNodeId,
    int? FromStableNodeId,
    string TaxiwayName,
    bool IsRunwayCenterline
);

internal sealed record FilletPlan(
    IReadOnlyDictionary<int, ResolvedArmCut> Cuts,
    IReadOnlyList<ArmCutOp> ArmCuts,
    IReadOnlyList<TangentMergeOp> TangentMerges,
    IReadOnlyList<ArmChainEdgeOp> ArmChainEdges,
    IReadOnlyList<CornerArcOp> CornerArcs,
    IReadOnlyList<StraightConnectorOp> StraightConnectors,
    IReadOnlyList<ArmBypassOp> ArmBypasses,
    IReadOnlyList<ReconnectEdgeOp> ReconnectEdges,
    IReadOnlyList<ReconnectHoldShortOp> HoldShortReconnects,
    IReadOnlyList<PreserveStubOp> PreserveStubs,
    IReadOnlyList<SurvivingEdgeOp> SurvivingEdges,
    IReadOnlyList<int> JunctionNodesToRemove,
    IReadOnlySet<GroundEdge> EdgesToRemove,
    IReadOnlyList<PlanWarning> Warnings,
    IReadOnlySet<int> StableAnchoredEndpointIds
)
{
    public static FilletPlan Empty { get; } =
        new(
            Cuts: new Dictionary<int, ResolvedArmCut>(),
            ArmCuts: [],
            TangentMerges: [],
            ArmChainEdges: [],
            CornerArcs: [],
            StraightConnectors: [],
            ArmBypasses: [],
            ReconnectEdges: [],
            HoldShortReconnects: [],
            PreserveStubs: [],
            SurvivingEdges: [],
            JunctionNodesToRemove: [],
            EdgesToRemove: new HashSet<GroundEdge>(),
            Warnings: [],
            StableAnchoredEndpointIds: new HashSet<int>()
        );
}
