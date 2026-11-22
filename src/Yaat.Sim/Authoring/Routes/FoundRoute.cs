namespace Yaat.Sim.Authoring.Routes;

/// <summary>
/// A route a source offers between two airports.
/// </summary>
/// <param name="RouteText">The route as filed, e.g. "SSTIK5 SUSEY EBAYE BURGL IRNMN2".</param>
/// <param name="Count">How many flights were observed filing it; 0 for a published route.</param>
/// <param name="Sources">The names of the sources that offered it, in source order.</param>
/// <param name="Published">What the navdata source knows about a published SID and STAR route; null for an observed route.</param>
/// <param name="Preferred">What the FAA publishes about a preferred route; null for a route that is not one.</param>
/// <param name="Observed">
/// The most-flown airline prefix and aircraft type the SWIM route history saw with the route; null for routes from other sources.
/// </param>
public sealed record FoundRoute(
    string RouteText,
    int Count,
    IReadOnlyList<string> Sources,
    PublishedRouteInfo? Published,
    PreferredRouteInfo? Preferred,
    ObservedRouteSample? Observed
);

/// <summary>
/// A route source that failed or answered only from a stale cache.
/// </summary>
/// <param name="Source">The source's <see cref="IRouteSource.Name"/>.</param>
/// <param name="Message">What went wrong, for display.</param>
public sealed record RouteSourceFailure(string Source, string Message);

/// <summary>
/// What <see cref="RouteFinder"/> offers: the merged, checked routes and each source's failure.
/// </summary>
/// <param name="Routes">The routes, most often observed first.</param>
/// <param name="Failures">One entry per source that failed or answered from a stale cache.</param>
public sealed record RouteFindResult(IReadOnlyList<FoundRoute> Routes, IReadOnlyList<RouteSourceFailure> Failures);
