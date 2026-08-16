using Avalonia;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.Views;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// Tests for GitHub issue #361: a window saved at the edge of a monitor (frame origin
/// slightly outside the working area due to Windows' invisible resize borders) must be
/// restored verbatim, not clamped inward. Clamping is only a rescue for geometry that
/// would be substantially off-screen, and its math must respect per-monitor DPI scaling
/// (saved Width/Height are DIPs; positions and working areas are device pixels).
/// </summary>
public class WindowGeometryResolveTests
{
    private static SavedWindowGeometry Geo(int x, int y, double width, double height, int screenIndex = 0) =>
        new()
        {
            X = x,
            Y = y,
            Width = width,
            Height = height,
            IsMaximized = false,
            ScreenIndex = screenIndex,
            IsTopmost = false,
        };

    private static WindowGeometryHelper.ScreenInfo Screen(int x, int y, int width, int height, double scaling = 1.0) =>
        new(new PixelRect(x, y, width, height), scaling);

    [Fact]
    public void EdgeSnappedWindow_RestoresVerbatim()
    {
        // Reporter's "Local 2" profile: Main flush against the left screen edge, frame at x=-5.
        var screens = new[] { Screen(0, 0, 1920, 1009) };
        var resolved = WindowGeometryHelper.ResolveGeometry(Geo(-5, 0, 610, 1000), screens);

        Assert.Equal(-5, resolved.X);
        Assert.Equal(0, resolved.Y);
        Assert.Equal(610, resolved.Width);
        Assert.Equal(1000, resolved.Height);
    }

    [Fact]
    public void FullyOnScreenWindow_RestoresVerbatim()
    {
        var screens = new[] { Screen(0, 0, 1920, 1009) };
        var resolved = WindowGeometryHelper.ResolveGeometry(Geo(0, 0, 611, 1000), screens);

        Assert.Equal(0, resolved.X);
        Assert.Equal(0, resolved.Y);
        Assert.Equal(611, resolved.Width);
        Assert.Equal(1000, resolved.Height);
    }

    [Fact]
    public void SecondMonitorNegativeCoordinates_RestoreVerbatim()
    {
        // A 1440x877 monitor sits left of the primary; window fills it at x=-1440.
        var screens = new[] { Screen(0, 0, 1920, 1009), Screen(-1440, -398, 1440, 877) };
        var resolved = WindowGeometryHelper.ResolveGeometry(Geo(-1440, -398, 1440, 877, screenIndex: 1), screens);

        Assert.Equal(-1440, resolved.X);
        Assert.Equal(-398, resolved.Y);
        Assert.Equal(1440, resolved.Width);
        Assert.Equal(877, resolved.Height);
    }

    [Fact]
    public void OffScreenWindow_IsClampedIntoWorkArea()
    {
        // Saved on a monitor that no longer exists — rescue-clamp into the primary work area.
        var screens = new[] { Screen(0, 0, 1920, 1009) };
        var resolved = WindowGeometryHelper.ResolveGeometry(Geo(5000, 200, 610, 500), screens);

        Assert.Equal(1920 - 610, resolved.X);
        Assert.Equal(200, resolved.Y);
        Assert.Equal(610, resolved.Width);
        Assert.Equal(500, resolved.Height);
    }

    [Fact]
    public void IconicSentinelPosition_IsClampedIntoWorkArea()
    {
        // Legacy prefs/profiles poisoned with the Windows minimized sentinel (-32000,-32000).
        var screens = new[] { Screen(0, 0, 1920, 1009) };
        var resolved = WindowGeometryHelper.ResolveGeometry(Geo(-32000, -32000, 900, 620), screens);

        Assert.Equal(0, resolved.X);
        Assert.Equal(0, resolved.Y);
        Assert.Equal(900, resolved.Width);
        Assert.Equal(620, resolved.Height);
    }

    [Fact]
    public void OffScreenClamp_UsesDevicePixelWidthAtHighDpi()
    {
        // 1000 DIP at 150% scaling is 1500 device px; the clamp bound must subtract 1500, not 1000.
        var screens = new[] { Screen(0, 0, 1920, 1040, scaling: 1.5) };
        var resolved = WindowGeometryHelper.ResolveGeometry(Geo(5000, 5000, 1000, 600), screens);

        Assert.Equal(1920 - 1500, resolved.X);
        Assert.Equal(1040 - 900, resolved.Y);
        Assert.Equal(1000, resolved.Width);
        Assert.Equal(600, resolved.Height);
    }

    [Fact]
    public void OversizedWindow_IsCappedToWorkAreaInDips()
    {
        // Work area is 1920 device px = 1280 DIP at 150%; a 1400-DIP-wide window must cap to 1280 DIP.
        var screens = new[] { Screen(0, 0, 1920, 1040, scaling: 1.5) };
        var resolved = WindowGeometryHelper.ResolveGeometry(Geo(0, 0, 1400, 600), screens);

        Assert.Equal(0, resolved.X);
        Assert.Equal(0, resolved.Y);
        Assert.Equal(1920 / 1.5, resolved.Width);
        Assert.Equal(600, resolved.Height);
    }

    [Fact]
    public void BarelyOverhangingWindow_WithMostOfItOffScreen_IsClamped()
    {
        // Only a 20px sliver remains on-screen — too little to grab; rescue applies.
        var screens = new[] { Screen(0, 0, 1920, 1009) };
        var resolved = WindowGeometryHelper.ResolveGeometry(Geo(1900, 300, 610, 500), screens);

        Assert.Equal(1920 - 610, resolved.X);
        Assert.Equal(300, resolved.Y);
    }
}
