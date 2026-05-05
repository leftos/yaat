using Xunit;
using Yaat.Sim;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;

namespace Yaat.Sim.Tests;

/// <summary>
/// Verifies <see cref="PatternEntryPhase.ClassifyDownwindEntry"/> across the
/// (angular delta) × (side of runway) classification grid:
/// <list type="bullet">
/// <item>Direct (≤20°): regardless of side.</item>
/// <item>FortyFive (20-60°): pattern side or near centerline → FortyFive; non-pattern side → Midfield.</item>
/// <item>Perpendicular (>60°): pattern side → Midfield; non-pattern side or near centerline → Crosswind.</item>
/// </list>
/// All scenarios use RWY 27 (heading 270°, downwind course 90°). For right
/// traffic, downwind lies on the right of the runway heading — i.e. north of
/// the runway. For left traffic, the mirror.
/// </summary>
public class PatternEntryKindTests
{
    private static readonly LatLon Threshold = new(37.0, -122.0);
    private static readonly TrueHeading RunwayHeading = new(270);
    private static readonly TrueHeading DownwindCourse = new(90);

    private static LatLon PointAt(double bearingDeg, double distNm) => GeoMath.ProjectPoint(Threshold, new TrueHeading(bearingDeg), distNm);

    // For RWY 27 with right traffic, north of the runway is the pattern side.
    private static LatLon NorthOfRunway2nm => PointAt(0, 2.0);
    private static LatLon SouthOfRunway2nm => PointAt(180, 2.0);
    private static LatLon NearCenterline => PointAt(0, 0.1);

    private static PatternEntryKind Classify(LatLon position, TrueHeading track, PatternDirection direction) =>
        PatternEntryPhase.ClassifyDownwindEntry(position, track, Threshold, RunwayHeading, DownwindCourse, direction);

    [Theory]
    [InlineData(90)]
    [InlineData(80)]
    [InlineData(100)]
    [InlineData(70)]
    [InlineData(110)]
    public void Direct_RegardlessOfSide(double trackDeg)
    {
        var track = new TrueHeading(trackDeg);
        Assert.Equal(PatternEntryKind.Direct, Classify(NorthOfRunway2nm, track, PatternDirection.Right));
        Assert.Equal(PatternEntryKind.Direct, Classify(SouthOfRunway2nm, track, PatternDirection.Right));
    }

    [Fact]
    public void FortyFive_OnPatternSide_RightTraffic()
    {
        // Aircraft north (pattern side), inbound at 45° to downwind course (track 045° vs downwind 090° = 45°).
        var track = new TrueHeading(45);
        Assert.Equal(PatternEntryKind.FortyFive, Classify(NorthOfRunway2nm, track, PatternDirection.Right));
    }

    [Fact]
    public void FortyFive_OnNonPatternSide_IsMidfield()
    {
        // Aircraft south (non-pattern side) at 45° angular delta — geometrically not an AIM 4-3-3 entry.
        var track = new TrueHeading(45);
        Assert.Equal(PatternEntryKind.Midfield, Classify(SouthOfRunway2nm, track, PatternDirection.Right));
    }

    [Fact]
    public void Crosswind_FromNonPatternSide_RightTraffic()
    {
        // Aircraft south of runway (upwind/non-pattern side), perpendicular northbound — true crosswind→downwind.
        var track = new TrueHeading(0);
        Assert.Equal(PatternEntryKind.Crosswind, Classify(SouthOfRunway2nm, track, PatternDirection.Right));
    }

    [Fact]
    public void Perpendicular_FromPatternSide_IsMidfield()
    {
        // The user's bug: aircraft north of right-traffic runway, southbound.
        // Perpendicular to downwind but already on pattern side — the join turn opposes pattern direction.
        var track = new TrueHeading(180);
        Assert.Equal(PatternEntryKind.Midfield, Classify(NorthOfRunway2nm, track, PatternDirection.Right));
    }

    [Fact]
    public void Perpendicular_NearCenterline_BiasedToCrosswind()
    {
        // Within centerline epsilon: ambiguous side, bias to the friendly label for the angle bucket.
        var track = new TrueHeading(0);
        Assert.Equal(PatternEntryKind.Crosswind, Classify(NearCenterline, track, PatternDirection.Right));
    }

    [Fact]
    public void LeftTraffic_MirrorsRightTraffic()
    {
        // For left traffic, pattern side is south. Aircraft south + perpendicular = on pattern side = Midfield.
        var track = new TrueHeading(0);
        Assert.Equal(PatternEntryKind.Midfield, Classify(SouthOfRunway2nm, track, PatternDirection.Left));

        // Aircraft north + perpendicular for left traffic = non-pattern side = real Crosswind.
        Assert.Equal(PatternEntryKind.Crosswind, Classify(NorthOfRunway2nm, track, PatternDirection.Left));

        // 45° from south (pattern side for left traffic) = FortyFive.
        var fortyFiveTrack = new TrueHeading(135);
        Assert.Equal(PatternEntryKind.FortyFive, Classify(SouthOfRunway2nm, fortyFiveTrack, PatternDirection.Left));
    }
}
