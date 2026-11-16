using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Views.Radar.Flyouts;
using Yaat.Sim;
using Yaat.Sim.Testing;

namespace Yaat.Client.Tests.Views.Radar;

/// <summary>
/// Pins <see cref="HeadingPreviewRenderer.TurnFor"/> — the turn rate, radius and roll-in lead the
/// EuroScope-style heading-select preview draws its arc with. The rate comes from the same per-type
/// lookup the sim's turns use (jet 2.5, piston 3.0, helicopter 5.0 deg/s), and the lead from the
/// category's roll-in time under the ramp turn model the preview assumes.
/// </summary>
public class HeadingPreviewTurnTests
{
    public HeadingPreviewTurnTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void TurnFor_Jet_AtFullRate()
    {
        var ac = new AircraftModel { AircraftType = "B738", GroundSpeed = 250 };

        PreviewTurn turn = HeadingPreviewRenderer.TurnFor(ac, 90);

        Assert.Equal(2.5, turn.TurnRateDegPerSec, 4);
        Assert.Equal(1.5915, turn.RadiusNm, 4);
        Assert.Equal(0.1736, turn.LeadNm, 4);
    }

    [Fact]
    public void TurnFor_Piston_LeftTurnAtFullRate()
    {
        var ac = new AircraftModel { AircraftType = "C172", GroundSpeed = 100 };

        PreviewTurn turn = HeadingPreviewRenderer.TurnFor(ac, -90);

        Assert.Equal(3.0, turn.TurnRateDegPerSec, 4);
        Assert.Equal(0.5305, turn.RadiusNm, 4);
        Assert.Equal(0.0417, turn.LeadNm, 4);
    }

    [Fact]
    public void TurnFor_Jet_SmallTurn_ShorterLead()
    {
        var ac = new AircraftModel { AircraftType = "B738", GroundSpeed = 250 };

        PreviewTurn turn = HeadingPreviewRenderer.TurnFor(ac, 5);

        Assert.Equal(2.5, turn.TurnRateDegPerSec, 4);
        Assert.Equal(1.5915, turn.RadiusNm, 4);
        Assert.Equal(0.1502, turn.LeadNm, 4);
    }

    [Fact]
    public void TurnFor_Helicopter_UsesHelicopterRate()
    {
        var ac = new AircraftModel { AircraftType = "H60", GroundSpeed = 100 };

        PreviewTurn turn = HeadingPreviewRenderer.TurnFor(ac, 90);

        Assert.Equal(5.0, turn.TurnRateDegPerSec, 4);
        Assert.Equal(0.3183, turn.RadiusNm, 4);
    }

    [Fact]
    public void TurnGeometry_RightTurn_CentreIsAbeamRightOfTheLeadEnd()
    {
        var anchor = new TurnAnchor(new LatLon(37.7, -122.2), 0, 90, 90);
        var turn = new PreviewTurn(2.5, 1.5, 0.2);

        (LatLon leadEnd, LatLon center) = HeadingPreviewRenderer.TurnGeometry(anchor, turn);

        Assert.Equal(0.2, GeoMath.DistanceNm(anchor.Position, leadEnd), 3);
        Assert.Equal(0, Math.Abs(GeoMath.SignedBearingDifference(0, GeoMath.BearingTo(anchor.Position, leadEnd))), 3);
        Assert.Equal(1.5, GeoMath.DistanceNm(leadEnd, center), 2);
        Assert.Equal(90, GeoMath.BearingTo(leadEnd, center), 0);
    }

    [Fact]
    public void TurnGeometry_LeftTurn_CentreIsAbeamLeftOfTheLeadEnd()
    {
        var anchor = new TurnAnchor(new LatLon(37.7, -122.2), 0, 270, -90);
        var turn = new PreviewTurn(2.5, 1.5, 0.2);

        (LatLon leadEnd, LatLon center) = HeadingPreviewRenderer.TurnGeometry(anchor, turn);

        Assert.Equal(0.2, GeoMath.DistanceNm(anchor.Position, leadEnd), 3);
        Assert.Equal(0, Math.Abs(GeoMath.SignedBearingDifference(0, GeoMath.BearingTo(anchor.Position, leadEnd))), 3);
        Assert.Equal(1.5, GeoMath.DistanceNm(leadEnd, center), 2);
        Assert.Equal(270, GeoMath.BearingTo(leadEnd, center), 0);
    }
}
