using Xunit;
using Yaat.Sim;
using Yaat.Sim.Phases.Pattern;

namespace Yaat.Sim.Tests;

/// <summary>
/// Verifies <see cref="PatternEntryPhase.ClassifyDownwindEntry"/> for the three
/// downwind entry kinds — Direct, FortyFive, Crosswind — across the full 0-180°
/// angular range. Thresholds (aligned with AIM 4-3-3): Direct ≤20°,
/// FortyFive &gt;20°-60°, Crosswind &gt;60°.
/// </summary>
public class PatternEntryKindTests
{
    [Theory]
    [InlineData(0, PatternEntryKind.Direct)]
    [InlineData(10, PatternEntryKind.Direct)]
    [InlineData(20, PatternEntryKind.Direct)]
    [InlineData(21, PatternEntryKind.FortyFive)]
    [InlineData(45, PatternEntryKind.FortyFive)]
    [InlineData(60, PatternEntryKind.FortyFive)]
    [InlineData(61, PatternEntryKind.Crosswind)]
    [InlineData(90, PatternEntryKind.Crosswind)]
    [InlineData(135, PatternEntryKind.Crosswind)]
    [InlineData(180, PatternEntryKind.Crosswind)]
    public void Classify_ByAngularDelta(double deltaDeg, PatternEntryKind expected)
    {
        var downwind = new TrueHeading(270);
        var aircraftTrack = new TrueHeading(270 + deltaDeg);

        var result = PatternEntryPhase.ClassifyDownwindEntry(aircraftTrack, downwind);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Classify_IsSymmetric_NegativeDelta()
    {
        // Angular delta is absolute — left and right of downwind classify identically.
        var downwind = new TrueHeading(270);

        Assert.Equal(PatternEntryKind.Direct, PatternEntryPhase.ClassifyDownwindEntry(new TrueHeading(285), downwind));
        Assert.Equal(PatternEntryKind.Direct, PatternEntryPhase.ClassifyDownwindEntry(new TrueHeading(255), downwind));

        Assert.Equal(PatternEntryKind.FortyFive, PatternEntryPhase.ClassifyDownwindEntry(new TrueHeading(315), downwind));
        Assert.Equal(PatternEntryKind.FortyFive, PatternEntryPhase.ClassifyDownwindEntry(new TrueHeading(225), downwind));

        Assert.Equal(PatternEntryKind.Crosswind, PatternEntryPhase.ClassifyDownwindEntry(new TrueHeading(0), downwind));
        Assert.Equal(PatternEntryKind.Crosswind, PatternEntryPhase.ClassifyDownwindEntry(new TrueHeading(180), downwind));
    }
}
