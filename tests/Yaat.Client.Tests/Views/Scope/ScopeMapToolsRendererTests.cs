using SkiaSharp;
using Xunit;
using Yaat.Client.Views.Map;
using Yaat.Client.Views.Scope;
using Yaat.Sim;

namespace Yaat.Client.Tests.Views.Scope;

/// <summary>
/// The map tools' drawing geometry: the range ring's circles, one per step out to the viewport's farthest corner, none that
/// never reach it and none under a four-pixel step, and the weather lines, the last one lowest above the bottom inset.
/// </summary>
public class ScopeMapToolsRendererTests
{
    [Fact]
    public void RingRadii_CentreInView_StepOutToTheFarthestCorner()
    {
        // The farthest corner, (400, 300), lies 360.6 px from the centre.
        float[] radii = [.. ScopeMapToolsRenderer.RingRadii(new SKPoint(100, 100), 50, 400, 300)];

        Assert.Equal([50f, 100f, 150f, 200f, 250f, 300f, 350f], radii);
    }

    [Fact]
    public void RingRadii_CentreOffView_SkipTheRingsThatNeverReachIt()
    {
        // The view's nearest point lies 1000 px away, its farthest 1408 px.
        float[] radii = [.. ScopeMapToolsRenderer.RingRadii(new SKPoint(-1000, 150), 100, 400, 300)];

        Assert.Equal([1000f, 1100f, 1200f, 1300f, 1400f], radii);
    }

    [Fact]
    public void RingRadii_AStepUnderAPixel_DrawsNone() => Assert.Empty(ScopeMapToolsRenderer.RingRadii(new SKPoint(100, 100), 0.5f, 400, 300));

    [Fact]
    public void RingRadii_AStepUnder4Px_DrawsNone() => Assert.Empty(ScopeMapToolsRenderer.RingRadii(new SKPoint(100, 100), 3.9f, 400, 300));

    [Fact]
    public void RingRadii_AStepOf4Px_DrawsTheRingsToTheFarthestCorner()
    {
        // The farthest corner, (400, 300), lies 360.6 px from the centre: 90 circles fit inside it.
        float[] radii = [.. ScopeMapToolsRenderer.RingRadii(new SKPoint(100, 100), 4f, 400, 300)];

        Assert.Equal(90, radii.Length);
        Assert.Equal(4f, radii[0]);
        Assert.Equal(360f, radii[^1]);
    }

    [Fact]
    public void WeatherBaselines_GrowUpward_TheLastLineLowest_18PxApart()
    {
        float[] baselines = ScopeMapToolsRenderer.WeatherBaselines(3, 600, 10);

        Assert.Equal(3, baselines.Length);
        Assert.Equal(baselines[2] - 18, baselines[1]);
        Assert.Equal(baselines[1] - 18, baselines[0]);
        Assert.True(baselines[2] <= 590, $"the last line sits above the inset ({baselines[2]})");
        Assert.True(baselines[2] >= 580, $"the last line sits on the inset ({baselines[2]})");
    }

    [Fact]
    public void AMarker_DrawsAYellowDiamondAndItsLabel_AtItsPoint()
    {
        MapViewport vp = Viewport();
        (double lat, double lon) = vp.ScreenToLatLon(200, 150);

        using SKBitmap bitmap = DrawMarker(vp, new ScopeMarker("SUNOL", new LatLon(lat, lon)));

        // The diamond's upper right edge runs from (200, 144) to (206, 150).
        Assert.True(IsYellow(bitmap.GetPixel(203, 147)), $"the diamond's edge is yellow ({bitmap.GetPixel(203, 147)})");
        Assert.True(AnyYellow(bitmap, new SKRectI(208, 134, 260, 152)), "the label sits right of the diamond");
    }

    [Fact]
    public void AMarkerPastTheCullMargin_IsNotDrawn()
    {
        MapViewport vp = Viewport();
        (double lat, double lon) = vp.ScreenToLatLon(-60, 150);

        // The long label would reach into the view were the marker drawn.
        using SKBitmap bitmap = DrawMarker(vp, new ScopeMarker("SUNOLSUNOLSUNOL", new LatLon(lat, lon)));

        Assert.False(AnyYellow(bitmap, new SKRectI(0, 0, 400, 300)), "nothing is drawn");
    }

    private static MapViewport Viewport() =>
        new()
        {
            PixelWidth = 400,
            PixelHeight = 300,
            CenterLat = 37.6,
            CenterLon = -121.9,
        };

    private static SKBitmap DrawMarker(MapViewport vp, ScopeMarker marker)
    {
        var bitmap = new SKBitmap(400, 300);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Black);
        using var renderer = new ScopeMapToolsRenderer();
        renderer.DrawUnder(canvas, new ScopeOverlays { Markers = [marker] }, vp, ScopeBrightness.Default with { MapB = 100 });
        canvas.Flush();
        return bitmap;
    }

    private static bool IsYellow(SKColor c) => (c.Red > 120) && (c.Green > 100) && (c.Blue < 60);

    private static bool AnyYellow(SKBitmap bitmap, SKRectI rect)
    {
        for (int x = rect.Left; x < rect.Right; x++)
        {
            for (int y = rect.Top; y < rect.Bottom; y++)
            {
                if (IsYellow(bitmap.GetPixel(x, y)))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
