using SkiaSharp;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.Views.Map;
using Yaat.Client.Views.Scope;
using Yaat.Sim;

namespace Yaat.Client.Tests.Views.Scope;

/// <summary>
/// The ERAM sector dwell lock and the VCI hover preview: a track locked for the viewer's ERAM sector draws at Dwell on an ERAM
/// block only, after every other track but a hovered one; hovering the VCI cell (inflated as CRC's
/// <c>OnFrequencyIndicatorBounds</c>) lights the VCI without dwelling the block, and the 8 px symbol zone wins over it.
/// </summary>
[Collection(RadarBlinkClockCollection.Name)]
public class ScopeDwellLockTests
{
    private const float FontSize = 12f;

    private static readonly ScopeViewer Viewer44 = new(new ActingPosition("OAK_44_CTR", "ZOA", "C44"), "ZOA", "44");

    private static MapViewport Viewport() =>
        new()
        {
            PixelWidth = 1000,
            PixelHeight = 800,
            CenterLat = 37.0,
            CenterLon = -122.0,
        };

    private static ScopeTextMetrics Metrics()
    {
        SKFont font = PlatformHelper.MonospaceFontBold(FontSize);
        return ScopeTextMetrics.FromStyle(new TextStyle(font, new SKPaint()));
    }

    private static ScopeFrame Build(
        string templateId,
        ScopeViewer viewer,
        AircraftModel[] aircraft,
        string? hovered = null,
        string? vciHovered = null,
        Dictionary<string, SKPoint>? offsets = null
    )
    {
        var input = new ScopeFrameInput(
            aircraft,
            null,
            Viewport(),
            templateId,
            Metrics(),
            ScopeTestAircraft.AllAlerts,
            ScopeTestAircraft.AllDisplay with
            {
                Viewer = viewer,
            },
            offsets ?? [],
            new HashSet<string>(),
            HoveredCallsign: hovered,
            VciHoveredCallsign: vciHovered
        );
        return ScopeFrame.Build(input);
    }

    private static AircraftModel Named(string callsign, double lon, params EramSectorKey[] locked)
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.Callsign = callsign;
        ac.Position = new LatLon(37.0, lon);
        ac.EramDwellLockedSectors = locked;
        return ac;
    }

    private static ScopeBlockLayout LayoutAt(string templateId, ScopeBlockLevel level, ScopeViewer viewer, AircraftModel ac)
    {
        TrackPresenterInput input = ScopeTestAircraft.Input(ac, templateId) with
        {
            Level = level,
            Display = ScopeTestAircraft.AllDisplay with { Viewer = viewer },
        };
        return DatablockTemplate.Layout(TrackPresenter.Present(input), Metrics());
    }

    private static ScopeTrack TrackOf(ScopeFrame frame, string callsign) => Assert.Single(frame.Tracks, t => t.Aircraft.Callsign == callsign);

    [Fact]
    public void IsDwellLocked_ListHoldsTheViewersFacilityAndSector_IsLocked()
    {
        AircraftModel ac = Named("UAL238", -122.0, new EramSectorKey("ZOA", "14"), new EramSectorKey("ZOA", "44"));

        Assert.True(EramDwellLock.IsDwellLocked(ac, Viewer44));
    }

    [Theory]
    [InlineData("ZOA", "14")]
    [InlineData("ZLA", "44")]
    public void IsDwellLocked_AnotherSectorOrFacility_IsNotLocked(string facility, string sector)
    {
        AircraftModel ac = Named("UAL238", -122.0, new EramSectorKey(facility, sector));

        Assert.False(EramDwellLock.IsDwellLocked(ac, Viewer44));
    }

    [Fact]
    public void IsDwellLocked_NoViewerOrNoEramSector_IsNotLocked()
    {
        AircraftModel ac = Named("UAL238", -122.0, new EramSectorKey("ZOA", "44"), new EramSectorKey("NCT", "1A"));
        var starsViewer = new ScopeViewer(new ActingPosition("NCT_A", "NCT", "1A"), null, null);

        Assert.False(EramDwellLock.IsDwellLocked(ac, ScopeViewer.None));
        Assert.False(EramDwellLock.IsDwellLocked(ac, starsViewer));
    }

    [Fact]
    public void Build_EramLockedTrack_IsAtDwell_DrawnAfterOthers_AndBeforeTheHoveredTrack()
    {
        AircraftModel locked = Named("UAL238", -122.05, new EramSectorKey("ZOA", "44"));
        AircraftModel plain = Named("DAL55", -122.0);
        AircraftModel hovered = Named("SWA1", -121.95);

        ScopeFrame frame = Build(ScopeTemplates.EramId, Viewer44, [hovered, locked, plain], hovered: "SWA1");

        Assert.Equal(["DAL55", "UAL238", "SWA1"], frame.Tracks.Select(t => t.Aircraft.Callsign));
        Assert.Equal([ScopeBlockLevel.Full, ScopeBlockLevel.Dwell, ScopeBlockLevel.Dwell], frame.Tracks.Select(t => t.Level));
    }

    [Fact]
    public void Build_StarsLockedTrack_StaysAtFull()
    {
        AircraftModel locked = Named("UAL238", -122.0, new EramSectorKey("ZOA", "44"));

        ScopeFrame frame = Build(ScopeTemplates.StarsId, Viewer44, [locked]);

        Assert.Equal(ScopeBlockLevel.Full, Assert.Single(frame.Tracks).Level);
    }

    [Fact]
    public void Build_Mixed_LockedUnownedTrackIsAtDwell_LockedStarsOwnedTrackIsAtFull()
    {
        AircraftModel unowned = Named("UAL238", -122.05, new EramSectorKey("ZOA", "44"));
        AircraftModel starsOwned = Named("DAL55", -121.95, new EramSectorKey("ZOA", "44"));
        starsOwned.Owner = "NCT_A";
        starsOwned.OwnerSectorCode = "1A";

        ScopeFrame frame = Build(ScopeTemplates.MixedId, Viewer44, [unowned, starsOwned]);

        Assert.Equal(ScopeBlockLevel.Dwell, TrackOf(frame, "UAL238").Level);
        Assert.Equal(ScopeBlockLevel.Full, TrackOf(frame, "DAL55").Level);
    }

    [Theory]
    [InlineData(ScopeBlockLevel.Full)]
    [InlineData(ScopeBlockLevel.Dwell)]
    public void VciCell_OnAnEramFdb_IsTheLitVciRect_OutsideTheBoundsAndFields(ScopeBlockLevel level)
    {
        AircraftModel ac = Named("UAL238", -122.0);
        ScopeBlockLayout unlit = LayoutAt(ScopeTemplates.EramId, level, ScopeViewer.None, ac);
        ac.EramOnFrequencySectorIds = ["44"];
        ScopeBlockLayout lit = LayoutAt(ScopeTemplates.EramId, level, Viewer44, ac);

        SKRect cell = Assert.NotNull(unlit.VciCell);
        Assert.Equal(Assert.Single(lit.Fields, f => f.Slot == ScopeFieldSlot.Vci).Rect, cell);
        Assert.Equal(cell, lit.VciCell);
        Assert.DoesNotContain(unlit.Fields, f => f.Slot == ScopeFieldSlot.Vci);
        Assert.Equal(unlit.TextBounds, unlit.Bounds);
        Assert.True(cell.Left < unlit.Bounds.Left);
    }

    [Theory]
    [InlineData(ScopeTemplates.EramId, ScopeBlockLevel.Limited)]
    [InlineData(ScopeTemplates.EramId, ScopeBlockLevel.Partial)]
    [InlineData(ScopeTemplates.StarsId, ScopeBlockLevel.Full)]
    [InlineData(ScopeTemplates.StarsId, ScopeBlockLevel.Dwell)]
    public void VciCell_AbsentBelowTheEramFdbAndOnStars(string templateId, ScopeBlockLevel level) =>
        Assert.Null(LayoutAt(templateId, level, Viewer44, Named("UAL238", -122.0)).VciCell);

    [Fact]
    public void VciCell_TranslatesWithTheOrigin()
    {
        ScopeBlockLayout layout = LayoutAt(ScopeTemplates.EramId, ScopeBlockLevel.Full, Viewer44, Named("UAL238", -122.0));
        SKRect cell = Assert.NotNull(layout.VciCell);

        ScopeBlockLayout placed = layout.At(100f, 50f);

        Assert.Equal(new SKRect(cell.Left + 100f, cell.Top + 50f, cell.Right + 100f, cell.Bottom + 50f), placed.VciCell);
    }

    [Fact]
    public void Hover_InsideTheInflatedVciCell_PreviewsTheVci_AndLeavesTheLevel()
    {
        AircraftModel ac = Named("UAL238", -122.0);
        ScopeFrame frame = Build(ScopeTemplates.EramId, Viewer44, [ac]);
        ScopeTrack track = Assert.Single(frame.Tracks);
        SKRect cell = Assert.NotNull(track.Block.VciCell);
        var inside = new SKPoint(cell.Left - 4f, cell.Top - 2f);
        Assert.True(SKPoint.Distance(track.Symbol, inside) > ScopeFrame.DwellSymbolRadiusPx);
        Assert.DoesNotContain(track.Block.Fields, f => f.Slot == ScopeFieldSlot.Vci);

        ScopeHoverHit hit = frame.HoverAt(inside.X, inside.Y);
        Assert.Same(ac, hit.VciPreviewed);
        Assert.Null(hit.Dwelled);
        Assert.Null(frame.HoverAt(cell.Left - 6f, cell.MidY).VciPreviewed);
        Assert.Null(frame.HoverAt(cell.MidX, cell.Top - 4f).VciPreviewed);
        Assert.Null(frame.HoverAt(cell.Right + 4f, cell.MidY).VciPreviewed);
        Assert.Same(ac, frame.HoverAt(cell.Right + 2.5f, cell.Bottom + 4.5f).VciPreviewed);

        ScopeTrack previewed = Assert.Single(Build(ScopeTemplates.EramId, Viewer44, [ac], vciHovered: "UAL238").Tracks);
        Assert.Equal(cell, Assert.Single(previewed.Block.Fields, f => f.Slot == ScopeFieldSlot.Vci).Rect);
        Assert.Equal(ScopeBlockLevel.Full, previewed.Level);
    }

    [Fact]
    public void VciPreview_NotOnFrequency_AddsNoBoundsAndNoClickTarget()
    {
        // The viewer's own track: no R shows, so nothing else widens the bounds over column 0.
        AircraftModel ac = Named("UAL238", -122.0);
        ac.Owner = "OAK_44_CTR";
        ac.OwnerFacilityId = "ZOA";
        ac.OwnerSectorCode = "C44";
        ScopeTrack plain = Assert.Single(Build(ScopeTemplates.EramId, Viewer44, [ac]).Tracks);
        Assert.Equal(plain.Block.TextBounds, plain.Block.Bounds);
        ScopeFrame frame = Build(ScopeTemplates.EramId, Viewer44, [ac], vciHovered: "UAL238");
        ScopeTrack previewed = Assert.Single(frame.Tracks);
        FieldRect vci = Assert.Single(previewed.Block.Fields, f => f.Slot == ScopeFieldSlot.Vci);

        Assert.Equal(plain.Block.Bounds, previewed.Block.Bounds);
        Assert.Null(frame.BlockAt(vci.Rect.MidX, vci.Rect.MidY));
    }

    [Fact]
    public void Hover_WithinTheSymbolZone_OverTheInflatedVciCell_DwellsTheBlock()
    {
        AircraftModel ac = Named("UAL238", -122.0);
        SKRect atOrigin = Assert.NotNull(LayoutAt(ScopeTemplates.EramId, ScopeBlockLevel.Full, Viewer44, ac).VciCell);
        var offsets = new Dictionary<string, SKPoint> { ["UAL238"] = new(-atOrigin.MidX, -atOrigin.MidY) };
        ScopeFrame frame = Build(ScopeTemplates.EramId, Viewer44, [ac], offsets: offsets);
        ScopeTrack track = Assert.Single(frame.Tracks);
        SKRect cell = Assert.NotNull(track.Block.VciCell);
        var point = new SKPoint(track.Symbol.X, track.Symbol.Y + 1f);
        Assert.True(cell.Contains(point.X, point.Y));

        ScopeHoverHit hit = frame.HoverAt(point.X, point.Y);

        Assert.Same(ac, hit.Dwelled);
        Assert.Null(hit.VciPreviewed);
    }

    [Fact]
    public void VciPreview_WithNoViewerEramSector_IsNotLit()
    {
        AircraftModel ac = Named("UAL238", -122.0);

        ScopeFrame frame = Build(ScopeTemplates.EramId, ScopeViewer.None, [ac], vciHovered: "UAL238");

        ScopeTrack track = Assert.Single(frame.Tracks);
        Assert.DoesNotContain(track.Block.Fields, f => f.Slot == ScopeFieldSlot.Vci);
        Assert.Null(track.Block.VciCell);
    }
}
