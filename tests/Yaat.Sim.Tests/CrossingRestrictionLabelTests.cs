using Xunit;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim.Tests;

/// <summary>
/// Unit tests for <see cref="CrossingRestrictionLabel"/> — the ≥/≤, FL-aware label formatter that
/// drives the radar "Show nav route" crossing-restriction labels.
/// </summary>
public class CrossingRestrictionLabelTests
{
    private static CifpAltitudeRestriction Alt(CifpAltitudeRestrictionType type, int a1, int? a2 = null) => new(type, a1, a2);

    private static CifpSpeedRestriction Spd(int kts, CifpSpeedRestrictionType type = CifpSpeedRestrictionType.AtOrBelow) => new(kts, type);

    [Fact]
    public void NoRestrictions_ReturnsEmpty()
    {
        Assert.Empty(CrossingRestrictionLabel.BuildLines(null, null));
    }

    [Fact]
    public void At_RendersBareAltitude()
    {
        Assert.Equal(["6000"], CrossingRestrictionLabel.BuildLines(Alt(CifpAltitudeRestrictionType.At, 6000), null));
    }

    [Fact]
    public void AtOrAbove_RendersFloorGlyph()
    {
        Assert.Equal(["≥6000"], CrossingRestrictionLabel.BuildLines(Alt(CifpAltitudeRestrictionType.AtOrAbove, 6000), null));
    }

    [Fact]
    public void AtOrBelow_RendersCeilingGlyph()
    {
        Assert.Equal(["≤11000"], CrossingRestrictionLabel.BuildLines(Alt(CifpAltitudeRestrictionType.AtOrBelow, 11000), null));
    }

    [Fact]
    public void Between_RendersCeilingOverFloor()
    {
        // Altitude1 = upper (ceiling), Altitude2 = lower (floor).
        Assert.Equal(["≤17000", "≥11000"], CrossingRestrictionLabel.BuildLines(Alt(CifpAltitudeRestrictionType.Between, 17000, 11000), null));
    }

    [Fact]
    public void GlideSlopeIntercept_RendersAsFloor()
    {
        // GS-intercept altitude is an at-or-above minimum until intercept (AIM 5-4-5.b.2 Note 2).
        Assert.Equal(["≥2000"], CrossingRestrictionLabel.BuildLines(Alt(CifpAltitudeRestrictionType.GlideSlopeIntercept, 2000), null));
    }

    [Fact]
    public void HighAltitude_RendersAsFlightLevel()
    {
        Assert.Equal(["FL240"], CrossingRestrictionLabel.BuildLines(Alt(CifpAltitudeRestrictionType.At, 24000), null));
        Assert.Equal(["≥FL180"], CrossingRestrictionLabel.BuildLines(Alt(CifpAltitudeRestrictionType.AtOrAbove, 18000), null));
    }

    [Fact]
    public void SpeedCeiling_RendersBareKnots()
    {
        Assert.Equal(["250"], CrossingRestrictionLabel.BuildLines(null, Spd(250, CifpSpeedRestrictionType.AtOrBelow)));
        Assert.Equal(["250"], CrossingRestrictionLabel.BuildLines(null, Spd(250, CifpSpeedRestrictionType.Mandatory)));
    }

    [Fact]
    public void SpeedFloor_AnnotatedWithGlyph()
    {
        Assert.Equal(["≥280"], CrossingRestrictionLabel.BuildLines(null, Spd(280, CifpSpeedRestrictionType.AtOrAbove)));
    }

    [Fact]
    public void SingleAltitudeAndSpeed_ShareOneLine()
    {
        Assert.Equal(["≥6000  250"], CrossingRestrictionLabel.BuildLines(Alt(CifpAltitudeRestrictionType.AtOrAbove, 6000), Spd(250)));
    }

    [Fact]
    public void WindowAndSpeed_StacksSpeedBeneathWindow()
    {
        Assert.Equal(
            ["≤17000", "≥11000", "250"],
            CrossingRestrictionLabel.BuildLines(Alt(CifpAltitudeRestrictionType.Between, 17000, 11000), Spd(250))
        );
    }
}
