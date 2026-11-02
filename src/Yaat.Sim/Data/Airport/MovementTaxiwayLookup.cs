namespace Yaat.Sim.Data.Airport;

/// <summary>
/// The movement-area taxiway a position stands on, as the ground code names it: the TAXI handler's occupied-taxiway rule and the
/// pilot's "holding position on {taxiway}" report both read it. Ramp taxilanes are no movement-area taxiway, so neither ever
/// names one.
/// </summary>
public static class MovementTaxiwayLookup
{
    /// <summary>
    /// The movement-area taxiway at <paramref name="position"/>: the straight taxi edge it stands mid-way along
    /// (<see cref="MidEdge"/>), else the one whose line it projects onto close by (<see cref="Nearest"/>); null when neither names one.
    /// </summary>
    public static string? At(AirportGroundLayout layout, LatLon position) => MidEdge(layout, position) ?? Nearest(layout, position);

    /// <summary>
    /// The taxiway of the straight taxi edge <paramref name="position"/> stands mid-way along
    /// (<see cref="AirportGroundLayout.FindMidEdgeTaxiStart"/>), whatever a stale current taxiway says: the start node is an end of
    /// the edge and can lie on the stale one too (SFO: on F, its far end on E). Null when it stands mid-way along none, the edge is
    /// unnamed, or it is a ramp taxilane, which is no movement-area taxiway.
    /// </summary>
    public static string? MidEdge(AirportGroundLayout layout, LatLon position) =>
        (layout.FindMidEdgeTaxiStart(position) is { TaxiwayName.Length: > 0 } midEdge)
        && (MovementAreaClassification.For(layout).IsMovementArea(midEdge.TaxiwayName))
            ? midEdge.TaxiwayName
            : null;

    /// <summary>
    /// The movement-area taxiway whose line <paramref name="position"/> projects onto within
    /// <see cref="RampLaneReposition.CurrentLaneMaxFt"/> (the ground code's nearest taxi edge), or null: none is that close, its
    /// name is blank, or it is a ramp taxilane.
    /// </summary>
    public static string? Nearest(AirportGroundLayout layout, LatLon position)
    {
        if (layout.FindNearestTaxiEdge(position) is not { } nearest)
        {
            return null;
        }

        string name = nearest.Edge.TaxiwayName;
        return
            ((nearest.DistNm * GeoMath.FeetPerNm) <= RampLaneReposition.CurrentLaneMaxFt)
            && (name.Length > 0)
            && (MovementAreaClassification.For(layout).IsMovementArea(name))
            ? name
            : null;
    }
}
