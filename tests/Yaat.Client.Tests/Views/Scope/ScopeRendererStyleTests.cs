using SkiaSharp;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.Views.Map;
using Yaat.Client.Views.Radar;
using Yaat.Client.Views.Scope;

namespace Yaat.Client.Tests.Views.Scope;

/// <summary>
/// Pins how <see cref="ScopeRenderer"/> draws the field styles and the type-mismatch token: the dim squawk tail draws
/// pixel for pixel as today's <see cref="TargetRenderer.DrawSquawkMismatchLine"/>, a struck-through field gets a line of
/// the block colour at the font's strikeout position, and the type-mismatch token draws in today's amber.
/// </summary>
[Collection(RadarBlinkClockCollection.Name)]
public class ScopeRendererStyleTests
{
    private const float FontSize = 12f;
    private const int Width = 1000;
    private const int Height = 800;

    // Today's renderer's block colour and TargetRenderer.TypeMismatchColor, duplicated rather than exposed.
    private static readonly SKColor BlockColor = SKColors.White;
    private static readonly SKColor TypeMismatchColor = new(0xFF, 0xB0, 0x30);

    private static MapViewport Viewport() =>
        new()
        {
            PixelWidth = Width,
            PixelHeight = Height,
            CenterLat = 37.0,
            CenterLon = -122.0,
        };

    private static ScopeFrame Frame(AircraftModel ac)
    {
        using SKFont font = PlatformHelper.MonospaceFontBold(FontSize);
        using var paint = new SKPaint();
        var metrics = ScopeTextMetrics.FromStyle(new TextStyle(font, paint));
        var input = new ScopeFrameInput(
            [ac],
            null,
            Viewport(),
            false,
            metrics,
            ScopeTestAircraft.AllAlerts,
            ScopeTestAircraft.AllDisplay,
            new Dictionary<string, SKPoint>()
        );
        return ScopeFrame.Build(input);
    }

    private static SKBitmap Render(ScopeFrame frame)
    {
        var bitmap = new SKBitmap(Width, Height);
        using var canvas = new SKCanvas(bitmap);
        using var renderer = new ScopeRenderer();
        var snapshot = new ScopeRenderSnapshot(frame, [], new Dictionary<string, string>(), 1f, 1f, FontSize);
        renderer.Render(canvas, Viewport(), snapshot);
        canvas.Flush();
        return bitmap;
    }

    private static FieldRect FieldOf(ScopeFrame frame, ScopeFieldSlot slot) =>
        Assert.Single(Assert.Single(frame.Tracks).Block.Fields, f => f.Slot == slot);

    private static SKRectI PixelsOf(FieldRect field) => SKRectI.Ceiling(field.Rect, true);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DimTail_DrawsAsTodaysSquawkMismatchLine(bool flashOn)
    {
        using var pin = RadarBlinkPin.Phase(flashOn);
        ScopeFrame frame = Frame(ScopeTestAircraft.CreateSquawkMismatch());
        FieldRect squawk = FieldOf(frame, ScopeFieldSlot.Squawk);

        using SKBitmap scope = Render(frame);
        using var today = new SKBitmap(Width, Height);
        using (var canvas = new SKCanvas(today))
        using (SKFont font = PlatformHelper.MonospaceFontBold(FontSize))
        using (var paint = new SKPaint { IsAntialias = true })
        {
            canvas.Clear(SKColors.Black);
            TargetRenderer.DrawSquawkMismatchLine(canvas, squawk.Text, squawk.Rect.Left, squawk.Rect.Bottom, font, paint, BlockColor);
            canvas.Flush();
        }

        SKRectI area = PixelsOf(squawk);
        int lit = 0;
        var mismatches = new List<string>();
        for (int y = area.Top; y < area.Bottom; y++)
        {
            for (int x = area.Left; x < area.Right; x++)
            {
                SKColor expected = today.GetPixel(x, y);
                lit += (expected == SKColors.Black) ? 0 : 1;
                if (scope.GetPixel(x, y) != expected)
                {
                    mismatches.Add($"({x},{y}) {scope.GetPixel(x, y)} != {expected}");
                }
            }
        }

        Assert.True(lit > 0, "today's squawk line drew nothing in the field rect");
        Assert.Empty(mismatches);
    }

    [Fact]
    public void Strikethrough_BrightensEveryColumnOfModeCAtTheStrikeoutPosition()
    {
        using var pin = RadarBlinkPin.Phase(true);
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.TransponderMode = "Standby";
        ScopeFrame frame = Frame(ac);
        FieldRect modeC = FieldOf(frame, ScopeFieldSlot.ModeC);

        using SKBitmap struck = Render(frame);
        using SKBitmap unstruck = Render(WithoutStyle(frame));

        Assert.Empty(ColumnsNotBrightened(struck, unstruck, modeC));
    }

    [Fact]
    public void Strikethrough_ModeCOn_DrawsNoModeCField()
    {
        using var pin = RadarBlinkPin.Phase(true);
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.TransponderMode = "C";

        ScopeFrame frame = Frame(ac);

        Assert.DoesNotContain(Assert.Single(frame.Tracks).Block.Fields, f => f.Slot == ScopeFieldSlot.ModeC);
        Assert.All(Assert.Single(frame.Tracks).Block.Fields, f => Assert.Equal(ScopeFieldStyle.None, f.Style));
    }

    [Fact]
    public void TypeMismatchToken_DrawsTodaysAmber()
    {
        using var pin = RadarBlinkPin.Phase(true);
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.FiledAircraftType = "B744";
        ScopeFrame frame = Frame(ac);
        FieldRect cwtType = FieldOf(frame, ScopeFieldSlot.CwtType);

        using SKBitmap bitmap = Render(frame);

        Assert.Equal(ScopeColorToken.TypeMismatch, cwtType.Token);
        Assert.True(AnyPixel(bitmap, PixelsOf(cwtType), TypeMismatchColor), "no pixel of the type-mismatch colour in the cwt/type field");
    }

    private static ScopeFrame WithoutStyle(ScopeFrame frame)
    {
        ScopeTrack track = Assert.Single(frame.Tracks);
        ScopeBlockLayout block = track.Block with { Fields = [.. track.Block.Fields.Select(f => f with { Style = ScopeFieldStyle.None })] };
        return frame with { Tracks = [track with { Block = block }] };
    }

    /// <summary>
    /// The columns of <paramref name="field"/> where no pixel within a row of the strikeout position is brighter in
    /// <paramref name="struck"/> than in <paramref name="unstruck"/>. A pixel the text already fills with the block colour
    /// (a glyph stem) cannot get brighter under a strike of that colour, so it counts as struck.
    /// </summary>
    private static List<int> ColumnsNotBrightened(SKBitmap struck, SKBitmap unstruck, FieldRect field)
    {
        using SKFont font = PlatformHelper.MonospaceFontBold(FontSize);
        float offset = font.Metrics.StrikeoutPosition.GetValueOrDefault();
        if (offset == 0)
        {
            offset = -FontSize / 3f;
        }
        int strikeRow = (int)MathF.Floor(field.Rect.Bottom + offset);
        SKRectI area = PixelsOf(field);
        var missing = new List<int>();
        for (int x = area.Left; x < area.Right; x++)
        {
            bool brighter = false;
            for (int y = strikeRow - 1; (y <= strikeRow + 1) && !brighter; y++)
            {
                SKColor before = unstruck.GetPixel(x, y);
                brighter = (before == BlockColor) || (Brightness(struck.GetPixel(x, y)) > Brightness(before));
            }
            if (!brighter)
            {
                missing.Add(x);
            }
        }
        return missing;
    }

    private static int Brightness(SKColor c) => c.Red + c.Green + c.Blue;

    private static bool AnyPixel(SKBitmap bitmap, SKRectI area, SKColor color)
    {
        for (int y = area.Top; y < area.Bottom; y++)
        {
            for (int x = area.Left; x < area.Right; x++)
            {
                if (bitmap.GetPixel(x, y) == color)
                {
                    return true;
                }
            }
        }
        return false;
    }
}
