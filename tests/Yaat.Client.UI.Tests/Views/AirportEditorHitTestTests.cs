using System.Text.Json.Nodes;
using Xunit;
using Yaat.Client.Views.Authoring;
using Yaat.Client.Views.Map;
using Yaat.Sim.Authoring.AirportMap;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The Airport Editor's hit-test on the committed KOAK map: a selected line's vertices within 8 px first, then points
/// within 12 px, then line segments within 6 px of the segment clamped to its ends; the nearest wins within a tier, a
/// distance equal to the range is a hit, and the hit names the feature's index in the document. Each click is placed
/// in pixels from a real feature's projected position, on a view centred on it so that position is exactly the centre.
/// </summary>
public class AirportEditorHitTestTests
{
    private const float Cx = 500f;
    private const float Cy = 400f;

    private static readonly string OakPath = Path.Combine(AppContext.BaseDirectory, "TestData", "oak.geojson");

    private static readonly (float X, float Y)[] AxisDirections = [(1, 0), (-1, 0), (0, 1), (0, -1)];

    private static readonly float[] MidSpanOffsets = [6f, 6.5f];

    [Fact]
    public void AClickExactlyOnAParkingPoint_HitsThatPoint()
    {
        AirportEditorScene scene = OakScene();
        AirportEditorShape parking = scene.Shapes.First(s => s.Kind == AirportEditorFeatureKind.Parking);
        MapViewport view = ViewAt(parking.Positions[0]);

        Assert.Equal(new AirportEditorHit(parking.FeatureIndex, null), AirportEditorHitTest.Find(scene, view, Cx, Cy, null));
    }

    [Fact]
    public void TwelvePixelsFromAnIsolatedPoint_Hits_TwelveAndAHalfDoesNot()
    {
        AirportEditorScene scene = OakScene();
        AirportEditorShape parking = scene.Shapes.First(s =>
            (s.Kind == AirportEditorFeatureKind.Parking)
            && (DistanceToOthers(scene, ViewAt(s.Positions[0]), (Cx + 12f, Cy), s) > 15)
            && (DistanceToOthers(scene, ViewAt(s.Positions[0]), (Cx + 12.5f, Cy), s) > 15)
        );
        MapViewport view = ViewAt(parking.Positions[0]);
        Assert.True(NearestLine(scene, view, (Cx + 12.5f, Cy)) > 6);

        Assert.Equal(new AirportEditorHit(parking.FeatureIndex, null), AirportEditorHitTest.Find(scene, view, Cx + 12f, Cy, null));
        Assert.Null(AirportEditorHitTest.Find(scene, view, Cx + 12.5f, Cy, null));
    }

    [Fact]
    public void SixPixelsPerpendicularFromATaxiwayMidSpan_HitsIt_SixAndAHalfDoesNot()
    {
        AirportEditorScene scene = OakScene();
        (AirportEditorShape taxiway, MapViewport view, (float X, float Y) normal) = IsolatedMidSpan(scene);

        AirportEditorHit? near = AirportEditorHitTest.Find(scene, view, Cx + (6f * normal.X), Cy + (6f * normal.Y), null);
        AirportEditorHit? far = AirportEditorHitTest.Find(scene, view, Cx + (6.5f * normal.X), Cy + (6.5f * normal.Y), null);

        Assert.Equal(new AirportEditorHit(taxiway.FeatureIndex, null), near);
        Assert.Null(far);
    }

    [Fact]
    public void APointWithinTwelvePixels_WinsOverALineWithinSix()
    {
        AirportEditorScene scene = OakScene();
        (AirportEditorShape point, MapViewport view, (float X, float Y) click) = PointBesideALine(scene);
        Assert.True(NearestLine(scene, view, click) <= 6);

        AirportEditorHit? hit = AirportEditorHitTest.Find(scene, view, click.X, click.Y, null);

        Assert.Equal(new AirportEditorHit(point.FeatureIndex, null), hit);
    }

    [Fact]
    public void EightPixelsFromASelectedTaxiwaysVertex_HitsTheVertex_AndWithNothingSelectedTheClickMisses()
    {
        AirportEditorScene scene = OakScene();
        (AirportEditorShape taxiway, int vertex, MapViewport view, (float X, float Y) click) = IsolatedVertexClick(scene);

        Assert.Equal(
            new AirportEditorHit(taxiway.FeatureIndex, vertex),
            AirportEditorHitTest.Find(scene, view, click.X, click.Y, taxiway.FeatureIndex)
        );
        Assert.Null(AirportEditorHitTest.Find(scene, view, click.X, click.Y, null));
    }

    [Fact]
    public void PastALinesEnd_TheDistanceIsToTheEndPoint_NotToTheInfiniteLine()
    {
        AirportEditorScene scene = OakScene();
        (AirportEditorShape line, MapViewport view, (float X, float Y) along) = IsolatedLineEnd(scene);
        (float X, float Y) far = (Cx + (10f * along.X), Cy + (10f * along.Y));
        (float X, float Y) near = (Cx + (5f * along.X), Cy + (5f * along.Y));
        (float X, float Y) end = view.LatLonToScreen(line.Positions[0].Lat, line.Positions[0].Lon);
        (float X, float Y) next = view.LatLonToScreen(line.Positions[1].Lat, line.Positions[1].Lon);
        Assert.True(InfiniteLineDistance(far, end, next) < 1);

        Assert.Null(AirportEditorHitTest.Find(scene, view, far.X, far.Y, null));
        Assert.Equal(new AirportEditorHit(line.FeatureIndex, null), AirportEditorHitTest.Find(scene, view, near.X, near.Y, null));
    }

    [Fact]
    public void TheHitsFeatureIndex_IsTheDocumentIndex_NotTheShapeIndex()
    {
        JsonNode root = JsonNode.Parse(File.ReadAllText(OakPath))!;
        JsonObject first = root["features"]![0]!["properties"]!.AsObject();
        first["type"] = "not-drawn";
        var document = AirportMapDocument.Read(root.ToJsonString());
        var scene = AirportEditorScene.Build(document);
        AirportEditorShape shape = scene.Shapes[0];
        MapViewport view = ViewAt(shape.Positions[0]);

        AirportEditorHit? hit = AirportEditorHitTest.Find(scene, view, Cx, Cy, null);

        Assert.NotNull(hit);
        Assert.Equal(1, hit.FeatureIndex);
        Assert.Equal(shape.FeatureIndex, hit.FeatureIndex);
        Assert.Equal(shape.Positions[0], document.Features[hit.FeatureIndex].Coordinates[0]);
        Assert.Equal("parking", document.Features[hit.FeatureIndex].FeatureType);
    }

    [Fact]
    public void AnEmptyScene_HitsNothing()
    {
        var view = new MapViewport { PixelWidth = 1000, PixelHeight = 800 };

        Assert.Null(AirportEditorHitTest.Find(AirportEditorScene.Empty, view, Cx, Cy, null));
        Assert.Null(AirportEditorHitTest.Find(AirportEditorScene.Empty, view, Cx, Cy, 0));
    }

    private static AirportEditorScene OakScene() => AirportEditorScene.Build(AirportMapDocument.Read(File.ReadAllText(OakPath)));

    // A view about 1.1 m a pixel, centred on the position, so it projects to exactly (Cx, Cy).
    private static MapViewport ViewAt(MapPosition at) =>
        new()
        {
            PixelWidth = 2 * Cx,
            PixelHeight = 2 * Cy,
            Zoom = 20,
            CenterLat = at.Lat,
            CenterLon = at.Lon,
        };

    private static MapViewport ViewAt(double lat, double lon) => ViewAt(new MapPosition(lon, lat));

    // A taxiway segment at least 60 px long whose mid-span, 6 and 6.5 px off along its normal, is far from everything else.
    private static (AirportEditorShape Taxiway, MapViewport View, (float X, float Y) Normal) IsolatedMidSpan(AirportEditorScene scene)
    {
        foreach (AirportEditorShape shape in scene.Shapes.Where(s => s.Kind == AirportEditorFeatureKind.Taxiway))
        {
            for (int i = 1; i < shape.Positions.Length; i++)
            {
                MapPosition a = shape.Positions[i - 1];
                MapPosition b = shape.Positions[i];
                MapViewport view = ViewAt((a.Lat + b.Lat) / 2, (a.Lon + b.Lon) / 2);
                (float X, float Y) sa = view.LatLonToScreen(a.Lat, a.Lon);
                (float X, float Y) sb = view.LatLonToScreen(b.Lat, b.Lon);
                double length = Math.Sqrt(Square(sb.X - sa.X) + Square(sb.Y - sa.Y));
                if (length < 60)
                {
                    continue;
                }

                var normal = ((float)(-(sb.Y - sa.Y) / length), (float)((sb.X - sa.X) / length));
                bool clear = MidSpanOffsets.All(d =>
                    IsClearOfAllButSegment(scene, view, (Cx + (d * normal.Item1), Cy + (d * normal.Item2)), shape, i)
                );
                if (clear)
                {
                    return (shape, view, normal);
                }
            }
        }

        throw new InvalidOperationException("No isolated taxiway mid-span in the KOAK map");
    }

    private static bool IsClearOfAllButSegment(
        AirportEditorScene scene,
        MapViewport view,
        (float X, float Y) click,
        AirportEditorShape shape,
        int segment
    )
    {
        if (DistanceToOthers(scene, view, click, shape) <= 15)
        {
            return false;
        }

        for (int j = 1; j < shape.Positions.Length; j++)
        {
            if ((j != segment) && (SegmentDistance(view, click, shape.Positions[j - 1], shape.Positions[j]) <= 10))
            {
                return false;
            }
        }

        return true;
    }

    // A point with a line passing 0.5-10 px from it, and the click on that line at its nearest place to the point, where
    // every other point is farther from the click than this one.
    private static (AirportEditorShape Point, MapViewport View, (float X, float Y) Click) PointBesideALine(AirportEditorScene scene)
    {
        foreach (AirportEditorShape point in scene.Shapes.Where(s => !AirportEditorHitTest.IsLine(s.Kind)))
        {
            MapViewport view = ViewAt(point.Positions[0]);
            foreach (AirportEditorShape line in scene.Shapes.Where(s => AirportEditorHitTest.IsLine(s.Kind)))
            {
                for (int i = 1; i < line.Positions.Length; i++)
                {
                    (float X, float Y) q = NearestOnSegment(view, (Cx, Cy), line.Positions[i - 1], line.Positions[i]);
                    double d = Math.Sqrt(Square(q.X - Cx) + Square(q.Y - Cy));
                    if ((d >= 0.5) && (d <= 10) && OtherPointsFarther(scene, view, q, point, d + 1))
                    {
                        return (point, view, q);
                    }
                }
            }
        }

        throw new InvalidOperationException("No point beside a line in the KOAK map");
    }

    private static bool OtherPointsFarther(
        AirportEditorScene scene,
        MapViewport view,
        (float X, float Y) click,
        AirportEditorShape point,
        double than
    ) =>
        scene
            .Shapes.Where(s => (!AirportEditorHitTest.IsLine(s.Kind)) && (!ReferenceEquals(s, point)))
            .All(s => ScreenDistance(view, click, s.Positions[0]) > than);

    // An interior taxiway vertex with an axis-aligned click 8 px from it that is over 6.5 px from every line, over 15 px
    // from every other feature, and over 8.5 px from the taxiway's other vertices.
    private static (AirportEditorShape Taxiway, int Vertex, MapViewport View, (float X, float Y) Click) IsolatedVertexClick(AirportEditorScene scene)
    {
        foreach (AirportEditorShape shape in scene.Shapes.Where(s => s.Kind == AirportEditorFeatureKind.Taxiway))
        {
            for (int k = 1; k < shape.Positions.Length - 1; k++)
            {
                MapViewport view = ViewAt(shape.Positions[k]);
                foreach ((float dx, float dy) in AxisDirections)
                {
                    (float X, float Y) click = (Cx + (8f * dx), Cy + (8f * dy));
                    bool clear =
                        (DistanceToOthers(scene, view, click, shape) > 15)
                        && (NearestLine(scene, view, click) > 6.5)
                        && OtherVerticesFarFrom(view, shape, k, click);
                    if (clear)
                    {
                        return (shape, k, view, click);
                    }
                }
            }
        }

        throw new InvalidOperationException("No isolated taxiway vertex in the KOAK map");
    }

    private static bool OtherVerticesFarFrom(MapViewport view, AirportEditorShape shape, int vertex, (float X, float Y) click) =>
        shape.Positions.Select((p, i) => (p, i)).Where(x => x.i != vertex).All(x => ScreenDistance(view, click, x.p) > 8.5);

    // A runway or taxiway whose first position is an end nothing else comes within 15 px of, 5 and 10 px out along the
    // line's direction past that end.
    private static (AirportEditorShape Line, MapViewport View, (float X, float Y) Along) IsolatedLineEnd(AirportEditorScene scene)
    {
        foreach (AirportEditorShape shape in scene.Shapes.Where(s => AirportEditorHitTest.IsLine(s.Kind)))
        {
            MapViewport view = ViewAt(shape.Positions[0]);
            (float X, float Y) next = view.LatLonToScreen(shape.Positions[1].Lat, shape.Positions[1].Lon);
            double length = Math.Sqrt(Square(next.X - Cx) + Square(next.Y - Cy));
            if (length < 20)
            {
                continue;
            }

            var along = ((float)((Cx - next.X) / length), (float)((Cy - next.Y) / length));
            (float X, float Y) far = (Cx + (10f * along.Item1), Cy + (10f * along.Item2));
            (float X, float Y) near = (Cx + (5f * along.Item1), Cy + (5f * along.Item2));
            if ((DistanceToOthers(scene, view, far, shape) > 15) && (DistanceToOthers(scene, view, near, shape) > 15))
            {
                return (shape, view, along);
            }
        }

        throw new InvalidOperationException("No isolated line end in the KOAK map");
    }

    // The nearest any shape but one comes to the click: a point's position, or a line's nearest segment.
    private static double DistanceToOthers(AirportEditorScene scene, MapViewport view, (float X, float Y) click, AirportEditorShape except) =>
        scene.Shapes.Where(s => !ReferenceEquals(s, except)).Select(s => ShapeDistance(view, click, s)).DefaultIfEmpty(double.MaxValue).Min();

    private static double NearestLine(AirportEditorScene scene, MapViewport view, (float X, float Y) click) =>
        scene.Shapes.Where(s => AirportEditorHitTest.IsLine(s.Kind)).Select(s => ShapeDistance(view, click, s)).DefaultIfEmpty(double.MaxValue).Min();

    private static double ShapeDistance(MapViewport view, (float X, float Y) click, AirportEditorShape shape)
    {
        if (!AirportEditorHitTest.IsLine(shape.Kind))
        {
            return ScreenDistance(view, click, shape.Positions[0]);
        }

        double best = double.MaxValue;
        for (int i = 1; i < shape.Positions.Length; i++)
        {
            best = Math.Min(best, SegmentDistance(view, click, shape.Positions[i - 1], shape.Positions[i]));
        }

        return best;
    }

    private static double ScreenDistance(MapViewport view, (float X, float Y) click, MapPosition position)
    {
        (float X, float Y) s = view.LatLonToScreen(position.Lat, position.Lon);
        return Math.Sqrt(Square(s.X - click.X) + Square(s.Y - click.Y));
    }

    private static double SegmentDistance(MapViewport view, (float X, float Y) click, MapPosition a, MapPosition b)
    {
        (float X, float Y) q = NearestOnSegment(view, click, a, b);
        return Math.Sqrt(Square(q.X - click.X) + Square(q.Y - click.Y));
    }

    private static (float X, float Y) NearestOnSegment(MapViewport view, (float X, float Y) click, MapPosition a, MapPosition b)
    {
        (float X, float Y) sa = view.LatLonToScreen(a.Lat, a.Lon);
        (float X, float Y) sb = view.LatLonToScreen(b.Lat, b.Lon);
        double dx = sb.X - sa.X;
        double dy = sb.Y - sa.Y;
        double lengthSquared = (dx * dx) + (dy * dy);
        double t = lengthSquared > 0 ? Math.Clamp((((click.X - sa.X) * dx) + ((click.Y - sa.Y) * dy)) / lengthSquared, 0, 1) : 0;
        return ((float)(sa.X + (t * dx)), (float)(sa.Y + (t * dy)));
    }

    private static double InfiniteLineDistance((float X, float Y) p, (float X, float Y) a, (float X, float Y) b)
    {
        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        return Math.Abs((dx * (a.Y - p.Y)) - ((a.X - p.X) * dy)) / Math.Sqrt((dx * dx) + (dy * dy));
    }

    private static double Square(double v) => v * v;
}
