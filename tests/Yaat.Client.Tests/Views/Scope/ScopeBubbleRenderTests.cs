using SkiaSharp;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.Views.Map;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Profiles;

namespace Yaat.Client.Tests.Views.Scope;

/// <summary>
/// Pins the colours a <see cref="ScopeBubble"/> draws in: the SAY / pilot bubble's green border and fill, the WARN bubble's
/// amber, so a warning never reads as a pilot transmission.
/// </summary>
[Collection(MapBlinkClockCollection.Name)]
public class ScopeBubbleRenderTests
{
    private const float FontSize = 12f;
    private const int Width = 1000;
    private const int Height = 800;

    private static readonly ScopePalette NoTints = new(null, null, SKColors.White);

    private static readonly ScopeBubbleOptions Shown = new(Show: true, AlwaysShowGround: false);

    private static MapViewport Viewport =>
        new()
        {
            PixelWidth = Width,
            PixelHeight = Height,
            CenterLat = 37.0,
            CenterLon = -122.0,
        };

    [Theory]
    [InlineData(SpeechBubbleSeverity.Speech)]
    [InlineData(SpeechBubbleSeverity.Warning)]
    public void Bubble_DrawsItsSeverityColor(SpeechBubbleSeverity severity)
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.SpeechBubble = new AircraftSpeechBubble("CLEARED FOR TAKEOFF RUNWAY 30", DateTime.UtcNow.AddMinutes(1), severity);
        ScopeFrame frame = Frame(ac);
        using SKFont font = PlatformHelper.MonospaceFontBold(FontSize);
        IReadOnlyList<ScopeBubble> bubbles = ScopeBubbles.Place(frame.Tracks, Shown, DateTime.UtcNow, font);
        ScopeBubble bubble = Assert.Single(bubbles);

        using SKBitmap bitmap = Render(frame, bubbles);

        (int green, int amber) = CountHues(bitmap, bubble.Rect);
        Assert.True(green + amber > 0, "the bubble drew nothing");
        if (severity == SpeechBubbleSeverity.Warning)
        {
            Assert.True(amber > green, $"expected the amber warning colours, saw {green} green and {amber} amber pixels");
        }
        else
        {
            Assert.True(green > amber, $"expected the green speech colours, saw {green} green and {amber} amber pixels");
        }
    }

    private static ScopeFrame Frame(AircraftModel ac)
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

    private static SKBitmap Render(ScopeFrame frame, IReadOnlyList<ScopeBubble> bubbles)
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
                new ScopeOverlays { Bubbles = bubbles }
            );
            renderer.Render(canvas, Viewport, snapshot);
            canvas.Flush();
        }

        return bitmap;
    }

    // A bubble's fill and border are both green-tinted for a speech bubble and amber for a warning: count the pixels whose
    // hue reads one way or the other, antialiased blends included.
    private static (int Green, int Amber) CountHues(SKBitmap bitmap, SKRect rect)
    {
        int green = 0;
        int amber = 0;
        int left = Math.Max(0, (int)Math.Floor(rect.Left));
        int top = Math.Max(0, (int)Math.Floor(rect.Top));
        int right = Math.Min(bitmap.Width, (int)Math.Ceiling(rect.Right));
        int bottom = Math.Min(bitmap.Height, (int)Math.Ceiling(rect.Bottom));
        for (int y = top; y < bottom; y++)
        {
            for (int x = left; x < right; x++)
            {
                SKColor pixel = bitmap.GetPixel(x, y);
                if ((pixel.Green > pixel.Red + 20) && (pixel.Green > pixel.Blue))
                {
                    green++;
                }
                else if ((pixel.Red > pixel.Green + 20) && (pixel.Green > pixel.Blue))
                {
                    amber++;
                }
            }
        }

        return (green, amber);
    }
}
