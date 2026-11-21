using SkiaSharp;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.Views.Map;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Profiles;
using Yaat.Sim;

namespace Yaat.Client.Tests.Views.Scope;

/// <summary>
/// Pins how <see cref="ScopeRenderer"/> draws an ERAM-template track in the ERAM look: the target glyph in target yellow in
/// place of the blue circle, the track-position symbol, the history as the glyph in uniform dim yellow, the leader from 6 px
/// out, the FDB and LDB yellows and CRC's default leader placement; a STARS track in the same frame keeps today's look.
/// </summary>
[Collection(MapBlinkClockCollection.Name)]
public class ScopeEramLookRenderTests
{
    private const float FontSize = 12f;
    private const int Width = 1000;
    private const int Height = 800;

    /// <summary>The datablock row pitch at the tests' font: its size plus 2 (<see cref="TextStyle.LineHeight"/>).</summary>
    private const float RowPitch = FontSize + 2f;

    /// <summary>The environment variable that, set to 1, has <see cref="MixedScene_KeepsEachSamplesHistoryGlyph_AndDrawsEveryTrack_SavingThePngOnlyOnRequest"/> write its PNG.</summary>
    private const string SaveRendersVariable = "YAAT_SAVE_RENDERS";

    // CRC's ERAM yellows at its default brightness groups, and today's symbol blue, duplicated rather than exposed.
    private static readonly SKColor TargetYellow = new(200, 200, 0);
    private static readonly SKColor FdbYellow = new(196, 196, 0);
    private static readonly SKColor LdbYellow = new(129, 129, 0);
    private static readonly SKColor HistoryYellow = new(34, 34, 0);
    private static readonly SKColor SymbolBlue = new(0, 176, 255);

    private static readonly ScopePalette NoTints = new(null, null, SKColors.White);

    // How far west of the target each history sample sits, in pixels, oldest first.
    private static readonly float[] HistoryOffsets = [180f, 120f, 60f];

    /// <summary>A viewer acting at a STARS position (NCT 2S) with ERAM sector ZOA 44, so other tracks take the others' level.</summary>
    private static readonly ScopeViewer Viewer = new(new ActingPosition("OAK_APP", "NCT", "2S"), "ZOA", "44");

    /// <summary>How a frame is built: its template, the others' level, the history count, manual offsets, viewer and width.</summary>
    private sealed record Setup
    {
        public string TemplateId { get; init; } = ScopeTemplates.EramId;
        public string OthersLevel { get; init; } = ScopeProfile.OthersFull;
        public int HistoryCount { get; init; }
        public IReadOnlyDictionary<string, SKPoint>? Manual { get; init; }
        public ScopeViewer? Viewer { get; init; }
        public int ViewportWidth { get; init; } = Width;
        public int ViewportHeight { get; init; } = Height;
    }

    /// <summary>An ERAM frame whose unowned tracks draw ERAM's Partial block (an LDB).</summary>
    private static readonly Setup Ldb = new() { OthersLevel = ScopeProfile.OthersPartial, Viewer = Viewer };

    private static MapViewport Viewport(Setup setup) =>
        new()
        {
            PixelWidth = setup.ViewportWidth,
            PixelHeight = setup.ViewportHeight,
            CenterLat = 37.0,
            CenterLon = -122.0,
        };

    private static ScopeFrame Frame(Setup setup, params AircraftModel[] aircraft)
    {
        using SKFont font = PlatformHelper.MonospaceFontBold(FontSize);
        using var paint = new SKPaint();
        var input = new ScopeFrameInput(
            aircraft,
            null,
            Viewport(setup),
            setup.TemplateId,
            ScopeTextMetrics.FromStyle(new TextStyle(font, paint)),
            ScopeTestAircraft.AllAlerts,
            ScopeTestAircraft.AllDisplay with
            {
                Viewer = setup.Viewer ?? ScopeViewer.None,
            },
            setup.Manual ?? new Dictionary<string, SKPoint>(),
            new HashSet<string>(),
            MvaFacilityId: null,
            OthersLevel: setup.OthersLevel,
            VectorMinutes: 0,
            VectorOwnership: ScopeVectorOwnership.Own,
            HistoryCount: setup.HistoryCount,
            LocalRings: new Dictionary<string, ScopeTrackRings>(),
            IntentPaths: new ScopeIntentPathCache()
        );
        return ScopeFrame.Build(input);
    }

    private static SKBitmap Render(ScopeFrame frame, Setup setup) => Render(frame, setup, 1f);

    /// <summary>Renders <paramref name="frame"/> at <paramref name="scale"/> device pixels per DIP, as a scaled display does.</summary>
    private static SKBitmap Render(ScopeFrame frame, Setup setup, float scale)
    {
        var bitmap = new SKBitmap((int)MathF.Ceiling(setup.ViewportWidth * scale), (int)MathF.Ceiling(setup.ViewportHeight * scale));
        using var canvas = new SKCanvas(bitmap);
        canvas.Scale(scale);
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
            new Dictionary<int, SKRect>(),
            null,
            null
        );
        renderer.Render(canvas, Viewport(setup), snapshot);
        canvas.Flush();
        return bitmap;
    }

    /// <summary>A correlated Mode C beacon squawking 4521, filed to <paramref name="destination"/>.</summary>
    private static AircraftModel CorrelatedBeacon(string destination = "KORD")
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.BeaconCode = 4521;
        ac.EramIsCorrelated = true;
        ac.Destination = destination;
        return ac;
    }

    private static ScopeTrack TrackOf(ScopeFrame frame, string callsign) => Assert.Single(frame.Tracks, t => t.Aircraft.Callsign == callsign);

    /// <summary>The top-left of the 12 x 15 glyph cell drawn at <paramref name="at"/> (the mock's <c>glyphAt</c>).</summary>
    private static SKPoint CellOrigin(SKPoint at) => new(MathF.Round(at.X) - 6f, MathF.Round(at.Y) - 7f);

    /// <summary>The 3 x 3 pixels around the cell point (<paramref name="x"/>, <paramref name="y"/>) of the glyph at <paramref name="at"/>.</summary>
    private static SKRectI CellArea(SKPoint at, float x, float y)
    {
        SKPoint o = CellOrigin(at);
        int px = (int)MathF.Floor(o.X + x);
        int py = (int)MathF.Floor(o.Y + y);
        return new SKRectI(px - 1, py - 1, px + 2, py + 2);
    }

    private static SKRectI PixelAt(float x, float y)
    {
        int px = (int)MathF.Floor(x);
        int py = (int)MathF.Floor(y);
        return new SKRectI(px, py, px + 1, py + 1);
    }

    private static SKRectI FieldPixels(ScopeTrack track, ScopeFieldSlot slot) =>
        SKRectI.Ceiling(Assert.Single(track.Block.Fields, f => f.Slot == slot).Rect, true);

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

    private static bool AnyPixel(SKBitmap bitmap, SKRectI area, SKColor color) =>
        Pixels(bitmap, area).Any(c => (c.Red == color.Red) && (c.Green == color.Green) && (c.Blue == color.Blue));

    private static bool AnyLit(SKBitmap bitmap, SKRectI area) => Pixels(bitmap, area).Any(c => (c.Red | c.Green | c.Blue) != 0);

    /// <summary>The 13 x 16 pixels the glyph cell at <paramref name="at"/> covers, one pixel of margin round it.</summary>
    private static SKRectI CellBox(SKPoint at)
    {
        SKPoint o = CellOrigin(at);
        return new SKRectI((int)o.X - 1, (int)o.Y - 1, (int)o.X + 13, (int)o.Y + 16);
    }

    [Fact]
    public void CorrelatedBeacon_DrawsTheBackslashInTargetYellow_AndNoCircle()
    {
        using var pin = MapBlinkPin.Phase(true);
        ScopeFrame frame = Frame(Ldb, CorrelatedBeacon());
        ScopeTrack track = Assert.Single(frame.Tracks);
        Assert.Equal(ScopeBlockLevel.Partial, track.Level);

        using SKBitmap bitmap = Render(frame, Ldb);

        // The backslash runs from cell (1, 1) to (11, 14).
        foreach (float t in new[] { 0.25f, 0.5f, 0.75f })
        {
            Assert.True(AnyPixel(bitmap, CellArea(track.Symbol, 1 + (10 * t), 1 + (13 * t)), TargetYellow), $"no target yellow at {t} along the \\");
        }

        // Today's 5 px circle, at the points the backslash does not cross.
        foreach (float deg in new[] { 0f, 45f, 90f, 180f, 225f, 270f })
        {
            float rad = deg * MathF.PI / 180f;
            SKRectI ring = PixelAt(track.Symbol.X + (5f * MathF.Cos(rad)), track.Symbol.Y - (5f * MathF.Sin(rad)));
            Assert.False(AnyLit(bitmap, ring), $"a pixel lit on today's circle at {deg} degrees");
        }

        Assert.False(AnyPixel(bitmap, CellBox(track.Symbol), SymbolBlue), "a symbol-blue pixel in the glyph's cell");
    }

    [Fact]
    public void MixedFrame_StarsTrack_KeepsTheBlueCircle_WhileTheEramTrackDrawsItsGlyph()
    {
        using var pin = MapBlinkPin.Phase(true);
        var setup = new Setup { TemplateId = ScopeTemplates.MixedId };
        AircraftModel stars = ScopeTestAircraft.CreateModel();
        stars.Callsign = "SWA1832";
        stars.OwnerSectorCode = "2S";
        (double lat, double lon) = Viewport(setup).ScreenToLatLon(300, 400);
        stars.Position = new LatLon(lat, lon);
        AircraftModel eram = CorrelatedBeacon();
        ScopeFrame frame = Frame(setup, stars, eram);
        ScopeTrack starsTrack = TrackOf(frame, "SWA1832");
        ScopeTrack eramTrack = TrackOf(frame, eram.Callsign);

        using SKBitmap bitmap = Render(frame, setup);

        Assert.False(starsTrack.EramLook);
        Assert.True(eramTrack.EramLook);
        int x = (int)MathF.Round(starsTrack.Symbol.X - 5f);
        int y = (int)MathF.Round(starsTrack.Symbol.Y);
        Assert.True(AnyPixel(bitmap, new SKRectI(x - 2, y - 1, x + 2, y + 2), SymbolBlue), "the STARS track lost its blue circle");
        Assert.True(AnyPixel(bitmap, CellArea(eramTrack.Symbol, 6f, 7.5f), TargetYellow), "the ERAM track drew no target yellow");
        Assert.False(AnyPixel(bitmap, CellBox(eramTrack.Symbol), SymbolBlue), "a symbol-blue pixel in the ERAM glyph's cell");
    }

    [Fact]
    public void EramHistory_DrawsEachSampleAsTheGlyph_InUniformDimYellow()
    {
        using var pin = MapBlinkPin.Phase(true);
        Setup setup = Ldb with { HistoryCount = 3 };
        AircraftModel ac = CorrelatedBeacon();
        MapViewport vp = Viewport(setup);
        ac.PositionHistory =
        [
            .. HistoryOffsets.Select(d =>
            {
                (double lat, double lon) = vp.ScreenToLatLon((Width / 2f) - d, Height / 2f);
                return new[] { lat, lon };
            }),
        ];
        ScopeFrame frame = Frame(setup, ac);
        ScopeTrack track = Assert.Single(frame.Tracks);

        using SKBitmap bitmap = Render(frame, setup);

        IReadOnlyList<SKPoint> samples = Assert.IsAssignableFrom<IReadOnlyList<SKPoint>>(track.History);
        Assert.Equal(3, samples.Count);
        Assert.Equal([EramTargetGlyph.CorrelatedBeacon, EramTargetGlyph.CorrelatedBeacon, EramTargetGlyph.CorrelatedBeacon], track.HistoryGlyphs);
        foreach (SKPoint sample in samples)
        {
            // Two points on the sample's backslash, each the one dim yellow: no alpha ramp from the oldest to the newest.
            Assert.True(AnyPixel(bitmap, CellArea(sample, 6f, 7.5f), HistoryYellow), $"no history yellow at the centre of {sample}");
            Assert.True(AnyPixel(bitmap, CellArea(sample, 3.5f, 4.25f), HistoryYellow), $"no history yellow on the \\ of {sample}");
            Assert.False(AnyPixel(bitmap, CellBox(sample), SymbolBlue), $"a blue history dot at {sample}");
        }
    }

    [Fact]
    public void Leader_StartsSixPixelsOutFromTheTarget()
    {
        using var pin = MapBlinkPin.Phase(true);
        var setup = new Setup();
        ScopeFrame frame = Frame(setup, CorrelatedBeacon());
        ScopeTrack track = Assert.Single(frame.Tracks);
        Assert.True(track.ShowsLeader);

        using SKBitmap bitmap = Render(frame, setup);

        SKPoint end = ScopeRenderer.LeaderEnd(track);
        SKPoint start = Assert.NotNull(ScopeRenderer.EramLeaderStart(track));
        Assert.Equal(6f, SKPoint.Distance(track.Symbol, start), 3);
        Assert.Equal(SKPoint.Distance(track.Symbol, end), SKPoint.Distance(track.Symbol, start) + SKPoint.Distance(start, end), 3);

        // 1.5 px out along the leader's ray, inside the flat diamond and short of the leader's start: nothing drawn.
        float u = 1.5f / SKPoint.Distance(track.Symbol, end);
        Assert.False(
            AnyLit(bitmap, PixelAt(track.Symbol.X + ((end.X - track.Symbol.X) * u), track.Symbol.Y + ((end.Y - track.Symbol.Y) * u))),
            "the leader starts at the target"
        );
        int mx = (int)MathF.Round((start.X + end.X) / 2f);
        int my = (int)MathF.Round((start.Y + end.Y) / 2f);
        Assert.True(AnyPixel(bitmap, new SKRectI(mx - 1, my - 1, mx + 2, my + 2), FdbYellow), "the leader is not FDB yellow");
    }

    [Fact]
    public void LimitedBlock_DrawsInTheLdbYellow()
    {
        using var pin = MapBlinkPin.Phase(true);
        ScopeFrame frame = Frame(Ldb, CorrelatedBeacon());
        ScopeTrack track = Assert.Single(frame.Tracks);

        using SKBitmap bitmap = Render(frame, Ldb);

        SKRectI callsign = FieldPixels(track, ScopeFieldSlot.Callsign);
        Assert.True(AnyPixel(bitmap, callsign, LdbYellow), "no LDB yellow in the callsign");
        Assert.False(AnyPixel(bitmap, callsign, FdbYellow), "an FDB yellow pixel in a limited block");
        Assert.False(AnyPixel(bitmap, callsign, SKColors.White), "a white pixel in a limited block");
    }

    [Fact]
    public void FullBlock_DrawsInTheFdbYellow()
    {
        using var pin = MapBlinkPin.Phase(true);
        var setup = new Setup();
        ScopeFrame frame = Frame(setup, CorrelatedBeacon());
        ScopeTrack track = Assert.Single(frame.Tracks);

        using SKBitmap bitmap = Render(frame, setup);

        SKRectI callsign = FieldPixels(track, ScopeFieldSlot.Callsign);
        Assert.True(AnyPixel(bitmap, callsign, FdbYellow), "no FDB yellow in the callsign");
        Assert.False(AnyPixel(bitmap, callsign, SKColors.White), "a white pixel in an ERAM full block");
    }

    [Fact]
    public void CoastingTrack_DrawsTheHash_AndNoTargetGlyph()
    {
        using var pin = MapBlinkPin.Phase(true);
        var setup = new Setup();
        AircraftModel ac = CorrelatedBeacon();
        ac.EramIsCoastTrack = true;
        ScopeFrame frame = Frame(setup, ac);
        ScopeTrack track = Assert.Single(frame.Tracks);

        using SKBitmap bitmap = Render(frame, setup);

        // The hash's left bar spans cell x 3 to 5; cell (2, 2) lies on the backslash a target would draw and off the hash.
        Assert.True(AnyPixel(bitmap, CellArea(track.Symbol, 4f, 2.5f), FdbYellow), "no FDB-yellow hash bar");
        Assert.False(AnyLit(bitmap, PixelAt(CellOrigin(track.Symbol).X + 2f, CellOrigin(track.Symbol).Y + 2f)), "a target glyph drew");
        Assert.False(AnyPixel(bitmap, CellBox(track.Symbol), TargetYellow), "a target-yellow pixel while coasting");
    }

    [Theory]
    [InlineData(1000, 31.25f)]
    [InlineData(2400, 60f)]
    public void NewEramTrack_PlacesItsBlockNorthEast_AtTheLeaderLengthAndTheGap(int width, float length)
    {
        using var pin = MapBlinkPin.Phase(true);
        var setup = new Setup { ViewportWidth = width };
        ScopeFrame frame = Frame(setup, CorrelatedBeacon());
        ScopeTrack track = Assert.Single(frame.Tracks);

        // The leader runs from 6 px out to its end e, 6 px plus length 1 out, both on the NE ray (lane A2's a and e); the text
        // box's left sits 5 px past e and its top two row pitches above it, so e meets the block's third row.
        float u = 1f / MathF.Sqrt(2f);
        SKPoint start = Assert.NotNull(ScopeRenderer.EramLeaderStart(track));
        SKPoint end = ScopeRenderer.LeaderEnd(track);
        Assert.Equal(track.Symbol.X + (6f * u), start.X, 2);
        Assert.Equal(track.Symbol.Y - (6f * u), start.Y, 2);
        Assert.Equal(track.Symbol.X + ((6f + length) * u), end.X, 2);
        Assert.Equal(track.Symbol.Y - ((6f + length) * u), end.Y, 2);
        Assert.Equal(end.X + 5f, track.Block.TextBounds.Left, 2);
        Assert.Equal(end.Y - (2f * RowPitch), track.Block.TextBounds.Top, 2);
    }

    [Fact]
    public void EramLimitedBlock_SitsThreePixelsRightOfTheGlyph_WithNoLeader()
    {
        using var pin = MapBlinkPin.Phase(true);
        ScopeTrack track = Assert.Single(Frame(Ldb, CorrelatedBeacon()).Tracks);
        Assert.Equal(ScopeBlockLevel.Partial, track.Level);

        // Lane A2: the text 3 px right of the 12 px cell, its first baseline (the first row's field bottom) on the cell's bottom.
        SKPoint cell = CellOrigin(track.Symbol);
        Assert.False(track.ShowsLeader);
        Assert.Equal(cell.X + 12f + 3f, track.Block.TextBounds.Left, 2);
        Assert.Equal(cell.Y + 15f, Assert.Single(track.Block.Fields, f => f.Slot == ScopeFieldSlot.Callsign).Rect.Bottom, 2);
    }

    [Fact]
    public void FirstSeenCoasting_HistoryDrawsTheSteadyGlyph()
    {
        AircraftModel ac = WithHistory(CorrelatedBeacon(), 3);
        ac.EramIsCoastTrack = true;

        ScopeTrack track = HistoryTrack(ac, 3);

        Assert.Null(track.EramSymbols.Target);
        Assert.Equal([EramTargetGlyph.CorrelatedBeacon, EramTargetGlyph.CorrelatedBeacon, EramTargetGlyph.CorrelatedBeacon], track.HistoryGlyphs);
    }

    [Fact]
    public void FirstSeenIdenting_HistoryDrawsTheSteadyGlyph_AndTheTargetTheBars()
    {
        AircraftModel ac = WithHistory(CorrelatedBeacon(), 3);
        ac.IsIdenting = true;

        ScopeTrack track = HistoryTrack(ac, 3);

        Assert.Equal(EramTargetGlyph.Ident, track.EramSymbols.Target);
        Assert.Equal([EramTargetGlyph.CorrelatedBeacon, EramTargetGlyph.CorrelatedBeacon, EramTargetGlyph.CorrelatedBeacon], track.HistoryGlyphs);
    }

    [Fact]
    public void FirstSeenWithOneSampleWhileIdenting_HistoryDrawsTheBars()
    {
        AircraftModel ac = WithHistory(CorrelatedBeacon(), 1);
        ac.IsIdenting = true;

        ScopeTrack track = HistoryTrack(ac, 1);

        Assert.Equal(EramTargetGlyph.Ident, track.EramSymbols.Target);
        Assert.Equal([EramTargetGlyph.Ident], track.HistoryGlyphs);
    }

    [Fact]
    public void FirstSeenWithOneSampleWhileCoasting_HistoryDrawsNothing()
    {
        AircraftModel ac = WithHistory(CorrelatedBeacon(), 1);
        ac.EramIsCoastTrack = true;

        ScopeTrack track = HistoryTrack(ac, 1);

        Assert.Null(track.EramSymbols.Target);
        Assert.Equal([(EramTargetGlyph?)null], track.HistoryGlyphs);
    }

    [Fact]
    public void FirstSeenSteady_HistoryDrawsItsGlyph()
    {
        AircraftModel ac = WithHistory(CorrelatedBeacon(), 3);
        ac.BeaconCode = EramSymbolState.VfrCode;

        ScopeTrack track = HistoryTrack(ac, 3);

        Assert.Equal(EramTargetGlyph.Vfr, track.EramSymbols.Target);
        Assert.Equal([EramTargetGlyph.Vfr, EramTargetGlyph.Vfr, EramTargetGlyph.Vfr], track.HistoryGlyphs);
    }

    [Fact]
    public void HistRaisedFromZero_SeedsTheSteadyGlyph()
    {
        AircraftModel ac = WithHistory(CorrelatedBeacon(), 3);
        ac.IsIdenting = true;
        HistoryTrack(ac, 0);

        ScopeTrack track = HistoryTrack(ac, 3);

        Assert.Equal([EramTargetGlyph.CorrelatedBeacon, EramTargetGlyph.CorrelatedBeacon, EramTargetGlyph.CorrelatedBeacon], track.HistoryGlyphs);
    }

    [Fact]
    public void TemplateSwitchAwayAndBack_ReseedsTheTrailWithTheSteadyGlyph()
    {
        AircraftModel ac = WithHistory(CorrelatedBeacon(), 3);
        using var pin = MapBlinkPin.Phase(true);
        Frame(new Setup { HistoryCount = 3 }, ac);
        Frame(new Setup { TemplateId = ScopeTemplates.StarsId, HistoryCount = 3 }, ac);
        ac.IsIdenting = true;
        AddNewestSample(ac);

        ScopeTrack track = Assert.Single(Frame(new Setup { HistoryCount = 3 }, ac).Tracks);

        Assert.Equal(EramTargetGlyph.Ident, track.EramSymbols.Target);
        Assert.Equal([EramTargetGlyph.CorrelatedBeacon, EramTargetGlyph.CorrelatedBeacon, EramTargetGlyph.CorrelatedBeacon], track.HistoryGlyphs);
    }

    [Fact]
    public void GlyphChangeMidTrail_KeepsTheOlderSamplesGlyphs_AndGivesNewSamplesTheNewGlyph()
    {
        AircraftModel ac = WithHistory(CorrelatedBeacon(), 3);
        HistoryTrack(ac, 3);
        ac.IsIdenting = true;
        AddNewestSample(ac);

        ScopeTrack track = HistoryTrack(ac, 3);

        Assert.Equal([EramTargetGlyph.CorrelatedBeacon, EramTargetGlyph.CorrelatedBeacon, EramTargetGlyph.Ident], track.HistoryGlyphs);
    }

    [Fact]
    public void Coast_SuppressesTheCurrentGlyph_AndKeepsTheHistory()
    {
        AircraftModel ac = WithHistory(CorrelatedBeacon(), 3);
        HistoryTrack(ac, 3);
        ac.EramIsCoastTrack = true;
        AddNewestSample(ac);

        ScopeTrack track = HistoryTrack(ac, 3);

        Assert.Null(track.EramSymbols.Target);
        Assert.Equal([EramTargetGlyph.CorrelatedBeacon, EramTargetGlyph.CorrelatedBeacon, null], track.HistoryGlyphs);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    public void HistoryGlyphs_FollowTheHistCount(int hist)
    {
        AircraftModel ac = WithHistory(CorrelatedBeacon(), 5);

        ScopeTrack track = HistoryTrack(ac, hist);

        Assert.Equal(hist, Assert.IsAssignableFrom<IReadOnlyList<SKPoint>>(track.History).Count);
        Assert.Equal(hist, Assert.IsAssignableFrom<IReadOnlyList<EramTargetGlyph?>>(track.HistoryGlyphs).Count);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(18)]
    [InlineData(19)]
    public void CaseTableRow_EramLookSymbolsAndHistoryGlyphsAgree(int row)
    {
        EramSymbolStateTests.Case c = EramSymbolStateTests.Rows[row];
        AircraftModel ac = WithHistory(EramSymbolStateTests.ModelOf(c), 3);
        EramTargetGlyph? steady = EramSymbolState.TargetOf(
            EramSymbolStateTests.ModelOf(c with { Ident = false, Status = (c.Status == "frozen") ? "frozen" : "" })
        );

        ScopeTrack track = HistoryTrack(ac, 3);

        Assert.True(track.EramLook);
        Assert.Equal(EramSymbolState.Of(ac, track.Level), track.EramSymbols);
        Assert.Equal(c.Target, track.EramSymbols.Target);
        IReadOnlyList<EramTargetGlyph?> glyphs = Assert.IsAssignableFrom<IReadOnlyList<EramTargetGlyph?>>(track.HistoryGlyphs);
        Assert.Equal(Assert.IsAssignableFrom<IReadOnlyList<SKPoint>>(track.History).Count, glyphs.Count);
        Assert.All(glyphs, g => Assert.Equal(steady, g));
    }

    /// <summary>
    /// At 150 % the glyphs scale with the 12 x 15 cell: the V's stem, the + and the ident bars (filled) and the D's bar (a stroke)
    /// all render 3 device pixels wide, 2 px at 100 % times the scale.
    /// </summary>
    [Fact]
    public void AtScaleOneAndAHalf_GlyphBarsAndStrokesRenderThreePixelsWide()
    {
        using var pin = MapBlinkPin.Phase(true);
        AircraftModel vfr = CorrelatedBeacon();
        vfr.BeaconCode = EramSymbolState.VfrCode;
        AircraftModel primary = CorrelatedBeacon();
        primary.TransponderMode = "Standby";
        primary.EramIsCorrelated = false;
        AircraftModel ident = CorrelatedBeacon();
        ident.IsIdenting = true;
        AircraftModel free = CorrelatedBeacon();
        free.EramIsCorrelated = false;

        Assert.Equal(3, ScaledRun(vfr, Ldb, (6f, 12f), horizontal: true));
        Assert.Equal(3, ScaledRun(primary, Ldb, (6f, 3f), horizontal: true));
        Assert.Equal(3, ScaledRun(primary, Ldb, (3f, 8f), horizontal: false));
        Assert.Equal(3, ScaledRun(ident, Ldb, (6f, 3f), horizontal: false));
        Assert.Equal(3, ScaledRun(free, new Setup(), (10f, 10f), horizontal: true));
    }

    // Draws `ac` alone at 150 % and counts the lit device pixels in a row (or column) through cell point `at` of its glyph.
    private static int ScaledRun(AircraftModel ac, Setup setup, (float X, float Y) at, bool horizontal)
    {
        const float scale = 1.5f;
        ScopeFrame frame = Frame(setup, ac);
        SKPoint o = CellOrigin(Assert.Single(frame.Tracks).Symbol);
        using SKBitmap bitmap = Render(frame, setup, scale);
        int x = (int)MathF.Floor((o.X + at.X) * scale);
        int y = (int)MathF.Floor((o.Y + at.Y) * scale);
        (int dx, int dy) = horizontal ? (1, 0) : (0, 1);
        Assert.True(IsLit(bitmap, x, y), $"nothing drawn at cell point {at}");
        int run = 1;
        for (int i = 1; IsLit(bitmap, x + (i * dx), y + (i * dy)); i++)
        {
            run++;
        }

        for (int i = 1; IsLit(bitmap, x - (i * dx), y - (i * dy)); i++)
        {
            run++;
        }

        return run;
    }

    private static bool IsLit(SKBitmap bitmap, int x, int y)
    {
        SKColor c = bitmap.GetPixel(x, y);
        return (c.Red | c.Green | c.Blue) != 0;
    }

    /// <summary><paramref name="ac"/> with <paramref name="count"/> history samples 30 px apart west of its target, oldest first.</summary>
    private static AircraftModel WithHistory(AircraftModel ac, int count)
    {
        MapViewport vp = Viewport(new Setup());
        ac.PositionHistory =
        [
            .. Enumerable
                .Range(1, count)
                .Reverse()
                .Select(k =>
                {
                    (double lat, double lon) = vp.ScreenToLatLon((Width / 2f) - (30f * k) - 30f, Height / 2f);
                    return new[] { lat, lon };
                }),
        ];
        return ac;
    }

    /// <summary>Adds a sample newer than every one <paramref name="ac"/> has, dropping its oldest.</summary>
    private static void AddNewestSample(AircraftModel ac)
    {
        MapViewport vp = Viewport(new Setup());
        (double lat, double lon) = vp.ScreenToLatLon((Width / 2f) - 30f, Height / 2f);
        IReadOnlyList<double[]>? history = ac.PositionHistory;
        Assert.NotNull(history);
        ac.PositionHistory = [.. history.Skip(1), [lat, lon]];
    }

    /// <summary><paramref name="ac"/>'s track in an ERAM frame with HIST at <paramref name="hist"/>.</summary>
    private static ScopeTrack HistoryTrack(AircraftModel ac, int hist)
    {
        using var pin = MapBlinkPin.Phase(true);
        return Assert.Single(Frame(new Setup { HistoryCount = hist }, ac).Tracks);
    }

    [Fact]
    public void EramBlockInTheNorthSlot_LeaderEndsFivePixelsUnderTheBox_WithoutCrossingIt() => AssertLeaderClearsBoxOverOrUnder(above: true);

    [Fact]
    public void EramBlockDraggedBelowItsTarget_LeaderEndsFivePixelsOverTheBox_WithoutCrossingIt() => AssertLeaderClearsBoxOverOrUnder(above: false);

    // Places a full block centred over (above) or under (below) its target, 18 px off it as the N compass slot does, and asserts
    // the leader ends 5 px off the box's near edge, at the target's x, with no point of the segment inside the text box.
    private static void AssertLeaderClearsBoxOverOrUnder(bool above)
    {
        using var pin = MapBlinkPin.Phase(true);
        AircraftModel probe = CorrelatedBeacon();
        ScopeTrack plain = Assert.Single(Frame(new Setup(), probe).Tracks);
        SKPoint origin = plain.Symbol + plain.BlockOffset;
        SKRect atOrigin = plain.Block.TextBounds;
        atOrigin.Offset(-origin.X, -origin.Y);
        float top = above ? -18f - atOrigin.Height : 18f;
        var offset = new SKPoint((-atOrigin.Width / 2f) - atOrigin.Left, top - atOrigin.Top);
        AircraftModel ac = CorrelatedBeacon();
        var setup = new Setup { Manual = new Dictionary<string, SKPoint> { [ac.Callsign] = offset } };

        ScopeTrack track = Assert.Single(Frame(setup, ac).Tracks);

        SKRect box = track.Block.TextBounds;
        SKPoint start = Assert.NotNull(ScopeRenderer.EramLeaderStart(track));
        SKPoint end = ScopeRenderer.LeaderEnd(track);
        Assert.Equal(track.Symbol.X, end.X, 2);
        Assert.Equal(above ? box.Bottom + 5f : box.Top - 5f, end.Y, 2);
        for (int i = 0; i <= 100; i++)
        {
            float t = i / 100f;
            float x = start.X + ((end.X - start.X) * t);
            float y = start.Y + ((end.Y - start.Y) * t);
            Assert.False(box.Contains(x, y), $"the leader crosses the text box at ({x}, {y})");
        }
    }

    [Fact]
    public void DraggedEramBlock_KeepsItsOffset()
    {
        using var pin = MapBlinkPin.Phase(true);
        AircraftModel ac = CorrelatedBeacon();
        var dragged = new SKPoint(40f, 10f);
        var setup = new Setup { Manual = new Dictionary<string, SKPoint> { [ac.Callsign] = dragged } };

        ScopeTrack track = Assert.Single(Frame(setup, ac).Tracks);

        Assert.Equal(dragged, track.BlockOffset);
    }

    /// <summary>
    /// Renders the YAAT-2683 mock's Mixed scene (two STARS tracks and the six ERAM states) and checks its history glyphs; with
    /// <c>YAAT_SAVE_RENDERS=1</c> it also writes the render to <c>.tmp/eram-look.png</c> under the repo root, to compare by eye
    /// with lane A2.
    /// </summary>
    [Fact]
    public void MixedScene_KeepsEachSamplesHistoryGlyph_AndDrawsEveryTrack_SavingThePngOnlyOnRequest()
    {
        using var pin = MapBlinkPin.Phase(true);
        var setup = new Setup
        {
            TemplateId = ScopeTemplates.MixedId,
            OthersLevel = ScopeProfile.OthersPartial,
            Viewer = Viewer,
            HistoryCount = 5,
            ViewportWidth = 720,
            ViewportHeight = 380,
        };
        MapViewport vp = Viewport(setup);
        var scene = EramMockScene.Build(vp);

        // The first frame records the older history samples' glyphs before the newest arrive in their new state.
        Frame(setup, [.. scene.Aircraft]);
        scene.Advance(vp);
        ScopeFrame frame = Frame(setup, [.. scene.Aircraft]);
        using SKBitmap bitmap = Render(frame, setup);

        // SKW5410's newest sample idents and AAL88's two newest coast; their older samples keep the beacon they had.
        EramTargetGlyph b = EramTargetGlyph.CorrelatedBeacon;
        Assert.Equal([b, b, b, b, EramTargetGlyph.Ident], TrackOf(frame, "SKW5410").HistoryGlyphs);
        Assert.Equal([b, b, b, null, null], TrackOf(frame, "AAL88").HistoryGlyphs);
        Assert.Equal(8, frame.Tracks.Count);
        if (Environment.GetEnvironmentVariable(SaveRendersVariable) != "1")
        {
            return;
        }

        string path = Path.Combine(RepoRoot(), ".tmp", "eram-look.png");
        string? dir = Path.GetDirectoryName(path);
        Assert.NotNull(dir);
        Directory.CreateDirectory(dir);
        using (SKData png = bitmap.Encode(SKEncodedImageFormat.Png, 100))
        {
            File.WriteAllBytes(path, png.ToArray());
        }

        Assert.True(File.Exists(path));
    }

    private static string RepoRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "yaat.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("no yaat.slnx above the test's base directory");
    }

    /// <summary>The mock's Mixed scene: each track at its screen point, with five history samples back along its heading.</summary>
    private sealed class EramMockScene
    {
        private readonly List<(AircraftModel Ac, float X, float Y, float Hdg, float Speed, int Gap)> _tracks = [];

        public IEnumerable<AircraftModel> Aircraft => _tracks.Select(t => t.Ac);

        public static EramMockScene Build(MapViewport vp)
        {
            var scene = new EramMockScene();
            scene.Add(vp, Stars("SWA1832", "2S"), (110, 100, 270, 14), 0);
            scene.Add(vp, Stars("DAL412", "3E"), (330, 335, 60, 14), 0);
            scene.Add(vp, Eram("UAL123", "C44", "KORD", true), (150, 255, 90, 18), 0);
            scene.Add(vp, Eram("SKW5410", "C44", "KDEN", true), (430, 160, 160, 16), 1);
            AircraftModel aal = Eram("AAL88", "C44", "KDFW", true);
            scene.Add(vp, aal, (470, 290, 300, 16), 2);
            AircraftModel vfr = Eram("N512RK", null, "", false);
            vfr.BeaconCode = 1200;
            scene.Add(vp, vfr, (80, 300, 10, 12), 0);
            scene.Add(vp, Eram("N4621", null, "", false), (330, 75, 120, 15), 0);
            AircraftModel primary = Eram("N88PR", null, "", false);
            primary.TransponderMode = "Standby";
            scene.Add(vp, primary, (695, 330, 190, 16), 0);
            return scene;
        }

        /// <summary>
        /// The newest samples arrive: SKW5410 starts to ident with its newest sample, AAL88 starts to coast with its two newest.
        /// </summary>
        public void Advance(MapViewport vp)
        {
            foreach ((AircraftModel ac, float x, float y, float hdg, float speed, int _) in _tracks)
            {
                ac.PositionHistory = HistoryOf(vp, (x, y, hdg, speed), 0);
            }

            _tracks.Single(t => t.Ac.Callsign == "SKW5410").Ac.IsIdenting = true;
            _tracks.Single(t => t.Ac.Callsign == "AAL88").Ac.EramIsCoastTrack = true;
        }

        private void Add(MapViewport vp, AircraftModel ac, (float X, float Y, float Hdg, float Speed) at, int held)
        {
            (double lat, double lon) = vp.ScreenToLatLon(at.X, at.Y);
            ac.Position = new LatLon(lat, lon);
            ac.TrueTrack = at.Hdg;
            ac.PositionHistory = HistoryOf(vp, at, held);
            _tracks.Add((ac, at.X, at.Y, at.Hdg, at.Speed, held));
        }

        // The samples k = 1 to 7 back along the heading, oldest first, without the newest `held` ones.
        private static List<double[]> HistoryOf(MapViewport vp, (float X, float Y, float Hdg, float Speed) at, int held)
        {
            float back = (at.Hdg + 180f) * MathF.PI / 180f;
            var samples = new List<double[]>();
            for (int k = 7; k > held; k--)
            {
                (double lat, double lon) = vp.ScreenToLatLon(at.X + (MathF.Sin(back) * at.Speed * k), at.Y - (MathF.Cos(back) * at.Speed * k));
                samples.Add([lat, lon]);
            }

            return samples;
        }

        private static AircraftModel Stars(string callsign, string sector)
        {
            AircraftModel ac = ScopeTestAircraft.CreateModel();
            ac.Callsign = callsign;
            ac.Owner = "OAK_APP";
            ac.OwnerFacilityId = "NCT";
            ac.OwnerSectorCode = sector;
            return ac;
        }

        private static AircraftModel Eram(string callsign, string? sector, string destination, bool correlated)
        {
            AircraftModel ac = ScopeTestAircraft.CreateModel();
            ac.Callsign = callsign;
            ac.BeaconCode = 4521;
            ac.OwnerFacilityId = (sector is null) ? null : "ZOA";
            ac.OwnerSectorCode = sector;
            ac.Destination = destination;
            ac.EramIsCorrelated = correlated;
            return ac;
        }
    }
}
