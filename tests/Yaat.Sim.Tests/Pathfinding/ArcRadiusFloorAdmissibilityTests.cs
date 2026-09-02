using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Pathfinding;

namespace Yaat.Sim.Tests.Pathfinding;

/// <summary>
/// A fillet whose tightest radius is below what any aircraft's nose gear can steer is generator noise, not
/// pavement to route over: OAK's RWY 15/33 → D fillet at the F junction has a 6 ft minimum radius, and once
/// fillets were priced honestly the explicit route "F 33 D" resolved through it instead of the 33 ft of
/// runway centerline between the two junctions. Such arcs are inadmissible for every category; the route
/// goes through the junction nodes instead, which the navigator rounds at the nose-wheel radius.
/// </summary>
public class ArcRadiusFloorAdmissibilityTests
{
    private static GroundNode Node(int id, double lat, double lon) =>
        new()
        {
            Id = id,
            Position = new LatLon(lat, lon),
            Type = GroundNodeType.TaxiwayIntersection,
        };

    private static (GroundArc Arc, GroundNode From, GroundNode To) ArcWithMinRadius(double minRadiusFt)
    {
        var from = Node(1, 37.700, -122.200);
        var (toLat, toLon) = GeoMath.ProjectPoint(from.Position, new TrueHeading(90.0), 100.0 / GeoMath.FeetPerNm);
        var to = Node(2, toLat, toLon);
        var arc = new GroundArc
        {
            Nodes = [from, to],
            TaxiwayNames = ["D", "RWY15/33"],
            DistanceNm = GeoMath.DistanceNm(from.Position, to.Position),
            P1Lat = from.Position.Lat + (to.Position.Lat - from.Position.Lat) / 3.0,
            P1Lon = from.Position.Lon + (to.Position.Lon - from.Position.Lon) / 3.0,
            P2Lat = from.Position.Lat + (2.0 * (to.Position.Lat - from.Position.Lat)) / 3.0,
            P2Lon = from.Position.Lon + (2.0 * (to.Position.Lon - from.Position.Lon)) / 3.0,
            MinRadiusOfCurvatureFt = minRadiusFt,
            TurnAngleDeg = 90.0,
        };
        from.Edges.Add(arc);
        to.Edges.Add(arc);
        return (arc, from, to);
    }

    [Theory]
    [InlineData(AircraftCategory.Jet)]
    [InlineData(AircraftCategory.Turboprop)]
    [InlineData(AircraftCategory.Piston)]
    [InlineData(AircraftCategory.Helicopter)]
    public void Arc_TighterThanAnyNoseWheelRadius_IsInadmissibleForEveryCategory(AircraftCategory category)
    {
        var (arc, from, to) = ArcWithMinRadius(6.0);

        Assert.False(GeometricAdmissibility.IsAdmissible(PartialRoute.StartAt(from.Id), arc, to, category));
    }

    [Theory]
    [InlineData(AircraftCategory.Jet)]
    [InlineData(AircraftCategory.Helicopter)]
    public void Arc_AtOrAboveTheFloor_StaysAdmissibleOnAFirstEdge(AircraftCategory category)
    {
        var (arc, from, to) = ArcWithMinRadius(GeometricAdmissibility.MinSteerableArcRadiusFt);

        Assert.True(GeometricAdmissibility.IsAdmissible(PartialRoute.StartAt(from.Id), arc, to, category));
    }

    [Fact]
    public void Floor_IsTheSmallestCategoryNoseWheelRadius()
    {
        double smallest = Enum.GetValues<AircraftCategory>().Min(CategoryPerformance.NoseWheelTurnRadiusFt);

        Assert.Equal(smallest, GeometricAdmissibility.MinSteerableArcRadiusFt);
    }
}
