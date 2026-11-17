using SkiaSharp;
using Xunit;
using Yaat.Client.Views.Scope;

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
}
