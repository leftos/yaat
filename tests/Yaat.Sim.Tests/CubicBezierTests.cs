using Xunit;
using Yaat.Sim;
using Yaat.Sim.Data.Airport;
using static Yaat.Sim.Data.Airport.AirportGroundLayout;

namespace Yaat.Sim.Tests;

/// <summary>
/// Unit tests for <see cref="CubicBezier"/> math: evaluation, projection, curvature,
/// arc length, and tangent bearing. Uses a 90° fillet arc at OAK-like latitude.
/// </summary>
public class CubicBezierTests
{
    // A 90° arc at ~37.72° lat. Node A is due east, Node B is due south of the intersection.
    // Intersection at (37.72, -122.22). Tangent distance ~75ft on each edge.
    // At 37.72° lat, 1° lon ≈ 47.3 nm ≈ 287,400 ft; 1° lat = 60 nm = 364,567 ft.
    // 75ft in lat = 75 / 364567 ≈ 0.000206°; 75ft in lon = 75 / 287400 ≈ 0.000261°
    private const double IntersectionLat = 37.72;
    private const double IntersectionLon = -122.22;

    // Node A: 75ft east of intersection on a west-east edge
    private const double NodeALat = IntersectionLat;
    private const double NodeALon = IntersectionLon + 0.000261;

    // Node B: 75ft south of intersection on a north-south edge
    private const double NodeBLat = IntersectionLat - 0.000206;
    private const double NodeBLon = IntersectionLon;

    // Control points: κ = (4/3) * tan(sweep/4) where sweep = π/2 (90° turn).
    // κ = (4/3) * tan(π/8) ≈ 0.5523. Control point depth = κ * tangent distance.
    // P1 is along the edge from A toward intersection (westward): A + 0.5523 * 75ft west
    // P2 is along the edge from B toward intersection (northward): B + 0.5523 * 75ft north
    private const double Kappa = 0.5523;
    private const double P1Lat = NodeALat;
    private const double P1Lon = NodeALon - (Kappa * 0.000261);

    private const double P2Lat = NodeBLat + (Kappa * 0.000206);
    private const double P2Lon = NodeBLon;

    private static CubicBezier MakeArc() => new(NodeALat, NodeALon, P1Lat, P1Lon, P2Lat, P2Lon, NodeBLat, NodeBLon);

    // A straight bezier (control points on the line between endpoints)
    private const double StraightEndLat = IntersectionLat;
    private const double StraightEndLon = IntersectionLon + 0.001; // ~288ft east

    private static CubicBezier MakeStraight() =>
        new(
            IntersectionLat,
            IntersectionLon,
            IntersectionLat,
            IntersectionLon + 0.000333,
            IntersectionLat,
            IntersectionLon + 0.000667,
            StraightEndLat,
            StraightEndLon
        );

    // --- Evaluate ---

    [Fact]
    public void Evaluate_AtT0_ReturnsP0()
    {
        var b = MakeArc();
        var (lat, lon) = b.Evaluate(0);
        Assert.Equal(NodeALat, lat, 1e-10);
        Assert.Equal(NodeALon, lon, 1e-10);
    }

    [Fact]
    public void Evaluate_AtT1_ReturnsP3()
    {
        var b = MakeArc();
        var (lat, lon) = b.Evaluate(1);
        Assert.Equal(NodeBLat, lat, 1e-10);
        Assert.Equal(NodeBLon, lon, 1e-10);
    }

    [Fact]
    public void Evaluate_AtMidpoint_IsOnCurve()
    {
        var b = MakeArc();
        var (lat, lon) = b.Evaluate(0.5);

        // Midpoint of a 90° arc should be offset from the chord midpoint toward the center.
        // It should be between the two endpoints in both lat and lon.
        Assert.True(lat < NodeALat && lat > NodeBLat, $"Midpoint lat {lat} should be between nodes");
        Assert.True(lon > NodeBLon && lon < NodeALon, $"Midpoint lon {lon} should be between nodes");
    }

    // --- ClosestT ---

    [Fact]
    public void ClosestT_PointAtStart_ReturnsNearZero()
    {
        var b = MakeArc();
        double t = b.ClosestT(NodeALat, NodeALon, 20);
        Assert.True(t < 0.05, $"Expected t near 0, got {t}");
    }

    [Fact]
    public void ClosestT_PointAtEnd_ReturnsNearOne()
    {
        var b = MakeArc();
        double t = b.ClosestT(NodeBLat, NodeBLon, 20);
        Assert.True(t > 0.95, $"Expected t near 1, got {t}");
    }

    [Fact]
    public void ClosestT_PointOnCurve_MatchesEvaluation()
    {
        var b = MakeArc();
        var (midLat, midLon) = b.Evaluate(0.5);
        double t = b.ClosestT(midLat, midLon, 20);
        Assert.Equal(0.5, t, 0.02);
    }

    [Fact]
    public void ClosestT_PointOffCurve_ReturnsNearestT()
    {
        var b = MakeArc();
        // Point at the intersection (inside the curve) — should project to somewhere around t=0.5
        double t = b.ClosestT(IntersectionLat, IntersectionLon, 20);
        Assert.True(t > 0.2 && t < 0.8, $"Expected t near midpoint for interior point, got {t}");
    }

    // --- RadiusOfCurvatureFt ---

    [Fact]
    public void RadiusOfCurvature_StraightBezier_IsVeryLarge()
    {
        var b = MakeStraight();
        double r = b.RadiusOfCurvatureFt(0.5, IntersectionLat);
        Assert.True(r > 1_000_000, $"Straight bezier should have very large radius, got {r:F0}ft");
    }

    [Fact]
    public void RadiusOfCurvature_90DegArc_IsReasonable()
    {
        var b = MakeArc();
        double r = b.RadiusOfCurvatureFt(0.5, IntersectionLat);

        // For a 75ft tangent distance at 90°, the curb radius R = T / tan(45°) = 75ft.
        // The bezier approximation should be close but not exact.
        Assert.True(r > 30 && r < 200, $"90° arc midpoint radius should be ~75ft, got {r:F1}ft");
    }

    [Fact]
    public void MinRadiusOfCurvature_90DegArc_IsTighterThanEndpoints()
    {
        var b = MakeArc();
        double minR = b.MinRadiusOfCurvatureFt(IntersectionLat, 20);
        double endpointR = b.RadiusOfCurvatureFt(0.0, IntersectionLat);

        // Min radius (usually at midpoint) should be tighter than endpoints
        Assert.True(minR < endpointR, $"Min radius {minR:F1}ft should be < endpoint radius {endpointR:F1}ft");
    }

    // --- ArcLengthNm ---

    [Fact]
    public void ArcLength_StraightBezier_ApproximatesChordLength()
    {
        var b = MakeStraight();
        double arcLen = b.ArcLengthNm(20);
        double chordLen = GeoMath.DistanceNm(IntersectionLat, IntersectionLon, StraightEndLat, StraightEndLon);
        double ratio = arcLen / chordLen;
        Assert.True(ratio > 0.99 && ratio < 1.01, $"Straight bezier arc length should ≈ chord, ratio = {ratio:F4}");
    }

    [Fact]
    public void ArcLength_CurvedArc_IsLongerThanChord()
    {
        var b = MakeArc();
        double arcLen = b.ArcLengthNm(20);
        double chordLen = GeoMath.DistanceNm(NodeALat, NodeALon, NodeBLat, NodeBLon);
        Assert.True(arcLen > chordLen, $"Arc length {arcLen:F6}nm should exceed chord {chordLen:F6}nm");
    }

    // --- TangentBearing ---

    [Fact]
    public void TangentBearing_AtT0_AlignsWithP0ToP1()
    {
        var b = MakeArc();
        double bearing = b.TangentBearing(0);

        // P0 to P1 direction: same lat, decreasing lon → heading west (270°)
        Assert.True(bearing > 250 && bearing < 290, $"Tangent at t=0 should be ~270° (west), got {bearing:F1}°");
    }

    [Fact]
    public void TangentBearing_AtT1_AlignsWithP2ToP3()
    {
        var b = MakeArc();
        double bearing = b.TangentBearing(1);

        // P2 to P3 direction: same lon, decreasing lat → heading south (180°)
        Assert.True(bearing > 160 && bearing < 200, $"Tangent at t=1 should be ~180° (south), got {bearing:F1}°");
    }

    // --- MaxSafeSpeedKts (GroundArc method) ---

    [Fact]
    public void MaxSafeSpeedKts_75FtRadius_LateralAccelModel()
    {
        // Lateral-accel cap: v = sqrt(a_lat · r), a_lat = 0.13 g. At 75 ft this is ~10.5 kt, under the
        // Jet corner ceiling (TaxiSpeed = 30 kt for a near-straight arc), so the radius term governs.
        double radiusFt = 75.0;
        double radiusM = radiusFt * 0.3048;
        double expected = Math.Sqrt(0.13 * 9.80665 * radiusM) / 0.514444;

        var arc = MakeGroundArc(radiusFt); // TurnAngleDeg defaults to 0 → corner ceiling non-binding

        Assert.Equal(expected, arc.MaxSafeSpeedKts(AircraftCategory.Jet), 0.01);
    }

    [Fact]
    public void MaxSafeSpeedKts_LargerRadius_ProducesHigherSpeed()
    {
        var smallArc = MakeGroundArc(75.0);
        var largeArc = MakeGroundArc(150.0);

        Assert.True(largeArc.MaxSafeSpeedKts(AircraftCategory.Jet) > smallArc.MaxSafeSpeedKts(AircraftCategory.Jet));
    }

    private static GroundArc MakeGroundArc(double minRadiusFt)
    {
        var nodeA = new GroundNode
        {
            Id = 1,
            Position = new LatLon(NodeALat, NodeALon),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var nodeB = new GroundNode
        {
            Id = 2,
            Position = new LatLon(NodeBLat, NodeBLon),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        return new GroundArc
        {
            Nodes = [nodeA, nodeB],
            TaxiwayNames = ["W"],
            P1Lat = P1Lat,
            P1Lon = P1Lon,
            P2Lat = P2Lat,
            P2Lon = P2Lon,
            MinRadiusOfCurvatureFt = minRadiusFt,
            DistanceNm = MakeArc().ArcLengthNm(20),
        };
    }
}
