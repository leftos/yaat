using Yaat.Client.Views.Map;
using Yaat.Sim.Authoring.AirportMap;

namespace Yaat.Client.Views.Authoring;

/// <summary>What a point on the Airport Editor's map lands on: a feature, and a vertex when it is one of a selected line's.</summary>
/// <param name="FeatureIndex">The feature's index in <see cref="AirportMapDocument.Features"/>.</param>
/// <param name="VertexIndex">The index of the line's vertex in its positions, or null when the hit is the feature itself.</param>
public sealed record AirportEditorHit(int FeatureIndex, int? VertexIndex);

/// <summary>Where on a line a screen point falls nearest: the segment, and the point projected onto it.</summary>
/// <param name="SegmentIndex">The segment's index, its first vertex's index: segment k runs from vertex k to vertex k + 1.</param>
/// <param name="X">The projected point's screen x, in pixels.</param>
/// <param name="Y">The projected point's screen y, in pixels.</param>
public sealed record AirportEditorSegmentHit(int SegmentIndex, double X, double Y);

/// <summary>
/// Finds the raw map feature under a screen point, in three tiers, the first with any candidate winning: the selected
/// line's vertices within <see cref="VertexRange"/>, then points within <see cref="PointRange"/>, then line segments
/// within <see cref="SegmentRange"/> (measured to the segment clamped to its ends). Within a tier the nearest wins, an
/// exact tie going to the later shape (drawn on top); a distance equal to the range is a hit.
/// </summary>
public static class AirportEditorHitTest
{
    /// <summary>How near, in pixels, a selected line's vertex is hit.</summary>
    public const float VertexRange = 8f;

    /// <summary>How near, in pixels, a parking, spot or helipad point is hit.</summary>
    public const float PointRange = 12f;

    /// <summary>How near, in pixels, a runway or taxiway segment is hit.</summary>
    public const float SegmentRange = 6f;

    // Projections land on floats, so a click placed at exactly a range can measure a few hundred-thousandths of a pixel
    // past it; this much past the range still counts as on it.
    private const double RoundingAllowance = 1e-3;

    /// <summary>Finds the feature under a screen point.</summary>
    /// <param name="scene">The open map's scene.</param>
    /// <param name="viewport">The view the point is in.</param>
    /// <param name="x">The point's screen x, in pixels.</param>
    /// <param name="y">The point's screen y, in pixels.</param>
    /// <param name="selectedFeatureIndex">The selected feature's index, whose vertices are hit first when it is a line.</param>
    /// <returns>The hit, or null when nothing is in range.</returns>
    public static AirportEditorHit? Find(AirportEditorScene scene, MapViewport viewport, float x, float y, int? selectedFeatureIndex)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(viewport);
        var at = new ScreenPoint(x, y);
        return FindVertex(scene, viewport, at, selectedFeatureIndex) ?? FindPoint(scene, viewport, at) ?? FindSegment(scene, viewport, at);
    }

    /// <summary>
    /// Finds the segment of a drawn line nearest a screen point, measured to each segment clamped to its ends (the earlier
    /// segment on an exact tie), and the point projected onto it; the caller has already hit the line.
    /// </summary>
    /// <param name="scene">The open map's scene.</param>
    /// <param name="viewport">The view the point is in.</param>
    /// <param name="x">The point's screen x, in pixels.</param>
    /// <param name="y">The point's screen y, in pixels.</param>
    /// <param name="featureIndex">The line's index in the map's features.</param>
    /// <returns>The nearest segment, or null when the feature is not a drawn line.</returns>
    public static AirportEditorSegmentHit? NearestSegment(AirportEditorScene scene, MapViewport viewport, float x, float y, int featureIndex)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(viewport);
        AirportEditorShape? shape = scene.Shapes.FirstOrDefault(s => s.FeatureIndex == featureIndex);
        if ((shape is null) || (!IsLine(shape.Kind)))
        {
            return null;
        }

        var at = new ScreenPoint(x, y);
        AirportEditorSegmentHit? best = null;
        double bestDistance = double.MaxValue;
        ScreenPoint a = Project(viewport, shape.Positions[0]);
        for (int i = 1; i < shape.Positions.Length; i++)
        {
            ScreenPoint b = Project(viewport, shape.Positions[i]);
            ScreenPoint q = NearestOnSegment(at, a, b);
            double d = Distance(at, q);
            if (d < bestDistance)
            {
                best = new AirportEditorSegmentHit(i - 1, q.X, q.Y);
                bestDistance = d;
            }

            a = b;
        }

        return best;
    }

    /// <summary>Whether a kind is drawn as a line (a runway or a taxiway) rather than a point.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>True for a runway or a taxiway.</returns>
    internal static bool IsLine(AirportEditorFeatureKind kind) => kind is AirportEditorFeatureKind.Runway or AirportEditorFeatureKind.Taxiway;

    /// <summary>The distance from a point to a segment, clamped to the segment's ends.</summary>
    /// <param name="p">The point.</param>
    /// <param name="a">The segment's start.</param>
    /// <param name="b">The segment's end.</param>
    /// <returns>The distance, in the points' units.</returns>
    internal static double SegmentDistance(ScreenPoint p, ScreenPoint a, ScreenPoint b) => Distance(p, NearestOnSegment(p, a, b));

    // The point of a segment nearest p, clamped to the segment's ends.
    private static ScreenPoint NearestOnSegment(ScreenPoint p, ScreenPoint a, ScreenPoint b)
    {
        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        double lengthSquared = (dx * dx) + (dy * dy);
        double t = lengthSquared > 0 ? Math.Clamp((((p.X - a.X) * dx) + ((p.Y - a.Y) * dy)) / lengthSquared, 0, 1) : 0;
        return new ScreenPoint(a.X + (t * dx), a.Y + (t * dy));
    }

    private static AirportEditorHit? FindVertex(AirportEditorScene scene, MapViewport viewport, ScreenPoint at, int? selectedFeatureIndex)
    {
        AirportEditorShape? shape = selectedFeatureIndex is int selected ? scene.Shapes.FirstOrDefault(s => s.FeatureIndex == selected) : null;
        if ((shape is null) || (!IsLine(shape.Kind)))
        {
            return null;
        }

        int? best = null;
        double bestDistance = double.MaxValue;
        for (int i = 0; i < shape.Positions.Length; i++)
        {
            double d = Distance(at, Project(viewport, shape.Positions[i]));
            if ((d <= VertexRange + RoundingAllowance) && (d <= bestDistance))
            {
                best = i;
                bestDistance = d;
            }
        }

        return best is int vertex ? new AirportEditorHit(shape.FeatureIndex, vertex) : null;
    }

    private static AirportEditorHit? FindPoint(AirportEditorScene scene, MapViewport viewport, ScreenPoint at)
    {
        AirportEditorShape? best = null;
        double bestDistance = double.MaxValue;
        foreach (AirportEditorShape shape in scene.Shapes)
        {
            if (IsLine(shape.Kind))
            {
                continue;
            }

            double d = Distance(at, Project(viewport, shape.Positions[0]));
            if ((d <= PointRange + RoundingAllowance) && (d <= bestDistance))
            {
                best = shape;
                bestDistance = d;
            }
        }

        return best is null ? null : new AirportEditorHit(best.FeatureIndex, null);
    }

    private static AirportEditorHit? FindSegment(AirportEditorScene scene, MapViewport viewport, ScreenPoint at)
    {
        AirportEditorShape? best = null;
        double bestDistance = double.MaxValue;
        foreach (AirportEditorShape shape in scene.Shapes)
        {
            if (!IsLine(shape.Kind))
            {
                continue;
            }

            double d = LineDistance(viewport, shape, at);
            if ((d <= SegmentRange + RoundingAllowance) && (d <= bestDistance))
            {
                best = shape;
                bestDistance = d;
            }
        }

        return best is null ? null : new AirportEditorHit(best.FeatureIndex, null);
    }

    private static double LineDistance(MapViewport viewport, AirportEditorShape shape, ScreenPoint at)
    {
        double best = double.MaxValue;
        ScreenPoint a = Project(viewport, shape.Positions[0]);
        for (int i = 1; i < shape.Positions.Length; i++)
        {
            ScreenPoint b = Project(viewport, shape.Positions[i]);
            best = Math.Min(best, SegmentDistance(at, a, b));
            a = b;
        }

        return best;
    }

    private static ScreenPoint Project(MapViewport viewport, MapPosition position)
    {
        (float x, float y) = viewport.LatLonToScreen(position.Lat, position.Lon);
        return new ScreenPoint(x, y);
    }

    private static double Distance(ScreenPoint a, ScreenPoint b) => Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));

    /// <summary>A point on the screen, in pixels.</summary>
    /// <param name="X">The x, rightwards.</param>
    /// <param name="Y">The y, downwards.</param>
    internal readonly record struct ScreenPoint(double X, double Y);
}
