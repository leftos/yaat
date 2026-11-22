using Microsoft.Extensions.Logging;
using Yaat.Sim.Data;

namespace Yaat.Sim.Authoring.Routes;

/// <summary>
/// Asks every route source at once, merges the routes they return, and offers only the routes YAAT's navdata can fly.
/// Two routes are the same when their tokens match after upper-casing, dropping "DCT" and dropping a leading departure
/// and a trailing destination token; the merged route keeps the first-seen text, sums the counts, lists every source and
/// keeps the published-route and preferred-route metadata of whichever duplicate carries each.
/// A route is offered only when <see cref="RouteExpander"/> expands it to at least one fix and every fix it emits is
/// known to the navdata (or is the departure or destination airport).
/// </summary>
/// <param name="sources">The sources to ask, in merge order: a duplicate keeps the text of the earliest source.</param>
/// <param name="navDb">The navdata each route is checked against.</param>
public sealed class RouteFinder(IReadOnlyList<IRouteSource> sources, NavigationDatabase navDb)
{
    private static readonly ILogger Log = SimLog.CreateLogger<RouteFinder>();

    private readonly IReadOnlyList<IRouteSource> _sources = sources;
    private readonly NavigationDatabase _navDb = navDb;

    /// <summary>
    /// Finds the routes every source offers for <paramref name="query"/>, merged and checked.
    /// </summary>
    /// <param name="query">The airports and filters to search for.</param>
    /// <param name="ct">Cancels the search; cancellation propagates as <see cref="OperationCanceledException"/>.</param>
    /// <returns>The offered routes, most observed first, and one failure per source that threw or reported one.</returns>
    public async Task<RouteFindResult> FindAsync(RouteQuery query, CancellationToken ct)
    {
        var tasks = _sources.Select(source => Task.Run(() => AskAsync(source, query, ct), ct)).ToList();
        (IReadOnlyList<FoundRoute> Routes, RouteSourceFailure? Failure)[] answers = await Task.WhenAll(tasks);

        List<RouteSourceFailure> failures = [.. answers.Select(answer => answer.Failure).OfType<RouteSourceFailure>()];
        List<FoundRoute> routes = [.. answers.SelectMany(answer => answer.Routes)];
        List<FoundRoute> offered = [.. Merge(routes, query).Where(route => IsExpandable(route.RouteText, query))];
        return new RouteFindResult([.. offered.OrderByDescending(route => route.Count)], failures);
    }

    /// <summary>
    /// One source's routes and its failure: the one it reported, or the exception it threw (its routes then empty).
    /// Only the caller's cancellation propagates; a source's own cancellation (an HTTP timeout) is its failure.
    /// </summary>
    private static async Task<(IReadOnlyList<FoundRoute> Routes, RouteSourceFailure? Failure)> AskAsync(
        IRouteSource source,
        RouteQuery query,
        CancellationToken ct
    )
    {
        try
        {
            RouteSourceResult result = await source.FindAsync(query, ct);
            RouteSourceFailure? failure = result.FailureMessage is { } message ? new RouteSourceFailure(source.Name, message) : null;
            return (result.Routes, failure);
        }
        catch (Exception ex) when (!((ex is OperationCanceledException) && ct.IsCancellationRequested))
        {
            Log.LogWarning(ex, "Route source {Source} failed", source.Name);
            return ([], new RouteSourceFailure(source.Name, ex.Message));
        }
    }

    private static List<FoundRoute> Merge(List<FoundRoute> routes, RouteQuery query)
    {
        var byKey = new Dictionary<string, int>(StringComparer.Ordinal);
        var merged = new List<FoundRoute>();
        foreach (FoundRoute route in routes)
        {
            string key = NormalizedKey(route.RouteText, query);
            if (!byKey.TryGetValue(key, out int index))
            {
                byKey[key] = merged.Count;
                merged.Add(route);
                continue;
            }

            FoundRoute first = merged[index];
            List<string> sources = [.. first.Sources.Union(route.Sources, StringComparer.Ordinal)];
            merged[index] = first with
            {
                Count = first.Count + route.Count,
                Sources = sources,
                Published = first.Published ?? route.Published,
                Preferred = first.Preferred ?? route.Preferred,
            };
        }

        return merged;
    }

    /// <summary>
    /// The route's duplicate key: its tokens upper-cased and single-spaced, "DCT" dropped, and a leading departure and
    /// trailing destination token dropped.
    /// </summary>
    internal static string NormalizedKey(string routeText, RouteQuery query)
    {
        List<string> tokens =
        [
            .. routeText.ToUpperInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Where(token => token != "DCT"),
        ];
        TrimAirports(tokens, query.Departure, query.Destination);
        return string.Join(' ', tokens);
    }

    /// <summary>
    /// Removes a leading token equal to <paramref name="departure"/> and a trailing token equal to
    /// <paramref name="destination"/> (case-insensitive) from <paramref name="tokens"/>.
    /// </summary>
    internal static void TrimAirports(List<string> tokens, string? departure, string? destination)
    {
        if ((tokens.Count > 0) && string.Equals(tokens[0], departure, StringComparison.OrdinalIgnoreCase))
        {
            tokens.RemoveAt(0);
        }

        if ((tokens.Count > 0) && string.Equals(tokens[^1], destination, StringComparison.OrdinalIgnoreCase))
        {
            tokens.RemoveAt(tokens.Count - 1);
        }
    }

    private bool IsExpandable(string routeText, RouteQuery query)
    {
        List<string> fixes = RouteExpander.Expand(routeText, _navDb, includeAllTransitionsOnMismatch: false);
        string? unresolved = fixes.Find(fix => !IsKnownPoint(fix, query));
        if ((fixes.Count > 0) && (unresolved is null))
        {
            return true;
        }

        if (Log.IsEnabled(LogLevel.Debug))
        {
            Log.LogDebug("Dropping route {Route}: {Reason}", routeText, unresolved is null ? "expands to no fixes" : $"unknown fix {unresolved}");
        }

        return false;
    }

    private bool IsKnownPoint(string fix, RouteQuery query) =>
        (string.Equals(fix, query.Departure, StringComparison.OrdinalIgnoreCase))
        || (string.Equals(fix, query.Destination, StringComparison.OrdinalIgnoreCase))
        || (_navDb.GetFixPosition(fix) is not null);
}
