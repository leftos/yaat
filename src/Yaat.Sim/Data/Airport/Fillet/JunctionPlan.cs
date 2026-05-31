namespace Yaat.Sim.Data.Airport.Fillet;

internal sealed record JunctionPlan(
    int JunctionNodeId,
    GroundNode JunctionNode,
    JunctionKind Kind,
    bool PreserveNode,
    IReadOnlyList<TaxiwayArm> Arms,
    IReadOnlyList<CornerSpec> Corners,
    IReadOnlyList<(int ArmIdA, int ArmIdB)> CollinearPairs
);
