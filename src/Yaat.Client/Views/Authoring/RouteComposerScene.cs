using System.Collections.Immutable;
using SkiaSharp;
using Yaat.Client.ViewModels.Authoring;
using Yaat.Client.Views.Map;
using Yaat.Sim;
using Yaat.Sim.Data;

namespace Yaat.Client.Views.Authoring;

/// <summary>A navdata fix the route composer draws, and a click near it snaps to.</summary>
/// <param name="Name">The fix ident.</param>
/// <param name="Position">Where the fix is.</param>
public sealed record RouteComposerFix(string Name, LatLon Position);

/// <summary>A route point as the composer draws it.</summary>
/// <param name="Index">The point's index in the route.</param>
/// <param name="Position">Where its text resolves.</param>
/// <param name="Anchor">An FRD point's anchor fix, joined to it by a dashed line; null otherwise.</param>
/// <param name="Ghost">Where the author placed a point whose text moved it more than 1 NM; null otherwise.</param>
public sealed record RouteComposerScenePoint(int Index, LatLon Position, LatLon? Anchor, LatLon? Ghost);

/// <summary>A route point projected onto the screen.</summary>
/// <param name="Index">The point's index in the route.</param>
/// <param name="At">The point's screen position.</param>
/// <param name="Anchor">The anchor fix's screen position, or null.</param>
/// <param name="Ghost">The ghost's screen position, or null.</param>
public sealed record RouteComposerScreenPoint(int Index, SKPoint At, SKPoint? Anchor, SKPoint? Ghost);

/// <summary>A drawn fix projected onto the screen.</summary>
/// <param name="Fix">The fix.</param>
/// <param name="At">Its screen position.</param>
public sealed record RouteComposerScreenFix(RouteComposerFix Fix, SKPoint At);

/// <summary>The composer's scene projected onto one view: what both the draw and the hit test read.</summary>
/// <param name="Points">The drawn route points in route order.</param>
/// <param name="Fixes">The drawn fixes.</param>
public sealed record RouteComposerLayout(ImmutableArray<RouteComposerScreenPoint> Points, ImmutableArray<RouteComposerScreenFix> Fixes);

/// <summary>
/// What the route composer draws: the route points that resolve (each with its FRD anchor and its ghost), and the fixes
/// near the view. Plain data; <see cref="Layout"/> is the one place its screen geometry is computed, for the draw and the
/// hit test alike.
/// </summary>
/// <param name="Points">The drawn route points in route order; a point that does not resolve is left out.</param>
/// <param name="Fixes">The fixes drawn under the route.</param>
public sealed record RouteComposerScene(ImmutableArray<RouteComposerScenePoint> Points, ImmutableArray<RouteComposerFix> Fixes)
{
    /// <summary>The widest view, in NM, at which fixes are drawn.</summary>
    public const double FixSpanLimitNm = 60.0;

    /// <summary>Gets the scene with nothing in it.</summary>
    public static RouteComposerScene Empty { get; } = new([], []);

    /// <summary>Builds the scene from the route's points and the fixes in view.</summary>
    /// <param name="points">The route's points.</param>
    /// <param name="fixes">The fixes to draw (<see cref="FixesInView"/>).</param>
    /// <returns>The scene.</returns>
    public static RouteComposerScene Build(IReadOnlyList<RouteComposerPoint> points, ImmutableArray<RouteComposerFix> fixes)
    {
        ArgumentNullException.ThrowIfNull(points);
        ImmutableArray<RouteComposerScenePoint>.Builder drawn = ImmutableArray.CreateBuilder<RouteComposerScenePoint>();
        for (int i = 0; i < points.Count; i++)
        {
            RouteComposerPoint point = points[i];
            if (point.Position is { } position)
            {
                drawn.Add(new RouteComposerScenePoint(i, position, point.AnchorPosition, point.IsDisplaced ? point.Placed : null));
            }
        }

        return new RouteComposerScene(drawn.ToImmutable(), fixes.IsDefault ? [] : fixes);
    }

    /// <summary>The scene with one point drawn at a drag's position, with no anchor line or ghost.</summary>
    /// <param name="index">The dragged point's index.</param>
    /// <param name="position">Where the pointer is.</param>
    /// <returns>The scene.</returns>
    public RouteComposerScene WithPointAt(int index, LatLon position) =>
        this with
        {
            Points = [.. Points.Select(p => (p.Index == index) ? new RouteComposerScenePoint(index, position, null, null) : p)],
        };

    /// <summary>Projects the scene onto a view.</summary>
    /// <param name="viewport">The view.</param>
    /// <returns>The screen positions of every drawn point and fix.</returns>
    public RouteComposerLayout Layout(MapViewport viewport)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        ImmutableArray<RouteComposerScreenPoint> points =
        [
            .. Points.Select(p => new RouteComposerScreenPoint(
                p.Index,
                Project(viewport, p.Position),
                ProjectOrNull(viewport, p.Anchor),
                ProjectOrNull(viewport, p.Ghost)
            )),
        ];
        ImmutableArray<RouteComposerScreenFix> fixes = [.. Fixes.Select(f => new RouteComposerScreenFix(f, Project(viewport, f.Position)))];
        return new RouteComposerLayout(points, fixes);
    }

    /// <summary>How far the view spans across its wider side, in NM.</summary>
    /// <param name="viewport">The view.</param>
    /// <returns>The span; 0 for a view with no size.</returns>
    public static double SpanNm(MapViewport viewport)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        float w = viewport.PixelWidth;
        float h = viewport.PixelHeight;
        if ((w <= 0) || (h <= 0))
        {
            return 0.0;
        }

        double across = GeoMath.DistanceNm(At(viewport, 0, h / 2), At(viewport, w, h / 2));
        double down = GeoMath.DistanceNm(At(viewport, w / 2, 0), At(viewport, w / 2, h));
        return Math.Max(across, down);
    }

    /// <summary>
    /// The named fixes (FRD idents left out) within the view's half diagonal of its centre, while the view spans at most
    /// <see cref="FixSpanLimitNm"/>; none above it, with no size, or with no navigation database.
    /// </summary>
    /// <param name="navDb">The navigation database, or null while it loads.</param>
    /// <param name="viewport">The view.</param>
    /// <returns>The fixes to draw.</returns>
    public static ImmutableArray<RouteComposerFix> FixesInView(NavigationDatabase? navDb, MapViewport viewport)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        double span = SpanNm(viewport);
        if ((navDb is null) || (span <= 0.0) || (span > FixSpanLimitNm))
        {
            return [];
        }

        LatLon center = new(viewport.CenterLat, viewport.CenterLon);
        double radius = GeoMath.DistanceNm(center, At(viewport, 0, 0));
        return
        [
            .. navDb
                .FixesWithin(center, radius)
                .Where(f => !FrdResolver.IsFrdIdentifier(f.Name))
                .Select(f => new RouteComposerFix(f.Name, f.Position)),
        ];
    }

    private static LatLon At(MapViewport viewport, float x, float y)
    {
        (double lat, double lon) = viewport.ScreenToLatLon(x, y);
        return new LatLon(lat, lon);
    }

    private static SKPoint Project(MapViewport viewport, LatLon position)
    {
        (float x, float y) = viewport.LatLonToScreen(position.Lat, position.Lon);
        return new SKPoint(x, y);
    }

    private static SKPoint? ProjectOrNull(MapViewport viewport, LatLon? position) => position is { } p ? Project(viewport, p) : null;
}
