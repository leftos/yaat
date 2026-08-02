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

        var variant = SelectVariant(route, entryAnchor, exitAnchor, navDb);
        var points = variant?.Points ?? route.Points;
        var entryPoints = variant?.EntryPoints ?? route.EntryPoints;
        var exitPoints = variant?.ExitPoints ?? route.ExitPoints;

        int entryIndex = SnapAnchor(route.Designator, points, entryAnchor, navDb)?.Index ?? DefaultIndex(points, entryPoints, 0);
        int exitIndex = SnapAnchor(route.Designator, points, exitAnchor, navDb)?.Index ?? DefaultIndex(points, exitPoints, points.Count - 1);

        if (exitIndex < entryIndex)
        {
            // Far more likely a bad snap than a genuinely reversed filing: AP/1B routes are one-way
            // and course reversals are prohibited. Fly forward from the entry rather than reverse.
            Log.LogWarning(
                "{Route}: exit point {Exit} precedes entry point {Entry}; flying forward to the end of the route",
                route.Designator,
                points[exitIndex].Id,
                points[entryIndex].Id
            );
            exitIndex = points.Count - 1;
        }

        var names = new List<string>(exitIndex - entryIndex + 1);
        for (int i = entryIndex; i <= exitIndex; i++)
        {
            names.Add(points[i].Name);
        }

        return names;
    }

    /// <summary>
    /// The published direction the filing describes, or null for a route that publishes only one.
    ///
    /// A refueling track's two directions are separate geometries sharing one designator, so the
    /// bracketing anchors are what say which one was filed: the direction whose points the anchors
    /// sit closest to, in the order they were filed, is the direction being flown. Scoring the whole
    /// pair rather than the entry alone is what distinguishes offset parallels, whose entries can be
    /// nearly co-located while their exits are a hundred miles apart.
    /// </summary>
    public static MilitaryRouteVariant? SelectVariant(MilitaryRoute route, string? entryAnchor, string? exitAnchor, NavigationDatabase navDb)
    {
        if (route.Variants.Count <= 1)
        {
            return route.Variants.Count == 1 ? route.Variants[0] : null;
        }

        MilitaryRouteVariant? best = null;
        double bestScore = double.MaxValue;
        foreach (var variant in route.Variants)
        {
            double score = ScoreVariant(route.Designator, variant, entryAnchor, exitAnchor, navDb);
            if (score < bestScore)
            {
                bestScore = score;
                best = variant;
            }
        }

        Log.LogDebug("{Route}: filed anchors select the {Direction} direction (score {Score:F1})", route.Designator, best?.Direction, bestScore);
        return best ?? route.Variants[0];
    }

    private static double ScoreVariant(
        string designator,
        MilitaryRouteVariant variant,
        string? entryAnchor,
        string? exitAnchor,
        NavigationDatabase navDb
    )
    {
        var entry = SnapAnchor(designator, variant.Points, entryAnchor, navDb);
        var exit = SnapAnchor(designator, variant.Points, exitAnchor, navDb);

        // An anchor that does not resolve against this direction is no evidence either way, so it
        // costs the tolerance rather than disqualifying the direction outright.
        double score = (entry?.DistanceNm ?? AnchorSnapToleranceNm) + (exit?.DistanceNm ?? AnchorSnapToleranceNm);
        if (entry is not null && exit is not null && exit.Value.Index < entry.Value.Index)
        {
            score += ReversedOrderPenaltyNm;
        }

        return score;
    }

    /// <summary>
    /// What it costs a direction to have the filed anchors land on it back to front. Larger than any
    /// snap distance can be, so a correctly ordered direction always wins over a reversed one.
    /// </summary>
    private const double ReversedOrderPenaltyNm = 1000.0;

    /// <summary>
    /// The route point an anchor names and how far away it landed, or null when the anchor is
    /// absent, does not resolve, or lands further than <see cref="AnchorSnapToleranceNm"/> from
    /// every point.
    /// </summary>
    private static (int Index, double DistanceNm)? SnapAnchor(
        string designator,
        IReadOnlyList<MilitaryRoutePoint> points,
        string? anchor,
        NavigationDatabase navDb
    )
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

        for (int i = 0; i < points.Count; i++)
        {
            var point = points[i].Position;
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
            Log.LogDebug("{Route}: anchor {Anchor} is {Distance:F1} NM from the nearest point; treating as unknown", designator, anchor, best);
            return null;
        }

        Log.LogDebug(
            "{Route}: anchor {Anchor} snapped to {Point} at {Best:F1} NM (next nearest {Second:F1} NM)",
            designator,
            anchor,
            points[bestIndex].Id,
            best,
            secondBest
        );
        return (bestIndex, best);
    }

    private static int DefaultIndex(IReadOnlyList<MilitaryRoutePoint> points, IReadOnlyList<string> published, int fallback)
    {
        if (published.Count == 0)
        {
            return fallback;
        }

        for (int i = 0; i < points.Count; i++)
        {
            if (string.Equals(points[i].Id, published[0], StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return fallback;
    }
}
