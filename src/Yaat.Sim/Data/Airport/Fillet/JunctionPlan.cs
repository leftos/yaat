namespace Yaat.Sim.Data.Airport.Fillet;

/// <summary>
/// One junction the pass fillets. <see cref="CollinearPairs"/> are its through pairs; <see cref="SuppressedPairs"/> the arm
/// pairs a corner fillet override suppresses. Either keeps the junction node (<see cref="PreserveNode"/>).
/// </summary>
internal sealed record JunctionPlan(
    int JunctionNodeId,
    GroundNode JunctionNode,
    JunctionKind Kind,
    bool PreserveNode,
    IReadOnlyList<TaxiwayArm> Arms,
    IReadOnlyList<CornerSpec> Corners,
    IReadOnlyList<(int ArmIdA, int ArmIdB)> CollinearPairs,
    IReadOnlyList<(int ArmIdA, int ArmIdB)> SuppressedPairs
);
