namespace Yaat.Sim.Authoring.Routes;

/// <summary>
/// A place real routes between two airports come from: observed flights (csko) or published procedures.
/// </summary>
public interface IRouteSource
{
    /// <summary>Gets the source's name, as shown beside each route it offers and in its failure, e.g. "csko".</summary>
    string Name { get; }

    /// <summary>
    /// Finds the routes this source knows for <paramref name="query"/>. <see cref="RouteFinder"/> runs each source on the
    /// thread pool, so an implementation may do synchronous work (file reads, parsing) before or instead of awaiting.
    /// </summary>
    /// <param name="query">The airports and filters to search for.</param>
    /// <param name="ct">Cancels the search; cancellation surfaces as <see cref="OperationCanceledException"/>.</param>
    /// <returns>The routes found, and a failure message when the source could not answer fully (its routes may then be stale).</returns>
    Task<RouteSourceResult> FindAsync(RouteQuery query, CancellationToken ct);
}

/// <summary>
/// One source's answer to a <see cref="RouteQuery"/>.
/// </summary>
/// <param name="Routes">The routes found, possibly from a stale cache when <paramref name="FailureMessage"/> is set.</param>
/// <param name="FailureMessage">Why the source could not answer fully, or null when it did.</param>
public sealed record RouteSourceResult(IReadOnlyList<FoundRoute> Routes, string? FailureMessage);
