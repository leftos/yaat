namespace Yaat.Sim.Authoring.Routes;

/// <summary>
/// One bucket of the SWIM route history: how many flights filed one route between two airports on one UTC day, with one
/// airline prefix and aircraft type. The route text is <see cref="RouteFinder"/>'s normalized key (upper-cased, "DCT" and the
/// end airports dropped), so its variants share a bucket.
/// </summary>
/// <param name="Day">The UTC day the flights were observed.</param>
/// <param name="Departure">The departure airport's identifier, upper-cased.</param>
/// <param name="Destination">The destination airport's identifier, upper-cased.</param>
/// <param name="RouteText">The route, normalized.</param>
/// <param name="AircraftType">The ICAO aircraft type designator, upper-cased, or null when the plan carried none.</param>
/// <param name="AirlinePrefix">The callsign's three-letter airline prefix, or null for a callsign without one.</param>
/// <param name="Count">How many flights the bucket holds; at least 1.</param>
public sealed record RouteHistoryRecord(
    DateOnly Day,
    string Departure,
    string Destination,
    string RouteText,
    string? AircraftType,
    string? AirlinePrefix,
    int Count
);

/// <summary>
/// One flight plan seen on the SWIM feed, as <see cref="RouteHistoryIndex.Add"/> takes it. The callsign is reduced to its
/// airline prefix there and never stored.
/// </summary>
/// <param name="ObservedUtc">When the plan was observed, in UTC; its date is the bucket's day.</param>
/// <param name="Callsign">The flight's callsign, or null when unknown.</param>
/// <param name="AircraftType">The ICAO aircraft type designator, or null when unknown.</param>
/// <param name="Departure">The departure airport's identifier, or null when unknown.</param>
/// <param name="Destination">The destination airport's identifier, or null when unknown.</param>
/// <param name="RouteText">The filed route, or null when unknown.</param>
public sealed record RouteObservation(
    DateTime ObservedUtc,
    string? Callsign,
    string? AircraftType,
    string? Departure,
    string? Destination,
    string? RouteText
);
