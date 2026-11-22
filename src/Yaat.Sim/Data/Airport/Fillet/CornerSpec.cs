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

    /// <summary>
    /// The tangent distance from the apex, feet, a corner fillet override pins on arm <see cref="ArmIdA"/>, or null when it pins
    /// none there. The planner sets it only for a pin it honours (<see cref="CornerPlanner.MinPinnedTangentFt"/> to
    /// <see cref="CornerPlanner.PinnedTangentFarMarginFt"/> short of the arm's far node, and no conflict on the arm).
    /// </summary>
    public double? PinAFt { get; init; }

    /// <summary>The honoured pinned tangent distance on arm <see cref="ArmIdB"/>, feet, as <see cref="PinAFt"/> is on arm A.</summary>
    public double? PinBFt { get; init; }

    /// <summary>True when the corner has an honoured pin on either arm.</summary>
    public bool IsPinned => (PinAFt is not null) || (PinBFt is not null);

    /// <summary>
    /// True when both arms are pinned: the arc's radius is then the one the pinned cuts allow, and
    /// <see cref="RequestedRadiusFt"/> caps nothing.
    /// </summary>
    public bool RadiusFromPins => (PinAFt is not null) && (PinBFt is not null);

    /// <summary>The honoured pin on arm <paramref name="armId"/>, feet, or null.</summary>
    public double? PinOn(int armId)
    {
        if (armId == ArmIdA)
        {
            return PinAFt;
        }

        return (armId == ArmIdB) ? PinBFt : null;
    }
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
