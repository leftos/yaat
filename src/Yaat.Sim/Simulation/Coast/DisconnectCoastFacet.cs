namespace Yaat.Sim.Simulation.Coast;

/// <summary>
/// One display a disconnected track coasts on, and the sim second it stops.
/// </summary>
/// <param name="Scope">Which kind of display.</param>
/// <param name="FacilityId">The surface display's airport id; null for ERAM, whose picture is room-wide.</param>
/// <param name="IsDrop">
/// True when the surface display is at the track's destination, so the track is a drop rather than a coast; always
/// false for ERAM.
/// </param>
/// <param name="DeadlineSimSeconds">The scenario-elapsed second at or after which the facet expires.</param>
public sealed record DisconnectCoastFacet(DisconnectCoastScope Scope, string? FacilityId, bool IsDrop, double DeadlineSimSeconds);
