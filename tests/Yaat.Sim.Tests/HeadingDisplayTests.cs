using Xunit;

namespace Yaat.Sim.Tests;

/// <summary>
/// Pins the controller-facing display formatting on the heading structs:
/// three-digit zero-padded, normalized to 001..360 (north shows "360", never "000").
/// </summary>
public class HeadingDisplayTests
{
    [Theory]
    [InlineData(90.0, "090")]
    [InlineData(5.0, "005")]
    [InlineData(0.0, "360")]
    [InlineData(360.0, "360")]
    [InlineData(359.6, "360")]
    [InlineData(270.0, "270")]
    [InlineData(1.0, "001")]
    [InlineData(89.5, "090")]
    [InlineData(450.0, "090")]
    [InlineData(-10.0, "350")]
    public void MagneticHeading_ToDisplayString_ThreeDigitZeroPadded(double degrees, string expected)
    {
        Assert.Equal(expected, new MagneticHeading(degrees).ToDisplayString());
    }

    [Theory]
    [InlineData(90.0, "090")]
    [InlineData(5.0, "005")]
    [InlineData(0.0, "360")]
    [InlineData(360.0, "360")]
    [InlineData(270.0, "270")]
    public void TrueHeading_ToDisplayString_ThreeDigitZeroPadded(double degrees, string expected)
    {
        Assert.Equal(expected, new TrueHeading(degrees).ToDisplayString());
    }
}
