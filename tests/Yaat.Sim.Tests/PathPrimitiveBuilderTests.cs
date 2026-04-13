using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Ground;

namespace Yaat.Sim.Tests;

/// <summary>
/// Unit tests for <see cref="PathPrimitiveBuilder.FromSegment"/>. Exercises
/// the pure-math layer of GroundNavigator V2 without any real airport data —
/// every fixture is a synthesised <see cref="GroundNode"/> + edge pair wrapped
/// in a <see cref="DirectionalEdge"/> + <see cref="TaxiRouteSegment"/>.
/// </summary>
public class PathPrimitiveBuilderTests
{
    // ---- Straight segments ----

    [Fact]
    public void FromSegment_Straight_ProducesStraightPrimitiveWithCorrectGeometry()
    {
        // Two nodes 100 ft apart heading east from anchor (37.0, -122.0).
        const double fromLat = 37.0;
        const double fromLon = -122.0;
        var (toLat, toLon) = GeoMath.ProjectPoint(fromLat, fromLon, new TrueHeading(90.0), 100.0 / GeoMath.FeetPerNm);

        var fromNode = new GroundNode
        {
            Id = 1,
            Latitude = fromLat,
            Longitude = fromLon,
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var toNode = new GroundNode
        {
            Id = 2,
            Latitude = toLat,
            Longitude = toLon,
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var edge = new GroundEdge
        {
            Nodes = [fromNode, toNode],
            TaxiwayName = "A",
            DistanceNm = 100.0 / GeoMath.FeetPerNm,
        };
        var directed = new DirectionalEdge
        {
            Edge = edge,
            FromNode = fromNode,
            ToNode = toNode,
        };
        var segment = new TaxiRouteSegment { Edge = directed, TaxiwayName = "A" };

        var primitive = PathPrimitiveBuilder.FromSegment(segment);

        var straight = Assert.IsType<PathPrimitiveStraight>(primitive);
        Assert.Equal(PathPrimitiveKind.Straight, straight.Kind);
        Assert.Equal(2, straight.ToNodeId);
        Assert.Equal(fromLat, straight.FromLat);
        Assert.Equal(fromLon, straight.FromLon);
        Assert.Equal(toLat, straight.ToLat);
        Assert.Equal(toLon, straight.ToLon);
        Assert.InRange(straight.LengthFt, 99.5, 100.5);
        Assert.InRange(straight.BearingDeg, 89.9, 90.1);
    }

    [Fact]
    public void FromSegment_StraightReversed_FlipsBearing()
    {
        // Construct a straight edge from A to B, but wrap it in a DirectionalEdge
        // traversing B to A. The resulting primitive should have bearing +180°
        // from the underlying edge direction.
        const double aLat = 37.0;
        const double aLon = -122.0;
        var (bLat, bLon) = GeoMath.ProjectPoint(aLat, aLon, new TrueHeading(90.0), 100.0 / GeoMath.FeetPerNm);

        var aNode = new GroundNode
        {
            Id = 1,
            Latitude = aLat,
            Longitude = aLon,
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var bNode = new GroundNode
        {
            Id = 2,
            Latitude = bLat,
            Longitude = bLon,
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var edge = new GroundEdge
        {
            Nodes = [aNode, bNode],
            TaxiwayName = "A",
            DistanceNm = 100.0 / GeoMath.FeetPerNm,
        };
        // Traverse B → A.
        var directed = new DirectionalEdge
        {
            Edge = edge,
            FromNode = bNode,
            ToNode = aNode,
        };
        var segment = new TaxiRouteSegment { Edge = directed, TaxiwayName = "A" };

        var primitive = PathPrimitiveBuilder.FromSegment(segment);

        var straight = Assert.IsType<PathPrimitiveStraight>(primitive);
        Assert.Equal(1, straight.ToNodeId);
        // Bearing from B to A is 270° (west) since B is east of A.
        Assert.InRange(straight.BearingDeg, 269.9, 270.1);
    }

    // ---- Arc segments ----

    [Fact]
    public void FromSegment_Arc90DegreeRightTurn_ProducesCorrectCircle()
    {
        // Scenario: aircraft enters arc at P0 heading north (0°), turns 90° right,
        // exits at P3 heading east (90°). Radius 70 ft. Centre is 70 ft east of P0
        // (perpendicular-right of north).
        const double p0Lat = 37.0;
        const double p0Lon = -122.0;
        const double radiusFt = 70.0;
        double rNm = radiusFt / GeoMath.FeetPerNm;

        // Compute the circle centre and exit point from the expected geometry.
        var (centerLat, centerLon) = GeoMath.ProjectPoint(p0Lat, p0Lon, new TrueHeading(90.0), rNm);
        var (p3Lat, p3Lon) = GeoMath.ProjectPoint(centerLat, centerLon, new TrueHeading(0.0), rNm);

        // Bezier control points using the kappa formula (4/3·tan(θ/4)) for a 90° arc.
        double kappa = (4.0 / 3.0) * Math.Tan(Math.PI / 8.0);
        // P1: P0 projected along entry tangent (north, 0°) by kappa·r.
        var (p1Lat, p1Lon) = GeoMath.ProjectPoint(p0Lat, p0Lon, new TrueHeading(0.0), kappa * rNm);
        // P2: P3 projected along reverse of exit tangent. Exit tangent is east (90°),
        // reverse is west (270°) by kappa·r.
        var (p2Lat, p2Lon) = GeoMath.ProjectPoint(p3Lat, p3Lon, new TrueHeading(270.0), kappa * rNm);

        var node0 = new GroundNode
        {
            Id = 10,
            Latitude = p0Lat,
            Longitude = p0Lon,
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var node1 = new GroundNode
        {
            Id = 11,
            Latitude = p3Lat,
            Longitude = p3Lon,
            Type = GroundNodeType.TaxiwayIntersection,
        };
        // Arc length for a 90° sweep at r=70 ft is (π/2)·r ≈ 110 ft.
        double arcLenFt = (Math.PI / 2.0) * radiusFt;
        var arc = new GroundArc
        {
            Nodes = [node0, node1],
            P1Lat = p1Lat,
            P1Lon = p1Lon,
            P2Lat = p2Lat,
            P2Lon = p2Lon,
            MinRadiusOfCurvatureFt = radiusFt,
            DistanceNm = arcLenFt / GeoMath.FeetPerNm,
            TaxiwayNames = ["A"],
        };
        var directed = new DirectionalEdge
        {
            Edge = arc,
            FromNode = node0,
            ToNode = node1,
        };
        var segment = new TaxiRouteSegment { Edge = directed, TaxiwayName = "A" };

        var primitive = PathPrimitiveBuilder.FromSegment(segment);

        var arcPrim = Assert.IsType<PathPrimitiveArc>(primitive);
        Assert.Equal(PathPrimitiveKind.Arc, arcPrim.Kind);
        Assert.Equal(11, arcPrim.ToNodeId);
        Assert.Equal(radiusFt, arcPrim.RadiusFt);
        Assert.True(arcPrim.RightTurn, "0°→90° short way is a right turn");
        Assert.InRange(arcPrim.SweepDeg, 89.0, 91.0);
        Assert.InRange(arcPrim.EntryTangentBearingDeg, -0.5, 0.5);
        Assert.InRange(arcPrim.ExitTangentBearingDeg, 89.5, 90.5);
        // Length should be ~π/2 · 70 ≈ 110 ft.
        Assert.InRange(arcPrim.LengthFt, 108.0, 112.0);

        // Centre should be at (centerLat, centerLon) — 70 ft east of P0.
        double centerErrFt = GeoMath.DistanceNm(arcPrim.CenterLat, arcPrim.CenterLon, centerLat, centerLon) * GeoMath.FeetPerNm;
        Assert.True(centerErrFt < 1.0, $"centre off expected by {centerErrFt:F3}ft");

        // StartBearingFromCenterDeg: centre is east of P0, so P0 is west of
        // centre → bearing from centre to P0 is 270°.
        Assert.InRange(arcPrim.StartBearingFromCenterDeg, 269.5, 270.5);
    }

    [Fact]
    public void FromSegment_Arc90DegreeLeftTurn_ProducesCorrectCircle()
    {
        // Mirror: enter heading north, turn 90° left, exit heading west (270°).
        // Centre is 70 ft west of P0.
        const double p0Lat = 37.0;
        const double p0Lon = -122.0;
        const double radiusFt = 70.0;
        double rNm = radiusFt / GeoMath.FeetPerNm;

        var (centerLat, centerLon) = GeoMath.ProjectPoint(p0Lat, p0Lon, new TrueHeading(270.0), rNm);
        var (p3Lat, p3Lon) = GeoMath.ProjectPoint(centerLat, centerLon, new TrueHeading(0.0), rNm);

        double kappa = (4.0 / 3.0) * Math.Tan(Math.PI / 8.0);
        var (p1Lat, p1Lon) = GeoMath.ProjectPoint(p0Lat, p0Lon, new TrueHeading(0.0), kappa * rNm);
        var (p2Lat, p2Lon) = GeoMath.ProjectPoint(p3Lat, p3Lon, new TrueHeading(90.0), kappa * rNm);

        var node0 = new GroundNode
        {
            Id = 20,
            Latitude = p0Lat,
            Longitude = p0Lon,
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var node1 = new GroundNode
        {
            Id = 21,
            Latitude = p3Lat,
            Longitude = p3Lon,
            Type = GroundNodeType.TaxiwayIntersection,
        };
        double arcLenFt = (Math.PI / 2.0) * radiusFt;
        var arc = new GroundArc
        {
            Nodes = [node0, node1],
            P1Lat = p1Lat,
            P1Lon = p1Lon,
            P2Lat = p2Lat,
            P2Lon = p2Lon,
            MinRadiusOfCurvatureFt = radiusFt,
            DistanceNm = arcLenFt / GeoMath.FeetPerNm,
            TaxiwayNames = ["A"],
        };
        var directed = new DirectionalEdge
        {
            Edge = arc,
            FromNode = node0,
            ToNode = node1,
        };
        var segment = new TaxiRouteSegment { Edge = directed, TaxiwayName = "A" };

        var primitive = PathPrimitiveBuilder.FromSegment(segment);

        var arcPrim = Assert.IsType<PathPrimitiveArc>(primitive);
        Assert.False(arcPrim.RightTurn, "0°→270° short way is a left turn");
        Assert.InRange(arcPrim.SweepDeg, 89.0, 91.0);
        Assert.InRange(arcPrim.EntryTangentBearingDeg, -0.5, 0.5);
        Assert.InRange(arcPrim.ExitTangentBearingDeg, 269.5, 270.5);

        // Centre is west of P0 → bearing from centre to P0 is 90° (east).
        Assert.InRange(arcPrim.StartBearingFromCenterDeg, 89.5, 90.5);
    }
}
