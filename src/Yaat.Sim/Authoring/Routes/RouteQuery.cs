namespace Yaat.Sim.Authoring.Routes;

/// <summary>
/// What the editor asks route sources for: the routes flown or published between two airports, optionally narrowed.
/// </summary>
/// <param name="Departure">The departure airport's ICAO identifier, e.g. "KSFO".</param>
/// <param name="Destination">The destination airport's ICAO identifier, e.g. "KLAX".</param>
/// <param name="Fix">A fix the route must pass, or null for any route.</param>
/// <param name="AirlinePrefix">A callsign prefix the observed flights must start with (e.g. "UAL"), or null for any.</param>
/// <param name="AircraftType">An ICAO aircraft type designator the observed flights must have, or null for any.</param>
/// <param name="Wake">A wake-turbulence class the routes are for, or null for any; sources that cannot filter on it ignore it.</param>
public sealed record RouteQuery(
    string Departure,
    string Destination,
    string? Fix = null,
    string? AirlinePrefix = null,
    string? AircraftType = null,
    WakeTurbulenceData.WakeClass? Wake = null
);
