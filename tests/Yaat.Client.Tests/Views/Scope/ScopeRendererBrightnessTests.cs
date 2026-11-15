using SkiaSharp;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.Views.Map;
using Yaat.Client.Views.Radar;
using Yaat.Client.Views.Scope;
using Yaat.Sim.Data;

namespace Yaat.Client.Tests.Views.Scope;

/// <summary>
/// Pins how <see cref="ScopeRenderer"/> draws the BRITE groups: each group scales only the layer it names, DWELL lifts a
/// dwelled track toward full brightness, and the selection border never dims.
/// </summary>
[Collection(RadarBlinkClockCollection.Name)]
public class ScopeRendererBrightnessTests
{
    private const float FontSize = 12f;
    private const int Width = 1000;
    private const int Height = 800;

    // Today's renderer's colours, duplicated rather than exposed.
    private static readonly SKColor White = SKColors.White;
    private static readonly SKColor HalfWhite = new(127, 127, 127);
    private static readonly SKColor SymbolBlue = new(0, 176, 255);
    private static readonly SKColor HalfSymbolBlue = new(0, 88, 127);
    private static readonly SKColor ConflictRed = new(255, 60, 60);
    private static readonly SKColor HalfConflictRed = new(127, 30, 30);

    // VideoMapRenderer's grey map base.
    private const int MapGrey = 160;

    private static readonly ScopePalette NoTints = new(null, null, SKColors.White);

    private static MapViewport Viewport() =>
        new()
        {
            PixelWidth = Width,
            PixelHeight = Height,
            CenterLat = 37.0,
            CenterLon = -122.0,
        };

    private static ScopeFrame Frame(string templateId, string? hovered, AircraftModel? selected, params AircraftModel[] aircraft)
    {
        using SKFont font = PlatformHelper.MonospaceFontBold(FontSize);
        using var paint = new SKPaint();
        var input = new ScopeFrameInput(
            aircraft,
            selected,
            Viewport(),
            templateId,
            ScopeTextMetrics.FromStyle(new TextStyle(font, paint)),
            ScopeTestAircraft.AllAlerts,
            ScopeTestAircraft.AllDisplay,
            new Dictionary<string, SKPoint>(),
            new HashSet<string>(),
            hovered
        );
        return ScopeFrame.Build(input);
    }

    private static ScopeFrame Stars(AircraftModel ac, bool dwelled = false) => Frame(ScopeTemplates.StarsId, dwelled ? ac.Callsign : null, null, ac);

    private static SKBitmap Render(ScopeFrame frame, ScopeBrightness brightness) => Render(frame, brightness, [], new Dictionary<string, string>());

    private static SKBitmap Render(
        ScopeFrame frame,
        ScopeBrightness brightness,
        IReadOnlyList<VideoMapData> maps,
        IReadOnlyDictionary<string, string> lookup
    )
    {
        var bitmap = new SKBitmap(Width, Height);
        using var canvas = new SKCanvas(bitmap);
        using var renderer = new ScopeRenderer();
        renderer.Render(canvas, Viewport(), new ScopeRenderSnapshot(frame, maps, lookup, brightness, FontSize, NoTints));
        canvas.Flush();
        return bitmap;
    }

    private static ScopeTrack TrackOf(ScopeFrame frame, string callsign) => Assert.Single(frame.Tracks, t => t.Aircraft.Callsign == callsign);

    private static SKRectI FieldPixels(ScopeTrack track, ScopeFieldSlot slot) =>
        SKRectI.Ceiling(Assert.Single(track.Block.Fields, f => f.Slot == slot).Rect, true);

    /// <summary>The pixels around the midpoint of the track's leader, from its symbol to its block's nearest edge.</summary>
    private static SKRectI LeaderMidpoint(ScopeTrack track)
    {
        SKPoint end = ScopeRenderer.LeaderEnd(track);
        int x = (int)MathF.Round((track.Symbol.X + end.X) / 2f);
        int y = (int)MathF.Round((track.Symbol.Y + end.Y) / 2f);
        return new SKRectI(x - 1, y - 1, x + 2, y + 2);
    }

    /// <summary>The pixels around the symbol circle's leftmost point, away from the leader.</summary>
    private static SKRectI SymbolLeftEdge(ScopeTrack track)
    {
        int x = (int)MathF.Round(track.Symbol.X - 5f);
        int y = (int)MathF.Round(track.Symbol.Y);
        return new SKRectI(x - 2, y - 1, x + 2, y + 2);
    }

    /// <summary>The pixels around the middle of <paramref name="rect"/>'s top edge.</summary>
    private static SKRectI TopEdgeMiddle(SKRect rect)
    {
        int x = (int)MathF.Round(rect.MidX);
        int y = (int)MathF.Floor(rect.Top);
        return new SKRectI(x - 3, y - 1, x + 3, y + 2);
    }

    private static bool AnyPixel(SKBitmap bitmap, SKRectI area, SKColor color) => Pixels(bitmap, area).Any(c => c == color);

    private static bool AnyLit(SKBitmap bitmap, SKRectI area) => Pixels(bitmap, area).Any(c => (c.Red + c.Green + c.Blue) > 0);

    private static IEnumerable<SKColor> Pixels(SKBitmap bitmap, SKRectI area)
    {
        for (int y = area.Top; y < area.Bottom; y++)
        {
            for (int x = area.Left; x < area.Right; x++)
            {
                yield return bitmap.GetPixel(x, y);
            }
        }
    }

    [Fact]
    public void Fdb50_HalvesTheLeaderAndTheDefaultText_AndLeavesTheSymbol()
    {
        using var pin = RadarBlinkPin.Phase(true);
        ScopeFrame frame = Stars(ScopeTestAircraft.CreateModel());
        ScopeTrack track = Assert.Single(frame.Tracks);

        using SKBitmap bitmap = Render(frame, ScopeBrightness.Default with { Fdb = 50 });

        SKRectI callsign = FieldPixels(track, ScopeFieldSlot.Callsign);
        Assert.True(AnyPixel(bitmap, callsign, HalfWhite), "no half-white pixel in the callsign");
        Assert.False(AnyPixel(bitmap, callsign, White), "a full-white pixel in the callsign");
        Assert.True(AnyPixel(bitmap, LeaderMidpoint(track), HalfWhite), "no half-white pixel on the leader");
        Assert.False(AnyPixel(bitmap, LeaderMidpoint(track), White), "a full-white pixel on the leader");
        Assert.True(AnyPixel(bitmap, SymbolLeftEdge(track), SymbolBlue), "the symbol dimmed with the block");
    }

    [Fact]
    public void Targets50_HalvesTheSymbolOnly()
    {
        using var pin = RadarBlinkPin.Phase(true);
        ScopeFrame frame = Stars(ScopeTestAircraft.CreateModel());
        ScopeTrack track = Assert.Single(frame.Tracks);

        using SKBitmap bitmap = Render(frame, ScopeBrightness.Default with { Targets = 50 });

        Assert.True(AnyPixel(bitmap, SymbolLeftEdge(track), HalfSymbolBlue), "no half-blue pixel on the symbol");
        Assert.False(AnyPixel(bitmap, SymbolLeftEdge(track), SymbolBlue), "a full-blue pixel on the symbol");
        Assert.True(AnyPixel(bitmap, FieldPixels(track, ScopeFieldSlot.Callsign), White), "the callsign dimmed with the symbol");
        Assert.True(AnyPixel(bitmap, LeaderMidpoint(track), White), "the leader dimmed with the symbol");
    }

    [Fact]
    public void Alerts50_HalvesTheConflictToken_AndLeavesTheText()
    {
        using var pin = RadarBlinkPin.Phase(true);
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.ConflictPeerCallsign = "SWA1234";
        ScopeFrame frame = Frame(ScopeTemplates.StarsId, null, null, ac, ScopeTestAircraft.CreateConflictPeer());
        ScopeTrack track = TrackOf(frame, ac.Callsign);
        SKRectI conflict = FieldPixels(track, ScopeFieldSlot.ConflictAlert);

        using SKBitmap bitmap = Render(frame, ScopeBrightness.Default with { Alerts = 50 });

        Assert.True(AnyPixel(bitmap, conflict, HalfConflictRed), "no half-red pixel in the conflict token");
        Assert.False(AnyPixel(bitmap, conflict, ConflictRed), "a full-red pixel in the conflict token");
        Assert.True(AnyPixel(bitmap, FieldPixels(track, ScopeFieldSlot.Callsign), White), "the callsign dimmed with the alerts");
    }

    [Fact]
    public void Line4Minus10_AtFdb100_DrawsLine4At90_AndNoOtherField()
    {
        using var pin = RadarBlinkPin.Phase(true);
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ScopeFrame frame = Frame(ScopeTemplates.EramId, null, null, ac);
        ScopeTrack track = Assert.Single(frame.Tracks);
        SKRectI line4 = FieldPixels(track, ScopeFieldSlot.Line4Notes);

        using SKBitmap bitmap = Render(frame, ScopeBrightness.Default with { Line4 = -10 });

        Assert.True(AnyPixel(bitmap, line4, new SKColor(229, 229, 229)), "no 90% pixel in line 4");
        Assert.False(AnyPixel(bitmap, line4, White), "a full-white pixel in line 4");
        Assert.True(AnyPixel(bitmap, FieldPixels(track, ScopeFieldSlot.Callsign), White), "the callsign dimmed with line 4");
    }

    [Fact]
    public void Line4Minus20_AtFdb0_ClampsToNothingDrawn()
    {
        using var pin = RadarBlinkPin.Phase(true);
        ScopeFrame frame = Frame(ScopeTemplates.EramId, null, null, ScopeTestAircraft.CreateModel());
        ScopeTrack track = Assert.Single(frame.Tracks);

        using SKBitmap bitmap = Render(frame, ScopeBrightness.Default with { Fdb = 0, Line4 = -20 });

        Assert.False(AnyLit(bitmap, FieldPixels(track, ScopeFieldSlot.Line4Notes)), "line 4 drew at FDB 0");
    }

    private static FieldRect FieldIn(ScopeFieldSlot slot, ScopeColorToken token) =>
        new(slot, new SKRect(0, 0, 10, 10), "X", token, ScopeFieldStyle.None);

    [Theory]
    [InlineData(0, -20, 0)]
    [InlineData(100, -10, 90)]
    [InlineData(30, -10, 20)]
    public void FieldPercent_Line4_IsFdbPlusLine4_Clamped(int fdb, int line4, int expected)
    {
        ScopeBrightness brightness = ScopeBrightness.Default with { Fdb = fdb, Line4 = line4 };

        Assert.Equal(expected, ScopeRenderer.FieldPercent(FieldIn(ScopeFieldSlot.Line4Notes, ScopeColorToken.Default), brightness));
        Assert.Equal(expected, ScopeRenderer.FieldPercent(FieldIn(ScopeFieldSlot.Line4Notes, ScopeColorToken.ConflictAlert), brightness));
    }

    [Fact]
    public void FieldPercent_OffLine4_IsAlertsForTheAlertTokens_AndFdbForEveryOther()
    {
        ScopeColorToken[] alerts =
        [
            ScopeColorToken.ConflictAlert,
            ScopeColorToken.NoLandingClearance,
            ScopeColorToken.AtpaWarning,
            ScopeColorToken.AtpaAlert,
        ];
        ScopeBrightness brightness = ScopeBrightness.Default with { Fdb = 30, Alerts = 70 };

        foreach (ScopeColorToken token in Enum.GetValues<ScopeColorToken>())
        {
            int expected = alerts.Contains(token) ? 70 : 30;
            Assert.True(
                expected == ScopeRenderer.FieldPercent(FieldIn(ScopeFieldSlot.Callsign, token), brightness),
                $"{token} does not draw at {expected}"
            );
        }
    }

    [Fact]
    public void DwelledLine4_AtFdb0Line4Minus20Dwell10_DrawsAt50()
    {
        using var pin = RadarBlinkPin.Phase(true);
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ScopeFrame frame = Frame(ScopeTemplates.EramId, ac.Callsign, null, ac);
        ScopeTrack track = Assert.Single(frame.Tracks);
        SKRectI line4 = FieldPixels(track, ScopeFieldSlot.Line4Notes);

        using SKBitmap bitmap = Render(frame, ScopeBrightness.Default with { Fdb = 0, Line4 = -20, Dwell = 10 });

        Assert.True(track.IsDwelled);
        Assert.True(AnyPixel(bitmap, line4, HalfWhite), "no 50% pixel in the dwelled line 4");
        Assert.False(Pixels(bitmap, line4).Any(c => c.Red > 127), "a pixel brighter than 50% in the dwelled line 4");
    }

    [Theory]
    [InlineData(20, 255)]
    [InlineData(0, 127)]
    public void DwelledBlock_AtFdb50_DrawsAtTheDwellLift(int dwell, byte expected)
    {
        using var pin = RadarBlinkPin.Phase(true);
        ScopeFrame frame = Stars(ScopeTestAircraft.CreateModel(), dwelled: true);
        ScopeTrack track = Assert.Single(frame.Tracks);
        var color = new SKColor(expected, expected, expected);

        using SKBitmap bitmap = Render(frame, ScopeBrightness.Default with { Fdb = 50, Dwell = dwell });

        Assert.True(track.IsDwelled);
        Assert.True(AnyPixel(bitmap, FieldPixels(track, ScopeFieldSlot.Callsign), color), $"no {expected} pixel in the callsign");
        Assert.True(AnyPixel(bitmap, LeaderMidpoint(track), color), $"no {expected} pixel on the leader");
        Assert.True(AnyPixel(bitmap, TopEdgeMiddle(track.DwellBox), color), $"no {expected} pixel on the dwell box");
        if (expected != 255)
        {
            Assert.False(AnyPixel(bitmap, TopEdgeMiddle(track.DwellBox), White), "a full-white pixel on the dwell box");
        }
    }

    [Fact]
    public void DwelledSymbol_AtTargets50Dwell20_DrawsAtFull()
    {
        using var pin = RadarBlinkPin.Phase(true);
        ScopeFrame frame = Stars(ScopeTestAircraft.CreateModel(), dwelled: true);
        ScopeTrack track = Assert.Single(frame.Tracks);

        using SKBitmap bitmap = Render(frame, ScopeBrightness.Default with { Targets = 50, Dwell = 20 });

        Assert.True(AnyPixel(bitmap, SymbolLeftEdge(track), SymbolBlue), "the dwelled symbol was not lifted to full");
    }

    [Fact]
    public void SelectionBorder_AtFdb0_IsUnchanged()
    {
        using var pin = RadarBlinkPin.Phase(true);
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ScopeFrame frame = Frame(ScopeTemplates.StarsId, null, ac, ac);
        ScopeTrack track = Assert.Single(frame.Tracks);

        using SKBitmap bitmap = Render(frame, ScopeBrightness.Default with { Fdb = 0 });

        Assert.True(track.IsSelected);
        Assert.True(AnyPixel(bitmap, TopEdgeMiddle(ScopeRenderer.SelectionBorder(track)), White), "the selection border dimmed");
        Assert.False(AnyLit(bitmap, FieldPixels(track, ScopeFieldSlot.Callsign)), "the callsign drew at FDB 0");
    }

    [Fact]
    public void DwelledStaleBlock_KeepsFullAlpha()
    {
        using var pin = RadarBlinkPin.Phase(true);
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.LiveTrafficStale = true;
        ScopeFrame frame = Stars(ac, dwelled: true);
        ScopeTrack track = Assert.Single(frame.Tracks);

        using SKBitmap bitmap = Render(frame, ScopeBrightness.Default);

        Assert.True(track.IsStale);
        Assert.True(AnyPixel(bitmap, FieldPixels(track, ScopeFieldSlot.Callsign), White), "the dwelled stale block lost its alpha");
    }

    [Theory]
    [InlineData(50, 30)]
    [InlineData(50, 100)]
    [InlineData(100, 30)]
    public void MapGroups_ScaleTheirOwnCategoryOnly(int mapA, int mapB)
    {
        VideoMapData a = HorizontalMap("a1", 37.01);
        VideoMapData b = HorizontalMap("b1", 36.99);
        var lookup = new Dictionary<string, string> { ["a1"] = "A", ["b1"] = "B" };

        using SKBitmap bitmap = Render(
            new ScopeFrame(ScopeTemplates.StarsId, []),
            ScopeBrightness.Default with
            {
                MapA = mapA,
                MapB = mapB,
            },
            [a, b],
            lookup
        );

        Assert.InRange(MaxRedOnLine(bitmap, 37.01), (MapGrey * mapA / 100) - 2, (MapGrey * mapA / 100) + 2);
        Assert.InRange(MaxRedOnLine(bitmap, 36.99), (MapGrey * mapB / 100) - 2, (MapGrey * mapB / 100) + 2);
    }

    private static VideoMapData HorizontalMap(string id, double lat) =>
        new() { MapId = id, Lines = [new VideoMapLine { Points = [(lat, -122.01), (lat, -121.99)] }] };

    /// <summary>The brightest red channel around the screen midpoint of the horizontal map line at <paramref name="lat"/>.</summary>
    private static int MaxRedOnLine(SKBitmap bitmap, double lat)
    {
        (float x, float y) = Viewport().LatLonToScreen(lat, -122.0);
        Assert.InRange(x, 3, Width - 3);
        Assert.InRange(y, 3, Height - 3);
        var area = new SKRectI((int)x - 2, (int)y - 2, (int)x + 3, (int)y + 3);
        return Pixels(bitmap, area).Max(c => (int)c.Red);
    }
}
