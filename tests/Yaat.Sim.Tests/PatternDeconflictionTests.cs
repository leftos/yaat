using Xunit;
using Yaat.Sim.Phases;
using Yaat.Sim.Testing;

namespace Yaat.Sim.Tests;

public class PatternDeconflictionTests
{
    public PatternDeconflictionTests()
    {
        TestVnasData.EnsureInitialized();
    }

    /// <summary>
    /// Measures perpendicular offset of the downwind abeam point from the runway centerline.
    /// </summary>
    private static double MeasureDownwindOffset(PatternWaypoints waypoints, RunwayInfo runway)
    {
        return Math.Abs(
            GeoMath.SignedCrossTrackDistanceNm(
                waypoints.DownwindAbeamLat,
                waypoints.DownwindAbeamLon,
                runway.ThresholdLatitude,
                runway.ThresholdLongitude,
                runway.TrueHeading
            )
        );
    }

    // -------------------------------------------------------------------------
    // OAK: 28L/10R, 28R/10L (~1000ft apart), 30/12 (~6300ft left of 28L), 15/33
    // -------------------------------------------------------------------------

    [Fact]
    public void LeftTraffic28L_Jet_ShrunkForRunway30()
    {
        var navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return;
        }

        var rwy28L = navDb.GetRunway("KOAK", "28L");
        Assert.NotNull(rwy28L);

        var allRunways = navDb.GetRunways("KOAK");

        // Jet default pattern is 1.5nm. Rwy 30 is ~1.04nm to the left.
        // Deconfliction should shrink the pattern to ~0.89nm (1.04 - 0.15 buffer).
        var waypoints = PatternGeometry.Compute(rwy28L, AircraftCategory.Jet, PatternDirection.Left, null, null, allRunways);

        double defaultSize = CategoryPerformance.PatternSizeNm(AircraftCategory.Jet);
        double actual = MeasureDownwindOffset(waypoints, rwy28L);

        Assert.True(actual < defaultSize, $"Jet downwind offset {actual:F3} should be less than default {defaultSize:F3}");
        Assert.True(
            actual >= PatternGeometry.MinPatternSizeNm,
            $"Jet downwind offset {actual:F3} should be at least minimum {PatternGeometry.MinPatternSizeNm:F3}"
        );
    }

    [Fact]
    public void LeftTraffic28L_Piston_AlreadyFits_NoShrinkage()
    {
        var navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return;
        }

        var rwy28L = navDb.GetRunway("KOAK", "28L");
        Assert.NotNull(rwy28L);

        var allRunways = navDb.GetRunways("KOAK");

        // Piston default 0.75nm < rwy 30 distance (1.04nm - 0.15nm buffer = 0.89nm)
        // No shrinkage needed.
        var waypoints = PatternGeometry.Compute(rwy28L, AircraftCategory.Piston, PatternDirection.Left, null, null, allRunways);

        double defaultSize = CategoryPerformance.PatternSizeNm(AircraftCategory.Piston);
        double actual = MeasureDownwindOffset(waypoints, rwy28L);

        Assert.True(actual >= defaultSize - 0.01, $"Piston should use default {defaultSize:F3}, got {actual:F3}");
    }

    [Fact]
    public void RightTraffic28L_Jet_NoConflictOnRightSide()
    {
        var navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return;
        }

        var rwy28L = navDb.GetRunway("KOAK", "28L");
        Assert.NotNull(rwy28L);

        var allRunways = navDb.GetRunways("KOAK");

        // Right traffic: rwy 30 is on the left, no conflict on right side
        var waypoints = PatternGeometry.Compute(rwy28L, AircraftCategory.Jet, PatternDirection.Right, null, null, allRunways);

        double defaultSize = CategoryPerformance.PatternSizeNm(AircraftCategory.Jet);
        double actual = MeasureDownwindOffset(waypoints, rwy28L);

        Assert.True(actual >= defaultSize - 0.01, $"Right traffic should use default {defaultSize:F3}, got {actual:F3}");
    }

    [Fact]
    public void LeftTraffic28R_TooCloseFor28L_SkipsDeconfliction()
    {
        var navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return;
        }

        var rwy28R = navDb.GetRunway("KOAK", "28R");
        Assert.NotNull(rwy28R);

        var allRunways = navDb.GetRunways("KOAK");

        // 28L is only ~0.16nm to the left of 28R. 0.16 - 0.15 buffer = 0.01nm, below min floor.
        // Deconfliction should skip — use default size.
        var waypoints = PatternGeometry.Compute(rwy28R, AircraftCategory.Piston, PatternDirection.Left, null, null, allRunways);

        double defaultSize = CategoryPerformance.PatternSizeNm(AircraftCategory.Piston);
        double actual = MeasureDownwindOffset(waypoints, rwy28R);

        Assert.True(actual >= defaultSize - 0.01, $"Should use default {defaultSize:F3} when parallels too close, got {actual:F3}");
    }

    // -------------------------------------------------------------------------
    // Crossing detection
    // -------------------------------------------------------------------------

    [Fact]
    public void RunwaysCross_ConvergingRunways_DoNotCross()
    {
        var navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return;
        }

        var rwy28L = navDb.GetRunway("KOAK", "28L");
        var rwy30 = navDb.GetRunway("KOAK", "30");
        Assert.NotNull(rwy28L);
        Assert.NotNull(rwy30);

        Assert.False(PatternGeometry.RunwaysCross(rwy28L, rwy30), "28L and 30 converge but their surfaces do not physically intersect");
    }

    [Fact]
    public void RunwaysCross_SyntheticCrossing_DetectedCorrectly()
    {
        // Two runways forming an X shape
        var rwyNS = TestRunwayFactory.Make(
            designator: "36",
            airportId: "KTEST",
            heading: 360,
            thresholdLat: 37.0,
            thresholdLon: -122.0,
            endLat: 37.02,
            endLon: -122.0
        );

        var rwyEW = TestRunwayFactory.Make(
            designator: "27",
            airportId: "KTEST",
            heading: 270,
            thresholdLat: 37.01,
            thresholdLon: -121.99,
            endLat: 37.01,
            endLon: -122.01
        );

        Assert.True(PatternGeometry.RunwaysCross(rwyNS, rwyEW));
    }

    [Fact]
    public void RunwaysCross_ParallelRunways_DoNotCross()
    {
        var navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return;
        }

        var rwy28L = navDb.GetRunway("KOAK", "28L");
        var rwy28R = navDb.GetRunway("KOAK", "28R");
        Assert.NotNull(rwy28L);
        Assert.NotNull(rwy28R);

        Assert.False(PatternGeometry.RunwaysCross(rwy28L, rwy28R));
    }

    // -------------------------------------------------------------------------
    // Edge cases
    // -------------------------------------------------------------------------

    [Fact]
    public void NoRunwayData_UsesDefaultSize()
    {
        var runway = TestRunwayFactory.Make(designator: "28", heading: 280, elevationFt: 100);

        var waypoints = PatternGeometry.Compute(runway, AircraftCategory.Piston, PatternDirection.Left, null, null, null);

        double defaultSize = CategoryPerformance.PatternSizeNm(AircraftCategory.Piston);
        double actual = MeasureDownwindOffset(waypoints, runway);

        Assert.True(actual >= defaultSize - 0.01, $"Without runway data, should use default {defaultSize:F3}, got {actual:F3}");
    }

    [Fact]
    public void SingleRunwayAirport_UsesDefaultSize()
    {
        var runway = TestRunwayFactory.Make(designator: "28", heading: 280, elevationFt: 100);

        var waypoints = PatternGeometry.Compute(runway, AircraftCategory.Piston, PatternDirection.Left, null, null, [runway]);

        double defaultSize = CategoryPerformance.PatternSizeNm(AircraftCategory.Piston);
        double actual = MeasureDownwindOffset(waypoints, runway);

        Assert.True(actual >= defaultSize - 0.01, $"Single runway should use default {defaultSize:F3}, got {actual:F3}");
    }
}
