using SkiaSharp;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.Views;
using Yaat.Client.Views.Map;
using Yaat.Client.Views.Radar;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Profiles;
using Yaat.Sim;

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

    // Today's TargetRenderer.MvaBelowColor / MvaAtColor, duplicated rather than exposed.
    private static readonly SKColor MvaBelowColor = new(255, 90, 90);
    private static readonly SKColor MvaAtColor = new(255, 200, 60);

    // Today's TargetRenderer.StarsUnownedColor and StaleAlpha, duplicated rather than exposed.
    private static readonly SKColor StarsUnownedColor = new(50, 205, 50);
    private const byte StaleAlpha = 128;

    /// <summary>The palette with no preferences: no tints, a white selected symbol.</summary>
    private static readonly ScopePalette NoTints = new(null, null, SKColors.White);

    /// <summary>KOAK, inside a 2,000 ft MVA sector.</summary>
    private static readonly LatLon Koak = new(37.7213, -122.2208);

    private static MapViewport Viewport() => Viewport(37.0, -122.0);

    private static MapViewport Viewport(double centerLat, double centerLon) =>
        new()
        {
            PixelWidth = Width,
            PixelHeight = Height,
            CenterLat = centerLat,
            CenterLon = centerLon,
        };

    private static ScopeFrame Frame(AircraftModel ac) => Frame(ac, Viewport());

    private static ScopeFrame Frame(AircraftModel ac, MapViewport viewport)
    {
        using SKFont font = PlatformHelper.MonospaceFontBold(FontSize);
        using var paint = new SKPaint();
        var metrics = ScopeTextMetrics.FromStyle(new TextStyle(font, paint));
        var input = new ScopeFrameInput(
            [ac],
            null,
            viewport,
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

    private static SKBitmap Render(ScopeFrame frame) => Render(frame, Viewport());

    private static SKBitmap Render(ScopeFrame frame, MapViewport viewport)
    {
        var bitmap = new SKBitmap(Width, Height);
        using var canvas = new SKCanvas(bitmap);
        using var renderer = new ScopeRenderer();
        var snapshot = new ScopeRenderSnapshot(
            frame,
            [],
            new Dictionary<string, string>(),
            ScopeBrightness.Default,
            FontSize,
            NoTints,
            [],
            null,
            new Dictionary<int, SKRect>()
        );
        renderer.Render(canvas, viewport, snapshot);
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

    [Fact]
    public void MvaBelowToken_DrawsTodaysRed()
    {
        using var pin = RadarBlinkPin.Phase(true);
        (FieldRect altitude, SKBitmap bitmap) = RenderAtKoak(1800);
        using (bitmap)
        {
            Assert.Equal(ScopeColorToken.MvaBelow, altitude.Token);
            Assert.True(AnyPixel(bitmap, PixelsOf(altitude), MvaBelowColor), "no pixel of the MVA-below colour in the altitude field");
        }
    }

    [Fact]
    public void MvaAtToken_DrawsTodaysAmber()
    {
        using var pin = RadarBlinkPin.Phase(true);
        (FieldRect altitude, SKBitmap bitmap) = RenderAtKoak(2000);
        using (bitmap)
        {
            Assert.Equal(ScopeColorToken.MvaAt, altitude.Token);
            Assert.True(AnyPixel(bitmap, PixelsOf(altitude), MvaAtColor), "no pixel of the MVA-at colour in the altitude field");
        }
    }

    [Fact]
    public void UnownedBlock_DrawsItsDefaultTextAndLeaderInLimeGreen()
    {
        using var pin = RadarBlinkPin.Phase(true);
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.StudentDatablockColor = StarsDatablockColor.Unowned;
        ScopeFrame frame = Frame(ac, selected: false, ScopeTestAircraft.AllDisplay);

        using SKBitmap bitmap = Render(frame, NoTints);

        ScopeTrack track = Assert.Single(frame.Tracks);
        Assert.Equal(ScopeBlockColor.StudentUnowned, track.BlockColor);
        Assert.True(AnyPixel(bitmap, PixelsOf(FieldOf(frame, ScopeFieldSlot.Callsign)), StarsUnownedColor), "no lime-green pixel in the callsign");
        Assert.True(AnyPixel(bitmap, LeaderMidpoint(track), StarsUnownedColor), "no lime-green pixel on the leader");
    }

    [Fact]
    public void AssignedToMeBlock_DrawsItsTextLeaderAndSymbolInThePaletteTint()
    {
        using var pin = RadarBlinkPin.Phase(true);
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.AssignedTo = "AB";
        ScopeDisplayOptions display = ScopeTestAircraft.AllDisplay with { UserInitials = "AB", AssignmentTint = true };
        ScopeFrame frame = Frame(ac, selected: false, display);
        var tint = new SKColor(0xFF, 0x00, 0xFF);

        using SKBitmap bitmap = Render(frame, NoTints with { AssignedToMe = tint });

        ScopeTrack track = Assert.Single(frame.Tracks);
        Assert.Equal(ScopeBlockColor.AssignedToMe, track.BlockColor);
        Assert.True(AnyPixel(bitmap, PixelsOf(FieldOf(frame, ScopeFieldSlot.Callsign)), tint), "no tint pixel in the callsign");
        Assert.True(AnyPixel(bitmap, LeaderMidpoint(track), tint), "no tint pixel on the leader");
        Assert.True(AnyPixel(bitmap, SymbolLeftEdge(track), tint), "no tint pixel on the symbol");
    }

    [Fact]
    public void SelectedDefaultBlock_KeepsWhiteText_AndItsSymbolTakesTheSelectedColor()
    {
        using var pin = RadarBlinkPin.Phase(true);
        ScopeFrame frame = Frame(ScopeTestAircraft.CreateModel(), selected: true, ScopeTestAircraft.AllDisplay);
        var selectedSymbol = new SKColor(0x00, 0xFF, 0x00);

        using SKBitmap bitmap = Render(frame, NoTints with { SelectedSymbol = selectedSymbol });

        ScopeTrack track = Assert.Single(frame.Tracks);
        SKRectI callsign = PixelsOf(FieldOf(frame, ScopeFieldSlot.Callsign));
        Assert.True(track.IsSelected);
        Assert.True(AnyPixel(bitmap, callsign, BlockColor), "no white pixel in the selected block's callsign");
        Assert.False(AnyPixel(bitmap, callsign, selectedSymbol), "the selected block's callsign took the selected colour");
        Assert.True(AnyPixel(bitmap, SymbolLeftEdge(track), selectedSymbol), "the selected symbol did not take the selected colour");
    }

    [Fact]
    public void StaleTrack_HalvesTheAlphaOfItsSymbolLeaderAndDefaultText_ButNotItsNote()
    {
        using var pin = RadarBlinkPin.Phase(true);
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.LiveTrafficStale = true;
        ac.Note = "Watch wake";
        ScopeFrame frame = Frame(ac, selected: false, ScopeTestAircraft.AllDisplay);

        ScopeTrack track = Assert.Single(frame.Tracks);
        SKColor block = ScopeRenderer.BlockColorOf(track, NoTints);
        Assert.True(track.IsStale);
        Assert.Equal(ScopeColorToken.Note, FieldOf(frame, ScopeFieldSlot.InstructorNote).Token);
        Assert.Equal(StaleAlpha, ScopeRenderer.SymbolColorOf(track, NoTints).Alpha);
        Assert.Equal(StaleAlpha, block.Alpha);
        Assert.Equal(StaleAlpha, ScopeRenderer.ColorFor(ScopeColorToken.Default, block).Alpha);
        Assert.Equal(255, ScopeRenderer.ColorFor(ScopeColorToken.Note, block).Alpha);
    }

    [Fact]
    public void HighlightedAssignedToMeTrack_DrawsACyanBlock_AndATintedSymbol()
    {
        using var pin = RadarBlinkPin.Phase(true);
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.AssignedTo = "AB";
        ScopeDisplayOptions display = ScopeTestAircraft.AllDisplay with { UserInitials = "AB", AssignmentTint = true };
        ScopeFrame frame = Frame(ac, selected: false, display, new HashSet<string> { ac.Callsign });
        var tint = new SKColor(0xFF, 0x00, 0xFF);

        using SKBitmap bitmap = Render(frame, NoTints with { AssignedToMe = tint });

        ScopeTrack track = Assert.Single(frame.Tracks);
        Assert.Equal(ScopeBlockColor.Highlighted, track.BlockColor);
        Assert.True(AnyPixel(bitmap, PixelsOf(FieldOf(frame, ScopeFieldSlot.Callsign)), SKColors.Cyan), "no cyan pixel in the callsign");
        Assert.True(AnyPixel(bitmap, LeaderMidpoint(track), SKColors.Cyan), "no cyan pixel on the leader");
        Assert.True(AnyPixel(bitmap, SymbolLeftEdge(track), tint), "the highlighted track's symbol lost its tint");
    }

    [Fact]
    public void AssignedToOtherTrack_DrawsItsSymbolInTheUnassignedTint()
    {
        using var pin = RadarBlinkPin.Phase(true);
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.AssignedTo = "XX";
        ScopeDisplayOptions display = ScopeTestAircraft.AllDisplay with { UserInitials = "AB", UnassignedTint = true };
        ScopeFrame frame = Frame(ac, selected: false, display);
        var tint = new SKColor(0xFF, 0x80, 0x00);

        using SKBitmap bitmap = Render(frame, NoTints with { AssignedToOther = tint });

        ScopeTrack track = Assert.Single(frame.Tracks);
        Assert.Equal(ScopeBlockColor.AssignedToOther, track.BlockColor);
        Assert.True(AnyPixel(bitmap, SymbolLeftEdge(track), tint), "no unassigned-tint pixel on the symbol");
    }

    [Fact]
    public void HsfPath_RunsRightThenDownToAHead_InsideItsCell()
    {
        using SKFont font = PlatformHelper.MonospaceFontBold(FontSize);
        float cellWidth = font.MeasureText(TrackPresenter.EramArrowCell);
        const float left = 10f;
        var field = new FieldRect(
            ScopeFieldSlot.HsfIndicator,
            new SKRect(left, 20f, left + cellWidth, 20f + FontSize),
            TrackPresenter.EramArrowCell,
            ScopeColorToken.Default,
            ScopeFieldStyle.HsfIndicator,
            ScopeFieldAction.None
        );

        using SKPath path = ScopeRenderer.HsfPath(field, font, 0);

        SKPoint[] p = path.Points;
        Assert.Equal(p[0].Y, p[1].Y);
        Assert.True(p[1].X > p[0].X, "the stroke does not run rightwards");
        Assert.Equal(p[1].X, p[2].X);
        Assert.True(p[2].Y > p[1].Y, "the stroke does not turn down");
        Assert.Equal(field.Rect.Bottom, p[2].Y);
        Assert.Contains(p.Skip(3), q => (q.Y < p[2].Y) && (q.X < p[2].X));
        Assert.Contains(p.Skip(3), q => (q.Y < p[2].Y) && (q.X > p[2].X));
        Assert.All(p, q => Assert.InRange(q.X, left, left + cellWidth));
        Assert.All(p, q => Assert.InRange(q.Y, field.Rect.Top, field.Rect.Bottom));
    }

    [Fact]
    public void HsfIndicator_DrawsInItsCellInTheBlockColour()
    {
        using var pin = RadarBlinkPin.Phase(true);
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.EramAssignedHeading = "270";
        using SKFont font = PlatformHelper.MonospaceFontBold(FontSize);
        using var paint = new SKPaint();
        var input = new ScopeFrameInput(
            [ac],
            null,
            Viewport(),
            ScopeTemplates.EramId,
            ScopeTextMetrics.FromStyle(new TextStyle(font, paint)),
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
        var frame = ScopeFrame.Build(input);
        FieldRect indicator = FieldOf(frame, ScopeFieldSlot.HsfIndicator);

        using SKBitmap drawn = Render(frame);
        using SKBitmap plain = Render(WithoutStyle(frame));

        Assert.Equal(TrackPresenter.EramArrowCell, indicator.Text);
        Assert.True(AnyLit(drawn, PixelsOf(indicator)), "nothing drawn in the HSF indicator cell");
        Assert.False(AnyLit(plain, PixelsOf(indicator)), "the indicator cell is not blank without the style");
    }

    // The pilot lens's colour, duplicated rather than exposed.
    private static readonly SKColor LensMagenta = new(255, 0, 255);

    // A shape running 200 px east to its tip, clear of the track's symbol and block.
    private static readonly SKPoint ShapeTail = new(100f, 700f);
    private static readonly SKPoint ShapeTip = new(300f, 700f);

    // Inside the arrowhead's flank above the line, 8 to 14 px back from the tip and 2 to 5 px off the line: the head is
    // 14 px long and 7 px wide each side at its base, and the 2 px line covers none of it.
    private static readonly SKRectI AboveTheLineNearTheTip = new(286, 695, 293, 698);

    private static SKBitmap RenderShape(NavRouteShapeKind kind)
    {
        ScopeTrack track = Assert.Single(Frame(ScopeTestAircraft.CreateModel()).Tracks);
        (bool dashed, bool arrow) = NavRouteShapeLook.For(kind);
        var shape = new ScopeIntentShape([ShapeTail, ShapeTip], dashed, arrow, null, []);
        return Render(new ScopeFrame(ScopeTemplates.StarsId, [track with { IntentPath = new ScopeIntentPath([], [shape]) }]));
    }

    [Fact]
    public void IntentShape_HeadingVector_DrawsAFilledArrowheadAtItsTip()
    {
        using var pin = RadarBlinkPin.Phase(true);

        using SKBitmap bitmap = RenderShape(NavRouteShapeKind.HeadingVector);

        Assert.True(AnyPixel(bitmap, AboveTheLineNearTheTip, LensMagenta), "no arrowhead pixel beside the heading vector's tip");
    }

    [Fact]
    public void IntentShape_FinalApproachCourse_DrawsNoArrowhead()
    {
        using var pin = RadarBlinkPin.Phase(true);

        using SKBitmap bitmap = RenderShape(NavRouteShapeKind.FinalApproachCourse);

        Assert.False(AnyLensTint(bitmap, AboveTheLineNearTheTip), "an arrowhead pixel beside the final approach course's tip");
    }

    // Any pixel the lens colour reached, even at the faint edge of an antialiased stroke: red and blue both lit.
    private static bool AnyLensTint(SKBitmap bitmap, SKRectI area)
    {
        for (int y = area.Top; y < area.Bottom; y++)
        {
            for (int x = area.Left; x < area.Right; x++)
            {
                SKColor c = bitmap.GetPixel(x, y);
                if ((c.Red > 20) && (c.Blue > 20))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static bool AnyLit(SKBitmap bitmap, SKRectI area)
    {
        for (int y = area.Top; y < area.Bottom; y++)
        {
            for (int x = area.Left; x < area.Right; x++)
            {
                SKColor c = bitmap.GetPixel(x, y);
                if ((Brightness(c) > 0) && (c.Red == c.Green) && (c.Green == c.Blue))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static ScopeFrame Frame(AircraftModel ac, bool selected, ScopeDisplayOptions display) =>
        Frame(ac, selected, display, new HashSet<string>());

    private static ScopeFrame Frame(AircraftModel ac, bool selected, ScopeDisplayOptions display, IReadOnlySet<string> highlighted)
    {
        using SKFont font = PlatformHelper.MonospaceFontBold(FontSize);
        using var paint = new SKPaint();
        var metrics = ScopeTextMetrics.FromStyle(new TextStyle(font, paint));
        var input = new ScopeFrameInput(
            [ac],
            selected ? ac : null,
            Viewport(),
            ScopeTemplates.StarsId,
            metrics,
            ScopeTestAircraft.AllAlerts,
            display,
            new Dictionary<string, SKPoint>(),
            highlighted,
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

    private static SKBitmap Render(ScopeFrame frame, ScopePalette palette)
    {
        var bitmap = new SKBitmap(Width, Height);
        using var canvas = new SKCanvas(bitmap);
        using var renderer = new ScopeRenderer();
        var snapshot = new ScopeRenderSnapshot(
            frame,
            [],
            new Dictionary<string, string>(),
            ScopeBrightness.Default,
            FontSize,
            palette,
            [],
            null,
            new Dictionary<int, SKRect>()
        );
        renderer.Render(canvas, Viewport(), snapshot);
        canvas.Flush();
        return bitmap;
    }

    /// <summary>The pixels around the midpoint of the track's leader, from its symbol to its block's nearest edge.</summary>
    private static SKRectI LeaderMidpoint(ScopeTrack track)
    {
        SKRect b = track.Block.Bounds;
        float endX = Math.Clamp(track.Symbol.X, b.Left, b.Right);
        float endY = Math.Clamp(track.Symbol.Y, b.Top, b.Bottom);
        int x = (int)MathF.Round((track.Symbol.X + endX) / 2f);
        int y = (int)MathF.Round((track.Symbol.Y + endY) / 2f);
        return new SKRectI(x - 1, y - 1, x + 2, y + 2);
    }

    /// <summary>The pixels around the symbol circle's leftmost point, away from the leader.</summary>
    private static SKRectI SymbolLeftEdge(ScopeTrack track)
    {
        int x = (int)MathF.Round(track.Symbol.X - 5f);
        int y = (int)MathF.Round(track.Symbol.Y);
        return new SKRectI(x - 2, y - 1, x + 2, y + 2);
    }

    /// <summary>Renders an IFR track at KOAK at <paramref name="altitude"/>, with the tint on, in a viewport centred on it.</summary>
    private static (FieldRect Altitude, SKBitmap Bitmap) RenderAtKoak(double altitude)
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.Position = Koak;
        ac.Altitude = altitude;
        ac.FlightRules = "IFR";
        MapViewport viewport = Viewport(Koak.Lat, Koak.Lon);
        ScopeFrame frame = Frame(ac, viewport);
        return (FieldOf(frame, ScopeFieldSlot.Altitude), Render(frame, viewport));
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
