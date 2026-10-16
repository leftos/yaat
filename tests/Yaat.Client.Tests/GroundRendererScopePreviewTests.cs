using SkiaSharp;
using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.Views.Ground;
using Yaat.Client.Views.Map;
using Yaat.Sim;
using Yaat.Sim.Data.Faa;

namespace Yaat.Client.Tests;

/// <summary>
/// Renders a ground view frame (<see cref="GroundRenderer.Render"/>, the aircraft drawn) to an offscreen bitmap and probes
/// the scope preview's marks: the ring around the aircraft the preview names, in the light highlight blue, standing clear
/// of the aircraft's drawn symbol however large it is drawn, and the amber ring at the point where the two routes meet;
/// with no preview, neither is drawn.
/// </summary>
public class GroundRendererScopePreviewTests
{
    public GroundRendererScopePreviewTests() => Yaat.Sim.Testing.TestVnasData.EnsureInitialized();

    private const double CenterLat = 37.62;
    private const double CenterLon = -122.38;
    private const int SurfacePx = 600;
    private const double FeetPerDegLat = 364_567.2;
    private const float PxPerFt = 1.5f;
    private const string Target = "UAL123";
    private const string TargetType = "B738";

    /// <summary>The meet point, 150 ft north of the target, clear of its symbol and its rings.</summary>
    private static readonly LatLon Meet = new(CenterLat + (150 / FeetPerDegLat), CenterLon);

    [Fact]
    public void PreviewSet_RingsTheTargetClearOfItsSymbol_AndRingsTheMeetPointInAmber()
    {
        MapViewport vp = Viewport();
        float halfSymbolPx = HalfSymbolPx();
        Assert.True(halfSymbolPx > GroundRenderer.ScopeRingMinRadiusPx, "the test zoom must draw the aircraft larger than the smallest ring");
        (float ringX, float ringY) = RingStroke(vp, halfSymbolPx + GroundRenderer.ScopeRingMarginPx);
        (float meetX, float meetY) = MeetStroke(vp);

        using SKBitmap bitmap = Render(vp, new ScopePreview(Target, null, null, null, Meet));

        SKColor ring = PixelAt(bitmap, ringX, ringY);
        Assert.True((ring.Blue > 200) && (ring.Red < 180), $"the target ring's stroke should be light blue past the symbol, was {ring}");
        SKColor meet = PixelAt(bitmap, meetX, meetY);
        Assert.True((meet.Red > 200) && (meet.Green > 140) && (meet.Blue < 140), $"the meet ring's stroke should be amber, was {meet}");
    }

    [Fact]
    public void NoPreview_DrawsNeitherRing()
    {
        MapViewport vp = Viewport();
        (float ringX, float ringY) = RingStroke(vp, HalfSymbolPx() + GroundRenderer.ScopeRingMarginPx);
        (float meetX, float meetY) = MeetStroke(vp);

        using SKBitmap bitmap = Render(vp, null);

        SKColor background = bitmap.GetPixel(1, 1);
        Assert.Equal(background, PixelAt(bitmap, ringX, ringY));
        Assert.Equal(background, PixelAt(bitmap, meetX, meetY));
    }

    /// <summary>
    /// A Hold short of… stop at the bar the route starts at sends the bar alone: the bar is boxed in the highlight blue, its
    /// box's left edge 22 px left of the bar, and the bar's centre stays clear.
    /// </summary>
    [Fact]
    public void BarPointAlone_BoxesTheBarInTheHighlightBlue()
    {
        MapViewport vp = Viewport();
        (float barX, float barY) = vp.LatLonToScreen(Meet.Lat, Meet.Lon);

        using SKBitmap bitmap = Render(vp, new ScopePreview(null, null, null, Meet, null));

        List<SKColor> leftEdge = [.. Enumerable.Range(-24, 5).Select(dx => PixelAt(bitmap, barX + dx, barY))];
        Assert.Contains(leftEdge, pixel => (pixel.Blue > 200) && (pixel.Red < 180));
        Assert.Equal(bitmap.GetPixel(1, 1), PixelAt(bitmap, barX, barY));
    }

    private static MapViewport Viewport() =>
        new()
        {
            CenterLat = CenterLat,
            CenterLon = CenterLon,
            Zoom = PxPerFt * FeetPerDegLat / 5000.0,
            PixelWidth = SurfacePx,
            PixelHeight = SurfacePx,
            RotationDeg = 0,
        };

    /// <summary>The larger of the target's half length and half wingspan as drawn, from its FAA record.</summary>
    private static float HalfSymbolPx()
    {
        FaaAircraftRecord record = FaaAircraftDatabase.Get(TargetType) ?? throw new InvalidOperationException($"no FAA record for {TargetType}");
        float halfLength = GroundRenderer.AircraftLengthFt(record) * 0.5f * PxPerFt;
        float halfWingspan = (float)(record.WingspanFt ?? 0) * 0.5f * PxPerFt;
        return MathF.Max(halfLength, halfWingspan);
    }

    /// <summary>A point on the target ring's inner stroke: <paramref name="radius"/> to the right of the target.</summary>
    private static (float X, float Y) RingStroke(MapViewport vp, float radius)
    {
        (float x, float y) = vp.LatLonToScreen(CenterLat, CenterLon);
        return (x + radius, y);
    }

    /// <summary>A point on the meet ring's stroke: its radius to the right of the meet point.</summary>
    private static (float X, float Y) MeetStroke(MapViewport vp)
    {
        (float x, float y) = vp.LatLonToScreen(Meet.Lat, Meet.Lon);
        return (x + GroundRenderer.ScopeMeetRingRadiusPx, y);
    }

    private static SKBitmap Render(MapViewport vp, ScopePreview? preview)
    {
        var bitmap = new SKBitmap(SurfacePx, SurfacePx);
        using var canvas = new SKCanvas(bitmap);
        using var renderer = new GroundRenderer();
        AircraftModel target = new()
        {
            Callsign = Target,
            AircraftType = TargetType,
            Position = new LatLon(CenterLat, CenterLon),
            Heading = new TrueHeading(0),
            IsOnGround = true,
        };
        renderer.Render(
            canvas,
            vp,
            new GroundLayoutDto("TST", [], [], null, null, null),
            [target],
            selectedAircraft: null,
            hoveredNodeId: null,
            hoveredRunwayEnd: null,
            hoverRoute: null,
            previewRoute: null,
            scopePreview: preview,
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
            showRunwayLabels: false,
            showTaxiwayLabels: false,
            showHoldShort: GroundFilterMode.Off,
            showParking: GroundFilterMode.Off,
            showSpot: GroundFilterMode.Off,
            shownTaxiRoutes: null,
            highlightedCallsigns: null,
            hiddenDataBlockCallsigns: new HashSet<string> { Target },
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

    private static SKColor PixelAt(SKBitmap bitmap, float x, float y) => bitmap.GetPixel((int)MathF.Round(x), (int)MathF.Round(y));
}
