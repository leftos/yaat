using Xunit;
using Yaat.Sim.Scenarios;

namespace Yaat.Sim.Tests.Scenarios;

/// <summary>
/// 14 CFR 91.159(a) / AIM 3-1-5 TBL 3-1-2: magnetic course 0°–179° flies odd thousands + 500,
/// 180°–359° flies even thousands + 500.
/// </summary>
public class HemisphericAltitudeTests
{
    [Theory]
    [InlineData(0, 3500)]
    [InlineData(90, 5500)]
    [InlineData(179, 7500)]
    [InlineData(180, 4500)]
    [InlineData(270, 6500)]
    [InlineData(359, 8500)]
    public void ConformingAltitudes_AreRecognised(double courseMagnetic, double altitudeFt) =>
        Assert.True(HemisphericAltitude.IsConforming(courseMagnetic, altitudeFt));

    [Theory]
    [InlineData(90, 4500)] // eastbound at a westbound level
    [InlineData(270, 5500)] // westbound at an eastbound level
    [InlineData(90, 5000)] // not an X500 at all
    public void NonConformingAltitudes_AreRejected(double courseMagnetic, double altitudeFt) =>
        Assert.False(HemisphericAltitude.IsConforming(courseMagnetic, altitudeFt));

    [Fact]
    public void Snap_PicksTheNearestConformingLevelInsideTheBand()
    {
        // Eastbound wants odd+500. 5200 is nearest 5500 (in band), not 3500.
        var snapped = HemisphericAltitude.Snap(magneticCourseDeg: 90, desiredFt: 5200, minFt: 3000, maxFt: 8000);
        Assert.Equal(5500, snapped);
        Assert.True(HemisphericAltitude.IsConforming(90, snapped!.Value));
    }

    [Fact]
    public void Snap_WestboundPicksEvenThousandsPlusFiveHundred()
    {
        var snapped = HemisphericAltitude.Snap(magneticCourseDeg: 270, desiredFt: 5200, minFt: 3000, maxFt: 8000);
        Assert.Equal(4500, snapped);
    }

    [Fact]
    public void Snap_WrapsCourseIntoRange()
    {
        Assert.Equal(HemisphericAltitude.Snap(90, 5200, 3000, 8000), HemisphericAltitude.Snap(450, 5200, 3000, 8000));
        Assert.Equal(HemisphericAltitude.Snap(270, 5200, 3000, 8000), HemisphericAltitude.Snap(-90, 5200, 3000, 8000));
    }

    /// <summary>
    /// A band too narrow to hold a conforming level still snaps, so long as one sits within the tolerance —
    /// flying the correct hemisphere matters more than honouring an over-tight authored band.
    /// </summary>
    [Fact]
    public void Snap_AcceptsALevelJustOutsideANarrowBand()
    {
        // Eastbound: only 5500 is legal nearby, and it sits 200 ft above the band ceiling.
        var snapped = HemisphericAltitude.Snap(magneticCourseDeg: 90, desiredFt: 5300, minFt: 5000, maxFt: 5300);
        Assert.Equal(5500, snapped);
    }

    /// <summary>Beyond the tolerance there is no honest answer — the caller keeps the raw altitude and warns.</summary>
    [Fact]
    public void Snap_ReturnsNull_WhenNoConformingLevelIsWithinTolerance()
    {
        // Eastbound wants 5500 or 7500. The band 6100-6300 sits 600 ft above the first and 1200 ft below the
        // second, so neither is reachable within the 500 ft tolerance.
        Assert.Null(HemisphericAltitude.Snap(magneticCourseDeg: 90, desiredFt: 6200, minFt: 6100, maxFt: 6300));
    }

    /// <summary>A level exactly at the tolerance edge is still accepted.</summary>
    [Fact]
    public void Snap_AcceptsALevelExactlyAtTheToleranceEdge()
    {
        // 5500 is exactly 500 ft below the band floor.
        Assert.Equal(5500, HemisphericAltitude.Snap(magneticCourseDeg: 90, desiredFt: 6100, minFt: 6000, maxFt: 6200));
    }

    [Fact]
    public void Snap_NeverReturnsANegativeLevel()
    {
        var snapped = HemisphericAltitude.Snap(magneticCourseDeg: 180, desiredFt: 500, minFt: 0, maxFt: 1000);
        Assert.NotNull(snapped);
        Assert.True(snapped >= 0);
    }
}
