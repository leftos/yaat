namespace Yaat.Sim.Data.Airport.Fillet;

/// <summary>
/// One tangent cut on a junction's arm. <see cref="ForcedOnly"/> marks a cut only corners planned because an override forces
/// them own: it stays out of the shared-arm sizing between junctions, so the neighbours' cuts are as without the force.
/// <see cref="Pinned"/> marks a cut at a corner fillet override's pinned tangent distance: a fixed point no pass moves (no
/// shared-arm scaling, no stable-node redirect), which wins over an unpinned cut it coalesces with.
/// </summary>
internal sealed record ResolvedArmCut(
    CutId CutId,
    int JunctionNodeId,
    int ArmId,
    double DistanceAlongArmFt,
    LatLon Position,
    double BearingTowardJunctionDeg,
    IReadOnlyList<int> OwningCornerIds,
    bool ForcedOnly,
    bool Pinned
);
