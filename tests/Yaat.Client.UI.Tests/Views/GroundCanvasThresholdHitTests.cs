using Avalonia;
using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.Views.Ground;

namespace Yaat.Client.UI.Tests.Views;

// Hit-testing for the runway-end click target. Drives the canvas with a
// hand-crafted layout so threshold lat/lon → screen mapping is predictable
// for the assertions.
public class GroundCanvasThresholdHitTests
{
    private const double ThresholdALat = 37.62;
    private const double ThresholdALon = -122.39;
    private const double ThresholdBLat = 37.63;
    private const double ThresholdBLon = -122.36;

    [AvaloniaFact]
    public void FindRunwayThresholdAtPoint_ClickOnEnd1Marker_ReturnsEnd1()
    {
        var canvas = MakeCanvas(800, 600);
        canvas.Layout = LayoutWith28L10R();

        var (sx, sy) = canvas.Viewport.LatLonToScreen(ThresholdALat, ThresholdALon);
        var hit = canvas.FindRunwayThresholdAtPoint(new Point(sx, sy));

        Assert.NotNull(hit);
        Assert.Equal("28L", hit!.Value.RunwayEnd);
    }

    [AvaloniaFact]
    public void FindRunwayThresholdAtPoint_ClickOnEnd2Marker_ReturnsEnd2()
    {
        var canvas = MakeCanvas(800, 600);
        canvas.Layout = LayoutWith28L10R();

        var (sx, sy) = canvas.Viewport.LatLonToScreen(ThresholdBLat, ThresholdBLon);
        var hit = canvas.FindRunwayThresholdAtPoint(new Point(sx, sy));

        Assert.NotNull(hit);
        Assert.Equal("10R", hit!.Value.RunwayEnd);
    }

    [AvaloniaFact]
    public void FindRunwayThresholdAtPoint_ClickFarFromAnyThreshold_ReturnsNull()
    {
        var canvas = MakeCanvas(800, 600);
        canvas.Layout = LayoutWith28L10R();

        // Click at canvas center — the runway midpoint in our layout sits at
        // viewport CenterLat/CenterLon, so 100 px below that lands well outside
        // the 18 px threshold hit radius.
        var hit = canvas.FindRunwayThresholdAtPoint(new Point(400, 400));

        Assert.Null(hit);
    }

    [AvaloniaFact]
    public void FindRunwayThresholdAtPoint_NoRunways_ReturnsNull()
    {
        var canvas = MakeCanvas(800, 600);
        canvas.Layout = new GroundLayoutDto("SFO", [], [], null, null);

        var (sx, sy) = canvas.Viewport.LatLonToScreen(ThresholdALat, ThresholdALon);
        var hit = canvas.FindRunwayThresholdAtPoint(new Point(sx, sy));

        Assert.Null(hit);
    }

    [AvaloniaFact]
    public void FindRunwayThresholdAtPoint_ZoomedOut_StillHitsAtScreenPos()
    {
        // Hit-radius is in screen pixels, so the same lat/lon click should hit
        // regardless of zoom — just project the threshold to screen first.
        var canvas = MakeCanvas(800, 600);
        canvas.ViewZoom = 0.25;
        canvas.Layout = LayoutWith28L10R();

        var (sx, sy) = canvas.Viewport.LatLonToScreen(ThresholdALat, ThresholdALon);
        var hit = canvas.FindRunwayThresholdAtPoint(new Point(sx, sy));

        Assert.NotNull(hit);
        Assert.Equal("28L", hit!.Value.RunwayEnd);
    }

    private static GroundCanvas MakeCanvas(double width, double height)
    {
        var canvas = new GroundCanvas();
        canvas.Viewport.CenterLat = (ThresholdALat + ThresholdBLat) / 2.0;
        canvas.Viewport.CenterLon = (ThresholdALon + ThresholdBLon) / 2.0;
        canvas.Viewport.Zoom = 1.0;
        canvas.Viewport.PixelWidth = (float)width;
        canvas.Viewport.PixelHeight = (float)height;
        return canvas;
    }

    private static GroundLayoutDto LayoutWith28L10R()
    {
        var runway = new GroundRunwayDto(
            "28L/10R",
            [
                [ThresholdALat, ThresholdALon],
                [ThresholdBLat, ThresholdBLon],
            ],
            150
        );
        return new GroundLayoutDto("SFO", [], [], null, [runway]);
    }
}
