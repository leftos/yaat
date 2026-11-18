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
/// A track in a conflict pair draws its block Full whatever the others' level gives it, so its CA field (STARS) or its
/// whole-block flash (ERAM) shows, while conflict alerts are on: a STARS-drawn track in any STARS pair, its CA line steady and
/// dimmed while the pair is suppressed; an ERAM-drawn track in an ERAM pair the viewer's gate shows or in an unsuppressed STARS
/// pair. An ERAM-drawn track in a suppressed STARS pair, in an ERAM pair the gate hides, or an ERAM pair's Mode C intruder
/// keeps its level. Every frame is built on the flash's off-phase, so a flashing field is laid out with no text.
/// </summary>
[Collection(MapBlinkClockCollection.Name)]
public class ScopeConflictLevelTests
{
    private static readonly ScopeViewer EramViewer = new(new ActingPosition("OAK_44_CTR", "ZOA", "C44"), "ZOA", "44");

    private static ScopeFrame Frame(
        string templateId,
        string othersLevel,
        IReadOnlyList<ConflictAlertDto>? eramPairs,
        params AircraftModel[] aircraft
    )
    {
        using var font = new SKFont { Size = 12f };
        using var paint = new SKPaint();
        var input = new ScopeFrameInput(
            aircraft,
            null,
            new MapViewport
            {
                PixelWidth = 1000,
                PixelHeight = 800,
                CenterLat = 37.0,
                CenterLon = -122.0,
            },
            templateId,
            ScopeTextMetrics.FromStyle(new TextStyle(font, paint)),
            ScopeTestAircraft.AllAlerts,
            ScopeTestAircraft.AllDisplay with
            {
                Viewer = EramViewer,
            },
            new Dictionary<string, SKPoint>(),
            new HashSet<string>(),
            MvaFacilityId: null,
            othersLevel,
            VectorMinutes: 0,
            VectorOwnership: ScopeVectorOwnership.Own,
            HistoryCount: 0,
            LocalRings: new Dictionary<string, ScopeTrackRings>(),
            IntentPaths: new ScopeIntentPathCache(),
            EramConflicts: eramPairs
        );
        using var pin = MapBlinkPin.Phase(false);
        return ScopeFrame.Build(input);
    }

    private static ScopeTrack TrackOf(ScopeFrame frame, string callsign) => Assert.Single(frame.Tracks, t => t.Aircraft.Callsign == callsign);

    /// <summary>A track owned by another ERAM sector of the viewer's facility, in a STARS pair with <see cref="Peer"/>.</summary>
    private static AircraftModel OthersTrack(bool inStarsPair)
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.Owner = "OAK_12_CTR";
        ac.OwnerFacilityId = "ZOA";
        ac.OwnerSectorCode = "C12";
        ac.ConflictPeerCallsign = inStarsPair ? "SWA1234" : null;
        return ac;
    }

    private static AircraftModel Peer(bool inStarsPair)
    {
        AircraftModel peer = ScopeTestAircraft.CreateConflictPeer();
        peer.ConflictPeerCallsign = inStarsPair ? "UAL238" : null;
        return peer;
    }

    private static ConflictAlertDto EramPair(string shownFacility, string? intruder = null) =>
        new("ESTCA_SWA1234_UAL238", "UAL238", "SWA1234", ConflictAlertKind.Eram, IntruderCallsign: intruder) { ShownFacilities = [shownFacility] };

    /// <summary>The CA field laid out in <paramref name="track"/>'s block.</summary>
    private static FieldRect CaFieldOf(ScopeTrack track) => Assert.Single(track.Block.Fields, f => f.Slot == ScopeFieldSlot.ConflictAlert);

    [Fact]
    public void StarsLimited_InAnUnsuppressedStarsPair_IsFull_WithTheCaFieldFlashing()
    {
        ScopeTrack track = TrackOf(Frame(ScopeTemplates.StarsId, ScopeProfile.OthersPartial, null, OthersTrack(true), Peer(true)), "UAL238");
        FieldRect ca = CaFieldOf(track);

        Assert.Equal(ScopeBlockLevel.Full, track.Level);
        Assert.Equal("", ca.Text);
        Assert.Equal(ScopeColorToken.ConflictAlert, ca.Token);
        Assert.Equal(ScopeFieldStyle.None, ca.Style);
    }

    [Fact]
    public void StarsLimited_InASuppressedStarsPair_IsFull_WithTheCaFieldSteadyAndDimmed()
    {
        AircraftModel ac = OthersTrack(true);
        ac.ConflictSuppressed = true;

        ScopeTrack track = TrackOf(Frame(ScopeTemplates.StarsId, ScopeProfile.OthersPartial, null, ac, Peer(true)), "UAL238");
        FieldRect ca = CaFieldOf(track);

        Assert.Equal(ScopeBlockLevel.Full, track.Level);
        Assert.NotEqual("", ca.Text);
        Assert.Equal(ScopeColorToken.ConflictAlert, ca.Token);
        Assert.Equal(ScopeFieldStyle.Dimmed, ca.Style);
    }

    [Fact]
    public void StarsFull_InAnAcknowledgedStarsPair_StaysFull_WithTheCaFieldSteadyAtFullBrightness()
    {
        AircraftModel ac = OthersTrack(true);
        ac.ConflictAcknowledged = true;

        ScopeTrack track = TrackOf(Frame(ScopeTemplates.StarsId, ScopeProfile.OthersFull, null, ac, Peer(true)), "UAL238");
        FieldRect ca = CaFieldOf(track);

        Assert.Equal(ScopeBlockLevel.Full, track.Level);
        Assert.NotEqual("", ca.Text);
        Assert.Equal(ScopeColorToken.ConflictAlert, ca.Token);
        Assert.Equal(ScopeFieldStyle.None, ca.Style);
    }

    [Fact]
    public void EramPartial_InAnEramPairShownToTheViewer_IsFull_AndFlashes()
    {
        ScopeTrack track = TrackOf(
            Frame(ScopeTemplates.EramId, ScopeProfile.OthersPartial, [EramPair("ZOA")], OthersTrack(false), Peer(false)),
            "UAL238"
        );

        Assert.Equal(ScopeBlockLevel.Full, track.Level);
        // The ERAM look flashes the whole block and draws no CA line (TrackPresenterEramConflictTests); line 2's ground
        // speed is the Full block's own content.
        Assert.Contains(track.Block.Fields, f => f.Slot == ScopeFieldSlot.GroundSpeed);
        Assert.All(track.Block.Fields, f => Assert.Equal("", f.Text));
    }

    [Fact]
    public void EramPartial_InAnUnsuppressedStarsPair_IsFull_WithTheCaField()
    {
        ScopeTrack track = TrackOf(Frame(ScopeTemplates.EramId, ScopeProfile.OthersPartial, null, OthersTrack(true), Peer(true)), "UAL238");
        FieldRect ca = CaFieldOf(track);

        Assert.Equal(ScopeBlockLevel.Full, track.Level);
        Assert.Equal(ScopeColorToken.ConflictAlert, ca.Token);
    }

    [Fact]
    public void EramPartial_InASuppressedStarsPair_StaysPartial()
    {
        AircraftModel ac = OthersTrack(true);
        ac.ConflictSuppressed = true;

        ScopeTrack track = TrackOf(Frame(ScopeTemplates.EramId, ScopeProfile.OthersPartial, null, ac, Peer(true)), "UAL238");

        Assert.Equal(ScopeBlockLevel.Partial, track.Level);
        Assert.DoesNotContain(track.Block.Fields, f => f.Slot == ScopeFieldSlot.ConflictAlert);
    }

    [Fact]
    public void EramPartial_InAnEramPairTheGateHides_StaysPartial_AndDoesNotFlash()
    {
        ScopeTrack track = TrackOf(
            Frame(ScopeTemplates.EramId, ScopeProfile.OthersPartial, [EramPair("ZNY")], OthersTrack(false), Peer(false)),
            "UAL238"
        );

        Assert.Equal(ScopeBlockLevel.Partial, track.Level);
        Assert.Contains(track.Block.Fields, f => f.Text.Length > 0);
    }

    [Fact]
    public void EramPartial_TheIntruderOfAnEramMciPair_KeepsItsPartialBlock_AndDoesNotFlash()
    {
        ScopeTrack track = TrackOf(
            Frame(ScopeTemplates.EramId, ScopeProfile.OthersPartial, [EramPair("ZOA", intruder: "UAL238")], OthersTrack(false), Peer(false)),
            "UAL238"
        );

        Assert.Equal(ScopeBlockLevel.Partial, track.Level);
        Assert.Contains(track.Block.Fields, f => f.Text.Length > 0);
    }

    [Theory]
    [InlineData(ScopeTemplates.StarsId)]
    [InlineData(ScopeTemplates.EramId)]
    public void Full_WithNoPair_IsUnchanged(string templateId) =>
        Assert.Equal(
            ScopeBlockLevel.Full,
            TrackOf(Frame(templateId, ScopeProfile.OthersFull, null, OthersTrack(false), Peer(false)), "UAL238").Level
        );
}
