using SkiaSharp;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.Views.Map;
using Yaat.Client.Views.Scope;
using Yaat.Sim;

namespace Yaat.Client.Tests.Views.Scope;

/// <summary>
/// Hover dwell as CRC's <c>ViewDataBlocks.HandleMouseMove</c> judges it: within 8 px of a track's symbol, or inside its drawn
/// block's text bounds inflated 2 px, topmost block first; a dwelled track draws last with a 1 px box in its block colour.
/// </summary>
[Collection(RadarBlinkClockCollection.Name)]
public class ScopeDwellTests
{
    private const float FontSize = 12f;
    private const int Width = 1000;
    private const int Height = 800;

    private static readonly ScopePalette NoTints = new(null, null, SKColors.White);

    private static MapViewport Viewport() =>
        new()
        {
            PixelWidth = Width,
            PixelHeight = Height,
            CenterLat = 37.0,
            CenterLon = -122.0,
        };

    private static ScopeFrame Build(string? hovered, ScopeBlockLevel defaultLevel, AircraftModel? selected, params AircraftModel[] aircraft)
    {
        using SKFont font = PlatformHelper.MonospaceFontBold(FontSize);
        using var paint = new SKPaint();
        var input = new ScopeFrameInput(
            aircraft,
            selected,
            Viewport(),
            ScopeTemplates.StarsId,
            ScopeTextMetrics.FromStyle(new TextStyle(font, paint)),
            ScopeTestAircraft.AllAlerts,
            ScopeTestAircraft.AllDisplay,
            new Dictionary<string, SKPoint>(),
            new HashSet<string>(),
            hovered,
            defaultLevel
        );
        return ScopeFrame.Build(input);
    }

    private static ScopeFrame Build(string? hovered, params AircraftModel[] aircraft) => Build(hovered, ScopeBlockLevel.Full, null, aircraft);

    private static AircraftModel Named(string callsign, double lon = -122.0)
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.Callsign = callsign;
        ac.Position = new LatLon(37.0, lon);
        return ac;
    }

    private static SKBitmap Render(ScopeFrame frame)
    {
        var bitmap = new SKBitmap(Width, Height);
        using var canvas = new SKCanvas(bitmap);
        using var renderer = new ScopeRenderer();
        renderer.Render(
            canvas,
            Viewport(),
            new ScopeRenderSnapshot(frame, [], new Dictionary<string, string>(), ScopeBrightness.Default, FontSize, NoTints)
        );
        canvas.Flush();
        return bitmap;
    }

    [Fact]
    public void DwellAt_Within8PxOfTheSymbol_Dwells_9PxDoesNot()
    {
        AircraftModel ac = Named("UAL238");
        ScopeFrame frame = Build(null, ac);
        ScopeTrack track = Assert.Single(frame.Tracks);
        SKPoint s = track.Symbol;

        Assert.False(track.DwellBox.Contains(s.X, s.Y + 9f));
        Assert.Same(ac, frame.DwellAt(s.X, s.Y + 8f));
        Assert.Null(frame.DwellAt(s.X, s.Y + 9f));
    }

    [Fact]
    public void DwellAt_InsideTheTextBoundsInflated2Px_Dwells_3PxOutsideDoesNot()
    {
        AircraftModel ac = Named("UAL238");
        ScopeFrame frame = Build(null, ac);
        ScopeTrack track = Assert.Single(frame.Tracks);
        SKRect text = track.Block.TextBounds;
        float y = text.MidY;

        Assert.True(SKPoint.Distance(track.Symbol, new SKPoint(text.Right + 3f, y)) > 9f);
        Assert.Same(ac, frame.DwellAt(text.Right + 1.5f, y));
        Assert.Null(frame.DwellAt(text.Right + 3f, y));
        Assert.Same(ac, frame.DwellAt(text.MidX, text.Top - 1.5f));
        Assert.Null(frame.DwellAt(text.MidX, text.Top - 3f));
    }

    [Fact]
    public void DwellAt_OverTwoOverlappingBlocks_DwellsTheTopmost()
    {
        AircraftModel under = Named("UAL238");
        AircraftModel over = Named("DAL55");
        ScopeFrame frame = Build(null, under, over);
        SKRect text = frame.Tracks[^1].Block.TextBounds;

        Assert.Same(over, frame.Tracks[^1].Aircraft);
        Assert.True(frame.Tracks[0].Block.TextBounds.Contains(text.MidX, text.MidY));
        Assert.Same(over, frame.DwellAt(text.MidX, text.MidY));
    }

    [Fact]
    public void Build_DwelledTrack_IsLastInTracks_AtDwell_OthersKeepTheirOrderAtTheDefault()
    {
        AircraftModel a = Named("UAL238", -122.05);
        AircraftModel b = Named("DAL55", -122.0);
        AircraftModel c = Named("SWA1", -121.95);

        ScopeFrame frame = Build("UAL238", a, b, c);

        Assert.Equal(["DAL55", "SWA1", "UAL238"], frame.Tracks.Select(t => t.Aircraft.Callsign));
        Assert.Equal(ScopeBlockLevel.Dwell, frame.Tracks[^1].Level);
        Assert.True(frame.Tracks[^1].IsDwelled);
        Assert.All(frame.Tracks.Take(2), t => Assert.Equal(ScopeBlockLevel.Full, t.Level));
    }

    [Fact]
    public void DwelledTrack_EmphasisRect_IsTheTextBoundsInflated2Px()
    {
        ScopeTrack track = Build("UAL238", Named("UAL238")).Tracks[^1];
        SKRect text = track.Block.TextBounds;

        Assert.Equal(new SKRect(text.Left - 2f, text.Top - 2f, text.Right + 2f, text.Bottom + 2f), track.DwellBox);
    }

    [Fact]
    public void DwellAt_ReadsTheDwelledLayout()
    {
        AircraftModel ac = Named("UAL238");
        ScopeFrame reduced = Build(null, ScopeBlockLevel.Limited, null, ac);
        ScopeFrame dwelled = Build("UAL238", ScopeBlockLevel.Limited, null, ac);
        ScopeTrack track = Assert.Single(dwelled.Tracks);
        // The cwt/type token sits on the Full line 2, right of the Limited block's one short line.
        FieldRect probe = Assert.Single(track.Block.Fields, f => f.Slot == ScopeFieldSlot.CwtType);
        (float x, float y) = (probe.Rect.MidX, probe.Rect.MidY);

        Assert.False(Assert.Single(reduced.Tracks).DwellBox.Contains(x, y));
        Assert.True(SKPoint.Distance(track.Symbol, new SKPoint(x, y)) > 8f);
        Assert.Null(reduced.DwellAt(x, y));
        Assert.Same(ac, dwelled.DwellAt(x, y));
    }

    /// <summary>How many pixels of the 3-row band about <paramref name="y"/> between the box's left and right are <paramref name="color"/>.</summary>
    private static int LitOnRow(SKBitmap bitmap, SKRect box, float y, SKColor color)
    {
        int lit = 0;
        int row = (int)MathF.Floor(y);
        for (int py = row - 1; py <= row + 1; py++)
        {
            for (int px = (int)box.Left + 2; px < (int)box.Right - 2; px++)
            {
                lit += bitmap.GetPixel(px, py) == color ? 1 : 0;
            }
        }
        return lit;
    }

    [Fact]
    public void Render_DwelledBlock_DrawsA1PxBoxInTheBlockColour_AndLeavesOtherBlocksUnchanged()
    {
        AircraftModel dwelled = Named("UAL238", -122.05);
        dwelled.StudentDatablockColor = StarsDatablockColor.Unowned;
        AircraftModel other = Named("DAL55", -121.9);
        ScopeFrame plain = Build(null, dwelled, other);
        ScopeFrame hovered = Build("UAL238", dwelled, other);
        ScopeTrack track = hovered.Tracks[^1];
        SKColor blockColor = ScopeRenderer.BlockColorOf(track, NoTints);

        using SKBitmap before = Render(plain);
        using SKBitmap after = Render(hovered);

        SKRect box = track.DwellBox;
        int span = (int)box.Width - 4;
        Assert.Equal(0, LitOnRow(before, box, box.Top, blockColor));
        Assert.True(LitOnRow(after, box, box.Top, blockColor) >= span, "the box's top edge is drawn in the block colour");
        Assert.True(LitOnRow(after, box, box.Bottom, blockColor) >= span, "the box's bottom edge is drawn in the block colour");

        ScopeTrack untouched = hovered.Tracks[0];
        var area = SKRectI.Ceiling(untouched.DwellBox, true);
        area.Inflate(4, 4);
        for (int y = area.Top; y < area.Bottom; y++)
        {
            for (int x = area.Left; x < area.Right; x++)
            {
                Assert.Equal(before.GetPixel(x, y), after.GetPixel(x, y));
            }
        }
    }

    [Fact]
    public void Render_SelectedAndDwelledBlock_ShowsTheSelectionBorderAndTheDwellBox()
    {
        AircraftModel ac = Named("UAL238");
        ScopeFrame frame = Build("UAL238", ScopeBlockLevel.Full, ac, ac);
        ScopeTrack track = Assert.Single(frame.Tracks);

        using SKBitmap bitmap = Render(frame);

        SKRect border = ScopeRenderer.SelectionBorder(track);
        int span = (int)border.Width - 4;
        Assert.True(LitOnRow(bitmap, border, border.Top, SKColors.White) >= span, "the selection border is drawn");
        Assert.True(LitOnRow(bitmap, track.DwellBox, track.DwellBox.Top, SKColors.White) >= span, "the dwell box is drawn");
    }

    [Fact]
    public void Render_DwelledBlock_KeepsTheSquawkMismatchPulsesOffPhase()
    {
        using var pin = RadarBlinkPin.Phase(false);
        AircraftModel ac = ScopeTestAircraft.CreateSquawkMismatch();
        ScopeFrame plain = Build(null, ac);
        ScopeFrame hovered = Build(ac.Callsign, ac);
        FieldRect squawk = Assert.Single(Assert.Single(plain.Tracks).Block.Fields, f => f.Slot == ScopeFieldSlot.Squawk);
        Assert.Equal(ScopeFieldStyle.DimTail, squawk.Style);
        Assert.Equal(squawk, Assert.Single(Assert.Single(hovered.Tracks).Block.Fields, f => f.Slot == ScopeFieldSlot.Squawk));

        using SKBitmap before = Render(plain);
        using SKBitmap after = Render(hovered);

        var area = SKRectI.Ceiling(squawk.Rect, true);
        int lit = 0;
        for (int y = area.Top; y < area.Bottom; y++)
        {
            for (int x = area.Left; x < area.Right; x++)
            {
                lit += before.GetPixel(x, y) == SKColors.Black ? 0 : 1;
                Assert.Equal(before.GetPixel(x, y), after.GetPixel(x, y));
            }
        }
        Assert.True(lit > 0);
    }

    [Fact]
    public void DwelledStaleBlock_IsNotDimmed()
    {
        AircraftModel ac = Named("UAL238");
        ac.LiveTrafficStale = true;

        ScopeTrack plain = Assert.Single(Build(null, ac).Tracks);
        ScopeTrack dwelled = Assert.Single(Build("UAL238", ac).Tracks);

        Assert.Equal(128, ScopeRenderer.BlockColorOf(plain, NoTints).Alpha);
        Assert.Equal(255, ScopeRenderer.BlockColorOf(dwelled, NoTints).Alpha);
    }
}
