using Xunit;

namespace Yaat.Sim.Tests;

public class GeoMathTests
{
    // -------------------------------------------------------------------------
    // DistanceNm
    // -------------------------------------------------------------------------

    [Fact]
    public void DistanceNm_IdenticalPoints_ReturnsZero()
    {
        double d = GeoMath.DistanceNm(37.6213, -122.3790, 37.6213, -122.3790);
        Assert.Equal(0.0, d, precision: 10);
    }

    [Fact]
    public void DistanceNm_SfoToOak_ReturnsApproximateDistance()
    {
        // SFO (37.6213, -122.3790) to OAK (37.7213, -122.2208) — roughly 8-9 nm
        double d = GeoMath.DistanceNm(37.6213, -122.3790, 37.7213, -122.2208);
        Assert.InRange(d, 8.0, 10.0);
    }

    [Fact]
    public void DistanceNm_OneDegreeLat_ApproximatelySixtyNm()
    {
        // 1 degree of latitude ≈ 60 nm (NmPerDegLat constant in GeoMath)
        double d = GeoMath.DistanceNm(0.0, 0.0, 1.0, 0.0);
        Assert.InRange(d, 59.9, 60.1);
    }

    // -------------------------------------------------------------------------
    // BearingTo
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(0.0, 0.0, 1.0, 0.0, 0.0, 1.0)] // due north → 0°
    [InlineData(0.0, 0.0, 0.0, 1.0, 89.0, 91.0)] // due east  → 90°
    [InlineData(1.0, 0.0, 0.0, 0.0, 179.0, 181.0)] // due south → 180°
    [InlineData(0.0, 1.0, 0.0, 0.0, 269.0, 271.0)] // due west  → 270°
    public void BearingTo_CardinalDirections_ReturnsExpectedRange(
        double lat1,
        double lon1,
        double lat2,
        double lon2,
        double minExpected,
        double maxExpected
    )
    {
        double bearing = GeoMath.BearingTo(lat1, lon1, lat2, lon2);
        Assert.InRange(bearing, minExpected, maxExpected);
    }

    [Fact]
    public void BearingTo_DueNorth_ReturnsBearingNear0Or360()
    {
        double bearing = GeoMath.BearingTo(0.0, 0.0, 1.0, 0.0);
        // Normalize: due north wraps at 0/360 boundary — accept either
        bool isNortherly = bearing < 1.0 || bearing > 359.0;
        Assert.True(isNortherly, $"Expected bearing near 0° or 360°, got {bearing}°");
    }

    [Fact]
    public void BearingTo_Northeast_ReturnsApproximately45Degrees()
    {
        double bearing = GeoMath.BearingTo(0.0, 0.0, 1.0, 1.0);
        Assert.InRange(bearing, 43.0, 47.0);
    }

    [Fact]
    public void BearingTo_ReturnsBearingIn0To360Range()
    {
        double bearing = GeoMath.BearingTo(10.0, 20.0, 5.0, 15.0);
        Assert.InRange(bearing, 0.0, 360.0);
    }

    // -------------------------------------------------------------------------
    // TurnHeadingToward
    // -------------------------------------------------------------------------

    [Fact]
    public void TurnHeadingToward_CurrentEqualsTarget_ReturnsTarget()
    {
        double result = GeoMath.TurnHeadingToward(new TrueHeading(270.0), 270.0, 30.0).Degrees;
        Assert.Equal(270.0, result, precision: 5);
    }

    [Fact]
    public void TurnHeadingToward_DiffWithinMax_ReturnsTarget()
    {
        // 350 → 10: shortest path is right 20°, max 30° → snaps to target
        double result = GeoMath.TurnHeadingToward(new TrueHeading(350.0), 10.0, 30.0).Degrees;
        Assert.Equal(10.0, result, precision: 5);
    }

    [Fact]
    public void TurnHeadingToward_DiffExceedsMax_TurnsRightByMax()
    {
        // 350 → 10: shortest path is right 20°... wait, 20 > 5, so turn right 5° → 355
        double result = GeoMath.TurnHeadingToward(new TrueHeading(350.0), 10.0, 5.0).Degrees;
        Assert.Equal(355.0, result, precision: 5);
    }

    [Fact]
    public void TurnHeadingToward_LeftTurnAcross360_TurnsLeftByMax()
    {
        // 10 → 350: shortest path is left 20°, max 5° → turn left → 5
        double result = GeoMath.TurnHeadingToward(new TrueHeading(10.0), 350.0, 5.0).Degrees;
        Assert.Equal(5.0, result, precision: 5);
    }

    [Fact]
    public void TurnHeadingToward_180DegreeDiff_TurnsRightByMax()
    {
        // 0 → 180: diff = 180, sign(180) = +1, turns right
        double result = GeoMath.TurnHeadingToward(new TrueHeading(0.0), 180.0, 10.0).Degrees;
        Assert.Equal(10.0, result, precision: 5);
    }

    [Fact]
    public void TurnHeadingToward_OppositeViaLeft_TurnsLeftByMax()
    {
        // 180 → 0: diff = -180, sign(-180) = -1, turns left
        double result = GeoMath.TurnHeadingToward(new TrueHeading(180.0), 0.0, 10.0).Degrees;
        Assert.Equal(170.0, result, precision: 5);
    }

    [Fact]
    public void TurnHeadingToward_ResultAlwaysIn0To360Range()
    {
        // Turning from near 0 leftward should not produce negative result
        double result = GeoMath.TurnHeadingToward(new TrueHeading(2.0), 350.0, 10.0).Degrees;
        Assert.InRange(result, 0.0, 360.0);
    }

    // -------------------------------------------------------------------------
    // ProjectPoint
    // -------------------------------------------------------------------------

    [Fact]
    public void ProjectPoint_DueNorth60Nm_LatIncreasesBy1Degree()
    {
        var (lat, lon) = GeoMath.ProjectPoint(0.0, 0.0, new TrueHeading(0.0), 60.0);
        Assert.InRange(lat, 0.99, 1.01);
        Assert.Equal(0.0, lon, precision: 5);
    }

    [Fact]
    public void ProjectPoint_DueEast60NmAtEquator_LonIncreasesBy1Degree()
    {
        var (lat, lon) = GeoMath.ProjectPoint(0.0, 0.0, new TrueHeading(90.0), 60.0);
        Assert.Equal(0.0, lat, precision: 5);
        Assert.InRange(lon, 0.99, 1.01);
    }

    [Fact]
    public void ProjectPoint_DueSouth60Nm_LatDecreases()
    {
        var (lat, _) = GeoMath.ProjectPoint(0.0, 0.0, new TrueHeading(180.0), 60.0);
        Assert.InRange(lat, -1.01, -0.99);
    }

    [Fact]
    public void ProjectPoint_DueWest60NmAtEquator_LonDecreases()
    {
        var (_, lon) = GeoMath.ProjectPoint(0.0, 0.0, new TrueHeading(270.0), 60.0);
        Assert.InRange(lon, -1.01, -0.99);
    }

    [Fact]
    public void ProjectPoint_RoundTrip_ProjectedDistanceMatchesInput()
    {
        double startLat = 37.6213;
        double startLon = -122.3790;
        double headingDeg = 045.0;
        double distanceNm = 25.0;

        var (newLat, newLon) = GeoMath.ProjectPoint(startLat, startLon, new TrueHeading(headingDeg), distanceNm);
        double roundTripDist = GeoMath.DistanceNm(startLat, startLon, newLat, newLon);

        Assert.InRange(roundTripDist, 24.9, 25.1);
    }

    // -------------------------------------------------------------------------
    // GenerateArcPoints
    // -------------------------------------------------------------------------

    [Fact]
    public void GenerateArcPoints_RightArc90Degrees_Step30_Returns3Points()
    {
        // Right turn 0→90, step 30: intermediates at 30, 60; end at 90 → 3 total
        var pts = GeoMath.GenerateArcPoints(0.0, 0.0, 10.0, 0.0, 90.0, turnRight: true, stepDeg: 30.0);
        Assert.Equal(3, pts.Count);
    }

    [Fact]
    public void GenerateArcPoints_ArcSmallerThanStep_ReturnsOnlyEndPoint()
    {
        // Right turn 0→3, step 5: totalSweep=3 < stepDeg=5, loop never runs → only end
        var pts = GeoMath.GenerateArcPoints(0.0, 0.0, 10.0, 0.0, 3.0, turnRight: true, stepDeg: 5.0);
        Assert.Single(pts);
    }

    [Fact]
    public void GenerateArcPoints_FullCircle_Returns72Points()
    {
        // start==end, turnRight: totalSweep=0 → +=360; step=5 → 71 intermediates + 1 end = 72
        var pts = GeoMath.GenerateArcPoints(0.0, 0.0, 10.0, 0.0, 0.0, turnRight: true, stepDeg: 5.0);
        Assert.Equal(72, pts.Count);
    }

    [Fact]
    public void GenerateArcPoints_LeftArc90Degrees_Step30_Returns3Points()
    {
        // Left turn 90→0, step 30: intermediates at 60, 30; end at 0 → 3 total
        var pts = GeoMath.GenerateArcPoints(0.0, 0.0, 10.0, 90.0, 0.0, turnRight: false, stepDeg: 30.0);
        Assert.Equal(3, pts.Count);
    }

    [Fact]
    public void GenerateArcPoints_EndPointMatchesProjectedEndBearing()
    {
        double centerLat = 37.0;
        double centerLon = -122.0;
        double radiusNm = 5.0;
        double endBearing = 90.0;

        var pts = GeoMath.GenerateArcPoints(centerLat, centerLon, radiusNm, 0.0, endBearing, turnRight: true, stepDeg: 30.0);
        var expectedEnd = GeoMath.ProjectPoint(centerLat, centerLon, new TrueHeading(endBearing), radiusNm);

        Assert.Equal(expectedEnd.Lat, pts[^1].Lat, precision: 10);
        Assert.Equal(expectedEnd.Lon, pts[^1].Lon, precision: 10);
    }

    [Fact]
    public void GenerateArcPoints_RightArcAcross360Boundary_ReturnsCorrectCount()
    {
        // Right turn 330→60: totalSweep = 60-330 = -270 → +=360 = 90; step 30 → intermediates at 360/0, 30; end 60 → 3 total
        var pts = GeoMath.GenerateArcPoints(0.0, 0.0, 10.0, 330.0, 60.0, turnRight: true, stepDeg: 30.0);
        Assert.Equal(3, pts.Count);
    }

    // -------------------------------------------------------------------------
    // SignedCrossTrackDistanceNm
    // -------------------------------------------------------------------------

    [Fact]
    public void SignedCrossTrackDistanceNm_PointOnHeadingLine_ReturnsNearZero()
    {
        // Heading north; point directly ahead on the same meridian → cross-track ≈ 0
        double xtk = GeoMath.SignedCrossTrackDistanceNm(1.0, 0.0, 0.0, 0.0, new TrueHeading(0.0));
        Assert.InRange(xtk, -0.01, 0.01);
    }

    [Fact]
    public void SignedCrossTrackDistanceNm_PointToRight_ReturnsPositive()
    {
        // Heading north (0°), point shifted east (+lon) → right of track → positive
        double xtk = GeoMath.SignedCrossTrackDistanceNm(0.0, 1.0, 0.0, 0.0, new TrueHeading(0.0));
        Assert.True(xtk > 0, $"Expected positive cross-track for point east of northbound heading, got {xtk}");
    }

    [Fact]
    public void SignedCrossTrackDistanceNm_PointToLeft_ReturnsNegative()
    {
        // Heading north (0°), point shifted west (-lon) → left of track → negative
        double xtk = GeoMath.SignedCrossTrackDistanceNm(0.0, -1.0, 0.0, 0.0, new TrueHeading(0.0));
        Assert.True(xtk < 0, $"Expected negative cross-track for point west of northbound heading, got {xtk}");
    }

    [Fact]
    public void SignedCrossTrackDistanceNm_Symmetry_OppositeSignsForMirroredPoints()
    {
        double xtkRight = GeoMath.SignedCrossTrackDistanceNm(0.0, 0.5, 0.0, 0.0, new TrueHeading(0.0));
        double xtkLeft = GeoMath.SignedCrossTrackDistanceNm(0.0, -0.5, 0.0, 0.0, new TrueHeading(0.0));
        Assert.Equal(xtkRight, -xtkLeft, precision: 5);
    }

    // -------------------------------------------------------------------------
    // AlongTrackDistanceNm
    // -------------------------------------------------------------------------

    [Fact]
    public void AlongTrackDistanceNm_PointAhead_ReturnsPositive()
    {
        // Heading north, point north of reference → positive
        double atd = GeoMath.AlongTrackDistanceNm(1.0, 0.0, 0.0, 0.0, new TrueHeading(0.0));
        Assert.True(atd > 0, $"Expected positive along-track for point ahead, got {atd}");
    }

    [Fact]
    public void AlongTrackDistanceNm_PointBehind_ReturnsNegative()
    {
        // Heading north, point south of reference → negative
        double atd = GeoMath.AlongTrackDistanceNm(-1.0, 0.0, 0.0, 0.0, new TrueHeading(0.0));
        Assert.True(atd < 0, $"Expected negative along-track for point behind, got {atd}");
    }

    [Fact]
    public void AlongTrackDistanceNm_PointAt90Degrees_ReturnsNearZero()
    {
        // Heading north, point due east → 90° off heading → along-track ≈ 0
        double atd = GeoMath.AlongTrackDistanceNm(0.0, 1.0, 0.0, 0.0, new TrueHeading(0.0));
        Assert.InRange(atd, -0.01, 0.01);
    }

    [Fact]
    public void AlongTrackDistanceNm_PointDirectlyAhead_ApproximatesDistance()
    {
        // Heading north; point 1° north (≈ 60 nm); along-track should ≈ DistanceNm
        double dist = GeoMath.DistanceNm(0.0, 0.0, 1.0, 0.0);
        double atd = GeoMath.AlongTrackDistanceNm(1.0, 0.0, 0.0, 0.0, new TrueHeading(0.0));
        Assert.InRange(atd, dist - 0.01, dist + 0.01);
    }
}
