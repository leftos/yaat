using Avalonia.Headless.XUnit;
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
/// Pins realistic Scope text: each track lays out and draws with its owner kind's text (the STARS pair or the ERAM pair), a
/// realistic kind measures every glyph at its strike's glyph pitch and pitches its rows at CRC's row pitch, a compact kind
/// stays as today, and bubbles stay compact whatever the style.
/// </summary>
[Collection(MapBlinkClockCollection.Name)]
public sealed class ScopeRealisticTextTests : IDisposable
{
    // The preferences files PrivatePreferences made for this test, deleted when it ends.
    private readonly List<string> _privateFiles = [];

    public void Dispose()
    {
        foreach (string path in _privateFiles)
        {
            File.Delete(path);
        }
    }

    private const float CompactSize = 12f;
    private const int Width = 1000;
    private const int Height = 800;

    // CRC's ERAM full-block yellow at the default brightness groups, duplicated rather than exposed.
    private static readonly SKColor FdbYellow = new(196, 196, 0);

    private static readonly ScopePalette NoTints = new(null, null, SKColors.White);

    private static MapViewport Viewport() =>
        new()
        {
            PixelWidth = Width,
            PixelHeight = Height,
            CenterLat = 37.0,
            CenterLon = -122.0,
        };

    // Compact metrics measure with this font for as long as a frame built from them is read, so it lives as long as the class.
    private static readonly SKFont CompactFont = PlatformHelper.MonospaceFontBold(CompactSize);
    private static readonly SKPaint CompactPaint = new();

    private static ScopeTextMetrics Compact() => ScopeTextMetrics.FromStyle(new TextStyle(CompactFont, CompactPaint));

    private static ScopeFrame Frame(string templateId, ScopeTextMetrics stars, ScopeTextMetrics? eram, params AircraftModel[] aircraft) =>
        ScopeFrame.Build(Input(templateId, stars, eram, aircraft));

    private static ScopeFrameInput Input(string templateId, ScopeTextMetrics stars, ScopeTextMetrics? eram, params AircraftModel[] aircraft) =>
        new(
            aircraft,
            null,
            Viewport(),
            templateId,
            stars,
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
            IntentPaths: new ScopeIntentPathCache(),
            EramMetrics: eram
        );

    /// <summary>A STARS-owned track (owner sector 2S) at screen (300, 400).</summary>
    private static AircraftModel StarsOwned()
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.Callsign = "SWA1832";
        ac.OwnerSectorCode = "2S";
        (double lat, double lon) = Viewport().ScreenToLatLon(300, 400);
        ac.Position = new LatLon(lat, lon);
        return ac;
    }

    /// <summary>An ERAM-owned track (no owner sector) at the centre.</summary>
    private static AircraftModel EramOwned()
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.BeaconCode = 4521;
        ac.EramIsCorrelated = true;
        ac.Destination = "KORD";
        return ac;
    }

    /// <summary>An ERAM-owned track climbing to 300 at 253 on Mode C.</summary>
    private static AircraftModel Climbing()
    {
        AircraftModel ac = EramOwned();
        ac.EramPlanAltitudeFeet = 30000;
        ac.Altitude = 25300;
        return ac;
    }

    /// <summary>An ERAM-owned track descending to 230 from 253 on Mode C.</summary>
    private static AircraftModel Descending()
    {
        AircraftModel ac = EramOwned();
        ac.EramPlanAltitudeFeet = 23000;
        ac.Altitude = 25300;
        return ac;
    }

    /// <summary>The block's ERAM altitude field, the one carrying the climb or descend arrow.</summary>
    private static FieldRect ArrowField(ScopeFrame frame) =>
        Assert.Single(Assert.Single(frame.Tracks).Block.Fields, f => (f.Style & (ScopeFieldStyle.ArrowUp | ScopeFieldStyle.ArrowDown)) != 0);

    /// <summary>The index of the blank arrow cell in the ERAM altitude field's text.</summary>
    private static int ArrowCell(FieldRect field)
    {
        int cell = field.Text.IndexOf(TrackPresenter.EramArrowCell, StringComparison.Ordinal);
        Assert.True(cell >= 0, $"field '{field.Text}' holds no arrow cell");
        return cell;
    }

    /// <summary>
    /// Asserts the arrow cell of <paramref name="field"/> drew as the <paramref name="strikePx"/> Terminus strike's own
    /// <paramref name="glyph"/> at the cell's origin, aliased in the block colour: every pixel of the cell's box is lit exactly
    /// where a direct draw of the glyph lights it, at <see cref="FdbYellow"/> and black everywhere else.
    /// </summary>
    private static void AssertRealisticCellDrawsGlyph(SKBitmap rendered, FieldRect field, string glyph, int strikePx, float rowPitch)
    {
        float pitch = ScopeTextFonts.GlyphPitch(strikePx);
        float left = field.Rect.Left + (ArrowCell(field) * pitch);
        using SKBitmap mask = ArrowMask(field, glyph, strikePx);
        SKRectI box = CellBox(left, pitch, field.Rect.Bottom, rowPitch);

        List<SKPointI> lit = PointsWhere(rendered, box, c => c != SKColors.Black);
        Assert.NotEmpty(lit);
        Assert.All(lit, p => Assert.Equal(FdbYellow, rendered.GetPixel(p.X, p.Y)));
        List<SKPointI> want = PointsWhere(mask, box, c => c == SKColors.White);
        List<SKPointI> extra = [.. lit.Except(want)];
        List<SKPointI> missing = [.. want.Except(lit)];
        Assert.True(
            (extra.Count == 0) && (missing.Count == 0),
            $"the arrow cell {box} is not the {strikePx} px strike's '{glyph}': extra {string.Join(" ", extra.Take(4))},"
                + $" missing {string.Join(" ", missing.Take(4))}"
        );
    }

    /// <summary>
    /// The box one field cell occupies on the frame: its columns from <paramref name="left"/> for <paramref name="width"/>, and
    /// the rows of its own line above the baseline, stopping <paramref name="rowPitch"/> up at the line above's baseline.
    /// </summary>
    private static SKRectI CellBox(float left, float width, float baseline, float rowPitch) =>
        new((int)MathF.Round(left), (int)MathF.Floor(baseline - rowPitch) + 2, (int)MathF.Round(left + width), (int)MathF.Floor(baseline) + 3);

    /// <summary>
    /// What the block should draw at the <paramref name="strikePx"/> strike: the field's own text glyph by glyph at the strike's
    /// pitch, then <paramref name="glyph"/> over its arrow cell — the aliased strike's own bitmaps, so a neighbouring glyph's
    /// one-pixel-wider bitmap into the cell reads the same here as on the frame.
    /// </summary>
    private static SKBitmap ArrowMask(FieldRect field, string glyph, int strikePx)
    {
        float pitch = ScopeTextFonts.GlyphPitch(strikePx);
        var mask = new SKBitmap(Width, Height);
        using var canvas = new SKCanvas(mask);
        canvas.Clear(SKColors.Black);
        using SKFont font = ScopeTextFonts.MakeFont(strikePx);
        using var paint = new SKPaint { Color = SKColors.White, IsAntialias = false };
        for (int i = 0; i < field.Text.Length; i++)
        {
            canvas.DrawText(field.Text[i].ToString(), field.Rect.Left + (i * pitch), field.Rect.Bottom, SKTextAlign.Left, font, paint);
        }

        canvas.DrawText(glyph, field.Rect.Left + (ArrowCell(field) * pitch), field.Rect.Bottom, SKTextAlign.Left, font, paint);
        return mask;
    }

    /// <summary>
    /// The points of <paramref name="box"/> where <paramref name="bitmap"/>'s pixel satisfies <paramref name="predicate"/>,
    /// row by row.
    /// </summary>
    private static List<SKPointI> PointsWhere(SKBitmap bitmap, SKRectI box, Func<SKColor, bool> predicate)
    {
        List<SKPointI> points = [];
        for (int y = box.Top; y < box.Bottom; y++)
        {
            for (int x = box.Left; x < box.Right; x++)
            {
                if (predicate(bitmap.GetPixel(x, y)))
                {
                    points.Add(new SKPointI(x, y));
                }
            }
        }
        return points;
    }

    private static ScopeTrack TrackOf(ScopeFrame frame, string callsign) => Assert.Single(frame.Tracks, t => t.Aircraft.Callsign == callsign);

    private static FieldRect CallsignOf(ScopeTrack track) => Assert.Single(track.Block.Fields, f => f.Slot == ScopeFieldSlot.Callsign);

    /// <summary>The block's row pitch: the gap between its first two distinct field baselines.</summary>
    private static float RowPitchOf(ScopeTrack track)
    {
        float[] baselines = [.. track.Block.Fields.Select(f => f.Rect.Bottom).Distinct().Order()];
        return baselines[1] - baselines[0];
    }

    /// <summary>Asserts the callsign field is its text's length of glyph pitches wide and every field a whole number of them.</summary>
    private static void AssertGlyphPitch(ScopeTrack track, float pitch)
    {
        FieldRect callsign = CallsignOf(track);
        Assert.Equal(callsign.Text.Length * pitch, callsign.Rect.Width, 3);
        foreach (FieldRect field in track.Block.Fields.Where(f => f.Rect.Width > 0))
        {
            float cells = field.Rect.Width / pitch;
            Assert.True(MathF.Abs(cells - MathF.Round(cells)) < 0.001f, $"field {field.Slot} '{field.Text}' is {field.Rect.Width} px wide");
        }
    }

    [Fact]
    public void MixedFrame_EachKindLaysOutWithItsOwnRealisticSize()
    {
        ScopeFrame frame = Frame(ScopeTemplates.MixedId, ScopeTextFonts.StarsMetrics(3), ScopeTextFonts.EramMetrics(1), StarsOwned(), EramOwned());
        ScopeTrack eram = TrackOf(frame, "UAL238");
        ScopeTrack stars = TrackOf(frame, "SWA1832");
        Assert.True(eram.EramLook);
        Assert.False(stars.EramLook);

        Assert.Equal(16f, RowPitchOf(eram), 3);
        Assert.Equal(16f, eram.EramRowPitch, 3);
        AssertGlyphPitch(eram, 8f);
        // STARS size 3: CRC's line height 12 plus spacing 3, on the 18 px strike's 10 px cell.
        Assert.Equal(15f, RowPitchOf(stars), 3);
        AssertGlyphPitch(stars, 10f);
    }

    [Fact]
    public void MixedFrame_ACompactStarsKind_LaysOutAsAnAllCompactFrame()
    {
        ScopeTextMetrics compact = Compact();
        ScopeFrame mixed = Frame(ScopeTemplates.MixedId, compact, ScopeTextFonts.EramMetrics(1), StarsOwned(), EramOwned());
        ScopeFrame allCompact = Frame(ScopeTemplates.MixedId, compact, null, StarsOwned(), EramOwned());

        ScopeTrack stars = TrackOf(mixed, "SWA1832");
        ScopeTrack reference = TrackOf(allCompact, "SWA1832");
        Assert.Equal(reference.Block.Bounds, stars.Block.Bounds);
        Assert.Equal(reference.Block.Fields.Select(f => (f.Slot, f.Rect)), stars.Block.Fields.Select(f => (f.Slot, f.Rect)));
        Assert.NotEqual(RowPitchOf(TrackOf(allCompact, "UAL238")), RowPitchOf(TrackOf(mixed, "UAL238")));
    }

    [Theory]
    [InlineData(0, 15f, 6f)]
    [InlineData(2, 18f, 10f)]
    [InlineData(4, 22f, 12f)]
    public void EramRealistic_RowPitchAndGlyphPitchFollowTheLadder(int size, float rowPitch, float glyphPitch)
    {
        ScopeFrame frame = Frame(ScopeTemplates.EramId, Compact(), ScopeTextFonts.EramMetrics(size), EramOwned());
        ScopeTrack track = Assert.Single(frame.Tracks);

        Assert.Equal(rowPitch, RowPitchOf(track), 3);
        Assert.Equal(rowPitch, track.EramRowPitch, 3);
        AssertGlyphPitch(track, glyphPitch);
    }

    [Fact]
    public void EramRealistic_TheDefaultLeaderOffsetUsesTheRealisticRowPitch()
    {
        ScopeFrame frame = Frame(ScopeTemplates.EramId, Compact(), ScopeTextFonts.EramMetrics(1), EramOwned());
        ScopeTrack track = Assert.Single(frame.Tracks);

        SKPoint origin = new(track.Symbol.X + track.BlockOffset.X, track.Symbol.Y + track.BlockOffset.Y);
        SKRect textAtOrigin = track.Block.TextBounds;
        textAtOrigin.Offset(-origin.X, -origin.Y);
        SKPoint expected = ScopeFrame.EramDefaultOffset(Width, textAtOrigin, 16f);
        Assert.Equal(expected.X, track.BlockOffset.X, 3);
        Assert.Equal(expected.Y, track.BlockOffset.Y, 3);
    }

    [AvaloniaFact]
    public void EramRealistic_FdbLineMiddleY_AgreesWithTheLaidOutLines()
    {
        UserPreferences prefs = PrivatePreferences();
        prefs.SetScopeText(ScopeTextStyle.Compact, 2, ScopeTextStyle.Realistic, 1);
        ScopeCanvas canvas = CanvasWith(prefs, EramOwned());
        try
        {
            ScopeTrack track = Assert.Single(canvas.CurrentFrame.Tracks);
            FieldRect callsign = CallsignOf(track);
            double line1 = Assert.IsType<double>(canvas.FdbLineMiddleY(track.Aircraft.Callsign, 1));
            double line2 = Assert.IsType<double>(canvas.FdbLineMiddleY(track.Aircraft.Callsign, 2));

            Assert.Equal(16.0, line2 - line1, 3);
            Assert.Equal(16f, RowPitchOf(track), 3);
            Assert.InRange(line1, callsign.Rect.Top, callsign.Rect.Bottom);
        }
        finally
        {
            canvas.Dispose();
        }
    }

    [Fact]
    public void EramRealistic_TheCallsignDrawsTheStrikeGlyphByGlyphAtTheCellPitch()
    {
        using var pin = MapBlinkPin.Phase(true);
        ScopeFrame frame = Frame(ScopeTemplates.EramId, Compact(), ScopeTextFonts.EramMetrics(1), EramOwned());
        ScopeTrack track = Assert.Single(frame.Tracks);
        FieldRect callsign = CallsignOf(track);

        using SKBitmap rendered = Render(frame, starsStrikePx: null, eramStrikePx: 14);

        AssertDrawnGlyphByGlyph(rendered, callsign, 14, FdbYellow);
    }

    [Fact]
    public void StarsRealistic_TheCallsignDrawsTheStrikeGlyphByGlyphAtTheCellPitch()
    {
        using var pin = MapBlinkPin.Phase(true);
        ScopeFrame frame = Frame(ScopeTemplates.MixedId, ScopeTextFonts.StarsMetrics(2), Compact(), StarsOwned(), EramOwned());
        FieldRect callsign = CallsignOf(TrackOf(frame, "SWA1832"));

        using SKBitmap rendered = Render(frame, starsStrikePx: 14, eramStrikePx: null);

        // STARS size 2 draws the 14 px strike at its 8 px cell; the block's colour is read off the drawn callsign.
        AssertDrawnGlyphByGlyph(rendered, callsign, 14, color: null);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Realistic_ClimbArrow_DrawsAsTheTerminusGlyph(int size) => AssertRealisticArrow(size, climbing: true);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Realistic_DescendArrow_DrawsAsTheTerminusGlyph(int size) => AssertRealisticArrow(size, climbing: false);

    /// <summary>
    /// Asserts a climbing (or descending) track's arrow cell at ERAM text size <paramref name="size"/> draws its own strike's
    /// Terminus glyph: the strike carries U+2191 and U+2193, and the cell's pixels are exactly a direct draw of the glyph.
    /// </summary>
    private static void AssertRealisticArrow(int size, bool climbing)
    {
        using var pin = MapBlinkPin.Phase(true);
        int strikePx = ScopeTextFonts.EramStrikePx[size];
        using (SKFont strikeFont = ScopeTextFonts.MakeFont(strikePx))
        {
            Assert.True(strikeFont.ContainsGlyphs("↑↓"), $"the {strikePx} px Terminus strike carries no arrow glyph");
        }

        ScopeFrame frame = Frame(ScopeTemplates.EramId, Compact(), ScopeTextFonts.EramMetrics(size), climbing ? Climbing() : Descending());
        FieldRect altitude = ArrowField(frame);
        Assert.Equal(climbing ? ScopeFieldStyle.ArrowUp : ScopeFieldStyle.ArrowDown, altitude.Style);

        using SKBitmap rendered = Render(frame, starsStrikePx: null, eramStrikePx: strikePx);

        AssertRealisticCellDrawsGlyph(rendered, altitude, climbing ? "↑" : "↓", strikePx, RowPitchOf(Assert.Single(frame.Tracks)));
    }

    [Fact]
    public void Compact_ClimbArrow_StillDrawsThePath()
    {
        using var pin = MapBlinkPin.Phase(true);
        ScopeFrame frame = Frame(ScopeTemplates.EramId, Compact(), null, Climbing());
        FieldRect altitude = ArrowField(frame);
        Assert.Equal(ScopeFieldStyle.ArrowUp, altitude.Style);
        int cell = ArrowCell(altitude);
        float left = altitude.Rect.Left + CompactFont.MeasureText(altitude.Text[..cell]);
        SKRectI box = CellBox(
            left,
            CompactFont.MeasureText(TrackPresenter.EramArrowCell),
            altitude.Rect.Bottom,
            RowPitchOf(Assert.Single(frame.Tracks))
        );

        using SKBitmap rendered = Render(frame, starsStrikePx: null, eramStrikePx: null);

        // The antialiased 1.2 px stroke lands at partial coverage, so the cell holds pixels short of the block colour.
        List<SKPointI> drawn = PointsWhere(rendered, box, c => c != SKColors.Black);
        Assert.NotEmpty(drawn);
        Assert.Contains(drawn, p => rendered.GetPixel(p.X, p.Y) != FdbYellow);
    }

    [AvaloniaFact]
    public void TheRenderSnapshot_CarriesEachKindsStrike_FromThePreferences()
    {
        UserPreferences prefs = PrivatePreferences();
        prefs.SetScopeText(ScopeTextStyle.Realistic, 3, ScopeTextStyle.Realistic, 1);
        ScopeCanvas canvas = CanvasWith(prefs, StarsOwned(), EramOwned());
        try
        {
            ScopeRenderSnapshot realistic = Assert.IsType<ScopeRenderSnapshot>(canvas.TryRenderSnapshotForTesting());
            Assert.Equal(18, realistic.StarsStrikePx);
            Assert.Equal(14, realistic.EramStrikePx);

            prefs.SetScopeText(ScopeTextStyle.Compact, 3, ScopeTextStyle.Compact, 1);
            ScopeRenderSnapshot compact = Assert.IsType<ScopeRenderSnapshot>(canvas.TryRenderSnapshotForTesting());
            Assert.Null(compact.StarsStrikePx);
            Assert.Null(compact.EramStrikePx);
        }
        finally
        {
            canvas.Dispose();
        }
    }

    [Fact]
    public void MixedFrame_TheCrrReadoutAndQueueBadge_MeasureWithTheTracksKind()
    {
        AircraftModel stars = StarsOwned();
        // One pending line past the active one, so each badge reads "1".
        stars.PendingCommands = "[Active] FH 270\n[1] AT BRIXX DM 040";
        AircraftModel eram = EramOwned();
        eram.PendingCommands = "[Active] FH 270\n[1] AT BRIXX DM 040";
        eram.EramCrrGroup = "A";
        ScopeFrameInput input = Input(ScopeTemplates.MixedId, Compact(), ScopeTextFonts.EramMetrics(1), stars, eram) with
        {
            LensQueue = true,
            CrrGroups = [new EramCrrGroupHubDto("A", "Coral", 37.05, -122.0)],
        };

        var frame = ScopeFrame.Build(input);

        ScopeTrack eramTrack = TrackOf(frame, "UAL238");
        ScopeCrrReadoutView crr = Assert.IsType<ScopeCrrReadoutView>(eramTrack.CrrReadout);
        Assert.Equal(crr.Text.Length * 8f, crr.Rect.Width, 3);
        ScopeQueueBadgeView eramBadge = Assert.IsType<ScopeQueueBadgeView>(eramTrack.QueueBadge);
        Assert.Equal(8f, eramBadge.Rect.Width, 3);
        ScopeQueueBadgeView starsBadge = Assert.IsType<ScopeQueueBadgeView>(TrackOf(frame, "SWA1832").QueueBadge);
        Assert.Equal(CompactFont.MeasureText("1"), starsBadge.Rect.Width, 3);
    }

    [Fact]
    public void EramRealisticSize4_DeconflictedBlocks_StayClearOfEveryTargetGlyph()
    {
        AircraftModel first = EramOwned();
        AircraftModel second = EramOwned();
        second.Callsign = "SWA1234";
        second.Position = new LatLon(37.0, -122.0005);
        ScopeFrameInput input = Input(ScopeTemplates.EramId, Compact(), ScopeTextFonts.EramMetrics(4), first, second);

        ScopeFrame frame = new ScopeDeconfliction().Build(DatablockDeconflictMode.CompassSnap, input, step: true);

        Assert.Equal(2, frame.Tracks.Count);
        Assert.All(frame.Tracks, t => Assert.Equal(22f, t.EramRowPitch, 3));
        foreach (ScopeTrack block in frame.Tracks)
        {
            foreach (ScopeTrack target in frame.Tracks)
            {
                SKPoint at = target.Symbol;
                var glyph = new SKRect(
                    at.X - EramGlyphs.HalfWidth,
                    at.Y - EramGlyphs.HalfHeight,
                    at.X + EramGlyphs.HalfWidth,
                    at.Y + EramGlyphs.HalfHeight
                );
                Assert.False(
                    block.Block.Bounds.IntersectsWith(glyph),
                    $"{block.Aircraft.Callsign}'s block {block.Block.Bounds} covers {target.Aircraft.Callsign}'s glyph {glyph}"
                );
            }
        }

        Assert.False(frame.Tracks[0].Block.Bounds.IntersectsWith(frame.Tracks[1].Block.Bounds), "the two blocks still overlap");
    }

    /// <summary>
    /// Asserts <paramref name="field"/>'s text drew as the <paramref name="strikePx"/> Terminus strike placed glyph by glyph at
    /// its glyph pitch from the field's left on its baseline: every pixel of the line's box is lit exactly where a direct
    /// per-glyph draw lights it, in <paramref name="color"/>, or with null in the one colour the drawn text has.
    /// </summary>
    private static void AssertDrawnGlyphByGlyph(SKBitmap rendered, FieldRect field, int strikePx, SKColor? color)
    {
        float pitch = ScopeTextFonts.GlyphPitch(strikePx);
        using SKBitmap mask = new(Width, Height);
        using (var canvas = new SKCanvas(mask))
        {
            canvas.Clear(SKColors.Black);
            using SKFont font = ScopeTextFonts.MakeFont(strikePx);
            using var paint = new SKPaint { Color = SKColors.White, IsAntialias = false };
            for (int i = 0; i < field.Text.Length; i++)
            {
                canvas.DrawText(field.Text[i].ToString(), field.Rect.Left + (i * pitch), field.Rect.Bottom, SKTextAlign.Left, font, paint);
            }
        }

        var box = new SKRectI(
            (int)MathF.Floor(field.Rect.Left),
            (int)MathF.Floor(field.Rect.Bottom) - strikePx + 2,
            (int)MathF.Ceiling(field.Rect.Left + (field.Text.Length * pitch)),
            (int)MathF.Floor(field.Rect.Bottom) + 3
        );
        List<SKPointI> lit = [];
        for (int y = box.Top; y < box.Bottom; y++)
        {
            for (int x = box.Left; x < box.Right; x++)
            {
                if (mask.GetPixel(x, y) == SKColors.White)
                {
                    lit.Add(new SKPointI(x, y));
                }
            }
        }

        Assert.NotEmpty(lit);
        SKColor ink = color ?? rendered.GetPixel(lit[0].X, lit[0].Y);
        Assert.NotEqual(SKColors.Black, ink);
        for (int y = box.Top; y < box.Bottom; y++)
        {
            for (int x = box.Left; x < box.Right; x++)
            {
                SKColor want = (mask.GetPixel(x, y) == SKColors.White) ? ink : SKColors.Black;
                Assert.Equal(want, rendered.GetPixel(x, y));
            }
        }
    }

    [AvaloniaFact]
    public void Bubbles_StayCompact_WhateverTheTextStyle()
    {
        SKRect compact = BubbleRect(ScopeTextStyle.Compact);
        SKRect realistic = BubbleRect(ScopeTextStyle.Realistic);

        Assert.Equal(compact.Width, realistic.Width, 3);
        Assert.Equal(compact.Height, realistic.Height, 3);
    }

    [Fact]
    public void EramRealistic_AFieldsHitRectIsItsLaidOutRect()
    {
        ScopeFrame frame = Frame(ScopeTemplates.EramId, Compact(), ScopeTextFonts.EramMetrics(1), EramOwned());
        ScopeTrack track = Assert.Single(frame.Tracks);
        FieldRect callsign = CallsignOf(track);

        ScopeFieldHit hit = Assert.IsType<ScopeFieldHit>(frame.FieldAt(callsign.Rect.MidX, callsign.Rect.MidY));
        Assert.Equal(ScopeFieldSlot.Callsign, hit.Field.Slot);
        Assert.Equal(callsign.Rect, hit.Field.Rect);
        Assert.Equal(callsign.Text.Length * 8f, hit.Field.Rect.Width, 3);
        ScopeFieldHit? past = frame.FieldAt(callsign.Rect.Right + 0.5f, callsign.Rect.MidY);
        Assert.NotEqual(ScopeFieldSlot.Callsign, past?.Field.Slot);
    }

    private SKRect BubbleRect(ScopeTextStyle style)
    {
        UserPreferences prefs = PrivatePreferences();
        prefs.SetShowSpeechBubbles(true);
        prefs.SetScopeText(style, 3, style, 1);
        AircraftModel ac = EramOwned();
        ac.SpeechBubble = new AircraftSpeechBubble("CLEARED FOR TAKEOFF RUNWAY 30", DateTime.UtcNow.AddMinutes(1), SpeechBubbleSeverity.Speech);
        ScopeCanvas canvas = CanvasWith(prefs, ac);
        try
        {
            return Assert.Single(canvas.CurrentBubbles).Rect;
        }
        finally
        {
            canvas.Dispose();
        }
    }

    /// <summary>
    /// Preferences on a file of their own under the assembly's redirected app-data root, so a setter here never writes the file
    /// the rest of the suite reads: an instance keeps the file it was created on. Swapping the process-wide
    /// <see cref="UserPreferences.FilePath"/> for the one construction is safe only because this assembly runs its test
    /// collections serially (xunit.runner.json). <see cref="Dispose"/> deletes the file after the test.
    /// </summary>
    private UserPreferences PrivatePreferences()
    {
        string shared = UserPreferences.FilePath;
        string path = Path.Combine(YaatPaths.AppDataRoot, $"realistic-text-{Guid.NewGuid():N}.json");
        _privateFiles.Add(path);
        UserPreferences.UseFile(path);
        try
        {
            return new UserPreferences();
        }
        finally
        {
            UserPreferences.UseFile(shared);
        }
    }

    private static ScopeCanvas CanvasWith(UserPreferences prefs, params AircraftModel[] aircraft)
    {
        var canvas = new ScopeCanvas
        {
            Aircraft = aircraft,
            TemplateId = ScopeTemplates.EramId,
            Preferences = prefs,
        };
        canvas.Viewport.PixelWidth = Width;
        canvas.Viewport.PixelHeight = Height;
        canvas.Viewport.CenterLat = 37.0;
        canvas.Viewport.CenterLon = -122.0;
        return canvas;
    }

    private static SKBitmap Render(ScopeFrame frame, int? starsStrikePx, int? eramStrikePx)
    {
        var bitmap = new SKBitmap(Width, Height);
        using var canvas = new SKCanvas(bitmap);
        using var renderer = new ScopeRenderer();
        var snapshot = new ScopeRenderSnapshot(
            frame,
            [],
            new Dictionary<string, string>(),
            ScopeBrightness.Default,
            CompactSize,
            NoTints,
            [],
            null,
            new Dictionary<int, SKRect>(),
            null,
            null
        )
        {
            StarsStrikePx = starsStrikePx,
            EramStrikePx = eramStrikePx,
        };
        renderer.Render(canvas, Viewport(), snapshot);
        canvas.Flush();
        return bitmap;
    }
}
