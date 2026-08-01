namespace Yaat.Sim.Data.MilitaryRoutes;

/// <summary>
/// The altitude published for the segment <em>terminating at</em> a route point.
///
/// AP/1B prints the altitude in the leftmost column of the row that names the point, and that
/// altitude governs the leg flown <em>into</em> the point, not out of it. This is confirmed against
/// the FAA AIS MTRSegment layer, which carries the same blocks per segment: IR-002's row for point
/// B reads "05 AGL B 60 MSL to B", and the FAA layer's A-to-B segment is 500 HEI to 6000 ALT.
///
/// Floors are frequently AGL while ceilings are MSL. YAAT has no terrain model, so an AGL bound
/// cannot be resolved exactly along a route crossing arbitrary terrain — see
/// <c>docs/military-training-routes.md</c> for how the simulation handles that.
/// </summary>
public sealed record MilitaryRouteAltitude
{
    public required MilitaryRouteAltitudeKind Kind { get; init; }

    /// <summary>The Altitude Data cell exactly as published, including any wrapped qualifier rows.</summary>
    public required string Raw { get; init; }

    public int? FloorFt { get; init; }
    public AltitudeReference? FloorReference { get; init; }
    public int? CeilingFt { get; init; }
    public AltitudeReference? CeilingReference { get; init; }

    /// <summary>True when a floor and ceiling are both published, i.e. the aircraft may fly a block.</summary>
    public bool IsBlock => FloorFt is not null && CeilingFt is not null && FloorFt != CeilingFt;

    /// <summary>
    /// True when either bound is referenced to ground level. Callers that resolve the block to MSL
    /// must consult this before trusting <see cref="FloorFt"/> as an absolute altitude.
    /// </summary>
    public bool HasAglBound => FloorReference == AltitudeReference.Agl || CeilingReference == AltitudeReference.Agl;

    public static MilitaryRouteAltitude None { get; } = new() { Kind = MilitaryRouteAltitudeKind.None, Raw = string.Empty };
}
