using SkiaSharp;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.Views.Ground;
using Yaat.Client.Views.Map;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;

namespace Yaat.Client.Tests;

/// <summary>
/// Renders a ground view frame (<see cref="GroundRenderer.Render"/>) of an airport with no ground map, whose layout is its
/// navdata runways alone (<see cref="GroundLayoutDto.IsRunwaysOnly"/>), to an offscreen bitmap: each runway end is labelled
/// with its designator 16 px beyond its threshold, the runway carries no mid label, and the corner note says there is no
/// ground map. Each probe is compared with the same frame of a full layout, which draws none of them there.
/// </summary>
public class GroundRendererRunwaysOnlyTests
{
    public GroundRendererRunwaysOnlyTests() => Yaat.Sim.Testing.TestVnasData.EnsureInitialized();

    private const int SurfacePx = 600;
    private const float RunwayLengthPx = 400;

    /// <summary>Half the side of the square probed around a label's centre, in pixels.</summary>
    private const int LabelProbeHalfPx = 4;

    [Fact]
    public void RunwaysOnly_LabelsEachEndWithItsDesignator_16PxBeyondItsThreshold()
    {
        GroundRunwayDto runway = SmfRunway();
        MapViewport vp = Viewport(runway);
        var ids = RunwayIdentifier.Parse(runway.Name);
        (float x1, float y1) = vp.LatLonToScreen(runway.Coordinates[0][0], runway.Coordinates[0][1]);
        (float x2, float y2) = vp.LatLonToScreen(runway.Coordinates[^1][0], runway.Coordinates[^1][1]);

        IReadOnlyList<(string Text, SKPoint At)> labels = GroundRenderer.RunwayEndLabels(runway, vp);

        Assert.Equal([RunwayIdentifier.ToDisplayDesignator(ids.End1), RunwayIdentifier.ToDisplayDesignator(ids.End2)], labels.Select(l => l.Text));
        Assert.Equal(16, Distance(labels[0].At, new SKPoint(x1, y1)), 2);
        Assert.Equal(16, Distance(labels[1].At, new SKPoint(x2, y2)), 2);
        // Beyond the threshold: farther from the other end than the threshold is.
        Assert.True(Distance(labels[0].At, new SKPoint(x2, y2)) > Distance(new SKPoint(x1, y1), new SKPoint(x2, y2)));
        Assert.True(Distance(labels[1].At, new SKPoint(x1, y1)) > Distance(new SKPoint(x1, y1), new SKPoint(x2, y2)));

        using SKBitmap runwaysOnly = Render(vp, Layout(runway, runwaysOnly: true), showRunwayLabels: true);
        using SKBitmap full = Render(vp, Layout(runway, runwaysOnly: false), showRunwayLabels: true);
        SKColor background = full.GetPixel(1, 1);
        foreach ((string text, SKPoint at) in labels)
        {
            Assert.All(Probe(full, at, LabelProbeHalfPx), pixel => Assert.Equal(background, pixel));
            Assert.Contains(Probe(runwaysOnly, at, LabelProbeHalfPx), pixel => pixel != background);
        }
    }

    /// <summary>
    /// Two crossing runways, the first's end label lying on the second's surface: the label is drawn after both surfaces,
    /// so the second runway's fill does not paint over it.
    /// </summary>
    [Fact]
    public void RunwaysOnly_EndLabelOnALaterRunwaysSurface_IsDrawnOverIt()
    {
        var vp = new MapViewport
        {
            CenterLat = 38.70,
            CenterLon = -121.60,
            Zoom = 10,
            PixelWidth = SurfacePx,
            PixelHeight = SurfacePx,
            RotationDeg = 0,
        };
        // The first runway runs left to right from x = 150; its end-1 label sits 16 px left of that, at x = 134, which is
        // the centreline of the second runway, a wide one running top to bottom and drawn after it.
        GroundRunwayDto first = ScreenRunway(vp, "9/27", new SKPoint(150, 300), new SKPoint(350, 300), 150);
        GroundRunwayDto second = ScreenRunway(vp, "18/36", new SKPoint(134, 100), new SKPoint(134, 500), 400);
        SKPoint label = GroundRenderer.RunwayEndLabels(first, vp)[0].At;
        Assert.Equal(134, label.X, 1);
        Assert.Equal(300, label.Y, 1);
        var layout = new GroundLayoutDto("SMF", [], [], null, [first, second], null, IsRunwaysOnly: true);

        using SKBitmap labelled = Render(vp, layout, showRunwayLabels: true);
        using SKBitmap bare = Render(vp, layout, showRunwayLabels: false);

        Assert.NotEqual(Probe(bare, label, LabelProbeHalfPx), Probe(labelled, label, LabelProbeHalfPx));
    }

    /// <summary>A runway between two screen points of <paramref name="vp"/>, <paramref name="widthFt"/> wide.</summary>
    private static GroundRunwayDto ScreenRunway(MapViewport vp, string name, SKPoint from, SKPoint to, double widthFt)
    {
        (double lat1, double lon1) = vp.ScreenToLatLon(from.X, from.Y);
        (double lat2, double lon2) = vp.ScreenToLatLon(to.X, to.Y);
        return new GroundRunwayDto(
            name,
            [
                [lat1, lon1],
                [lat2, lon2],
            ],
            widthFt
        );
    }

    [Fact]
    public void RunwaysOnly_DrawsNoMidLabel()
    {
        GroundRunwayDto runway = SmfRunway();
        MapViewport vp = Viewport(runway);
        (float mx, float my) = vp.LatLonToScreen(
            (runway.Coordinates[0][0] + runway.Coordinates[^1][0]) / 2.0,
            (runway.Coordinates[0][1] + runway.Coordinates[^1][1]) / 2.0
        );
        var mid = new SKPoint(mx, my);

        using SKBitmap fullLabelled = Render(vp, Layout(runway, runwaysOnly: false), showRunwayLabels: true);
        using SKBitmap fullBare = Render(vp, Layout(runway, runwaysOnly: false), showRunwayLabels: false);
        using SKBitmap onlyLabelled = Render(vp, Layout(runway, runwaysOnly: true), showRunwayLabels: true);
        using SKBitmap onlyBare = Render(vp, Layout(runway, runwaysOnly: true), showRunwayLabels: false);

        // The full layout's mid label changes the probed box, so the probe can see a mid label.
        Assert.NotEqual(Probe(fullBare, mid, 30), Probe(fullLabelled, mid, 30));
        Assert.Equal(Probe(onlyBare, mid, 30), Probe(onlyLabelled, mid, 30));
    }

    [Fact]
    public void RunwaysOnly_DrawsTheNoGroundMapNote_BottomRight()
    {
        GroundRunwayDto runway = SmfRunway();
        MapViewport vp = Viewport(runway);
        // Just inside the note's right end: 14 px in from the right edge, on the text 12 px up from the bottom.
        var inNote = new SKPoint(SurfacePx - 20, SurfacePx - 16);

        using SKBitmap runwaysOnly = Render(vp, Layout(runway, runwaysOnly: true), showRunwayLabels: false);
        using SKBitmap full = Render(vp, Layout(runway, runwaysOnly: false), showRunwayLabels: false);

        Assert.Equal("No ground map — runways only", GroundRenderer.RunwaysOnlyNote);
        SKColor background = full.GetPixel(1, 1);
        Assert.All(Probe(full, inNote, 5), pixel => Assert.Equal(background, pixel));
        Assert.Contains(Probe(runwaysOnly, inNote, 5), pixel => (pixel.Red > 120) && (pixel.Blue > 120));
    }

    /// <summary>SMF 17L/35R from navdata, as the server sends a runways-only airport's runway: both thresholds and the width.</summary>
    private static GroundRunwayDto SmfRunway()
    {
        RunwayInfo info = NavigationDatabase.Instance.GetRunway("SMF", "17L") ?? throw new InvalidOperationException("SMF 17L is not in navdata");
        return new GroundRunwayDto(
            info.Id.ToString(),
            [
                [info.Lat1, info.Lon1],
                [info.Lat2, info.Lon2],
            ],
            info.WidthFt
        );
    }

    private static GroundLayoutDto Layout(GroundRunwayDto runway, bool runwaysOnly) =>
        new("SMF", [], [], null, [runway], null, IsRunwaysOnly: runwaysOnly);

    /// <summary>Centred on the runway, zoomed so it spans <see cref="RunwayLengthPx"/>.</summary>
    private static MapViewport Viewport(GroundRunwayDto runway)
    {
        var vp = new MapViewport
        {
            CenterLat = (runway.Coordinates[0][0] + runway.Coordinates[^1][0]) / 2.0,
            CenterLon = (runway.Coordinates[0][1] + runway.Coordinates[^1][1]) / 2.0,
            Zoom = 1.0,
            PixelWidth = SurfacePx,
            PixelHeight = SurfacePx,
            RotationDeg = 0,
        };
        (float x1, float y1) = vp.LatLonToScreen(runway.Coordinates[0][0], runway.Coordinates[0][1]);
        (float x2, float y2) = vp.LatLonToScreen(runway.Coordinates[^1][0], runway.Coordinates[^1][1]);
        vp.Zoom = RunwayLengthPx / Distance(new SKPoint(x1, y1), new SKPoint(x2, y2));
        return vp;
    }

    private static float Distance(SKPoint a, SKPoint b) => SKPoint.Distance(a, b);

    /// <summary>The pixels of the square of half side <paramref name="halfPx"/> centred on <paramref name="at"/>, row by row.</summary>
    private static List<SKColor> Probe(SKBitmap bitmap, SKPoint at, int halfPx)
    {
        int cx = (int)MathF.Round(at.X);
        int cy = (int)MathF.Round(at.Y);
        var pixels = new List<SKColor>();
        for (int y = cy - halfPx; y <= cy + halfPx; y++)
        {
            for (int x = cx - halfPx; x <= cx + halfPx; x++)
            {
                pixels.Add(bitmap.GetPixel(x, y));
            }
        }

        return pixels;
    }

    private static SKBitmap Render(MapViewport vp, GroundLayoutDto layout, bool showRunwayLabels)
    {
        var bitmap = new SKBitmap(SurfacePx, SurfacePx);
        using var canvas = new SKCanvas(bitmap);
        using var renderer = new GroundRenderer();
        renderer.Render(
            canvas,
            vp,
            layout,
            [],
            selectedAircraft: null,
            hoveredNodeId: null,
            hoveredRunwayEnd: null,
            hoverRoute: null,
            previewRoute: null,
            scopePreview: null,
            drawnRoutePreview: null,
            drawHoverPreview: null,
            drawWaypoints: null,
            pushWaypointMarks: null,
            pushPlan: null,
            dataBlockOffsets: null,
            deconflictOffsets: null,
            showDebugInfo: false,
            weatherInfo: null,
            weatherNote: null,
            showRunwayLabels: showRunwayLabels,
            showTaxiwayLabels: false,
            showHoldShort: GroundFilterMode.Off,
            showParking: GroundFilterMode.Off,
            showSpot: GroundFilterMode.Off,
            shownTaxiRoutes: null,
            highlightedCallsigns: null,
            hiddenDataBlockCallsigns: null,
            backgroundImage: null,
            towerCabMap: null,
            showSatelliteImage: false,
            satelliteImageBrightness: 100,
            showVideoMapOverlay: false,
            videoMapOverlayBrightness: 100,
            showYaatLayout: true,
            yaatLayoutBrightness: 100,
            showAdwMarkings: false,
            ctrlNodeHover: null
        );
        return bitmap;
    }
}
