using Xunit;
using Yaat.Sim.Phases;

namespace Yaat.Sim.Tests;

/// <summary>
/// Issue #412: pattern width must never be below the turn-radius flyability floor —
/// r(downwind speed) + r(base speed) — no matter where the requested size came from
/// (category default, authored airport data, PSIZE command, or runway deconfliction).
/// A narrower pattern geometrically forces the downwind→final turn through the final
/// approach course (AIM FIG 4-3-3 key 7: never a track that penetrates a parallel's
/// final). At OAK a PAY3 given the authored 0.5 nm 28L pattern rolled out on the 28R
/// final, ~1,370 ft right of the assigned centerline.
/// </summary>
public class PatternFlyabilityFloorTests
{
    public PatternFlyabilityFloorTests()
    {
        TestVnasData.EnsureInitialized();
    }

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

    [Fact]
    public void Turboprop_AuthoredHalfMilePattern_WidenedToFloor()
    {
        var navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return;
        }

        var rwy28L = navDb.GetRunway("KOAK", "28L");
        Assert.NotNull(rwy28L);

        // OAK authors 28L at 0.5 nm (sized for light GA). Category-speed turboprop floor:
        // r(150) + r(130) at 4°/s ≈ 1.11 nm — the authored width must lose.
        double floor = PatternGeometry.MinFlyablePatternSizeNm("", AircraftCategory.Turboprop, 0);
        var waypoints = PatternGeometry.Compute(
            rwy28L,
            AircraftCategory.Turboprop,
            "",
            0,
            PatternDirection.Left,
            0.5,
            null,
            navDb.GetRunways("KOAK"),
            authoredRunway: null
        );

        double actual = MeasureDownwindOffset(waypoints, rwy28L);
        Assert.True(actual >= floor - 0.01, $"Turboprop pattern {actual:F3} nm should be floored at {floor:F3} nm");
        Assert.Equal(floor, waypoints.PatternSizeNm, 2);
    }

    [Fact]
    public void Piston_DefaultPattern_NotWidened()
    {
        var navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return;
        }

        var rwy28R = navDb.GetRunway("KOAK", "28R");
        Assert.NotNull(rwy28R);

        // Piston default 0.75 nm already exceeds the category floor (r(90) + r(80) at 5°/s
        // ≈ 0.54 nm) — right traffic 28R has nothing to deconflict against, so the default
        // must come through untouched.
        double floor = PatternGeometry.MinFlyablePatternSizeNm("", AircraftCategory.Piston, 0);
        double defaultSize = CategoryPerformance.PatternSizeNm(AircraftCategory.Piston);
        Assert.True(floor < defaultSize, $"Piston floor {floor:F3} should be below the default {defaultSize:F3}");

        var waypoints = PatternGeometry.Compute(
            rwy28R,
            AircraftCategory.Piston,
            "",
            0,
            PatternDirection.Right,
            null,
            null,
            navDb.GetRunways("KOAK"),
            authoredRunway: null
        );

        Assert.Equal(defaultSize, waypoints.PatternSizeNm, 2);
    }

    [Fact]
    public void FloorWinsOverRunwayDeconfliction()
    {
        var navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return;
        }

        var rwy28L = navDb.GetRunway("KOAK", "28L");
        Assert.NotNull(rwy28L);

        // Left traffic 28L deconflicts against runway 30 (~1.04 nm south) down to ~0.89 nm —
        // below the category-jet floor (~1.96 nm). Overshooting the final onto the 28R
        // parallel is strictly worse than a downwind overlying 30, so the floor wins.
        double floor = PatternGeometry.MinFlyablePatternSizeNm("", AircraftCategory.Jet, 0);
        var waypoints = PatternGeometry.Compute(
            rwy28L,
            AircraftCategory.Jet,
            "",
            0,
            PatternDirection.Left,
            null,
            null,
            navDb.GetRunways("KOAK"),
            authoredRunway: null
        );

        Assert.Equal(floor, waypoints.PatternSizeNm, 2);
    }

    [Fact]
    public void WindInflatesTheFloor()
    {
        double calm = PatternGeometry.MinFlyablePatternSizeNm("", AircraftCategory.Turboprop, 0);
        double windy = PatternGeometry.MinFlyablePatternSizeNm("", AircraftCategory.Turboprop, 15);

        // r scales linearly with speed at a fixed turn rate: +15 kt on both legs adds
        // 2 × 15 / (4°/s × 62.832) ≈ 0.12 nm.
        Assert.True(windy > calm + 0.10, $"15 kt wind should widen the floor ({calm:F3} → {windy:F3})");
    }

    [Fact]
    public void BaseExtensionScalesWithFlooredSize()
    {
        var navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return;
        }

        var rwy28L = navDb.GetRunway("KOAK", "28L");
        Assert.NotNull(rwy28L);

        // The floor must be applied BEFORE the base-extension scaling: a 0.5 nm request
        // floored to ~1.11 nm keeps sizeRatio ≈ 1.11, so the base extension (and with it
        // the final-approach length) grows with the pattern instead of staying at the
        // too-short 0.5-ratio value that rolled out at ~240 ft AGL.
        var waypoints = PatternGeometry.Compute(
            rwy28L,
            AircraftCategory.Turboprop,
            "",
            0,
            PatternDirection.Left,
            0.5,
            null,
            null,
            authoredRunway: null
        );

        double baseExtNm = GeoMath.AlongTrackDistanceNm(
            waypoints.BaseTurnLat,
            waypoints.BaseTurnLon,
            waypoints.DownwindAbeamLat,
            waypoints.DownwindAbeamLon,
            waypoints.DownwindHeading
        );
        double expectedRatio = waypoints.PatternSizeNm / CategoryPerformance.PatternSizeNm(AircraftCategory.Turboprop);
        double expected = CategoryPerformance.BaseExtensionNm(AircraftCategory.Turboprop) * expectedRatio;
        Assert.Equal(expected, baseExtNm, 1);
    }
}
