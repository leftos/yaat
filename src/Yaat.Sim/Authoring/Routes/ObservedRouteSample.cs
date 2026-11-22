namespace Yaat.Sim.Authoring.Routes;

/// <summary>
/// What the SWIM route history saw flying a route: the airline prefix and aircraft type most often filed with it. No full
/// callsign is ever kept.
/// </summary>
/// <param name="AirlinePrefix">The three-letter airline prefix (e.g. "UAL"), or null for flights with no airline callsign.</param>
/// <param name="AircraftType">The ICAO aircraft type designator (e.g. "B738"), or null when the plans carried none.</param>
public sealed record ObservedRouteSample(string? AirlinePrefix, string? AircraftType);
