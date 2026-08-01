using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Data.MilitaryRoutes;

/// <summary>
/// Expands a military route designator appearing in a route string into its constituent points.
///
/// AP/1B chapter 1 §IV.B.1 files a route as <c>{entry FRD} {designator} {exit FRD}</c> —
/// <c>SAT263043 IR149 LRD040028</c> — so the bracketing tokens are fix/radial/distance points, not
/// names of points on the route. That is why this cannot reuse
/// <see cref="NavigationDatabase.ExpandAirwaySegment"/>, which matches anchors by name and walks the
/// fix list in either direction: military routes are one-way and course reversals are prohibited.
/// </summary>
public static class MilitaryRouteExpander
{
    private static readonly ILogger Log = SimLog.CreateLogger("MilitaryRouteExpander");

    /// <summary>
    /// How far a bracketing anchor may sit from the route point it is taken to name.
    ///
    /// AP/1B's published FRDs are magnetic-radial derived against the declination of their era while
    /// <see cref="FrdResolver"/> applies the live model, so a degree or two of drift on a 40 NM arm
    /// is around a mile. Consecutive route points are typically 5-15 NM apart, so 15 NM accepts the
    /// drift without letting an anchor claim the wrong point.
    /// </summary>
    public const double AnchorSnapToleranceNm = 15.0;

    /// <summary>
    /// The ordered point names to fly for a military route bracketed by the given anchors.
    ///
    /// Either anchor may be null or unresolvable — a route often ends on the training route, or
    /// starts with it. A missing anchor falls back to the route's own published entry or exit point,
    /// which is a deliberate divergence from <see cref="NavigationDatabase.ExpandAirwaySegment"/>
    /// (that returns nothing when an anchor is missing). The divergence is correct here because a
    /// military route has a published beginning and end that an airway does not.
    /// </summary>
    public static IReadOnlyList<string> Expand(string designator, string? entryAnchor, string? exitAnchor, NavigationDatabase navDb)
    {
        var route = navDb.GetMilitaryRoute(designator);
        if (route is null || route.Points.Count == 0)
        {
            return [];
        }

        int entryIndex = SnapAnchor(route, entryAnchor, navDb) ?? DefaultEntryIndex(route);
        int exitIndex = SnapAnchor(route, exitAnchor, navDb) ?? DefaultExitIndex(route);

        if (exitIndex < entryIndex)
        {
            // Far more likely a bad snap than a genuinely reversed filing: AP/1B routes are one-way
            // and course reversals are prohibited. Fly forward from the entry rather than reverse.
            Log.LogWarning(
                "{Route}: exit point {Exit} precedes entry point {Entry}; flying forward to the end of the route",
                route.Designator,
                route.Points[exitIndex].Id,
                route.Points[entryIndex].Id
            );
            exitIndex = route.Points.Count - 1;
        }

        var names = new List<string>(exitIndex - entryIndex + 1);
        for (int i = entryIndex; i <= exitIndex; i++)
        {
            names.Add(route.Points[i].Name);
        }

        return names;
    }

    /// <summary>
    /// The index of the route point an anchor names, or null when the anchor is absent, does not
    /// resolve, or lands further than <see cref="AnchorSnapToleranceNm"/> from every point.
    /// </summary>
    private static int? SnapAnchor(MilitaryRoute route, string? anchor, NavigationDatabase navDb)
    {
        if (string.IsNullOrWhiteSpace(anchor))
        {
            return null;
        }

        var position = navDb.ResolveFixOrFrd(anchor);
        if (position is null)
        {
            return null;
        }

        int bestIndex = -1;
        double best = double.MaxValue;
        double secondBest = double.MaxValue;

        for (int i = 0; i < route.Points.Count; i++)
        {
            var point = route.Points[i].Position;
            double distance = GeoMath.DistanceNm(position.Value.Lat, position.Value.Lon, point.Lat, point.Lon);
            if (distance < best)
            {
                secondBest = best;
                best = distance;
                bestIndex = i;
            }
            else if (distance < secondBest)
            {
                secondBest = distance;
            }
        }

        if (bestIndex < 0 || best > AnchorSnapToleranceNm)
        {
            // The filer entered by direct-to rather than at a published point. Treat the anchor as
            // unknown and use the route's own entry or exit, rather than inventing a leg to it.
            Log.LogDebug("{Route}: anchor {Anchor} is {Distance:F1} NM from the nearest point; treating as unknown", route.Designator, anchor, best);
            return null;
        }

        Log.LogDebug(
            "{Route}: anchor {Anchor} snapped to {Point} at {Best:F1} NM (next nearest {Second:F1} NM)",
            route.Designator,
            anchor,
            route.Points[bestIndex].Id,
            best,
            secondBest
        );
        return bestIndex;
    }

    private static int DefaultEntryIndex(MilitaryRoute route)
    {
        int published = route.EntryPoints.Count > 0 ? route.IndexOf(route.EntryPoints[0]) : -1;
        return published >= 0 ? published : 0;
    }

    private static int DefaultExitIndex(MilitaryRoute route)
    {
        int published = route.ExitPoints.Count > 0 ? route.IndexOf(route.ExitPoints[0]) : -1;
        return published >= 0 ? published : route.Points.Count - 1;
    }
}
