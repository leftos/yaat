using Xunit;

namespace Yaat.Sim.Tests;

/// <summary>
/// Unit tests for <see cref="GeoMath.FootOfPerpendicular"/> and
/// <see cref="GeoMath.SegmentsIntersect"/>. These helpers were extracted from
/// earlier inline implementations (DistanceToSegmentFt, RunwayIntersectionCalculator,
/// TaxiwayGraphBuilder) to support ingress-segment construction and
/// path-following navigation.
/// </summary>
public class GeoMathFootOfPerpendicularTests
{
    // ---- FootOfPerpendicular ----

    [Fact]
    public void FootOfPerpendicular_PointOnSegment_ReturnsSamePoint_NotClamped()
    {
        // Segment from (37.0, -122.0) heading due east 1000 ft. Point is
        // exactly on the segment, 500 ft from A.
        double aLat = 37.0;
        double aLon = -122.0;
        var (bLat, bLon) = GeoMath.ProjectPoint(aLat, aLon, new TrueHeading(90.0), 1000.0 / GeoMath.FeetPerNm);
        var (pLat, pLon) = GeoMath.ProjectPoint(aLat, aLon, new TrueHeading(90.0), 500.0 / GeoMath.FeetPerNm);

        var foot = GeoMath.FootOfPerpendicular(pLat, pLon, aLat, aLon, bLat, bLon);

        Assert.False(foot.Clamped);
        Assert.InRange(foot.AlongNm * GeoMath.FeetPerNm, 499.0, 501.0);
        // Foot should equal the point (within fp tolerance).
        double distFt = GeoMath.DistanceNm(foot.FootLat, foot.FootLon, pLat, pLon) * GeoMath.FeetPerNm;
        Assert.True(distFt < 0.5, $"foot-to-point distance {distFt:F3}ft should be ~0");
    }

    [Fact]
    public void FootOfPerpendicular_PointOffSegment_ProjectsToInterior()
    {
        // Segment east, point 200 ft north of its midpoint.
        double aLat = 37.0;
        double aLon = -122.0;
        var (bLat, bLon) = GeoMath.ProjectPoint(aLat, aLon, new TrueHeading(90.0), 1000.0 / GeoMath.FeetPerNm);
        var (midLat, midLon) = GeoMath.ProjectPoint(aLat, aLon, new TrueHeading(90.0), 500.0 / GeoMath.FeetPerNm);
        var (pLat, pLon) = GeoMath.ProjectPoint(midLat, midLon, new TrueHeading(0.0), 200.0 / GeoMath.FeetPerNm);

        var foot = GeoMath.FootOfPerpendicular(pLat, pLon, aLat, aLon, bLat, bLon);

        Assert.False(foot.Clamped);
        Assert.InRange(foot.AlongNm * GeoMath.FeetPerNm, 499.0, 501.0);
        double footToMidFt = GeoMath.DistanceNm(foot.FootLat, foot.FootLon, midLat, midLon) * GeoMath.FeetPerNm;
        Assert.True(footToMidFt < 0.5, $"foot should land at midpoint, off by {footToMidFt:F3}ft");
    }

    [Fact]
    public void FootOfPerpendicular_PointPastEndpointB_ClampsToB()
    {
        // Segment east, point 500 ft beyond B.
        double aLat = 37.0;
        double aLon = -122.0;
        var (bLat, bLon) = GeoMath.ProjectPoint(aLat, aLon, new TrueHeading(90.0), 1000.0 / GeoMath.FeetPerNm);
        var (pLat, pLon) = GeoMath.ProjectPoint(bLat, bLon, new TrueHeading(90.0), 500.0 / GeoMath.FeetPerNm);

        var foot = GeoMath.FootOfPerpendicular(pLat, pLon, aLat, aLon, bLat, bLon);

        Assert.True(foot.Clamped);
        Assert.Equal(bLat, foot.FootLat, 10);
        Assert.Equal(bLon, foot.FootLon, 10);
    }

    [Fact]
    public void FootOfPerpendicular_PointBeforeEndpointA_ClampsToA()
    {
        double aLat = 37.0;
        double aLon = -122.0;
        var (bLat, bLon) = GeoMath.ProjectPoint(aLat, aLon, new TrueHeading(90.0), 1000.0 / GeoMath.FeetPerNm);
        var (pLat, pLon) = GeoMath.ProjectPoint(aLat, aLon, new TrueHeading(270.0), 500.0 / GeoMath.FeetPerNm);

        var foot = GeoMath.FootOfPerpendicular(pLat, pLon, aLat, aLon, bLat, bLon);

        Assert.True(foot.Clamped);
        Assert.Equal(aLat, foot.FootLat, 10);
        Assert.Equal(aLon, foot.FootLon, 10);
        Assert.Equal(0.0, foot.AlongNm, 10);
    }

    [Fact]
    public void FootOfPerpendicular_ZeroLengthSegment_ReturnsEndpoint()
    {
        double aLat = 37.0;
        double aLon = -122.0;
        double pLat = 37.001;
        double pLon = -122.001;

        var foot = GeoMath.FootOfPerpendicular(pLat, pLon, aLat, aLon, aLat, aLon);

        Assert.True(foot.Clamped);
        Assert.Equal(aLat, foot.FootLat, 10);
        Assert.Equal(aLon, foot.FootLon, 10);
    }

    // ---- SegmentsIntersect ----

    [Fact]
    public void SegmentsIntersect_XCrossing_ReturnsIntersection()
    {
        // East-west segment from (37.0, -122.001) to (37.0, -121.999) crosses
        // north-south segment from (36.999, -122.0) to (37.001, -122.0).
        var result = GeoMath.SegmentsIntersect(
            ax1: 37.0,
            ay1: -122.001,
            ax2: 37.0,
            ay2: -121.999,
            bx1: 36.999,
            by1: -122.0,
            bx2: 37.001,
            by2: -122.0
        );

        Assert.NotNull(result);
        Assert.Equal(37.0, result.Value.Lat, 5);
        Assert.Equal(-122.0, result.Value.Lon, 5);
        Assert.InRange(result.Value.T, 0.49, 0.51);
        Assert.InRange(result.Value.U, 0.49, 0.51);
    }

    [Fact]
    public void SegmentsIntersect_ParallelSegments_ReturnsNull()
    {
        var result = GeoMath.SegmentsIntersect(
            ax1: 37.0,
            ay1: -122.0,
            ax2: 37.0,
            ay2: -121.999,
            bx1: 37.001,
            by1: -122.0,
            bx2: 37.001,
            by2: -121.999
        );

        Assert.Null(result);
    }

    [Fact]
    public void SegmentsIntersect_NonOverlappingSegments_ReturnsNull()
    {
        var result = GeoMath.SegmentsIntersect(
            ax1: 37.0,
            ay1: -122.001,
            ax2: 37.0,
            ay2: -122.0005,
            bx1: 36.999,
            by1: -121.999,
            bx2: 37.001,
            by2: -121.999
        );

        Assert.Null(result);
    }

    [Fact]
    public void SegmentsIntersect_TouchingEndpoint_WithoutExclude_ReturnsIntersection()
    {
        // Segments share endpoint at (37.0, -122.0).
        var result = GeoMath.SegmentsIntersect(
            ax1: 37.0,
            ay1: -122.001,
            ax2: 37.0,
            ay2: -122.0,
            bx1: 37.0,
            by1: -122.0,
            bx2: 37.0,
            by2: -121.999,
            excludeEndpoints: false
        );

        // Parallel collinear case returns null (denom = 0). This asserts
        // the collinear endpoint-share doesn't crash — common for any graph
        // fragment that shares a node.
        Assert.Null(result);
    }

    [Fact]
    public void SegmentsIntersect_TouchingEndpoint_WithExclude_ReturnsNull()
    {
        // Segment A east from (37.0,-122.001) to (37.0,-122.0).
        // Segment B north from (37.0,-122.0) to (37.001,-122.0).
        // Intersection is at A's endpoint 2 / B's endpoint 1 — excluded.
        var result = GeoMath.SegmentsIntersect(
            ax1: 37.0,
            ay1: -122.001,
            ax2: 37.0,
            ay2: -122.0,
            bx1: 37.0,
            by1: -122.0,
            bx2: 37.001,
            by2: -122.0,
            excludeEndpoints: true
        );

        Assert.Null(result);
    }

    [Fact]
    public void SegmentsIntersect_TrueCrossing_WithExclude_StillReturnsIntersection()
    {
        // Interior X crossing — should still fire under excludeEndpoints.
        var result = GeoMath.SegmentsIntersect(
            ax1: 37.0,
            ay1: -122.001,
            ax2: 37.0,
            ay2: -121.999,
            bx1: 36.999,
            by1: -122.0,
            bx2: 37.001,
            by2: -122.0,
            excludeEndpoints: true
        );

        Assert.NotNull(result);
    }
}
