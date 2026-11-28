using Yaat.Sim.Authoring.Routes;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;

namespace Yaat.Sim.Authoring.Stamping;

/// <summary>The route a stamp writes: route text from the route finder or the route library.</summary>
/// <param name="Name">What the route is called where it came from, for display.</param>
/// <param name="RouteText">The route text, fixes and procedures separated by spaces.</param>
/// <param name="Warnings">What the conversion warned about, carried into the stamp's warnings.</param>
public sealed record StampRoute(string Name, string RouteText, IReadOnlyList<string> Warnings)
{
    /// <summary>The route a route finder offered.</summary>
    /// <param name="found">The found route.</param>
    /// <returns>The stamp route, named by the sources that offered it (by its route text when none is named).</returns>
    public static StampRoute FromFoundRoute(FoundRoute found)
    {
        ArgumentNullException.ThrowIfNull(found);
        string name = (found.Sources.Count > 0) ? string.Join(", ", found.Sources) : found.RouteText;
        return new StampRoute(name, found.RouteText, []);
    }

    /// <summary>The route a route library entry draws, written as nav-path text.</summary>
    /// <param name="saved">The saved route.</param>
    /// <param name="ctx">The airports a fix or FRD anchor may fall back to, as the sim resolves the route.</param>
    /// <param name="navDb">The navigation database; null when it is not loaded.</param>
    /// <returns>The stamp route, or the failure of a route with no points or of the first point that does not resolve.</returns>
    public static StampRouteConversion FromSavedRoute(SavedRoute saved, FixResolutionContext ctx, NavigationDatabase? navDb)
    {
        ArgumentNullException.ThrowIfNull(saved);
        if (saved.Points.Count == 0)
        {
            return new StampRouteConversion(null, $"Route {saved.Name} has no points");
        }

        RoutePointResult text = RoutePointResolver.NavPathText(saved.Points, ctx, navDb);
        if (text.Failure is not null)
        {
            return new StampRouteConversion(null, $"Route {saved.Name}: {text.Failure}");
        }

        string[] warnings = (text.Warning is null) ? [] : [$"Route {saved.Name}: {text.Warning}"];
        return new StampRouteConversion(new StampRoute(saved.Name, text.Text ?? "", warnings), null);
    }
}

/// <summary>The outcome of <see cref="StampRoute.FromSavedRoute"/>.</summary>
/// <param name="Route">The stamp route; null when the route has no points or a point failed.</param>
/// <param name="Failure">Why the route could not be written; null on success.</param>
public sealed record StampRouteConversion(StampRoute? Route, string? Failure);
