using SkiaSharp;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.Views.Map;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Profiles;

namespace Yaat.Client.Tests.Views.Scope;

/// <summary>
/// The conflict ring: a STARS pair draws the Radar's 3 NM ring on both members, at half the ALERTS brightness while the pair is
/// suppressed; an ERAM pair draws a ring on each member at the probe's lateral minimum (5 NM, 3 NM when both are at or below
/// FL230) while CA RING is on, except on an MCI pair's Mode C intruder, and none while it is off.
/// </summary>
[Collection(MapBlinkClockCollection.Name)]
public class ScopeConflictRingTests
{
    private static readonly MapViewport Vp = new()
    {
        PixelWidth = 1000,
        PixelHeight = 800,
        CenterLat = 37.0,
        CenterLon = -122.0,
    };

    private static ScopeFrame Frame(
        string templateId,
        ScopeAlertOptions alerts,
        IReadOnlyList<ConflictAlertDto>? eramPairs,
        params AircraftModel[] aircraft
    )
    {
        using var font = new SKFont { Size = 12f };
        using var paint = new SKPaint();
        var input = new ScopeFrameInput(
            aircraft,
            null,
            Vp,
            templateId,
            ScopeTextMetrics.FromStyle(new TextStyle(font, paint)),
            alerts,
            ScopeTestAircraft.AllDisplay,
            new Dictionary<string, SKPoint>(),
            new HashSet<string>(),
            MvaFacilityId: null,
            ScopeProfile.OthersFull,
            VectorMinutes: 0,
            VectorOwnership: ScopeVectorOwnership.Own,
            HistoryCount: 0,
            LocalRings: new Dictionary<string, ScopeTrackRings>(),
            IntentPaths: new ScopeIntentPathCache(),
            EramConflicts: eramPairs
        );
        return ScopeFrame.Build(input);
    }

    private static ScopeConflictRing? RingOf(ScopeFrame frame, string callsign) =>
        Assert.Single(frame.Tracks, t => t.Aircraft.Callsign == callsign).Rings.ConflictRing;

    private static (AircraftModel A, AircraftModel B) StarsPair()
    {
        AircraftModel a = ScopeTestAircraft.CreateModel();
        AircraftModel b = ScopeTestAircraft.CreateConflictPeer();
        a.ConflictPeerCallsign = b.Callsign;
        b.ConflictPeerCallsign = a.Callsign;
        return (a, b);
    }

    private static (AircraftModel A, AircraftModel B) EramMembers(double altitudeA, double altitudeB)
    {
        AircraftModel a = ScopeTestAircraft.CreateModel();
        AircraftModel b = ScopeTestAircraft.CreateConflictPeer();
        a.Altitude = altitudeA;
        b.Altitude = altitudeB;
        return (a, b);
    }

    private static ConflictAlertDto EramPair(string? intruder = null) =>
        new("ESTCA_SWA1234_UAL238", "UAL238", "SWA1234", ConflictAlertKind.Eram, IntruderCallsign: intruder);

    private static void AssertRing(ScopeConflictRing? ring, AircraftModel ac, double radiusNm, bool dimmed)
    {
        Assert.NotNull(ring);
        Assert.Equal(radiusNm, ring.RadiusNm);
        Assert.Equal(dimmed, ring.Dimmed);
        Assert.Equal(ScopeRingGeometry.Circle(ac.Position, radiusNm, Vp), ring.Points);
    }

    [Fact]
    public void AStarsPair_DrawsA3NmRing_OnBothMembers()
    {
        (AircraftModel a, AircraftModel b) = StarsPair();

        ScopeFrame frame = Frame(ScopeTemplates.StarsId, ScopeTestAircraft.AllAlerts, null, a, b);

        AssertRing(RingOf(frame, a.Callsign), a, 3.0, dimmed: false);
        AssertRing(RingOf(frame, b.Callsign), b, 3.0, dimmed: false);
    }

    [Fact]
    public void ASuppressedStarsPair_DrawsItsRingDimmed_AtHalfTheAlertsBrightness()
    {
        (AircraftModel a, AircraftModel b) = StarsPair();
        a.ConflictSuppressed = true;
        b.ConflictSuppressed = true;

        ScopeConflictRing? ring = RingOf(Frame(ScopeTemplates.StarsId, ScopeTestAircraft.AllAlerts, null, a, b), a.Callsign);

        AssertRing(ring, a, 3.0, dimmed: true);
        Assert.Equal(40, ScopeRenderer.ConflictRingPercent(ring!, 80));
        Assert.Equal(80, ScopeRenderer.ConflictRingPercent(ring! with { Dimmed = false }, 80));
    }

    [Fact]
    public void WithConflictAlertsOff_AStarsPairDrawsNoRing()
    {
        (AircraftModel a, AircraftModel b) = StarsPair();

        Assert.Null(RingOf(Frame(ScopeTemplates.StarsId, ScopeTestAircraft.AllAlerts with { ShowConflictAlerts = false }, null, a, b), a.Callsign));
    }

    [Fact]
    public void AnEramPairAboveFl230_DrawsA5NmRing_OnEachMember()
    {
        (AircraftModel a, AircraftModel b) = EramMembers(35000, 22000);

        ScopeFrame frame = Frame(ScopeTemplates.EramId, ScopeTestAircraft.AllAlerts, [EramPair()], a, b);

        AssertRing(RingOf(frame, a.Callsign), a, 5.0, dimmed: false);
        AssertRing(RingOf(frame, b.Callsign), b, 5.0, dimmed: false);
    }

    [Fact]
    public void AnEramPairBothAtOrBelowFl230_DrawsA3NmRing()
    {
        (AircraftModel a, AircraftModel b) = EramMembers(23000, 22000);

        ScopeFrame frame = Frame(ScopeTemplates.EramId, ScopeTestAircraft.AllAlerts, [EramPair()], a, b);

        AssertRing(RingOf(frame, a.Callsign), a, 3.0, dimmed: false);
        AssertRing(RingOf(frame, b.Callsign), b, 3.0, dimmed: false);
    }

    [Fact]
    public void WithCaRingOff_AnEramPairDrawsNoRing()
    {
        (AircraftModel a, AircraftModel b) = EramMembers(35000, 35000);

        ScopeFrame frame = Frame(ScopeTemplates.EramId, ScopeTestAircraft.AllAlerts with { ShowEramCaRing = false }, [EramPair()], a, b);

        Assert.Null(RingOf(frame, a.Callsign));
        Assert.Null(RingOf(frame, b.Callsign));
    }

    [Fact]
    public void AnEramMciPair_DrawsNoRingOnItsModeCIntruder()
    {
        (AircraftModel a, AircraftModel b) = EramMembers(35000, 35000);

        ScopeFrame frame = Frame(ScopeTemplates.EramId, ScopeTestAircraft.AllAlerts, [EramPair(intruder: b.Callsign)], a, b);

        Assert.Null(RingOf(frame, b.Callsign));
    }

    [Fact]
    public void AnEramMciPair_ItsTrackedSideIsFull_Flashes_AndDrawsTheRing()
    {
        (AircraftModel a, AircraftModel b) = EramMembers(35000, 35000);

        ScopeFrame frame;
        using (MapBlinkPin.Phase(false))
        {
            frame = Frame(ScopeTemplates.EramId, ScopeTestAircraft.AllAlerts, [EramPair(intruder: b.Callsign)], a, b);
        }
        ScopeTrack tracked = Assert.Single(frame.Tracks, t => t.Aircraft.Callsign == a.Callsign);

        Assert.Equal(ScopeBlockLevel.Full, tracked.Level);
        Assert.All(tracked.Block.Fields, f => Assert.Equal("", f.Text));
        AssertRing(tracked.Rings.ConflictRing, a, 5.0, dimmed: false);
    }
}
