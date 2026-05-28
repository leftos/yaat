namespace Yaat.Sim.Data.Airport.Fillet.V2;

internal sealed record CornerSpec(
    int CornerId,
    int JunctionNodeId,
    int ArmIdA,
    int ArmIdB,
    GroundEdge EdgeA,
    GroundEdge EdgeB,
    double TurnAngleDeg,
    double RequestedRadiusFt,
    double IdealTangentFt,
    double BearingAToJunctionDeg,
    double BearingBToJunctionDeg
);
