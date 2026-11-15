using SkiaSharp;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.Views.Map;
using Yaat.Client.Views.Scope;
using Yaat.Sim;

namespace Yaat.Client.Tests.Views.Scope;

/// <summary>
/// ERAM column 0: the VCI mark on row 2 when the viewer's ERAM sector is on frequency, and <c>R</c> on row 3 when the track's
/// owner is not the viewer's sector, laid left of the text block inside widened bounds, with the leader on the text block.
/// </summary>
[Collection(RadarBlinkClockCollection.Name)]
public class ScopeEramColumn0Tests
{
    // An independent oracle for DatablockTemplate.ColumnGap (CRC: 2 + 1 + 3 px), deliberately not read from the engine.
    private const float ColumnGap = 6f;

    private static readonly ScopeViewer EramViewer = new(new ActingPosition("OAK_44_CTR", "ZOA", "C44"), "ZOA", "44");

    private static readonly ScopeViewer EramViewerWithoutActing = new(null, "ZOA", "44");

    private static readonly ScopeViewer StarsViewer = new(new ActingPosition("OAK_APP", "NCT", "2S"), "NCT", null);

    private static TrackPresenterInput Input(AircraftModel ac, ScopeViewer viewer, bool flashOn = true) =>
        ScopeTestAircraft.Input(ac, ScopeTemplates.EramId, flashOn) with
        {
            Display = ScopeTestAircraft.AllDisplay with { Viewer = viewer },
        };

    private static ScopeField FieldOf(AircraftModel ac, ScopeViewer viewer, ScopeFieldSlot slot) =>
        Assert.Single(TrackPresenter.Present(Input(ac, viewer)).Fields, f => f.Slot == slot);

    private static ScopeTextMetrics Metrics(SKFont font, SKPaint paint) => ScopeTextMetrics.FromStyle(new TextStyle(font, paint));

    private static ScopeBlockLayout LayOut(AircraftModel ac, ScopeViewer viewer, ScopeTextMetrics metrics, bool flashOn = true) =>
        DatablockTemplate.Layout(TrackPresenter.Present(Input(ac, viewer, flashOn)), metrics);

    /// <summary>A track another ERAM sector owns, with the viewer's sector on frequency: both marks lit.</summary>
    private static AircraftModel OtherSectorOnFrequency()
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.Owner = "OAK_45_CTR";
        ac.OwnerFacilityId = "ZOA";
        ac.OwnerSectorCode = "C45";
        ac.EramOnFrequencySectorIds = ["44"];
        return ac;
    }

    /// <summary>As <see cref="OtherSectorOnFrequency"/>, with no line 3 or line 4: the block has two rows.</summary>
    private static AircraftModel TwoRowsOnly()
    {
        AircraftModel ac = OtherSectorOnFrequency();
        ac.AircraftType = "";
        ac.FiledAircraftType = "";
        ac.Destination = "";
        return ac;
    }

    /// <summary>As <see cref="OtherSectorOnFrequency"/>, with a handoff on line 3.</summary>
    private static AircraftModel WithLine3()
    {
        AircraftModel ac = OtherSectorOnFrequency();
        ac.HandoffPeerSectorCode = "C40";
        return ac;
    }

    [Fact]
    public void Vci_LitWhenTheViewersSectorIsOnFrequency()
    {
        ScopeField vci = FieldOf(OtherSectorOnFrequency(), EramViewer, ScopeFieldSlot.Vci);

        Assert.Equal(TrackPresenter.EramArrowCell, vci.Text);
        Assert.Equal(ScopeFieldStyle.Vci, vci.Style);
        Assert.Equal(ScopeColorToken.Default, vci.Token);
    }

    [Fact]
    public void Vci_NotDrawnWhenTheViewersSectorIsNotOnFrequency()
    {
        AircraftModel ac = OtherSectorOnFrequency();
        ac.EramOnFrequencySectorIds = ["40"];

        ScopeField vci = FieldOf(ac, EramViewer, ScopeFieldSlot.Vci);

        Assert.Equal("", vci.Text);
        Assert.Equal(ScopeFieldStyle.None, vci.Style);
    }

    [Fact]
    public void Vci_NeverLitForAViewerWithoutAnEramSector()
    {
        ScopeField vci = FieldOf(OtherSectorOnFrequency(), StarsViewer, ScopeFieldSlot.Vci);

        Assert.Equal("", vci.Text);
    }

    [Fact]
    public void R_DrawnWhenAnotherSectorOwnsTheTrack()
    {
        ScopeField r = FieldOf(OtherSectorOnFrequency(), EramViewer, ScopeFieldSlot.NotYourControl);

        Assert.Equal("R", r.Text);
        Assert.Equal("R", r.StableText);
        Assert.Equal(ScopeColorToken.Default, r.Token);
        Assert.Equal(ScopeFieldAction.None, r.Action);
        Assert.Equal("R", FieldOf(OtherSectorOnFrequency(), StarsViewer, ScopeFieldSlot.NotYourControl).Text);
    }

    [Fact]
    public void R_DrawnForAnUnownedTrack()
    {
        Assert.Equal("R", FieldOf(ScopeTestAircraft.CreateModel(), EramViewer, ScopeFieldSlot.NotYourControl).Text);
        Assert.Equal("R", FieldOf(ScopeTestAircraft.CreateModel(), StarsViewer, ScopeFieldSlot.NotYourControl).Text);
    }

    [Fact]
    public void R_NotDrawnWhenTheViewerOwnsTheTrack_ByEramSector()
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.Owner = "SOMEONE_ELSE";
        ac.OwnerFacilityId = "ZOA";
        ac.OwnerSectorCode = "C44";

        Assert.Equal("", FieldOf(ac, EramViewer, ScopeFieldSlot.NotYourControl).Text);
        Assert.Equal("", FieldOf(ac, EramViewerWithoutActing, ScopeFieldSlot.NotYourControl).Text);
    }

    [Fact]
    public void R_NotDrawnWhenTheViewerOwnsTheTrack_ByCallsign()
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.Owner = "OAK_APP";
        ac.OwnerFacilityId = "NCT";
        ac.OwnerSectorCode = "2S";

        Assert.Equal("", FieldOf(ac, StarsViewer, ScopeFieldSlot.NotYourControl).Text);
    }

    [Fact]
    public void ColumnZero_NothingForTheNoneViewer()
    {
        AircraftModel ac = OtherSectorOnFrequency();

        Assert.Equal("", FieldOf(ac, ScopeViewer.None, ScopeFieldSlot.Vci).Text);
        Assert.Equal("", FieldOf(ac, ScopeViewer.None, ScopeFieldSlot.NotYourControl).Text);
        using var font = new SKFont { Size = 12f };
        using var paint = new SKPaint();
        ScopeBlockLayout layout = LayOut(ac, ScopeViewer.None, Metrics(font, paint));
        Assert.DoesNotContain(layout.Fields, f => f.Slot is ScopeFieldSlot.Vci or ScopeFieldSlot.NotYourControl);
        Assert.Equal(layout.TextBounds, layout.Bounds);
    }

    [Fact]
    public void ColumnZeroRects_SitLeftOfTheTextBlock()
    {
        using var font = new SKFont { Size = 12f };
        using var paint = new SKPaint();
        ScopeBlockLayout layout = LayOut(WithLine3(), EramViewer, Metrics(font, paint));

        List<FieldRect> column0 = [.. layout.Fields.Where(f => f.Slot is ScopeFieldSlot.Vci or ScopeFieldSlot.NotYourControl)];
        Assert.Equal(2, column0.Count);
        Assert.All(column0, f => Assert.True(f.Rect.Right <= -ColumnGap, $"{f.Slot} right {f.Rect.Right}"));
        Assert.All(layout.Fields.Except(column0), f => Assert.True(f.Rect.Left >= 0f, $"{f.Slot} left {f.Rect.Left}"));
    }

    [Fact]
    public void ColumnZeroRects_OnTheirRows()
    {
        using var font = new SKFont { Size = 12f };
        using var paint = new SKPaint();
        ScopeTextMetrics metrics = Metrics(font, paint);
        ScopeBlockLayout layout = LayOut(WithLine3(), EramViewer, metrics);

        FieldRect vci = Assert.Single(layout.Fields, f => f.Slot == ScopeFieldSlot.Vci);
        FieldRect r = Assert.Single(layout.Fields, f => f.Slot == ScopeFieldSlot.NotYourControl);
        Assert.Equal(-metrics.Size + metrics.LineHeight, vci.Rect.Top, 3);
        Assert.Equal(-metrics.Size + (2 * metrics.LineHeight), r.Rect.Top, 3);
        Assert.Equal("R", r.Text);
        Assert.Equal(ScopeFieldStyle.Vci, vci.Style);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EveryRect_LiesInsideTheWidenedBounds(bool line3)
    {
        using var font = new SKFont { Size = 12f };
        using var paint = new SKPaint();
        ScopeBlockLayout layout = LayOut(line3 ? WithLine3() : TwoRowsOnly(), EramViewer, Metrics(font, paint));

        Assert.Contains(layout.Fields, f => f.Slot == ScopeFieldSlot.NotYourControl);
        Assert.All(
            layout.Fields,
            f =>
                Assert.True(
                    (f.Rect.Left >= layout.Bounds.Left)
                        && (f.Rect.Right <= layout.Bounds.Right)
                        && (f.Rect.Top >= layout.Bounds.Top)
                        && (f.Rect.Bottom <= layout.Bounds.Bottom),
                    $"{f.Slot} {f.Rect} outside {layout.Bounds}"
                )
        );
    }

    [Fact]
    public void R_WithoutLine3_WidensTheBoundsBottom()
    {
        using var font = new SKFont { Size = 12f };
        using var paint = new SKPaint();
        ScopeBlockLayout layout = LayOut(TwoRowsOnly(), EramViewer, Metrics(font, paint));

        FieldRect r = Assert.Single(layout.Fields, f => f.Slot == ScopeFieldSlot.NotYourControl);
        Assert.Equal(r.Rect.Bottom + ScopeTemplates.Eram.Padding, layout.Bounds.Bottom, 3);
        Assert.True(layout.Bounds.Bottom > layout.TextBounds.Bottom);
    }

    [Fact]
    public void TextBounds_ExcludeColumnZero_AndEqualBoundsWithoutIt()
    {
        using var font = new SKFont { Size = 12f };
        using var paint = new SKPaint();
        ScopeTextMetrics metrics = Metrics(font, paint);
        ScopeBlockLayout withMarks = LayOut(WithLine3(), EramViewer, metrics);
        ScopeBlockLayout withoutMarks = LayOut(WithLine3(), ScopeViewer.None, metrics);

        Assert.Equal(withoutMarks.Bounds, withMarks.TextBounds);
        Assert.Equal(withoutMarks.Bounds, withoutMarks.TextBounds);
        float column0Left = withMarks.Fields.Where(f => f.Slot is ScopeFieldSlot.Vci or ScopeFieldSlot.NotYourControl).Min(f => f.Rect.Left);
        Assert.Equal(column0Left - ScopeTemplates.Eram.Padding, withMarks.Bounds.Left, 3);
        Assert.True(withMarks.Bounds.Left < withMarks.TextBounds.Left);
        Assert.Equal(withMarks.TextBounds.Right, withMarks.Bounds.Right);
        Assert.Equal(withMarks.TextBounds.Top, withMarks.Bounds.Top);

        ScopeBlockLayout stars = DatablockTemplate.Layout(
            TrackPresenter.Present(ScopeTestAircraft.Input(WithLine3(), ScopeTemplates.StarsId)),
            metrics
        );
        Assert.Equal(stars.Bounds, stars.TextBounds);
    }

    [Fact]
    public void Rects_IdenticalAcrossFlashPhases_WithColumnZero()
    {
        using var font = new SKFont { Size = 12f };
        using var paint = new SKPaint();
        ScopeTextMetrics metrics = Metrics(font, paint);
        ScopeBlockLayout on = LayOut(WithLine3(), EramViewer, metrics, flashOn: true);
        ScopeBlockLayout off = LayOut(WithLine3(), EramViewer, metrics, flashOn: false);

        Assert.Equal(on.Fields.Select(f => (f.Slot, f.Rect)), off.Fields.Select(f => (f.Slot, f.Rect)));
        Assert.Equal(on.Bounds, off.Bounds);
        Assert.Equal(on.TextBounds, off.TextBounds);
    }

    [Fact]
    public void Rects_TranslateWithTheOrigin_WithColumnZero()
    {
        using var font = new SKFont { Size = 12f };
        using var paint = new SKPaint();
        ScopeBlockLayout atOrigin = LayOut(WithLine3(), EramViewer, Metrics(font, paint));

        ScopeBlockLayout placed = atOrigin.At(100f, 50f);

        Assert.Equal(atOrigin.Fields.Select(f => f.Offset(100f, 50f).Rect), placed.Fields.Select(f => f.Rect));
        Assert.Equal(
            new SKRect(atOrigin.Bounds.Left + 100f, atOrigin.Bounds.Top + 50f, atOrigin.Bounds.Right + 100f, atOrigin.Bounds.Bottom + 50f),
            placed.Bounds
        );
        Assert.Equal(
            new SKRect(
                atOrigin.TextBounds.Left + 100f,
                atOrigin.TextBounds.Top + 50f,
                atOrigin.TextBounds.Right + 100f,
                atOrigin.TextBounds.Bottom + 50f
            ),
            placed.TextBounds
        );
    }

    private static ScopeFrame Frame(AircraftModel ac, ScopeViewer viewer)
    {
        using var font = new SKFont { Size = 12f };
        using var paint = new SKPaint();
        var input = new ScopeFrameInput(
            [ac],
            null,
            new MapViewport
            {
                PixelWidth = 1000,
                PixelHeight = 800,
                CenterLat = 37.0,
                CenterLon = -122.0,
            },
            ScopeTemplates.EramId,
            Metrics(font, paint),
            ScopeTestAircraft.AllAlerts,
            ScopeTestAircraft.AllDisplay with
            {
                Viewer = viewer,
            },
            new Dictionary<string, SKPoint>(),
            new HashSet<string>(),
            MvaFacilityId: null
        );
        return ScopeFrame.Build(input);
    }

    [Fact]
    public void Leader_ClampsToTheTextBounds()
    {
        ScopeTrack withMarks;
        ScopeTrack withoutMarks;
        using (RadarBlinkPin.Phase(true))
        {
            withMarks = Assert.Single(Frame(WithLine3(), EramViewer).Tracks);
            withoutMarks = Assert.Single(Frame(WithLine3(), ScopeViewer.None).Tracks);
        }

        Assert.True(withMarks.Block.Bounds.Left < withMarks.Block.TextBounds.Left);
        Assert.Equal(ScopeRenderer.LeaderEnd(withoutMarks), ScopeRenderer.LeaderEnd(withMarks));
    }

    [Fact]
    public void SelectionBorder_FramesTheTextBlock()
    {
        ScopeTrack track;
        using (RadarBlinkPin.Phase(true))
        {
            track = Assert.Single(Frame(WithLine3(), EramViewer).Tracks) with { IsSelected = true };
        }

        Assert.Contains(track.Block.Fields, f => (f.Slot == ScopeFieldSlot.NotYourControl) && (f.Text == "R"));
        Assert.NotEqual(track.Block.Bounds, track.Block.TextBounds);
        Assert.Equal(track.Block.TextBounds, ScopeRenderer.SelectionBorder(track));
    }

    [Fact]
    public void VciPath_LiesInsideItsCell()
    {
        using SKFont font = PlatformHelper.MonospaceFontBold(12f);
        float cellWidth = font.MeasureText(TrackPresenter.EramArrowCell);
        const float left = -20f;
        var field = new FieldRect(
            ScopeFieldSlot.Vci,
            new SKRect(left, 20f, left + cellWidth, 32f),
            TrackPresenter.EramArrowCell,
            ScopeColorToken.Default,
            ScopeFieldStyle.Vci
        );

        using SKPath path = ScopeRenderer.VciPath(field, font, 0);

        Assert.False(path.IsEmpty);
        SKRect tight = path.TightBounds;
        Assert.InRange(tight.Left, left - 0.01f, left + cellWidth);
        Assert.InRange(tight.Right, left, left + cellWidth + 0.01f);
        Assert.InRange(tight.Top, field.Rect.Top - 0.01f, field.Rect.Bottom);
        Assert.InRange(tight.Bottom, field.Rect.Top, field.Rect.Bottom + 0.01f);
        Assert.True(tight.Width > cellWidth / 2f, "the arcs do not span the cell");
    }
}
