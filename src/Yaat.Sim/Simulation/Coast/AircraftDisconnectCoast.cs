using System.Collections.Immutable;

namespace Yaat.Sim.Simulation.Coast;

/// <summary>
/// What a track removed from the world keeps coasting on: the last position and velocity it dead-reckons forward from,
/// and one facet per display it was showing on when it went.
/// </summary>
/// <param name="Anchor">The last position.</param>
/// <param name="AnchorTrackDeg">The last true ground track, in degrees.</param>
/// <param name="AnchorGroundSpeed">The last ground speed, in knots.</param>
/// <param name="CoastStartSimSeconds">The scenario-elapsed second the track was removed.</param>
/// <param name="Facets">The displays still coasting it, ERAM first, then ASDE-X, then SAID, each in ordinal airport order.</param>
public sealed record AircraftDisconnectCoast(
    LatLon Anchor,
    double AnchorTrackDeg,
    double AnchorGroundSpeed,
    double CoastStartSimSeconds,
    ImmutableArray<DisconnectCoastFacet> Facets
);
