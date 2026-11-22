namespace Yaat.Sim.Authoring.Routes;

/// <summary>
/// The YAAT server's reply to a route history search (<c>GET api/route-history</c>), in camelCase JSON.
/// </summary>
/// <param name="Routes">The routes the history holds for the search, most flown first.</param>
/// <param name="Message">Why the server could not answer fully (e.g. route history is not enabled), or null when it did.</param>
public sealed record RouteHistoryResponse(IReadOnlyList<RouteHistoryRow> Routes, string? Message);

/// <summary>
/// One route in a <see cref="RouteHistoryResponse"/>.
/// </summary>
/// <param name="Route">The route, normalized.</param>
/// <param name="Count">How many flights filed it within the history's window.</param>
/// <param name="AirlinePrefix">The most-flown airline prefix seen with it, or null.</param>
/// <param name="AircraftType">The most-flown aircraft type seen with it, or null.</param>
public sealed record RouteHistoryRow(string Route, int Count, string? AirlinePrefix, string? AircraftType);
