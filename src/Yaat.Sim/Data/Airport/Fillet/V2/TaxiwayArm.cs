namespace Yaat.Sim.Data.Airport.Fillet.V2;

internal sealed record TaxiwayArm(
    int Id,
    int JunctionNodeId,
    GroundEdge RootEdge,
    string TaxiwayName,
    double BearingFromJunctionDeg,
    double LengthFt,
    double IntersectionCapFt,
    TaxiwayArmTerminus Terminus,
    GroundNode TerminalNode,
    bool IsRunwayCenterline,
    TaxiwayWalk.WalkResult Walk
);
