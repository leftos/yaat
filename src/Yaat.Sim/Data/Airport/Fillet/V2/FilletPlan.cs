namespace Yaat.Sim.Data.Airport.Fillet.V2;

/// <summary>A union-find merge of two coincident cuts; both operands are pre-redirect cut IDs.</summary>
internal sealed record TangentMergeOp(CutId CutIdA, CutId CutIdB);

/// <summary>
/// An arc to materialize between two tangent-cut endpoints.
/// Arm endpoints are post-redirect <see cref="FilletEndpoint"/> values: either a surviving cut
/// (<see cref="FilletEndpoint.Cut"/>) or a stable anchor node (<see cref="FilletEndpoint.Node"/>).
/// Pre-redirect builders emit <see cref="FilletEndpoint.Cut"/> wrapping the raw cut ID;
/// <see cref="FilletPlanCutRedirect"/> rewrites them after tangent-merge resolution.
/// </summary>
internal sealed record CornerArcOp(int JunctionNodeId, int CornerId, FilletEndpoint EndpointAtArmA, FilletEndpoint EndpointAtArmB);

/// <summary>
/// A straight connector to materialize between two tangent-cut endpoints.
/// Arm endpoints follow the same pre/post-redirect contract as <see cref="CornerArcOp"/>.
/// </summary>
internal sealed record StraightConnectorOp(
    int JunctionNodeId,
    int CornerId,
    FilletEndpoint EndpointAtArmA,
    FilletEndpoint EndpointAtArmB,
    string TaxiwayName
);

/// <summary>
/// A surviving sub-segment of exactly one original edge, produced by the global edge-split.
/// Each endpoint is EITHER a resolved cut (<see cref="FilletEndpoint.Cut"/>) OR a
/// stable graph node (<see cref="FilletEndpoint.Node"/>). Cut endpoints are materialized by the
/// executor; node endpoints already exist.
/// </summary>
internal sealed record SurvivingEdgeOp(FilletEndpoint From, FilletEndpoint To, string TaxiwayName, bool IsRunwayCenterline, string Origin);

internal sealed record FilletPlan(
    IReadOnlyDictionary<CutId, ResolvedArmCut> Cuts,
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
            Cuts: new Dictionary<CutId, ResolvedArmCut>(),
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
