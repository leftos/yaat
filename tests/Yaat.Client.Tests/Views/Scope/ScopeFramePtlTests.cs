using SkiaSharp;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Views.Map;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Profiles;
using Yaat.Sim;

namespace Yaat.Client.Tests.Views.Scope;

/// <summary>
/// The frame's per-track PTL layer: a callsign in the Scope's PTL set draws its vector whatever the global VECTOR ownership
/// says, at the global length; 0 minutes and a stationary track draw none, and an empty set changes nothing.
/// </summary>
[Collection(RadarBlinkClockCollection.Name)]
public class ScopeFramePtlTests
{
    private static readonly ScopeViewer EramViewer = new(new ActingPosition("OAK_44_CTR", "ZOA", "C44"), "ZOA", "44");

    private static IReadOnlySet<string> Ptl(params string[] callsigns) => new HashSet<string>(callsigns, StringComparer.Ordinal);

    private static ScopeFrame Frame(
        ScopeViewer viewer,
        ScopeVectorOwnership ownership,
        double minutes,
        IReadOnlySet<string>? ptl,
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
            ScopeTemplates.EramId,
            ScopeTextMetrics.FromStyle(new TextStyle(font, paint)),
            ScopeTestAircraft.AllAlerts,
            ScopeTestAircraft.AllDisplay with
            {
                Viewer = viewer,
            },
            new Dictionary<string, SKPoint>(),
            new HashSet<string>(),
            MvaFacilityId: null,
            ScopeProfile.OthersFull,
            VectorMinutes: minutes,
            VectorOwnership: ownership,
            HistoryCount: 0,
            LocalRings: new Dictionary<string, ScopeTrackRings>(),
            IntentPaths: new ScopeIntentPathCache(),
            LocalPtlCallsigns: ptl
        );
        return ScopeFrame.Build(input);
    }

    private static AircraftModel Track(string callsign, string? owner, string? sectorCode)
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.Callsign = callsign;
        ac.TrueTrack = 90;
        ac.Owner = owner;
        ac.OwnerFacilityId = owner is null ? null : "ZOA";
        ac.OwnerSectorCode = sectorCode;
        ac.PositionHistory = [.. Enumerable.Range(0, 10).Select(i => new[] { 37.0 - (i * 0.01), -122.0 })];
        return ac;
    }

    private static AircraftModel[] ThreeTracks() =>
        [Track("OWN1", "OAK_44_CTR", "C44"), Track("OTH1", "OAK_12_CTR", "C12"), Track("UNO1", owner: null, sectorCode: null)];

    private static string[] WithVector(ScopeFrame frame) => [.. frame.Tracks.Where(t => t.VectorEnd is not null).Select(t => t.Aircraft.Callsign)];

    private static SKPoint? VectorEndOf(ScopeFrame frame, string callsign) =>
        Assert.Single(frame.Tracks, t => t.Aircraft.Callsign == callsign).VectorEnd;

    [Fact]
    public void LocalPtl_DrawsAVector_OnAnOtherTrack_WhenTheVectorIsOwn() =>
        Assert.Equal(["OWN1", "OTH1"], WithVector(Frame(EramViewer, ScopeVectorOwnership.Own, 1, Ptl("OTH1"), ThreeTracks())));

    [Fact]
    public void LocalPtl_DrawsNoVector_WhenTheMinutesAreZero() =>
        Assert.Empty(WithVector(Frame(EramViewer, ScopeVectorOwnership.Own, 0, Ptl("OTH1"), ThreeTracks())));

    [Fact]
    public void LocalPtl_OnAll_DrawsEveryTrack_AsWithoutASet() =>
        Assert.Equal(["OWN1", "OTH1", "UNO1"], WithVector(Frame(EramViewer, ScopeVectorOwnership.All, 1, Ptl("OTH1"), ThreeTracks())));

    [Fact]
    public void NoViewer_Own_WithALocalPtl_DrawsEveryTrack() =>
        Assert.Equal(["OWN1", "OTH1", "UNO1"], WithVector(Frame(ScopeViewer.None, ScopeVectorOwnership.Own, 1, Ptl("OTH1"), ThreeTracks())));

    [Fact]
    public void LocalPtl_ForNoAircraft_DrawsNothingExtra() =>
        Assert.Equal(["OWN1"], WithVector(Frame(EramViewer, ScopeVectorOwnership.Own, 1, Ptl("GHOST1"), ThreeTracks())));

    [Fact]
    public void LocalPtl_DrawsNoVector_OnAStationaryTrack()
    {
        AircraftModel parked = Track("OTH1", "OAK_12_CTR", "C12");
        parked.GroundSpeed = 0.5;

        Assert.Null(VectorEndOf(Frame(EramViewer, ScopeVectorOwnership.Own, 4, Ptl("OTH1"), parked), "OTH1"));
    }

    [Fact]
    public void LocalPtl_VectorEnd_EqualsTheGlobalVectorEnd()
    {
        SKPoint? byPtl = VectorEndOf(Frame(EramViewer, ScopeVectorOwnership.Own, 1, Ptl("OTH1"), ThreeTracks()), "OTH1");
        SKPoint? byGlobal = VectorEndOf(Frame(EramViewer, ScopeVectorOwnership.All, 1, null, ThreeTracks()), "OTH1");

        Assert.Equal(byGlobal, byPtl);
    }

    [Fact]
    public void NoLocalPtl_ChangesNothing()
    {
        foreach (IReadOnlySet<string>? ptl in new IReadOnlySet<string>?[] { null, Ptl() })
        {
            Assert.Equal(["OWN1"], WithVector(Frame(EramViewer, ScopeVectorOwnership.Own, 1, ptl, ThreeTracks())));
            Assert.Equal(["OWN1", "OTH1", "UNO1"], WithVector(Frame(EramViewer, ScopeVectorOwnership.All, 1, ptl, ThreeTracks())));
        }
    }
}
