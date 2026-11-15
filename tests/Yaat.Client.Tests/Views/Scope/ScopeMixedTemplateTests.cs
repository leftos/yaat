using SkiaSharp;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Views.Map;
using Yaat.Client.Views.Scope;
using Yaat.Sim;

namespace Yaat.Client.Tests.Views.Scope;

/// <summary>
/// The Mixed template: a routing id that draws each track in its owner's display kind (an ERAM sector's track the ERAM block,
/// a STARS position's track the STARS block, an unowned track or one with no sector code the ERAM block), resolved per track
/// when the frame is built; it has no layout of its own.
/// </summary>
[Collection(RadarBlinkClockCollection.Name)]
public class ScopeMixedTemplateTests
{
    [Theory]
    [InlineData("C44", ScopeTemplates.EramId)]
    [InlineData("1A", ScopeTemplates.StarsId)]
    [InlineData("3O", ScopeTemplates.StarsId)]
    public void OwnerKind_ReadsTheOwnersSectorCode(string sectorCode, string expected)
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.Owner = "OWNER";
        ac.OwnerSectorCode = sectorCode;

        Assert.Equal(expected, ScopeOwnerKind.TemplateIdFor(ac));
    }

    [Fact]
    public void OwnerKind_Unowned_IsEram() => Assert.Equal(ScopeTemplates.EramId, ScopeOwnerKind.TemplateIdFor(ScopeTestAircraft.CreateModel()));

    [Fact]
    public void OwnerKind_OwnerWithNoSectorCode_IsEram()
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.Owner = "?";
        ac.OwnerSectorCode = null;

        Assert.Equal(ScopeTemplates.EramId, ScopeOwnerKind.TemplateIdFor(ac));
    }

    [Theory]
    [InlineData(ScopeTemplates.StarsId)]
    [InlineData(ScopeTemplates.EramId)]
    [InlineData(ScopeTemplates.EuroScopeId)]
    public void Resolve_AnyOtherTemplate_IsItself(string templateId)
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.OwnerSectorCode = "1A";

        Assert.Equal(templateId, ScopeOwnerKind.Resolve(templateId, ac));
    }

    [Fact]
    public void MixedIsSelectable_ButHasNoTemplate()
    {
        Assert.True(ScopeTemplates.IsSelectable(ScopeTemplates.MixedId));
        Assert.True(ScopeTemplates.IsSelectable(ScopeTemplates.EramId));
        Assert.False(ScopeTemplates.IsSelectable("nope"));
        Assert.Throws<ArgumentException>(() => ScopeTemplates.Get(ScopeTemplates.MixedId));
    }

    [Fact]
    public void MixedFrame_LaysEachTrackOutAsItsOwnersTemplate()
    {
        AircraftModel eram = ScopeTestAircraft.CreateModel();
        eram.Owner = "OAK_44_CTR";
        eram.OwnerSectorCode = "C44";
        AircraftModel stars = ScopeTestAircraft.CreateModel();
        stars.Callsign = "DAL55";
        stars.Position = new LatLon(37.05, -121.95);
        stars.Owner = "NCT_A";
        stars.OwnerSectorCode = "1A";
        AircraftModel unowned = ScopeTestAircraft.CreateModel();
        unowned.Callsign = "SKW5410";
        unowned.Position = new LatLon(36.95, -122.05);

        using var pin = RadarBlinkPin.Phase(true);
        ScopeFrame mixed = Build(ScopeTemplates.MixedId, eram, stars, unowned);

        Assert.Equal(ScopeTemplates.MixedId, mixed.TemplateId);
        AssertLaidOutAs(mixed, eram, ScopeTemplates.EramId);
        AssertLaidOutAs(mixed, stars, ScopeTemplates.StarsId);
        AssertLaidOutAs(mixed, unowned, ScopeTemplates.EramId);
    }

    private static void AssertLaidOutAs(ScopeFrame mixed, AircraftModel ac, string templateId)
    {
        Assert.Equal(templateId, ScopeOwnerKind.Resolve(ScopeTemplates.MixedId, ac));
        ScopeTrack track = Assert.Single(mixed.Tracks, t => ReferenceEquals(t.Aircraft, ac));
        ScopeTrack direct = Assert.Single(Build(templateId, ac).Tracks);
        Assert.Equal(direct.Block.Fields, track.Block.Fields);
        Assert.Equal(direct.Block.Bounds, track.Block.Bounds);
        Assert.Equal(direct.Block.TextBounds, track.Block.TextBounds);
        Assert.Equal(direct.ShowsLeader, track.ShowsLeader);
    }

    private static ScopeFrame Build(string templateId, params AircraftModel[] aircraft)
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
            ScopeTestAircraft.AllDisplay,
            new Dictionary<string, SKPoint>(),
            new HashSet<string>()
        );
        return ScopeFrame.Build(input);
    }
}
