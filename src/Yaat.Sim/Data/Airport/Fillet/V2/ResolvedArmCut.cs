namespace Yaat.Sim.Data.Airport.Fillet.V2;

internal sealed record ResolvedArmCut(
    CutId CutId,
    int JunctionNodeId,
    int ArmId,
    double DistanceAlongArmFt,
    LatLon Position,
    double BearingTowardJunctionDeg,
    IReadOnlyList<int> OwningCornerIds
);
