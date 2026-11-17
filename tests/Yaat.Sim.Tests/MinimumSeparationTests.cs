using Xunit;

namespace Yaat.Sim.Tests;

/// <summary>
/// The STARS min-sep projection, CRC's stepped loop: both tracks advance at constant ground speed and track in 5 s steps, at
/// most 760 of them, until the distance first stops shrinking; a first step that closes by less than 0.0001 degrees reads
/// as diverging. Positions are laid out in NM east and north of a reference point in the loop's own planar frame (longitude
/// scaled by the reference latitude), so each expectation is computed by hand.
/// </summary>
public class MinimumSeparationTests
{
    private const double RefLat = 37.0;

    private const double RefLon = -122.0;

    private const double TolNm = 0.05;

    // CRC's NM per degree of longitude at the reference latitude, over 60: the loop's longitude scale.
    private static readonly double LonScale = 59.998660261511354 * Math.Cos(RefLat * Math.PI / 180.0) / 60.0;

    private static LatLon At(double eastNm, double northNm) => new(RefLat + (northNm / 60.0), RefLon + (eastNm / (60.0 * LonScale)));

    private static MinSepTrack Track(double eastNm, double northNm, double trackDeg, double groundSpeedKts) =>
        new(At(eastNm, northNm), trackDeg, groundSpeedKts);

    private static void AssertAt(LatLon expected, LatLon actual) =>
        Assert.True(GeoMath.DistanceNm(expected, actual) <= TolNm, $"{actual} is not at {expected}");

    // 240 kt each way closes 0.6667 NM a step, so 10 NM meets exactly on step 15.
    [Fact]
    public void HeadOn_Converging()
    {
        MinSepResult result = MinimumSeparation.Compute(Track(0, 0, 360, 240), Track(0, 10, 180, 240));

        MinSepConverging converging = Assert.IsType<MinSepConverging>(result);
        Assert.True(converging.ClosestNm <= TolNm, $"closest {converging.ClosestNm}");
        AssertAt(At(0, 5), converging.PointA);
        AssertAt(At(0, 5), converging.PointB);
    }

    // Relative position (10, 12) NM, relative velocity (-240, -240) kt: t* = 165 s (step 33), CPA (-1, 1) NM, sqrt 2.
    [Fact]
    public void Crossing_Converging()
    {
        MinSepResult result = MinimumSeparation.Compute(Track(0, 0, 90, 240), Track(10, 12, 180, 240));

        MinSepConverging converging = Assert.IsType<MinSepConverging>(result);
        Assert.Equal(Math.Sqrt(2), converging.ClosestNm, TolNm);
        AssertAt(At(11, 0), converging.PointA);
        AssertAt(At(10, 1), converging.PointB);
    }

    [Fact]
    public void Diverging_Away()
    {
        MinSepResult result = MinimumSeparation.Compute(Track(0, 0, 270, 250), Track(8, 0, 90, 250));

        MinSepDiverging diverging = Assert.IsType<MinSepDiverging>(result);
        Assert.Equal(8.0, diverging.DistanceNm, TolNm);
    }

    [Fact]
    public void Parallel_SameSpeed()
    {
        MinSepResult result = MinimumSeparation.Compute(Track(0, 0, 90, 250), Track(0, 5, 90, 250));

        MinSepDiverging diverging = Assert.IsType<MinSepDiverging>(result);
        Assert.Equal(5.0, diverging.DistanceNm, TolNm);
    }

    // A 10 kt overtake from 20 NM behind closes 10.556 NM in 760 steps (3800 s): still closing at the cap. A has gone
    // 274.444 NM north, B 263.889 NM from 20 NM ahead, 9.444 NM apart.
    [Fact]
    public void Overtaking_SlowClosure_Capped()
    {
        MinSepResult result = MinimumSeparation.Compute(Track(0, 0, 360, 260), Track(0, 20, 360, 250));

        MinSepConverging converging = Assert.IsType<MinSepConverging>(result);
        Assert.Equal(20.0 - (10.0 * 3800.0 / 3600.0), converging.ClosestNm, TolNm);
        AssertAt(At(0, 260.0 * 3800.0 / 3600.0), converging.PointA);
        AssertAt(At(0, 20.0 + (250.0 * 3800.0 / 3600.0)), converging.PointB);
    }

    // Reciprocal tracks 1 NM apart laterally pass abeam on step 15 (5 NM each); step 16 opens, so the points stay at step 15.
    [Fact]
    public void StopsAtFirstNonShrink()
    {
        MinSepResult result = MinimumSeparation.Compute(Track(0, 0, 90, 240), Track(10, 1, 270, 240));

        MinSepConverging converging = Assert.IsType<MinSepConverging>(result);
        Assert.Equal(1.0, converging.ClosestNm, TolNm);
        AssertAt(At(5, 0), converging.PointA);
        AssertAt(At(5, 1), converging.PointB);
    }

    [Fact]
    public void Invalid_LowGroundSpeed() => Assert.IsType<MinSepInvalid>(MinimumSeparation.Compute(Track(0, 0, 90, 0.5), Track(10, 0, 270, 250)));

    // The first-step threshold, 0.0001 degrees in 5 s, is a closure of 4.32 kt: 4 kt reads as diverging, 5 kt as converging.
    [Theory]
    [InlineData(254.0, false)]
    [InlineData(255.0, true)]
    public void Overtake_FirstStepThreshold(double overtakerKts, bool converging)
    {
        MinSepResult result = MinimumSeparation.Compute(Track(0, 0, 360, overtakerKts), Track(0, 5, 360, 250));

        Assert.IsType(converging ? typeof(MinSepConverging) : typeof(MinSepDiverging), result);
    }

    [Theory]
    [InlineData(360.5, 250.0)]
    [InlineData(-1.0, 250.0)]
    [InlineData(90.0, 10001.0)]
    public void Invalid_TrackOrSpeedOutOfRange(double trackDeg, double groundSpeedKts) =>
        Assert.IsType<MinSepInvalid>(MinimumSeparation.Compute(Track(0, 0, 90, 250), Track(10, 0, trackDeg, groundSpeedKts)));
}
