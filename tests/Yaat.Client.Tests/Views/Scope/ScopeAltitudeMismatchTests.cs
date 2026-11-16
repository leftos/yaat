using SkiaSharp;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.Views.Map;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Profiles;

namespace Yaat.Client.Tests.Views.Scope;

/// <summary>
/// The altitude-mismatch mark: a <c>≠</c> cell in the block colour right after the ERAM altitude while the ERAM plan altitude
/// and the filed (STARS) altitude are both set and differ on a plan that is neither VFR nor OTP, on the ERAM Full, Dwell and
/// Partial blocks; the STARS template carries none. On dwell each template lists the other side's altitude under line 2.
/// </summary>
[Collection(RadarBlinkClockCollection.Name)]
public class ScopeAltitudeMismatchTests
{
    private const float FontSize = 12f;

    // One measuring style for the class, never disposed: a static the test process owns.
    private static readonly TextStyle Style12 = new(PlatformHelper.MonospaceFontBold(FontSize), new SKPaint());

    private static ScopeTextMetrics Metrics => ScopeTextMetrics.FromStyle(Style12);

    /// <summary>In conformance at 240 (ERAM plan 24000), filed at <paramref name="cruise"/>.</summary>
    private static AircraftModel Aircraft(int? eramFeet = 24000, int cruise = 35000)
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.TransponderMode = "C";
        ac.Altitude = 24000;
        ac.EramPlanAltitudeFeet = eramFeet;
        ac.CruiseAltitude = cruise;
        return ac;
    }

    private static TrackPresentation Present(AircraftModel ac, string templateId, ScopeBlockLevel level = ScopeBlockLevel.Full) =>
        TrackPresenter.Present(ScopeTestAircraft.Input(ac, templateId) with { Level = level });

    private static ScopeBlockLayout Layout(AircraftModel ac, string templateId, ScopeBlockLevel level = ScopeBlockLevel.Full) =>
        DatablockTemplate.Layout(Present(ac, templateId, level), Metrics);

    private static FieldRect Rect(ScopeBlockLayout layout, ScopeFieldSlot slot) => Assert.Single(layout.Fields, f => f.Slot == slot);

    private static bool HasMark(ScopeBlockLayout layout) => layout.Fields.Any(f => f.Slot == ScopeFieldSlot.AltitudeMismatch);

    [Fact]
    public void Mismatch_PutsTheCellFlushAfterTheAltitude_AndShiftsTheRestOneCell()
    {
        ScopeBlockLayout marked = Layout(Aircraft(), ScopeTemplates.EramId);
        ScopeBlockLayout equal = Layout(Aircraft(cruise: 24000), ScopeTemplates.EramId);
        float cell = Metrics.Measure(TrackPresenter.EramArrowCell);

        FieldRect altitude = Rect(marked, ScopeFieldSlot.EramAltitude);
        FieldRect mark = Rect(marked, ScopeFieldSlot.AltitudeMismatch);
        Assert.Equal("240C", altitude.Text);
        Assert.Equal(TrackPresenter.EramArrowCell, mark.Text);
        Assert.Equal(ScopeFieldStyle.AltitudeMismatch, mark.Style);
        Assert.Equal(ScopeColorToken.Default, mark.Token);
        Assert.Equal(altitude.Rect.Top, mark.Rect.Top);
        Assert.Equal(altitude.Rect.Right, mark.Rect.Left, 3);
        Assert.Equal(cell, mark.Rect.Width, 3);
        Assert.Equal(Rect(equal, ScopeFieldSlot.EramAltitude).Rect, altitude.Rect);
        Assert.Equal(Rect(equal, ScopeFieldSlot.GroundSpeed).Rect.Left + cell, Rect(marked, ScopeFieldSlot.GroundSpeed).Rect.Left, 3);
        Assert.Equal(Rect(equal, ScopeFieldSlot.CwtType).Rect.Left + cell, Rect(marked, ScopeFieldSlot.CwtType).Rect.Left, 3);
    }

    [Fact]
    public void EqualSides_NoMark_Line2AsToday()
    {
        ScopeBlockLayout equal = Layout(Aircraft(cruise: 24000), ScopeTemplates.EramId);
        ScopeBlockLayout today = Layout(Aircraft(cruise: 0), ScopeTemplates.EramId);

        Assert.False(HasMark(equal));
        Assert.Equal(today.Fields, equal.Fields);
        Assert.Equal(today.Bounds, equal.Bounds);
    }

    [Theory]
    [InlineData(24000, 0)]
    [InlineData(null, 35000)]
    [InlineData(0, 35000)]
    public void OneSideUnset_NoMark(int? eramFeet, int cruise) => Assert.False(HasMark(Layout(Aircraft(eramFeet, cruise), ScopeTemplates.EramId)));

    [Fact]
    public void VfrPlan_NoMark()
    {
        AircraftModel ac = Aircraft();
        ac.FlightRules = "VFR";

        Assert.False(HasMark(Layout(ac, ScopeTemplates.EramId)));
    }

    [Fact]
    public void OtpPlan_NoMark()
    {
        AircraftModel ac = Aircraft();
        ac.IsVfrOnTop = true;

        Assert.False(HasMark(Layout(ac, ScopeTemplates.EramId)));
    }

    [Fact]
    public void AfterFixPassed_Marks()
    {
        AircraftModel ac = Aircraft(11000, 17000);
        ac.Altitude = 11000;

        Assert.True(HasMark(Layout(ac, ScopeTemplates.EramId)));
    }

    [Fact]
    public void EramPartial_MarksAfterFieldsBAndC_EramLimited_DoesNot()
    {
        ScopeBlockLayout partial = Layout(Aircraft(), ScopeTemplates.EramId, ScopeBlockLevel.Partial);
        ScopeBlockLayout limited = Layout(Aircraft(), ScopeTemplates.EramId, ScopeBlockLevel.Limited);

        FieldRect altitude = Rect(partial, ScopeFieldSlot.EramAltitude);
        FieldRect mark = Rect(partial, ScopeFieldSlot.AltitudeMismatch);
        Assert.Equal("240", altitude.Text);
        Assert.Equal(altitude.Rect.Top, mark.Rect.Top);
        Assert.Equal(altitude.Rect.Right, mark.Rect.Left, 3);
        Assert.Equal(ScopeFieldStyle.AltitudeMismatch, mark.Style);
        Assert.False(HasMark(limited));
    }

    [Theory]
    [InlineData(ScopeBlockLevel.Full)]
    [InlineData(ScopeBlockLevel.Dwell)]
    public void StarsTemplate_CarriesNoMark(ScopeBlockLevel level) =>
        Assert.DoesNotContain(Present(Aircraft(), ScopeTemplates.StarsId, level).Fields, f => f.Slot == ScopeFieldSlot.AltitudeMismatch);

    [Fact]
    public void EramDwell_Mismatch_ListsTheStarsAltitudeUnderLine2()
    {
        ScopeBlockLayout dwell = Layout(Aircraft(), ScopeTemplates.EramId, ScopeBlockLevel.Dwell);
        ScopeBlockLayout full = Layout(Aircraft(), ScopeTemplates.EramId);

        FieldRect other = Rect(dwell, ScopeFieldSlot.OtherAltitude);
        Assert.Equal("STARS 350", other.Text);
        Assert.Equal(0f, other.Rect.Left);
        Assert.Equal(Rect(dwell, ScopeFieldSlot.EramAltitude).Rect.Top + Metrics.LineHeight, other.Rect.Top, 3);
        Assert.True(Rect(dwell, ScopeFieldSlot.Line4Notes).Rect.Top > other.Rect.Top);
        Assert.DoesNotContain(full.Fields, f => f.Slot == ScopeFieldSlot.OtherAltitude);
    }

    [Fact]
    public void StarsDwell_Mismatch_ListsTheEramAltitudeUnderLine2()
    {
        ScopeBlockLayout dwell = Layout(Aircraft(), ScopeTemplates.StarsId, ScopeBlockLevel.Dwell);

        FieldRect other = Rect(dwell, ScopeFieldSlot.OtherAltitude);
        Assert.Equal("ERAM 240", other.Text);
        Assert.Equal(Rect(dwell, ScopeFieldSlot.Altitude).Rect.Top + Metrics.LineHeight, other.Rect.Top, 3);
        Assert.DoesNotContain(Layout(Aircraft(), ScopeTemplates.StarsId).Fields, f => f.Slot == ScopeFieldSlot.OtherAltitude);
    }

    [Theory]
    [InlineData(ScopeTemplates.StarsId)]
    [InlineData(ScopeTemplates.EramId)]
    public void Dwell_EqualSides_DrawsTheFullRows(string templateId)
    {
        AircraftModel ac = Aircraft(cruise: 24000);

        ScopeBlockLayout dwell = Layout(ac, templateId, ScopeBlockLevel.Dwell);

        Assert.DoesNotContain(dwell.Fields, f => f.Slot == ScopeFieldSlot.OtherAltitude);
        Assert.Equal(Layout(ac, templateId).Fields, dwell.Fields);
        Assert.Equal(Layout(ac, templateId).Bounds, dwell.Bounds);
    }

    private static readonly ScopeViewer Viewer = new(new Yaat.Sim.ActingPosition("OAK_44_CTR", "ZOA", "C44"), "ZOA", "44");

    /// <summary>An unowned, frozen track the viewer's sector is on frequency for: the VCI and <c>R</c> both show.</summary>
    private static ScopeBlockLayout Column0Layout(int cruise, ScopeBlockLevel level)
    {
        AircraftModel ac = Aircraft(cruise: cruise);
        ac.EramIsFrozen = true;
        ac.EramOnFrequencySectorIds = ["44"];
        TrackPresenterInput input = ScopeTestAircraft.Input(ac, ScopeTemplates.EramId) with
        {
            Level = level,
            Display = ScopeTestAircraft.AllDisplay with { Viewer = Viewer },
        };
        return DatablockTemplate.Layout(TrackPresenter.Present(input), Metrics);
    }

    [Theory]
    [InlineData(35000, ScopeBlockLevel.Dwell)]
    [InlineData(35000, ScopeBlockLevel.Full)]
    [InlineData(24000, ScopeBlockLevel.Dwell)]
    public void Column0_StaysBesideItsLine_WithOrWithoutTheDwellLine(int cruise, ScopeBlockLevel level)
    {
        ScopeBlockLayout layout = Column0Layout(cruise, level);

        Assert.Equal(Rect(layout, ScopeFieldSlot.EramAltitude).Rect.Top, Rect(layout, ScopeFieldSlot.Vci).Rect.Top, 3);
        Assert.Equal(Rect(layout, ScopeFieldSlot.StatusLine3).Rect.Top, Rect(layout, ScopeFieldSlot.NotYourControl).Rect.Top, 3);
        Assert.Equal((cruise != 24000) && (level == ScopeBlockLevel.Dwell), layout.Fields.Any(f => f.Slot == ScopeFieldSlot.OtherAltitude));
    }

    [Fact]
    public void NotEqualPath_TwoBarsAndADiagonal_InsideItsCell()
    {
        using SKFont font = PlatformHelper.MonospaceFontBold(FontSize);
        float cellWidth = font.MeasureText(TrackPresenter.EramArrowCell);
        float cap = font.Metrics.CapHeight > 0 ? font.Metrics.CapHeight : FontSize * 0.7f;
        const float left = 10f;
        var field = new FieldRect(
            ScopeFieldSlot.AltitudeMismatch,
            new SKRect(left, 20f, left + cellWidth, 20f + FontSize),
            TrackPresenter.EramArrowCell,
            ScopeColorToken.Default,
            ScopeFieldStyle.AltitudeMismatch
        );

        using SKPath path = ScopeRenderer.NotEqualPath(field, font, 0);

        SKPoint[] p = path.Points;
        Assert.Equal(6, p.Length);
        // Two horizontal bars, the upper first, then a diagonal rising to the right.
        Assert.Equal(p[0].Y, p[1].Y);
        Assert.Equal(p[2].Y, p[3].Y);
        Assert.True(p[0].Y < p[2].Y, "the first bar is not the upper one");
        Assert.True(p[1].X > p[0].X);
        Assert.True((p[4].X > p[5].X) && (p[4].Y < p[5].Y), "the diagonal does not rise to the right");
        Assert.All(p, q => Assert.InRange(q.X, left, left + cellWidth));
        // The diagonal's foot sits a twentieth of the cap height below the baseline, as the mock draws it.
        Assert.All(p, q => Assert.InRange(q.Y, field.Rect.Top, field.Rect.Bottom + (cap * 0.05f) + 0.001f));
    }

    [Fact]
    public void Mark_DrawsInItsCellInTheBlockColour_AndNothingElse()
    {
        using var pin = RadarBlinkPin.Phase(true);
        AircraftModel ac = Aircraft();
        ScopeFrame frame = Frame(ac);
        FieldRect mark = Assert.Single(Assert.Single(frame.Tracks).Block.Fields, f => f.Slot == ScopeFieldSlot.AltitudeMismatch);

        using SKBitmap drawn = Render(frame);
        using SKBitmap plain = Render(WithoutMarkStyle(frame));

        var cell = SKRectI.Ceiling(mark.Rect, true);
        // The cell's edge pixels may carry the altitude glyph's antialiasing, so the mark shows as pixels it brightens.
        Assert.True(AnyBrightenedLit(drawn, plain, cell), "nothing drawn in the block colour in the mismatch cell");
        SKRectI around = cell;
        around.Inflate(2, 2);
        var changed = new List<string>();
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                if (!around.Contains(x, y) && (drawn.GetPixel(x, y) != plain.GetPixel(x, y)))
                {
                    changed.Add($"({x},{y})");
                }
            }
        }
        Assert.Empty(changed);
    }

    private const int Width = 1000;
    private const int Height = 800;

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
        var input = new ScopeFrameInput(
            [ac],
            null,
            Viewport(),
            ScopeTemplates.EramId,
            Metrics,
            ScopeTestAircraft.AllAlerts,
            ScopeTestAircraft.AllDisplay,
            new Dictionary<string, SKPoint>(),
            new HashSet<string>(),
            MvaFacilityId: null,
            OthersLevel: ScopeProfile.OthersFull,
            VectorMinutes: 0,
            VectorOwnership: ScopeVectorOwnership.Own,
            HistoryCount: 0,
            LocalRings: new Dictionary<string, ScopeTrackRings>()
        );
        return ScopeFrame.Build(input);
    }

    private static SKBitmap Render(ScopeFrame frame)
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
            new ScopePalette(null, null, SKColors.White)
        );
        renderer.Render(canvas, Viewport(), snapshot);
        canvas.Flush();
        return bitmap;
    }

    private static ScopeFrame WithoutMarkStyle(ScopeFrame frame)
    {
        ScopeTrack track = Assert.Single(frame.Tracks);
        ScopeBlockLayout block = track.Block with
        {
            Fields = [.. track.Block.Fields.Select(f => f.Slot == ScopeFieldSlot.AltitudeMismatch ? f with { Style = ScopeFieldStyle.None } : f)],
        };
        return frame with { Tracks = [track with { Block = block }] };
    }

    // A lit grey pixel (the white block colour at some coverage) brighter in drawn than in plain.
    private static bool AnyBrightenedLit(SKBitmap drawn, SKBitmap plain, SKRectI area)
    {
        for (int y = area.Top; y < area.Bottom; y++)
        {
            for (int x = area.Left; x < area.Right; x++)
            {
                SKColor c = drawn.GetPixel(x, y);
                SKColor before = plain.GetPixel(x, y);
                bool grey = (c.Red == c.Green) && (c.Green == c.Blue);
                if (grey && ((c.Red + c.Green + c.Blue) > (before.Red + before.Green + before.Blue)))
                {
                    return true;
                }
            }
        }
        return false;
    }
}
