using SkiaSharp;
using Yaat.Client.Views.Map;

namespace Yaat.Client.Views.Authoring;

/// <summary>What a pointer is over in the route composer: a route point, or else a drawn fix.</summary>
/// <param name="PointIndex">The route point's index, or null when the hit is a fix.</param>
/// <param name="Fix">The fix, or null when the hit is a route point.</param>
public sealed record RouteComposerHit(int? PointIndex, RouteComposerFix? Fix);

/// <summary>
/// Finds what is under a screen point in the route composer, on the scene's <see cref="RouteComposerScene.Layout"/>: the
/// nearest route point within <see cref="Range"/> pixels wins (an exact tie goes to the later point, drawn on top), else
/// the nearest drawn fix within range; nothing in range is null.
/// </summary>
public static class RouteComposerHitTest
{
    /// <summary>How near, in pixels, a point or fix is hit.</summary>
    public const float Range = 10f;

    // Projections land on floats, so a click placed at exactly the range can measure a hair past it.
    private const double RoundingAllowance = 1e-3;

    /// <summary>Finds the route point, else the fix, under a screen point.</summary>
    /// <param name="scene">The composer's scene.</param>
    /// <param name="viewport">The view the point is in.</param>
    /// <param name="x">The point's screen x, in pixels.</param>
    /// <param name="y">The point's screen y, in pixels.</param>
    /// <returns>The hit, or null.</returns>
    public static RouteComposerHit? Find(RouteComposerScene scene, MapViewport viewport, float x, float y)
    {
        ArgumentNullException.ThrowIfNull(scene);
        RouteComposerLayout layout = scene.Layout(viewport);
        RouteComposerScreenPoint? point = Nearest(layout.Points, p => p.At, x, y);
        if (point is not null)
        {
            return new RouteComposerHit(point.Index, null);
        }

        return Nearest(layout.Fixes, f => f.At, x, y) is { } fix ? new RouteComposerHit(null, fix.Fix) : null;
    }

    /// <summary>Finds the drawn fix under a screen point, whatever route point is there too.</summary>
    /// <param name="scene">The composer's scene.</param>
    /// <param name="viewport">The view the point is in.</param>
    /// <param name="x">The point's screen x, in pixels.</param>
    /// <param name="y">The point's screen y, in pixels.</param>
    /// <returns>The nearest fix in range, or null.</returns>
    public static RouteComposerFix? FindFix(RouteComposerScene scene, MapViewport viewport, float x, float y)
    {
        ArgumentNullException.ThrowIfNull(scene);
        return Nearest(scene.Layout(viewport).Fixes, f => f.At, x, y)?.Fix;
    }

    private static T? Nearest<T>(IEnumerable<T> items, Func<T, SKPoint> at, float x, float y)
        where T : class
    {
        T? best = null;
        double bestDistance = double.MaxValue;
        foreach (T item in items)
        {
            SKPoint p = at(item);
            double d = Math.Sqrt(((p.X - x) * (double)(p.X - x)) + ((p.Y - y) * (double)(p.Y - y)));
            if ((d <= Range + RoundingAllowance) && (d <= bestDistance))
            {
                best = item;
                bestDistance = d;
            }
        }

        return best;
    }
}
