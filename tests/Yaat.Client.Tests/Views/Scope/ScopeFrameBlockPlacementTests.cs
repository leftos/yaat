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
/// Where a Scope block sits and whether it draws at all: a manual offset wins, a STARS track follows the student's leader
/// direction while the preference is on, a hidden but unselected track lays out no block and no leader while its symbol stays,
/// a selected one shows its block anyway, and the track carries the offset it was drawn at for a drag to start from.
/// </summary>
[Collection(MapBlinkClockCollection.Name)]
public class ScopeFrameBlockPlacementTests
{
    private const int Width = 1000;
    private const int Height = 800;
    private const float FontSize = 12f;

    private static MapViewport Viewport() =>
        new()
        {
            PixelWidth = Width,
            PixelHeight = Height,
            CenterLat = 37.0,
            CenterLon = -122.0,
        };

    private static ScopeFrame Frame(AircraftModel ac, FrameSetup setup = default)
    {
        using SKFont font = PlatformHelper.MonospaceFontBold(FontSize);
        using var paint = new SKPaint();
        var input = new ScopeFrameInput(
            [ac],
            setup.Selected,
            Viewport(),
            setup.TemplateId ?? ScopeTemplates.StarsId,
            ScopeTextMetrics.FromStyle(new TextStyle(font, paint)),
            ScopeTestAircraft.AllAlerts,
            ScopeTestAircraft.AllDisplay,
            setup.Offsets ?? new Dictionary<string, SKPoint>(),
            new HashSet<string>(),
            MvaFacilityId: null,
            ScopeProfile.OthersFull,
            VectorMinutes: 0,
            VectorOwnership: ScopeVectorOwnership.All,
            HistoryCount: 0,
            LocalRings: setup.Rings ?? new Dictionary<string, ScopeTrackRings>(),
            IntentPaths: new ScopeIntentPathCache(),
            LocalDwellLockedCallsigns: setup.DwellLocked,
            CrrGroups: setup.CrrGroups,
            HiddenCallsigns: setup.Hidden,
            SyncLeaderDirection: setup.SyncLeader
        );
        return ScopeFrame.Build(input);
    }

    private static (float X, float Y) SymbolOf(AircraftModel ac) => Viewport().LatLonToScreen(ac.Position.Lat, ac.Position.Lon);

    private static HashSet<string> Hidden(AircraftModel ac) => [ac.Callsign];

    [Fact]
    public void ThePlacedTrackCarriesItsDrawnOffset()
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();

        ScopeTrack track = Assert.Single(Frame(ac).Tracks);

        Assert.Equal(DatablockText.DefaultOffset, track.BlockOffset);
    }

    [Fact]
    public void AManualOffset_MovesTheBlock_AndIsTheOffsetItCarries()
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        var manual = new SKPoint(-60, 12);
        var offsets = new Dictionary<string, SKPoint> { [ac.Callsign] = manual };

        ScopeFrame frame = Frame(ac, new FrameSetup(Offsets: offsets));
        ScopeTrack track = Assert.Single(frame.Tracks);
        ScopeTrack atDefault = Assert.Single(Frame(ac).Tracks);

        Assert.Equal(manual, track.BlockOffset);
        Assert.Equal(atDefault.Block.Bounds.Left + (manual.X - DatablockText.DefaultOffset.X), track.Block.Bounds.Left, 0.5);
        Assert.Equal(atDefault.Block.Bounds.Top + (manual.Y - DatablockText.DefaultOffset.Y), track.Block.Bounds.Top, 0.5);
    }

    [Fact]
    public void StudentLeaderDirection_PlacesAStarsTrack_WhileThePreferenceIsOn()
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.StudentLeaderDirection = 8;

        ScopeTrack synced = Assert.Single(Frame(ac, new FrameSetup(SyncLeader: true)).Tracks);
        ScopeTrack off = Assert.Single(Frame(ac).Tracks);

        // North: the block centres over the symbol; without the preference it keeps the default up-and-right offset.
        Assert.Equal(synced.Symbol.X, synced.Block.Bounds.MidX, 0.5);
        Assert.True(off.Block.Bounds.MidX > off.Symbol.X);
        Assert.Equal(DatablockText.DefaultOffset, off.BlockOffset);
    }

    [Theory]
    [InlineData(ScopeTemplates.EramId)]
    [InlineData(ScopeTemplates.EuroScopeId)]
    public void StudentLeaderDirection_LeavesANonStarsTrackAtTheDefault(string templateId)
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.StudentLeaderDirection = 8;

        ScopeTrack track = Assert.Single(Frame(ac, new FrameSetup(TemplateId: templateId, SyncLeader: true)).Tracks);

        Assert.Equal(DatablockText.DefaultOffset, track.BlockOffset);
    }

    [Fact]
    public void AManualOffset_BeatsTheStudentsLeaderDirection()
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.StudentLeaderDirection = 8;
        var manual = new SKPoint(70, 40);
        var offsets = new Dictionary<string, SKPoint> { [ac.Callsign] = manual };

        ScopeTrack track = Assert.Single(Frame(ac, new FrameSetup(SyncLeader: true, Offsets: offsets)).Tracks);

        Assert.Equal(manual, track.BlockOffset);
    }

    [Fact]
    public void AHiddenTrack_LaysOutNoBlockAndNoLeader_AndKeepsItsSymbol()
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        (float sx, float sy) = SymbolOf(ac);
        ScopeFrame frame = Frame(ac, new FrameSetup(Hidden: Hidden(ac)));
        ScopeTrack track = Assert.Single(frame.Tracks);

        Assert.Equal(DatablockText.DefaultOffset, track.BlockOffset);
        Assert.Empty(track.Block.Fields);
        Assert.False(track.ShowsLeader);
        Assert.False(track.IsDwelled);
        Assert.NotNull(Frame(ac).BlockAt(sx + 40, sy - 30));
        Assert.Null(frame.BlockAt(sx + 40, sy - 30));
        Assert.Same(ac, frame.TargetAt(sx, sy));
        Assert.Null(frame.HoverAt(sx + 40, sy - 30).Dwelled);
    }

    [Fact]
    public void ASelectedHiddenTrack_StillShowsItsBlock()
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();

        ScopeTrack track = Assert.Single(Frame(ac, new FrameSetup(Hidden: Hidden(ac), Selected: ac)).Tracks);

        Assert.NotEmpty(track.Block.Fields);
        Assert.Equal(DatablockText.DefaultOffset, track.BlockOffset);
    }

    [Fact]
    public void AHiddenDwellLockedTrack_KeepsItsLock_ButStaysHidden()
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        HashSet<string> locked = [ac.Callsign];

        ScopeTrack track = Assert.Single(
            Frame(ac, new FrameSetup(TemplateId: ScopeTemplates.EramId, Hidden: Hidden(ac), DwellLocked: locked)).Tracks
        );

        Assert.True(track.IsDwellLocked);
        Assert.False(track.IsDwelled);
        Assert.Empty(track.Block.Fields);
    }

    [Fact]
    public void AHiddenEramTrack_HidesItsRangeDataBlock()
    {
        var group = new EramCrrGroupHubDto("A", "Coral", 37.0, -122.0);
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.Position = new LatLon(37.0 + (12.0 / 60.0), -122.0);
        ac.EramCrrGroup = "A";

        ScopeTrack shown = Assert.Single(Frame(ac, new FrameSetup(TemplateId: ScopeTemplates.EramId, CrrGroups: [group])).Tracks);
        ScopeTrack hidden = Assert.Single(
            Frame(ac, new FrameSetup(TemplateId: ScopeTemplates.EramId, Hidden: Hidden(ac), CrrGroups: [group])).Tracks
        );

        Assert.NotNull(shown.CrrReadout);
        Assert.Null(hidden.CrrReadout);
    }

    [Fact]
    public void AHiddenTrack_KeepsItsRings()
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        var rings = new Dictionary<string, ScopeTrackRings> { [ac.Callsign] = new(3.0, null, null) };

        ScopeTrack track = Assert.Single(Frame(ac, new FrameSetup(Hidden: Hidden(ac), Rings: rings)).Tracks);

        Assert.NotNull(track.Rings.JRing);
    }
}

/// <summary>What one frame test varies from the defaults: the template, the leader sync, the hidden set and the per-track state.</summary>
/// <param name="TemplateId">The scope's picked template id; null is <see cref="ScopeTemplates.StarsId"/>.</param>
/// <param name="SyncLeader">Whether the student leader-direction preference is on.</param>
/// <param name="Hidden">The hidden callsigns.</param>
/// <param name="Offsets">The manual block offsets.</param>
/// <param name="DwellLocked">The dwell-locked callsigns.</param>
/// <param name="Selected">The selected aircraft.</param>
/// <param name="CrrGroups">The room's ERAM CRR groups.</param>
/// <param name="Rings">The rings the instructor placed.</param>
internal readonly record struct FrameSetup(
    string? TemplateId = null,
    bool SyncLeader = false,
    IReadOnlySet<string>? Hidden = null,
    IReadOnlyDictionary<string, SKPoint>? Offsets = null,
    IReadOnlySet<string>? DwellLocked = null,
    AircraftModel? Selected = null,
    IReadOnlyList<EramCrrGroupHubDto>? CrrGroups = null,
    IReadOnlyDictionary<string, ScopeTrackRings>? Rings = null
);
