using System.Globalization;
using SkiaSharp;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Map;
using Yaat.Sim;
using Yaat.Sim.Data;

namespace Yaat.Client.Views.Scope;

/// <summary>One track's intent path in screen points, as the pilot lens draws it.</summary>
/// <param name="Segments">The route polylines: the flown route first, then a disjoint expected approach when there is one.</param>
/// <param name="Shapes">The server's active-procedure shapes (holds, procedure turns, coded legs, corridors, orbits).</param>
public sealed record ScopeIntentPath(IReadOnlyList<ScopeIntentSegment> Segments, IReadOnlyList<ScopeIntentShape> Shapes);

/// <summary>One route polyline of an intent path.</summary>
/// <param name="LeaderFrom">The symbol's point a dashed leader runs from to the first point; null for no leader.</param>
/// <param name="Points">The polyline's points, synthetic arc vertices included.</param>
/// <param name="Fixes">The named fixes on it, each drawn as a diamond with its name and crossing-restriction lines.</param>
/// <param name="Tail">The vector tail off its end, or from the symbol for a vectored aircraft; null for none.</param>
public sealed record ScopeIntentSegment(
    SKPoint? LeaderFrom,
    IReadOnlyList<SKPoint> Points,
    IReadOnlyList<ScopeIntentFix> Fixes,
    ScopeIntentTail? Tail
);

/// <summary>A named fix on an intent path.</summary>
/// <param name="At">The fix's screen point.</param>
/// <param name="Name">The fix's name.</param>
/// <param name="Restrictions">Its crossing-restriction lines, each its tokens left to right; null when unrestricted.</param>
public sealed record ScopeIntentFix(SKPoint At, string Name, IReadOnlyList<IReadOnlyList<NavRouteRestrictionToken>>? Restrictions);

/// <summary>A vector tail: a dashed line with an arrowhead at <paramref name="To"/>.</summary>
/// <param name="From">Where the tail starts.</param>
/// <param name="To">Where its arrowhead points.</param>
public sealed record ScopeIntentTail(SKPoint From, SKPoint To);

/// <summary>One active-procedure shape of an intent path.</summary>
/// <param name="Points">The shape's points, drawn as one path.</param>
/// <param name="Dashed">
/// Whether it draws dashed: a hold queued ahead, an orbit, an open-ended coded leg and a military corridor do; a hold or
/// procedure turn being flown draws solid.
/// </param>
/// <param name="LabelAt">Where its label lines start, or null for none.</param>
/// <param name="Labels">Its label lines, top to bottom; empty for none.</param>
public sealed record ScopeIntentShape(IReadOnlyList<SKPoint> Points, bool Dashed, SKPoint? LabelAt, IReadOnlyList<string> Labels);

/// <summary>
/// One route polyline of an intent path, in latitudes and longitudes: what <see cref="ScopeIntentPathCache"/> keeps between
/// frames.
/// </summary>
/// <param name="Waypoints">Its waypoints, synthetic arc vertices included.</param>
/// <param name="Tail">The vector tail off its end, or from the aircraft for a vectored one; null for none.</param>
/// <param name="FromAircraft">Whether a dashed leader runs from the symbol to its first point: the flown route's does.</param>
/// <param name="TailFollowsAircraft">
/// Whether the tail starts at the aircraft (a vectored aircraft's heading tail), so each projection starts it where the
/// aircraft is then.
/// </param>
internal sealed record ScopeIntentRoute(IReadOnlyList<DrawnWaypoint> Waypoints, VectorTail? Tail, bool FromAircraft, bool TailFollowsAircraft);

/// <summary>
/// Pure: one track's intent path for the pilot lens, from the nav route, its shapes and the vector tail as the Radar's
/// "Show nav route" builds them (<see cref="ShownRouteBuilder"/>): straight legs and the server's shapes, no predicted
/// turn arcs. <see cref="Routes"/> builds its routes in latitudes and longitudes and <see cref="Project"/> puts them and
/// the shapes on screen.
/// </summary>
internal static class ScopeIntentPaths
{
    /// <summary>
    /// The intent path's routes for <paramref name="ac"/> in latitudes and longitudes: the flown route (with a leader from
    /// the symbol, or the vector tail of a vectored aircraft) and an expected approach not yet in the route; empty when
    /// there is neither or no <paramref name="navDb"/> is loaded yet.
    /// </summary>
    internal static IReadOnlyList<ScopeIntentRoute> Routes(AircraftModel ac, NavigationDatabase? navDb)
    {
        if (navDb is null)
        {
            return [];
        }

        var routes = new List<ScopeIntentRoute>(2);
        (List<DrawnWaypoint> waypoints, VectorTail? tail) = ShownRouteBuilder.BuildPrimary(ac, navDb);
        if ((waypoints.Count > 0) || (tail is not null))
        {
            // Mirrors ShownRouteBuilder.BuildPrimary's pure-vector rule: with no fixes and no procedure tail, it starts the
            // assigned heading's tail at the aircraft's position, so that tail follows the aircraft.
            bool follows = (waypoints.Count == 0) && (tail is not null) && (tail.FromLat == ac.Position.Lat) && (tail.FromLon == ac.Position.Lon);
            routes.Add(new ScopeIntentRoute(waypoints, tail, FromAircraft: true, TailFollowsAircraft: follows));
        }

        if (ShownRouteBuilder.BuildExpectedApproach(ac, navDb) is { } approach)
        {
            routes.Add(new ScopeIntentRoute(approach.Waypoints, approach.Tail, FromAircraft: false, TailFollowsAircraft: false));
        }

        return routes;
    }

    /// <summary>
    /// The intent path of <paramref name="ac"/> in <paramref name="vp"/>'s screen points: <paramref name="routes"/>, their
    /// leader and a heading tail starting where <paramref name="ac"/> is now, and the aircraft's shapes as they are now;
    /// null when there are neither routes nor shapes.
    /// </summary>
    internal static ScopeIntentPath? Project(IReadOnlyList<ScopeIntentRoute> routes, AircraftModel ac, MapViewport vp)
    {
        if ((routes.Count == 0) && (ac.NavRouteShapes.Count == 0))
        {
            return null;
        }

        SKPoint aircraft = Screen(vp, ac.Position.Lat, ac.Position.Lon);
        return new ScopeIntentPath(
            [.. routes.Select(route => SegmentOf(route, ac, aircraft, vp))],
            [.. ac.NavRouteShapes.Select(shape => ShapeOf(shape, vp))]
        );
    }

    /// <summary>
    /// The scalar inputs <see cref="ShownRouteBuilder"/> reads, as the Radar's nav-route cache keys them
    /// (<see cref="RadarViewModel.BuildShownPathFingerprint"/>); the cache keys the fix list by identity beside them.
    /// </summary>
    internal static string RouteScalars(AircraftModel ac) =>
        string.Join(
            "|",
            ac.ActiveSidId,
            ac.ActiveStarId,
            ac.ActiveApproachId,
            ac.ExpectedApproach,
            ac.Departure,
            ac.Destination,
            ac.DepartureRunway,
            ac.DestinationRunway,
            ac.AssignedHeading?.Degrees.ToString("F1", CultureInfo.InvariantCulture) ?? ""
        );

    private static ScopeIntentSegment SegmentOf(ScopeIntentRoute route, AircraftModel ac, SKPoint aircraft, MapViewport vp)
    {
        SKPoint[] points = [.. route.Waypoints.Select(wp => Screen(vp, wp.Lat, wp.Lon))];
        VectorTail? tail = route.Tail;
        if (route.TailFollowsAircraft && (tail is not null))
        {
            tail = tail with { FromLat = ac.Position.Lat, FromLon = ac.Position.Lon };
        }

        SKPoint? leader = (route.FromAircraft && (points.Length > 0)) ? aircraft : null;
        return new ScopeIntentSegment(leader, points, FixesOf(route.Waypoints, points), tail is null ? null : TailOf(tail, vp));
    }

    private static List<ScopeIntentFix> FixesOf(IReadOnlyList<DrawnWaypoint> waypoints, SKPoint[] points)
    {
        List<ScopeIntentFix> fixes = [];
        for (int i = 0; i < waypoints.Count; i++)
        {
            // Synthetic arc vertices (no name) are polyline points only: no diamond, label or restriction.
            if (!string.IsNullOrEmpty(waypoints[i].ResolvedName))
            {
                fixes.Add(new ScopeIntentFix(points[i], waypoints[i].ResolvedName, waypoints[i].RestrictionTokens));
            }
        }

        return fixes;
    }

    /// <summary>The tail's screen line: its magnetic heading turned true at its start, projected its length.</summary>
    private static ScopeIntentTail TailOf(VectorTail tail, MapViewport vp)
    {
        double trueHeadingDeg = MagneticDeclination.MagneticToTrue(tail.HeadingMag, tail.FromLat, tail.FromLon);
        (double endLat, double endLon) = GeoMath.ProjectPoint(tail.FromLat, tail.FromLon, new TrueHeading(trueHeadingDeg), tail.LengthNm);
        return new ScopeIntentTail(Screen(vp, tail.FromLat, tail.FromLon), Screen(vp, endLat, endLon));
    }

    private static ScopeIntentShape ShapeOf(NavRouteShapeDto shape, MapViewport vp)
    {
        SKPoint[] points = [.. shape.Points.Where(p => p.Length >= 2).Select(p => Screen(vp, p[0], p[1]))];
        bool dashed =
            shape.Kind
            is NavRouteShapeKind.PendingHoldRacetrack
                or NavRouteShapeKind.OrbitCircle
                or NavRouteShapeKind.CodedLegVector
                or NavRouteShapeKind.MilitaryRouteCorridor;
        return (shape is { Labels: { Count: > 0 } labels, LabelLat: { } lat, LabelLon: { } lon })
            ? new ScopeIntentShape(points, dashed, Screen(vp, lat, lon), [.. labels])
            : new ScopeIntentShape(points, dashed, null, []);
    }

    private static SKPoint Screen(MapViewport vp, double lat, double lon)
    {
        (float x, float y) = vp.LatLonToScreen(lat, lon);
        return new SKPoint(x, y);
    }
}

/// <summary>
/// The Scope canvas's intent-path routes per drawn track, so a frame whose inputs are unchanged only projects them. An
/// entry is keyed by the identity of the aircraft's fix list (<c>AircraftModel</c> replaces the list on every update), the
/// route scalars (<see cref="ScopeIntentPaths.RouteScalars"/>) and the navigation database it was built with; a new list
/// with the same fixes rebuilds. UI thread only.
/// </summary>
public sealed class ScopeIntentPathCache
{
    private readonly Func<AircraftModel, NavigationDatabase?, IReadOnlyList<ScopeIntentRoute>> _build;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly HashSet<string> _asked = new(StringComparer.Ordinal);

    /// <summary>A cache that builds with <see cref="ScopeIntentPaths.Routes"/>.</summary>
    public ScopeIntentPathCache()
        : this(ScopeIntentPaths.Routes) { }

    /// <summary>A cache that builds with <paramref name="build"/>: the seam a test counts builds through.</summary>
    internal ScopeIntentPathCache(Func<AircraftModel, NavigationDatabase?, IReadOnlyList<ScopeIntentRoute>> build) => _build = build;

    /// <summary>
    /// <paramref name="ac"/>'s intent path in <paramref name="vp"/>'s screen points (<see cref="ScopeIntentPaths.Project"/>);
    /// its routes are rebuilt only when the fix list, a route scalar or <paramref name="navDb"/> changed. With no
    /// <paramref name="navDb"/> (not loaded yet) only the shapes draw.
    /// </summary>
    internal ScopeIntentPath? PathFor(AircraftModel ac, NavigationDatabase? navDb, MapViewport vp)
    {
        _asked.Add(ac.Callsign);
        string scalars = ScopeIntentPaths.RouteScalars(ac);
        if (!_entries.TryGetValue(ac.Callsign, out Entry? entry) || !entry.Matches(ac.NavRouteFixes, scalars, navDb))
        {
            entry = new Entry(ac.NavRouteFixes, scalars, navDb, _build(ac, navDb));
            _entries[ac.Callsign] = entry;
        }

        return ScopeIntentPaths.Project(entry.Routes, ac, vp);
    }

    /// <summary>Ends a frame: drops every track no <see cref="PathFor"/> asked for since the last sweep.</summary>
    internal void Sweep()
    {
        foreach (string callsign in _entries.Keys.Where(c => !_asked.Contains(c)).ToList())
        {
            _entries.Remove(callsign);
        }

        _asked.Clear();
    }

    private sealed record Entry(List<NavRouteFixDto> Fixes, string Scalars, NavigationDatabase? NavDb, IReadOnlyList<ScopeIntentRoute> Routes)
    {
        public bool Matches(List<NavRouteFixDto> fixes, string scalars, NavigationDatabase? navDb) =>
            ReferenceEquals(Fixes, fixes) && (Scalars == scalars) && ReferenceEquals(NavDb, navDb);
    }
}
