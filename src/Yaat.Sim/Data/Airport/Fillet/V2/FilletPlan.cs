namespace Yaat.Sim.Data.Airport.Fillet.V2;

internal sealed record ArmCutOp(int CutId);

internal sealed record TangentMergeOp(int CutIdA, int CutIdB);

internal sealed record CornerArcOp(int CornerId, int CutIdAtArmA, int CutIdAtArmB);

internal sealed record PreserveStubOp(int JunctionNodeId, int CutId);

internal sealed record FilletPlan(
    IReadOnlyDictionary<int, ResolvedArmCut> Cuts,
    IReadOnlyList<ArmCutOp> ArmCuts,
    IReadOnlyList<TangentMergeOp> TangentMerges,
    IReadOnlyList<CornerArcOp> CornerArcs,
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
            PreserveStubs: [],
            JunctionNodesToRemove: [],
            EdgesToRemove: new HashSet<GroundEdge>(),
            Warnings: []
        );
}
