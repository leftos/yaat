using Xunit;
using Yaat.Sim.Data.Airport;
using static Yaat.Sim.Data.Airport.AirportGroundLayout;

namespace Yaat.Sim.Tests;

/// <summary>
/// Tests for arc-related helpers used by GroundNavigator.
/// </summary>
public class NavigatorArcSteeringTests
{
    [Fact]
    public void MaxSafeSpeedKts_MatchesFormula()
    {
        var nodeA = new GroundNode
        {
            Id = 1,
            Latitude = 37.72,
            Longitude = -122.2197,
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var nodeB = new GroundNode
        {
            Id = 2,
            Latitude = 37.7198,
            Longitude = -122.22,
            Type = GroundNodeType.TaxiwayIntersection,
        };

        const double kappa = 0.5523;
        double p1Lat = nodeA.Latitude;
        double p1Lon = nodeA.Longitude - (kappa * 0.000261);
        double p2Lat = nodeB.Latitude + (kappa * 0.000206);
        double p2Lon = nodeB.Longitude;

        var bezier = new CubicBezier(nodeA.Latitude, nodeA.Longitude, p1Lat, p1Lon, p2Lat, p2Lon, nodeB.Latitude, nodeB.Longitude);
        var arc = new GroundArc
        {
            Nodes = [nodeA, nodeB],
            TaxiwayNames = ["W"],
            P1Lat = p1Lat,
            P1Lon = p1Lon,
            P2Lat = p2Lat,
            P2Lon = p2Lon,
            MinRadiusOfCurvatureFt = 75.0,
            DistanceNm = bezier.ArcLengthNm(20),
        };

        double turnRate = CategoryPerformance.GroundTurnRate(AircraftCategory.Jet); // 20 deg/s
        double expected = turnRate * Math.PI / 180.0 * (75.0 / GeoMath.FeetPerNm) * 3600.0;
        double actual = arc.MaxSafeSpeedKts(turnRate);

        Assert.Equal(expected, actual, 0.01);
    }
}
