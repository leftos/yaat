using SkiaSharp;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Views.Map;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Profiles;
using Yaat.Sim;

namespace Yaat.Client.Tests.Views.Scope;

/// <summary>
/// Others' block level (<see cref="ScopeFrameInput.OthersLevel"/>): a track the viewer does not own takes the zoom band's
/// level, <c>partial</c> being ERAM's Partial (the LDB) and STARS' Limited, while EuroScope keeps its full tag. Dwell comes
/// first, the viewer's own track is Full, and with no viewer every track is Full.
/// </summary>
[Collection(MapBlinkClockCollection.Name)]
public class ScopeOthersLevelTests
{
    private static readonly ScopeViewer EramViewer = new(new ActingPosition("OAK_44_CTR", "ZOA", "C44"), "ZOA", "44");

    private static ScopeFrame Frame(string templateId, ScopeViewer viewer, string othersLevel, string? hovered, params AircraftModel[] aircraft)
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
                Viewer = viewer,
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
            hovered
        );
        return ScopeFrame.Build(input);
    }

    private static ScopeBlockLevel LevelOf(string templateId, ScopeViewer viewer, string othersLevel, AircraftModel ac) =>
        Assert.Single(Frame(templateId, viewer, othersLevel, null, ac).Tracks).Level;

    /// <summary>A track owned by another ERAM sector of the viewer's facility.</summary>
    private static AircraftModel OtherSectorsTrack()
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.Owner = "OAK_12_CTR";
        ac.OwnerFacilityId = "ZOA";
        ac.OwnerSectorCode = "C12";
        return ac;
    }

    private static AircraftModel ViewersTrack()
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.Owner = "OAK_44_CTR";
        ac.OwnerFacilityId = "ZOA";
        ac.OwnerSectorCode = "C44";
        return ac;
    }

    private static AircraftModel UnownedTrack()
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.Owner = null;
        ac.OwnerFacilityId = null;
        ac.OwnerSectorCode = null;
        return ac;
    }

    [Fact]
    public void EramTrackNotOwned_UnderPartial_IsThePartialLdb() =>
        Assert.Equal(ScopeBlockLevel.Partial, LevelOf(ScopeTemplates.EramId, EramViewer, ScopeProfile.OthersPartial, OtherSectorsTrack()));

    [Fact]
    public void StarsTrackNotOwned_UnderPartial_IsLimited() =>
        Assert.Equal(ScopeBlockLevel.Limited, LevelOf(ScopeTemplates.StarsId, EramViewer, ScopeProfile.OthersPartial, OtherSectorsTrack()));

    [Theory]
    [InlineData(ScopeTemplates.EramId)]
    [InlineData(ScopeTemplates.StarsId)]
    public void ViewersOwnTrack_UnderPartial_IsFull(string templateId) =>
        Assert.Equal(ScopeBlockLevel.Full, LevelOf(templateId, EramViewer, ScopeProfile.OthersPartial, ViewersTrack()));

    [Theory]
    [InlineData(ScopeTemplates.EramId)]
    [InlineData(ScopeTemplates.StarsId)]
    public void NoViewer_UnderPartial_EveryTrackIsFull(string templateId)
    {
        Assert.Equal(ScopeBlockLevel.Full, LevelOf(templateId, ScopeViewer.None, ScopeProfile.OthersPartial, OtherSectorsTrack()));
        Assert.Equal(ScopeBlockLevel.Full, LevelOf(templateId, ScopeViewer.None, ScopeProfile.OthersPartial, UnownedTrack()));
    }

    [Theory]
    [InlineData(ScopeTemplates.EramId, ScopeProfile.OthersPartial, ScopeBlockLevel.Partial)]
    [InlineData(ScopeTemplates.StarsId, ScopeProfile.OthersPartial, ScopeBlockLevel.Limited)]
    [InlineData(ScopeTemplates.StarsId, ScopeProfile.OthersFull, ScopeBlockLevel.Full)]
    public void UnownedTrack_WithAViewer_TakesTheOthersLevel(string templateId, string othersLevel, ScopeBlockLevel expected) =>
        Assert.Equal(expected, LevelOf(templateId, EramViewer, othersLevel, UnownedTrack()));

    [Fact]
    public void EuroScope_UnderPartial_IsUnchanged() =>
        Assert.Equal(ScopeBlockLevel.Full, LevelOf(ScopeTemplates.EuroScopeId, EramViewer, ScopeProfile.OthersPartial, OtherSectorsTrack()));

    [Fact]
    public void MixedTemplate_UnderPartial_DrawsEachOwnersKind()
    {
        AircraftModel eram = OtherSectorsTrack();
        AircraftModel stars = ScopeTestAircraft.CreateModel();
        stars.Callsign = "DAL55";
        stars.Owner = "NCT_A";
        stars.OwnerFacilityId = "NCT";
        stars.OwnerSectorCode = "1A";

        ScopeFrame frame = Frame(ScopeTemplates.MixedId, EramViewer, ScopeProfile.OthersPartial, null, eram, stars);

        Assert.Equal(ScopeBlockLevel.Partial, Assert.Single(frame.Tracks, t => ReferenceEquals(t.Aircraft, eram)).Level);
        Assert.Equal(ScopeBlockLevel.Limited, Assert.Single(frame.Tracks, t => ReferenceEquals(t.Aircraft, stars)).Level);
    }

    [Theory]
    [InlineData(ScopeTemplates.EramId)]
    [InlineData(ScopeTemplates.StarsId)]
    public void HoverDwell_StillRaisesAPartialBlock(string templateId)
    {
        AircraftModel ac = OtherSectorsTrack();

        ScopeTrack track = Assert.Single(Frame(templateId, EramViewer, ScopeProfile.OthersPartial, ac.Callsign, ac).Tracks);

        Assert.Equal(ScopeBlockLevel.Dwell, track.Level);
    }
}
