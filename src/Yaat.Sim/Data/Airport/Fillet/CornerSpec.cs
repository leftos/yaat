namespace Yaat.Sim.Data.Airport.Fillet;

/// <summary>
/// One corner the planner emitted. <see cref="RequestedRadiusFt"/> is the corner's single radius ceiling (a corner fillet
/// override's radius, else the edge types' maximum); <see cref="Force"/> says whether a corner fillet override forces it.
/// </summary>
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
    double BearingBToJunctionDeg,
    CornerForce Force
)
{
    /// <summary>True when a corner fillet override forces the corner, whether or not the planner would plan it anyway.</summary>
    public bool IsForced => Force != CornerForce.None;
}

/// <summary>Whether, and how, a corner fillet override forces a corner.</summary>
internal enum CornerForce
{
    /// <summary>No override forces the corner.</summary>
    None,

    /// <summary>
    /// The override forces a corner the planner plans anyway: it is sized with the junction's other corners as without the
    /// force, and only the later radius-floor drops (the degenerate-radius straightening and the executor's chord) let it
    /// through.
    /// </summary>
    Kept,

    /// <summary>
    /// The planner plans the corner only because the override forces it (it would skip it as ineligible, collinear,
    /// near-hairpin or below the radius floor): its tangent stays out of its arms' shared sizing, so the junction's other
    /// corners keep the cuts they have without the force.
    /// </summary>
    Added,
}
