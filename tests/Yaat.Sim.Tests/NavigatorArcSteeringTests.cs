using Xunit;
using Yaat.Sim.Data.Airport;
using static Yaat.Sim.Data.Airport.AirportGroundLayout;

namespace Yaat.Sim.Tests;

/// <summary>
/// Tests for the arc speed model used by GroundNavigator and the pathfinders.
/// <see cref="GroundArc.MaxSafeSpeedKts"/> is a lateral-acceleration (tire-scrub / comfort) cap on the
/// curvature radius, additionally capped by the angle-based corner speed and floored at
/// <see cref="CategoryPerformance.SlowTurnSpeedKts"/>.
/// </summary>
public class NavigatorArcSteeringTests
{
    [Fact]
    public void MaxSafeSpeedKts_TightRadius_YawRateCapGoverns()
    {
        // 75 ft radius, near-straight (0° turn): the lateral-accel cap is ~10.5 kt, but the yaw-rate cap
        // v = ω·r at the Jet ceiling (12 °/s · 75 ft ≈ 9.3 kt) is tighter, so it governs — the nose can't
        // track a 75 ft arc faster than that without exceeding the gear-limited turn rate. (For a jet the
        // yaw-rate cap governs below ~95 ft; above it the lateral-accel term takes over.)
        var arc = MakeArc(radiusFt: 75.0, turnAngleDeg: 0.0);
        double expectedKts = CategoryPerformance.TurnRateLimitedSpeedKts(AircraftCategory.Jet, 75.0);

        Assert.Equal(expectedKts, arc.MaxSafeSpeedKts(AircraftCategory.Jet), 0.01);
    }

    [Fact]
    public void MaxSafeSpeedKts_CornerCeilingBinds_ForSharpTurn()
    {
        // 200 ft radius gives a lateral-accel cap of ~17 kt, but a 120° turn caps the corner speed
        // lower (Jet: 11.5 kt). The angle-based ceiling must govern.
        var arc = MakeArc(radiusFt: 200.0, turnAngleDeg: 120.0);
        double expectedCeiling = CategoryPerformance.CornerSpeedForAngle(AircraftCategory.Jet, 120.0);

        Assert.Equal(expectedCeiling, arc.MaxSafeSpeedKts(AircraftCategory.Jet), 0.01);
    }

    [Fact]
    public void MaxSafeSpeedKts_FlooredAtSlowTurnSpeed_ForDegenerateRadius()
    {
        // A near-collapsed 3 ft radius would yield ~2.1 kt; the floor keeps it at SlowTurnSpeedKts so a
        // degenerate-bezier arc never commands a stop (the navigator can always make forward progress).
        var arc = MakeArc(radiusFt: 3.0, turnAngleDeg: 90.0);

        Assert.Equal(CategoryPerformance.SlowTurnSpeedKts, arc.MaxSafeSpeedKts(AircraftCategory.Jet), 1e-9);
    }

    private static GroundArc MakeArc(double radiusFt, double turnAngleDeg)
    {
        var nodeA = new GroundNode
        {
            Id = 1,
            Position = new LatLon(37.72, -122.2197),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var nodeB = new GroundNode
        {
            Id = 2,
            Position = new LatLon(37.7198, -122.22),
            Type = GroundNodeType.TaxiwayIntersection,
        };

        return new GroundArc
        {
            Nodes = [nodeA, nodeB],
            TaxiwayNames = ["W"],
            P1Lat = nodeA.Position.Lat,
            P1Lon = nodeA.Position.Lon,
            P2Lat = nodeB.Position.Lat,
            P2Lon = nodeB.Position.Lon,
            MinRadiusOfCurvatureFt = radiusFt,
            DistanceNm = GeoMath.DistanceNm(nodeA.Position, nodeB.Position),
            TurnAngleDeg = turnAngleDeg,
        };
    }
}
