namespace Yaat.Sim.Data.Airport;

/// <summary>
/// How a taxi clearance turns the aircraft about on the straight taxi edge it stood mid-way along
/// (<see cref="AirportGroundLayout.FindMidEdgeTaxiStart"/>), set by the TAXI handler on the route it assigns
/// (<see cref="TaxiRoute.TurnAboutShape"/>) and drawn as sent by the ground overlay.
/// </summary>
public enum TaxiTurnAboutShape
{
    /// <summary>The clearance turns nothing about.</summary>
    None,

    /// <summary>
    /// The route re-planned from the occupied edge's far end won, and the aircraft turns about toward that node
    /// (<see cref="TaxiRoute.TurnAboutTargetNodeId"/>), with or without the free-space leg back to it
    /// (<see cref="TaxiApproachLeg"/>): no leg is driven up to a runway holding position, for instance.
    /// </summary>
    FromFarEnd,

    /// <summary>
    /// The route from the occupied edge's node ahead was kept, and its segment 0 reverses in place over the occupied edge
    /// to the far node (<see cref="TaxiRoute.TurnAboutTargetNodeId"/>).
    /// </summary>
    InPlace,
}
