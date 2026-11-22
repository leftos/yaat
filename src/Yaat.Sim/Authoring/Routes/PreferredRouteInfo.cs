namespace Yaat.Sim.Authoring.Routes;

/// <summary>
/// What the FAA publishes about a preferred route <see cref="PreferredRouteSource"/> offers, each field the FAA's text
/// unchanged (null when the FAA leaves it empty).
/// </summary>
/// <param name="Type">The route type code: "H", "L", "HPD", "LPD", "SHD", "SLD" or "TEC".</param>
/// <param name="Area">The special area description, e.g. "SFO HAF TO SMF".</param>
/// <param name="Altitude">
/// The altitude description, e.g. "5000", or on a NorCal or SoCal TEC row aircraft-class letters each followed by a number,
/// e.g. "J30" or "JM120PQ100" (J jet, M turboprop, P non-jet 190 KT or more, Q non-jet under 190 KT).
/// </param>
/// <param name="Aircraft">The aircraft the route is limited to, e.g. "JETS".</param>
/// <param name="Hours">The hours the route applies, in UTC, e.g. "1400-0000".</param>
/// <param name="Direction">
/// The FAA's ROUTE_DIR_DESCRIP text verbatim: a direction on most rows (e.g. "E BND"), but on TEC rows an airport flow
/// (e.g. "SFOW", San Francisco west plan).
/// </param>
/// <param name="Designator">The route's published designator, e.g. "SFO01" on a TEC route.</param>
public sealed record PreferredRouteInfo(
    string Type,
    string? Area,
    string? Altitude,
    string? Aircraft,
    string? Hours,
    string? Direction,
    string? Designator
);
