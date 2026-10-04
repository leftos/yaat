using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.Views.Ground;
using Yaat.Sim;
using Yaat.Sim.Data.Airport;

namespace Yaat.Client.UI.Tests.Views;

// The runway-surface click target on the real KOAK and KSFO layouts: which runways a point lies on, and what a right-click
// there raises — the surface menu with a taxiable selection, the node menu near a hold short, the nearest-node fallback
// with nothing selected.
public class GroundCanvasRunwaySurfaceHitTests
{
    /// <summary>The runway's half-width the tests zoom to, in pixels: wide enough that a centerline click sits far from the edges.</summary>
    private const double HalfWidthPx = 30;

    /// <summary>How far inside the threshold a threshold click lands, in feet: solidly on the surface, inside the 18 px marker radius.</summary>
    private const double InsideThresholdFt = 10;

    [AvaloniaFact]
    public void FindRunwaysAtPoint_MidRunway_ReturnsThatRunway()
    {
        GroundLayoutDto layout = MenuGoldenFixtures.OakLayoutForClient;
        GroundCanvas canvas = MakeCanvas(layout);
        GroundRunwayDto runway = Runway(layout, "28R/10L");
        LatLon mid = Midpoint(runway);
        FocusOn(canvas, runway, mid, HalfWidthPx);

        Assert.Equal([runway.Name], canvas.FindRunwaysAtPoint(Screen(canvas, mid)));
    }

    [AvaloniaFact]
    public void FindRunwaysAtPoint_OffSurface_ReturnsNone()
    {
        GroundLayoutDto layout = MenuGoldenFixtures.OakLayoutForClient;
        GroundCanvas canvas = MakeCanvas(layout);
        GroundRunwayDto runway = Runway(layout, "28R/10L");
        LatLon mid = Midpoint(runway);
        FocusOn(canvas, runway, mid, HalfWidthPx);
        // Two half-widths off the centerline: beside the runway, well short of the parallel 28L/10R.
        LatLon beside = GeoMath.ProjectPoint(mid, new TrueHeading(Heading(runway) + 90), 2 * HalfWidthNm(runway));

        Assert.Empty(canvas.FindRunwaysAtPoint(Screen(canvas, beside)));
        // The same point, inside the rectangle when the click is on it, proves the projection lands where the test means.
        Assert.NotEmpty(canvas.FindRunwaysAtPoint(Screen(canvas, mid)));
    }

    [AvaloniaFact]
    public void FindRunwaysAtPoint_RunwayCrossing_ReturnsBoth()
    {
        GroundLayoutDto layout = SfoLayout.Value;
        GroundCanvas canvas = MakeCanvas(layout);
        GroundRunwayDto tenLeft = Runway(layout, "28R/10L");
        GroundRunwayDto oneLeft = Runway(layout, "1L/19R");
        LatLon crossing = Intersection(tenLeft, oneLeft);
        FocusOn(canvas, tenLeft, crossing, HalfWidthPx);

        IReadOnlyList<string> hits = canvas.FindRunwaysAtPoint(Screen(canvas, crossing));

        Assert.Equal(2, hits.Count);
        Assert.Contains(tenLeft.Name, hits);
        Assert.Contains(oneLeft.Name, hits);
    }

    [AvaloniaFact]
    public void FindRunwaysAtPoint_CrossingOffset_OrdersTheNearerCenterlineFirst()
    {
        GroundLayoutDto layout = SfoLayout.Value;
        GroundCanvas canvas = MakeCanvas(layout);
        GroundRunwayDto tenLeft = Runway(layout, "28R/10L");
        GroundRunwayDto oneLeft = Runway(layout, "1L/19R");
        LatLon crossing = Intersection(tenLeft, oneLeft);
        FocusOn(canvas, tenLeft, crossing, HalfWidthPx);

        // Half of the other runway's half-width off the crossing along one centerline: still inside both rectangles,
        // but this runway's centerline is the nearer of the two.
        LatLon alongTenLeft = GeoMath.ProjectPoint(crossing, new TrueHeading(Heading(tenLeft)), 0.5 * HalfWidthNm(oneLeft));
        LatLon alongOneLeft = GeoMath.ProjectPoint(crossing, new TrueHeading(Heading(oneLeft)), 0.5 * HalfWidthNm(tenLeft));

        IReadOnlyList<string> onTenLeft = canvas.FindRunwaysAtPoint(Screen(canvas, alongTenLeft));
        IReadOnlyList<string> onOneLeft = canvas.FindRunwaysAtPoint(Screen(canvas, alongOneLeft));

        Assert.Equal(2, onTenLeft.Count);
        Assert.Equal(tenLeft.Name, onTenLeft[0]);
        Assert.Contains(oneLeft.Name, onTenLeft);
        Assert.Equal(2, onOneLeft.Count);
        Assert.Equal(oneLeft.Name, onOneLeft[0]);
        Assert.Contains(tenLeft.Name, onOneLeft);
    }

    [AvaloniaFact]
    public void RightClick_RunwaySurfaceWithTaxiableSelection_RaisesSurfaceNotNode()
    {
        GroundLayoutDto layout = MenuGoldenFixtures.OakLayoutForClient;
        (GroundCanvas canvas, Window window) = ShowCanvas(layout);
        try
        {
            canvas.SelectedAircraft = GroundAircraft(layout);
            GroundRunwayDto runway = Runway(layout, "28R/10L");
            LatLon mid = Midpoint(runway);
            FocusOn(canvas, runway, mid, HalfWidthPx);
            (List<IReadOnlyList<string>> surface, List<int> nodes) = Record(canvas);

            RightClick(canvas, window, Screen(canvas, mid));

            Assert.Equal([runway.Name], Assert.Single(surface));
            Assert.Empty(nodes);
        }
        finally
        {
            Close(window);
        }
    }

    // The threshold marker outranks the runway surface: a click on the marker, with a taxiable selection, opens the
    // threshold menu rather than the surface menu. The layout carries the runway DTO alone (no nodes), so a real OAK
    // centerline node cannot win the node rung ahead of the threshold order under test.
    [AvaloniaFact]
    public void RightClick_ThresholdMarkerWithTaxiableSelection_StaysThresholdClick()
    {
        GroundRunwayDto runway = Runway(MenuGoldenFixtures.OakLayoutForClient, "28R/10L");
        var layout = new GroundLayoutDto("OAK", [], [], null, [runway], null);
        (GroundCanvas canvas, Window window) = ShowCanvas(layout);
        try
        {
            canvas.SelectedAircraft = GroundAircraft(MenuGoldenFixtures.OakLayoutForClient);
            var ids = RunwayIdentifier.Parse(runway.Name);
            var threshold = new LatLon(runway.Coordinates[0][0], runway.Coordinates[0][1]);
            FocusOn(canvas, runway, threshold, HalfWidthPx);
            // A few feet inside the threshold: on the surface, still within the marker's 18 px radius.
            LatLon clickPos = GeoMath.ProjectPoint(threshold, new TrueHeading(Heading(runway)), InsideThresholdFt / GeoMath.FeetPerNm);
            Point click = Screen(canvas, clickPos);
            Assert.Contains(runway.Name, canvas.FindRunwaysAtPoint(click));
            Assert.Equal(ids.End1, canvas.FindRunwayThresholdAtPoint(click)!.Value.RunwayEnd);
            var thresholds = new List<string>();
            canvas.RunwayThresholdRightClicked += (end, _) => thresholds.Add(end);
            (List<IReadOnlyList<string>> surface, List<int> nodes) = Record(canvas);

            RightClick(canvas, window, click);

            Assert.Equal([ids.End1], thresholds);
            Assert.Empty(surface);
            Assert.Empty(nodes);
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaFact]
    public void RightClick_HoldShortNodeOnRunway_StaysANodeClick()
    {
        GroundLayoutDto layout = MenuGoldenFixtures.OakLayoutForClient;
        (GroundCanvas canvas, Window window) = ShowCanvas(layout);
        try
        {
            canvas.SelectedAircraft = GroundAircraft(layout);
            GroundRunwayDto runway = Runway(layout, "28R/10L");
            var id = RunwayIdentifier.Parse(runway.Name);
            GroundNodeDto holdShort = layout.Nodes.First(n =>
                (n.Type == "RunwayHoldShort") && (n.RunwayId is { } rwy) && (RunwayIdentifier.Parse(rwy) == id)
            );
            var holdShortPos = new LatLon(holdShort.Latitude, holdShort.Longitude);
            LatLon foot = CenterlineFoot(runway, holdShortPos);
            // The hold short sits 12 px from the centerline point clicked: inside the 20 px node radius, on the surface.
            FocusOn(canvas, foot, holdShortPos, 12);
            Point click = Screen(canvas, foot);
            Assert.Contains(runway.Name, canvas.FindRunwaysAtPoint(click));
            (List<IReadOnlyList<string>> surface, List<int> nodes) = Record(canvas);

            RightClick(canvas, window, click);

            Assert.Empty(surface);
            Assert.Equal([holdShort.Id], nodes);
        }
        finally
        {
            Close(window);
        }
    }

    // The hold-short rung outranks the nearest-node rung: a click on the runway surface whose nearest node is an
    // ordinary one still opens the hold-short menu when a RunwayHoldShort sits within the node radius.
    [AvaloniaFact]
    public void RightClick_HoldShortWithin20pxButNotNearest_OpensTheHoldShort()
    {
        GroundLayoutDto layout = MenuGoldenFixtures.OakLayoutForClient;
        (GroundCanvas canvas, Window window) = ShowCanvas(layout);
        try
        {
            canvas.SelectedAircraft = GroundAircraft(layout);
            GroundNodeDto seed = layout.Nodes.First(n => n.Type == "RunwayHoldShort");
            GroundNodeDto nearestNode = NearestSurfaceNode(canvas, layout, Position(seed));
            GroundNodeDto holdShort = layout
                .Nodes.Where(n => n.Type == "RunwayHoldShort")
                .OrderBy(n => GeoMath.DistanceNm(Position(nearestNode), Position(n)))
                .First();
            // The click sits on the ordinary node, which is nearer; the hold short is 15 px away, inside the 20 px radius.
            FocusOn(canvas, Position(nearestNode), Position(holdShort), 15);
            Point click = Screen(canvas, Position(nearestNode));
            Assert.NotEmpty(canvas.FindRunwaysAtPoint(click));
            Assert.Equal(nearestNode.Id, canvas.FindNearestNode(click)!.Id);
            (List<IReadOnlyList<string>> surface, List<int> nodes) = Record(canvas);

            RightClick(canvas, window, click);

            Assert.Empty(surface);
            Assert.Equal([holdShort.Id], nodes);
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaFact]
    public void RightClick_RunwaySurfaceWithNoSelection_KeepsNearestNodeFallback()
    {
        GroundLayoutDto layout = MenuGoldenFixtures.OakLayoutForClient;
        (GroundCanvas canvas, Window window) = ShowCanvas(layout);
        try
        {
            GroundRunwayDto runway = Runway(layout, "28R/10L");
            LatLon mid = Midpoint(runway);
            FocusOn(canvas, runway, mid, HalfWidthPx);
            Point click = Screen(canvas, mid);
            Assert.Contains(runway.Name, canvas.FindRunwaysAtPoint(click));
            (List<IReadOnlyList<string>> surface, List<int> nodes) = Record(canvas);

            RightClick(canvas, window, click);

            Assert.Empty(surface);
            Assert.Equal([canvas.FindNearestNode(click)!.Id], nodes);
        }
        finally
        {
            Close(window);
        }
    }

    // An airborne selection never gets the runway-surface menu: the click falls through to the nearest-node fallback,
    // exactly as it does with nothing selected.
    [AvaloniaFact]
    public void RightClick_RunwaySurfaceWithAirborneSelection_KeepsNearestNodeFallback()
    {
        GroundLayoutDto layout = MenuGoldenFixtures.OakLayoutForClient;
        (GroundCanvas canvas, Window window) = ShowCanvas(layout);
        try
        {
            canvas.SelectedAircraft = AirborneAircraft();
            GroundRunwayDto runway = Runway(layout, "28R/10L");
            LatLon mid = Midpoint(runway);
            FocusOn(canvas, runway, mid, HalfWidthPx);
            Point click = Screen(canvas, mid);
            Assert.Contains(runway.Name, canvas.FindRunwaysAtPoint(click));
            (List<IReadOnlyList<string>> surface, List<int> nodes) = Record(canvas);

            RightClick(canvas, window, click);

            Assert.Empty(surface);
            Assert.Equal([canvas.FindNearestNode(click)!.Id], nodes);
        }
        finally
        {
            Close(window);
        }
    }

    // --- Fixtures ---------------------------------------------------------------------------

    private static readonly Lazy<GroundLayoutDto> SfoLayout = new(() =>
    {
        string path = Path.Combine(AppContext.BaseDirectory, "TestData", "sfo.geojson");
        return MenuGoldenFixtures.ToGroundLayoutDto(GeoJsonParser.Parse("SFO", File.ReadAllText(path), null, FilletMode.Standard));
    });

    private static GroundRunwayDto Runway(GroundLayoutDto layout, string name)
    {
        var id = RunwayIdentifier.Parse(name);
        return Assert.Single(layout.Runways!, r => RunwayIdentifier.Parse(r.Name) == id);
    }

    private static LatLon Position(GroundNodeDto node) => new(node.Latitude, node.Longitude);

    /// <summary>The ordinary node nearest <paramref name="from"/> that lies on a runway surface; throws when the layout has none.</summary>
    private static GroundNodeDto NearestSurfaceNode(GroundCanvas canvas, GroundLayoutDto layout, LatLon from)
    {
        IEnumerable<GroundNodeDto> ordered = layout.Nodes.Where(n => n.Type != "RunwayHoldShort").OrderBy(n => GeoMath.DistanceNm(from, Position(n)));
        foreach (GroundNodeDto node in ordered)
        {
            canvas.Viewport.CenterLat = node.Latitude;
            canvas.Viewport.CenterLon = node.Longitude;
            canvas.Viewport.Zoom = 1.0;
            if (canvas.FindRunwaysAtPoint(Screen(canvas, Position(node))).Count > 0)
            {
                return node;
            }
        }

        throw new InvalidOperationException("No ordinary OAK node lies on a runway surface");
    }

    private static LatLon Midpoint(GroundRunwayDto runway) =>
        new((runway.Coordinates[0][0] + runway.Coordinates[^1][0]) / 2.0, (runway.Coordinates[0][1] + runway.Coordinates[^1][1]) / 2.0);

    private static double Heading(GroundRunwayDto runway) =>
        GeoMath.BearingTo(runway.Coordinates[0][0], runway.Coordinates[0][1], runway.Coordinates[^1][0], runway.Coordinates[^1][1]);

    private static double HalfWidthNm(GroundRunwayDto runway) => runway.WidthFt / 2.0 / GeoMath.FeetPerNm;

    /// <summary>The point on <paramref name="runway"/>'s centerline nearest <paramref name="point"/>, on a local flat projection.</summary>
    private static LatLon CenterlineFoot(GroundRunwayDto runway, LatLon point)
    {
        double cos = Math.Cos(runway.Coordinates[0][0] * Math.PI / 180.0);
        (double ax, double ay) = (runway.Coordinates[0][1] * cos, runway.Coordinates[0][0]);
        (double bx, double by) = (runway.Coordinates[^1][1] * cos, runway.Coordinates[^1][0]);
        (double px, double py) = (point.Lon * cos, point.Lat);
        double t = (((px - ax) * (bx - ax)) + ((py - ay) * (by - ay))) / (((bx - ax) * (bx - ax)) + ((by - ay) * (by - ay)));
        t = Math.Clamp(t, 0, 1);
        return new LatLon(ay + (t * (by - ay)), (ax + (t * (bx - ax))) / cos);
    }

    /// <summary>Where the two runways' centerlines cross, on a local flat projection.</summary>
    private static LatLon Intersection(GroundRunwayDto a, GroundRunwayDto b)
    {
        double cos = Math.Cos(a.Coordinates[0][0] * Math.PI / 180.0);
        (double x1, double y1) = (a.Coordinates[0][1] * cos, a.Coordinates[0][0]);
        (double x2, double y2) = (a.Coordinates[^1][1] * cos, a.Coordinates[^1][0]);
        (double x3, double y3) = (b.Coordinates[0][1] * cos, b.Coordinates[0][0]);
        (double x4, double y4) = (b.Coordinates[^1][1] * cos, b.Coordinates[^1][0]);
        double denominator = ((x1 - x2) * (y3 - y4)) - ((y1 - y2) * (x3 - x4));
        double t = (((x1 - x3) * (y3 - y4)) - ((y1 - y3) * (x3 - x4))) / denominator;
        Assert.InRange(t, 0, 1);
        return new LatLon(y1 + (t * (y2 - y1)), (x1 + (t * (x2 - x1))) / cos);
    }

    /// <summary>
    /// Centres the view on <paramref name="center"/> and zooms so the runway's half-width spans <paramref name="halfWidthPx"/> pixels.
    /// </summary>
    private static void FocusOn(GroundCanvas canvas, GroundRunwayDto runway, LatLon center, double halfWidthPx) =>
        FocusOn(canvas, center, GeoMath.ProjectPoint(center, new TrueHeading(Heading(runway) + 90), HalfWidthNm(runway)), halfWidthPx);

    /// <summary>
    /// Centres the view on <paramref name="center"/> and zooms so <paramref name="other"/> is <paramref name="px"/> pixels from it.
    /// </summary>
    private static void FocusOn(GroundCanvas canvas, LatLon center, LatLon other, double px)
    {
        canvas.Viewport.CenterLat = center.Lat;
        canvas.Viewport.CenterLon = center.Lon;
        canvas.Viewport.Zoom = 1.0;
        Point a = Screen(canvas, center);
        Point b = Screen(canvas, other);
        double distance = Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));
        canvas.Viewport.Zoom = px / distance;
    }

    private static Point Screen(GroundCanvas canvas, LatLon position)
    {
        (float x, float y) = canvas.Viewport.LatLonToScreen(position.Lat, position.Lon);
        return new Point(x, y);
    }

    private static GroundCanvas MakeCanvas(GroundLayoutDto layout)
    {
        var canvas = new GroundCanvas();
        canvas.Viewport.PixelWidth = 800;
        canvas.Viewport.PixelHeight = 600;
        canvas.Layout = layout;
        return canvas;
    }

    private static (GroundCanvas Canvas, Window Window) ShowCanvas(GroundLayoutDto layout)
    {
        var canvas = new GroundCanvas();
        var window = new Window
        {
            Width = 800,
            Height = 600,
            Content = canvas,
        };
        window.Show();
        canvas.Layout = layout;
        for (int i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }

        return (canvas, window);
    }

    /// <summary>A B738 at parking on spot I30, which can be given a taxi route.</summary>
    private static AircraftModel GroundAircraft(GroundLayoutDto layout)
    {
        GroundNodeDto spot = layout.Nodes.First(n => (n.Type == "Spot") && (n.Name == "I30"));
        return new AircraftModel
        {
            Callsign = "AAL202",
            AircraftType = "B738",
            FlightRules = "IFR",
            IsOnGround = true,
            CurrentPhase = "At Parking",
            Position = new LatLon(spot.Latitude, spot.Longitude),
        };
    }

    /// <summary>A B738 enroute, which cannot be given a taxi route: the surface menu is withheld.</summary>
    private static AircraftModel AirborneAircraft() =>
        new()
        {
            Callsign = "AAL202",
            AircraftType = "B738",
            FlightRules = "IFR",
            CurrentPhase = "",
            IsOnGround = false,
            Position = new LatLon(37.5000, -121.7000),
        };

    private static (List<IReadOnlyList<string>> Surface, List<int> Nodes) Record(GroundCanvas canvas)
    {
        var surface = new List<IReadOnlyList<string>>();
        var nodes = new List<int>();
        canvas.RunwaySurfaceRightClicked += (runways, _) => surface.Add(runways);
        canvas.NodeRightClicked += (id, _) => nodes.Add(id);
        return (surface, nodes);
    }

    private static void RightClick(GroundCanvas canvas, Window window, Point canvasPoint)
    {
        Point? inWindow = canvas.TranslatePoint(canvasPoint, window);
        Assert.NotNull(inWindow);
        window.MouseDown(inWindow.Value, MouseButton.Right);
        window.MouseUp(inWindow.Value, MouseButton.Right);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Close(Window window)
    {
        window.Close();
        Dispatcher.UIThread.RunJobs();
    }
}
