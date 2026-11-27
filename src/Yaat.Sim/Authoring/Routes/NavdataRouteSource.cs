using Yaat.Sim.Data;

namespace Yaat.Sim.Authoring.Routes;

/// <summary>
/// Published routes from navdata: each SID at the departure joined direct to each STAR at the destination, written
/// "SID EXIT ENTRY STAR" (a shared exit and entry fix written once). For each SID and STAR only the shortest transition
/// pair is offered, and only when its expanded length is at most the larger of 1.25 times the airport-to-airport great
/// circle and the great circle plus 50 NM. Runway and equipment filters apply only when the query carries runways or an
/// equipment suffix; the query's fix, when set, must be on the expanded route. Routes come in rank order (ratio, length,
/// SID, STAR), with at most two rows per SID and per STAR among the first ten. Each route's count is 0.
/// </summary>
/// <param name="navDb">The navdata the SIDs, STARs and fixes come from.</param>
public sealed class NavdataRouteSource(NavigationDatabase navDb) : IRouteSource
{
    private readonly NavigationDatabase _navDb = navDb;

    /// <summary>The source's name, as the routes it offers carry it.</summary>
    public const string SourceName = "navdata";

    /// <inheritdoc />
    public string Name => SourceName;

    /// <inheritdoc />
    public Task<RouteSourceResult> FindAsync(RouteQuery query, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        List<FoundRoute> routes = NavdataRoutePairer.FindRoutes(_navDb, query, Name, ct);
        return Task.FromResult(new RouteSourceResult(routes, FailureMessage: null));
    }
}
