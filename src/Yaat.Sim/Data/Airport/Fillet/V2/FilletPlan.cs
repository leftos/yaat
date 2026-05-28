namespace Yaat.Sim.Data.Airport.Fillet.V2;

internal sealed record TangentMergeOp(int CutIdA, int CutIdB);

internal sealed record CornerArcOp(int JunctionNodeId, int CornerId, int CutIdAtArmA, int CutIdAtArmB);

internal sealed record StraightConnectorOp(int JunctionNodeId, int CornerId, int CutIdAtArmA, int CutIdAtArmB, string TaxiwayName);

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

internal sealed record FilletPlan(
    IReadOnlyDictionary<int, ResolvedArmCut> Cuts,
    IReadOnlyList<TangentMergeOp> TangentMerges,
    IReadOnlyList<CornerArcOp> CornerArcs,
    IReadOnlyList<StraightConnectorOp> StraightConnectors,
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
            TangentMerges: [],
            CornerArcs: [],
            StraightConnectors: [],
            SurvivingEdges: [],
            JunctionNodesToRemove: [],
            EdgesToRemove: new HashSet<GroundEdge>(),
            Warnings: [],
            StableAnchoredEndpointIds: new HashSet<int>()
        );
}
