using SkiaSharp;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Views.Map;
using Yaat.Client.Views.Scope;
using Yaat.Sim;
using Yaat.Sim.Data.Mva;

namespace Yaat.Client.Tests.Views.Scope;

/// <summary>
/// Pins <see cref="ScopeFrame.MvaRelationFor"/> against the committed MVA data, with today's radar's inhibit rule (tint
/// off, on the ground, established on an approach, VFR) and its ±100 ft at-band, and that the frame carries the relation
/// onto the Altitude field's token.
/// </summary>
[Collection(RadarBlinkClockCollection.Name)]
public class ScopeFrameMvaTests
{
    /// <summary>KOAK, inside a 2,000 ft MVA sector.</summary>
    private static readonly LatLon Koak = new(37.7213, -122.2208);

    private static AircraftModel IfrAt(double altitude)
    {
        Assert.Equal(2000, MvaDatabase.Default.FindSector(Koak)?.FloorFtMsl);
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.Position = Koak;
        ac.Altitude = altitude;
        ac.FlightRules = "IFR";
        return ac;
    }

    [Fact]
    public void Below_MoreThanTheBandUnderTheFloor() => Assert.Equal(MvaRelation.Below, ScopeFrame.MvaRelationFor(IfrAt(1800), true));

    [Fact]
    public void At_TheBandsLowEdge() => Assert.Equal(MvaRelation.At, ScopeFrame.MvaRelationFor(IfrAt(1900), true));

    [Fact]
    public void At_TheBandsHighEdge() => Assert.Equal(MvaRelation.At, ScopeFrame.MvaRelationFor(IfrAt(2100), true));

    [Fact]
    public void Above_PastTheBandsHighEdge() => Assert.Equal(MvaRelation.Above, ScopeFrame.MvaRelationFor(IfrAt(2101), true));

    [Fact]
    public void TintOff_IsNull() => Assert.Null(ScopeFrame.MvaRelationFor(IfrAt(1800), false));

    [Fact]
    public void OnGround_IsNull()
    {
        AircraftModel ac = IfrAt(1800);
        ac.IsOnGround = true;

        Assert.Null(ScopeFrame.MvaRelationFor(ac, true));
    }

    [Fact]
    public void EstablishedOnApproach_IsNull()
    {
        AircraftModel ac = IfrAt(1800);
        ac.IsEstablishedOnApproach = true;

        Assert.Null(ScopeFrame.MvaRelationFor(ac, true));
    }

    [Fact]
    public void Vfr_AnyCase_IsNull()
    {
        AircraftModel ac = IfrAt(1800);
        ac.FlightRules = "vfr";

        Assert.Null(ScopeFrame.MvaRelationFor(ac, true));
    }

    [Fact]
    public void NoSector_IsNoData()
    {
        AircraftModel ac = IfrAt(1800);
        ac.Position = new LatLon(40.0, -70.0);

        Assert.Equal(MvaRelation.NoData, ScopeFrame.MvaRelationFor(ac, true));
    }

    [Fact]
    public void Build_CarriesTheRelationOntoTheAltitudeToken_OnlyWithTheTintOn()
    {
        Assert.Equal(ScopeColorToken.MvaBelow, AltitudeToken(Build(ScopeTestAircraft.AllDisplay, IfrAt(1800))));

        ScopeDisplayOptions tintOff = ScopeTestAircraft.AllDisplay with { ShowMvaAltitudeTint = false };
        Assert.Equal(ScopeColorToken.Default, AltitudeToken(Build(tintOff, IfrAt(1800))));
    }

    private static ScopeColorToken AltitudeToken(ScopeFrame frame) =>
        Assert.Single(Assert.Single(frame.Tracks).Block.Fields, f => f.Slot == ScopeFieldSlot.Altitude).Token;

    private static ScopeFrame Build(ScopeDisplayOptions display, AircraftModel ac)
    {
        using var font = new SKFont { Size = 12f };
        using var paint = new SKPaint();
        var metrics = ScopeTextMetrics.FromStyle(new TextStyle(font, paint));
        var viewport = new MapViewport
        {
            PixelWidth = 1000,
            PixelHeight = 800,
            CenterLat = Koak.Lat,
            CenterLon = Koak.Lon,
        };
        var input = new ScopeFrameInput(
            [ac],
            null,
            viewport,
            false,
            metrics,
            ScopeTestAircraft.AllAlerts,
            display,
            new Dictionary<string, SKPoint>()
        );
        return ScopeFrame.Build(input);
    }
}
