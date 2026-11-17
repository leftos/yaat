using SkiaSharp;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.Views.Map;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Profiles;

namespace Yaat.Client.Tests.Views.Scope;

/// <summary>
/// Pins the symbol a live-traffic shadow draws: the same outline the track's own symbol draws, dashed, so a target the sim does
/// not own reads as "not mine" at a glance, and at the stale half alpha when it coasts.
/// </summary>
[Collection(MapBlinkClockCollection.Name)]
public class ScopeShadowSymbolTests
{
    private const float FontSize = 12f;
    private const int Width = 1000;
    private const int Height = 800;

    // The symbol's ring: the stroke is 1.5 px wide on a 5 px radius, so the band's pixel centres sit between these radii.
    private const float RingInnerPx = 4.2f;
    private const float RingOuterPx = 5.8f;

    private static readonly ScopePalette NoTints = new(null, null, SKColors.White);

    private static MapViewport Viewport =>
        new()
        {
            PixelWidth = Width,
            PixelHeight = Height,
            CenterLat = 37.0,
            CenterLon = -122.0,
        };

    [Fact]
    public void LiveTrafficShadow_DrawsADashedSymbol_AndAPlainTrackDoesNot()
    {
        ScopeFrame shadowFrame = FrameOf(LiveTraffic(stale: false));
        ScopeFrame plainFrame = FrameOf(PlainAircraft());

        using SKBitmap shadow = Render(shadowFrame);
        using SKBitmap plain = Render(plainFrame);

        (int shadowLit, int shadowDark) = RingPixels(shadow, Assert.Single(shadowFrame.Tracks).Symbol);
        (int plainLit, int plainDark) = RingPixels(plain, Assert.Single(plainFrame.Tracks).Symbol);

        Assert.True(plainLit > 0, "the plain symbol drew no ring");
        Assert.True(shadowLit > 0, "the shadow symbol drew no ring");
        Assert.True(
            plainLit > plainDark * 2,
            $"the plain symbol's ring is not solid: {plainLit} lit, {plainDark} dark pixels of {plainLit + plainDark}"
        );
        Assert.True(
            shadowDark * 2 > plainDark * 3,
            $"the shadow symbol's ring has no dash gaps: {shadowLit} lit, {shadowDark} dark, against the plain ring's "
                + $"{plainLit} lit, {plainDark} dark"
        );
    }

    [Fact]
    public void CoastingShadow_KeepsTheStaleHalfAlpha()
    {
        ScopeFrame shadowFrame = FrameOf(LiveTraffic(stale: true));
        ScopeFrame plainFrame = FrameOf(PlainAircraft());

        using SKBitmap shadow = Render(shadowFrame);
        using SKBitmap plain = Render(plainFrame);

        int shadowBrightest = BrightestGreen(shadow, Assert.Single(shadowFrame.Tracks).Symbol);
        int plainBrightest = BrightestGreen(plain, Assert.Single(plainFrame.Tracks).Symbol);

        // The symbol blue's green channel is 176 at full alpha and about 88 at half alpha over the black background.
        Assert.True(plainBrightest > 150, $"the plain symbol is not at full alpha: brightest green {plainBrightest}");
        Assert.True(shadowBrightest < 130, $"the coasting shadow is not at half alpha: brightest green {shadowBrightest}");
    }

    private static AircraftModel PlainAircraft() => ScopeTestAircraft.CreateModel();

    private static AircraftModel LiveTraffic(bool stale)
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.IsLiveTraffic = true;
        ac.LiveTrafficStale = stale;
        return ac;
    }

    private static ScopeFrame FrameOf(AircraftModel ac)
    {
        using SKFont font = PlatformHelper.MonospaceFontBold(FontSize);
        using var paint = new SKPaint();
        var metrics = ScopeTextMetrics.FromStyle(new TextStyle(font, paint));
        var input = new ScopeFrameInput(
            [ac],
            null,
            Viewport,
            ScopeTemplates.StarsId,
            metrics,
            ScopeTestAircraft.AllAlerts,
            ScopeTestAircraft.AllDisplay,
            new Dictionary<string, SKPoint>(),
            new HashSet<string>(),
            MvaFacilityId: null,
            OthersLevel: ScopeProfile.OthersFull,
            VectorMinutes: 0,
            VectorOwnership: ScopeVectorOwnership.Own,
            HistoryCount: 0,
            LocalRings: new Dictionary<string, ScopeTrackRings>(),
            IntentPaths: new ScopeIntentPathCache()
        );
        return ScopeFrame.Build(input);
    }

    private static SKBitmap Render(ScopeFrame frame)
    {
        var bitmap = new SKBitmap(Width, Height);
        using (var canvas = new SKCanvas(bitmap))
        using (var renderer = new ScopeRenderer())
        {
            var snapshot = new ScopeRenderSnapshot(
                frame,
                [],
                new Dictionary<string, string>(),
                ScopeBrightness.Default,
                FontSize,
                NoTints,
                [],
                null,
                new Dictionary<int, SKRect>(),
                null,
                null
            );
            renderer.Render(canvas, Viewport, snapshot);
            canvas.Flush();
        }

        return bitmap;
    }

    // Every pixel whose centre falls in the symbol's ring band, split into those the ring lit and those left black.
    private static (int Lit, int Dark) RingPixels(SKBitmap bitmap, SKPoint symbol)
    {
        int lit = 0;
        int dark = 0;
        int left = Math.Max(0, (int)Math.Floor(symbol.X - RingOuterPx));
        int top = Math.Max(0, (int)Math.Floor(symbol.Y - RingOuterPx));
        int right = Math.Min(bitmap.Width, (int)Math.Ceiling(symbol.X + RingOuterPx));
        int bottom = Math.Min(bitmap.Height, (int)Math.Ceiling(symbol.Y + RingOuterPx));
        for (int y = top; y < bottom; y++)
        {
            for (int x = left; x < right; x++)
            {
                float dx = (x + 0.5f) - symbol.X;
                float dy = (y + 0.5f) - symbol.Y;
                float radius = MathF.Sqrt((dx * dx) + (dy * dy));
                if ((radius < RingInnerPx) || (radius > RingOuterPx))
                {
                    continue;
                }

                if (bitmap.GetPixel(x, y) == SKColors.Black)
                {
                    dark++;
                }
                else
                {
                    lit++;
                }
            }
        }

        return (lit, dark);
    }

    private static int BrightestGreen(SKBitmap bitmap, SKPoint symbol)
    {
        int brightest = 0;
        int left = Math.Max(0, (int)Math.Floor(symbol.X - RingOuterPx));
        int top = Math.Max(0, (int)Math.Floor(symbol.Y - RingOuterPx));
        int right = Math.Min(bitmap.Width, (int)Math.Ceiling(symbol.X + RingOuterPx));
        int bottom = Math.Min(bitmap.Height, (int)Math.Ceiling(symbol.Y + RingOuterPx));
        for (int y = top; y < bottom; y++)
        {
            for (int x = left; x < right; x++)
            {
                brightest = Math.Max(brightest, bitmap.GetPixel(x, y).Green);
            }
        }

        return brightest;
    }
}
