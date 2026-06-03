using Xunit;
using Yaat.Client.Models;
using Yaat.Sim;

namespace Yaat.Client.Tests;

/// <summary>
/// Pins the aircraft-list heading columns: "AHdg" (assigned, magnetic) and "Hdg" (live, converted
/// from the wire's true heading to magnetic). Both render 3-digit zero-padded via the heading structs.
/// </summary>
public class AircraftModelHeadingDisplayTests
{
    [Theory]
    [InlineData(90.0, "090")]
    [InlineData(5.0, "005")]
    [InlineData(360.0, "360")]
    [InlineData(0.0, "360")]
    [InlineData(270.0, "270")]
    public void AssignedHeadingDisplay_ThreeDigitZeroPadded(double deg, string expected)
    {
        var ac = new AircraftModel { AssignedHeading = new MagneticHeading(deg) };
        Assert.Equal(expected, ac.AssignedHeadingDisplay);
    }

    [Fact]
    public void AssignedHeadingDisplay_NavigatingTo_TakesPrecedence()
    {
        var ac = new AircraftModel { AssignedHeading = new MagneticHeading(90), NavigatingTo = "MENLO" };
        Assert.Equal("MENLO", ac.AssignedHeadingDisplay);
    }

    [Fact]
    public void AssignedHeadingDisplay_NoAssignment_Empty()
    {
        var ac = new AircraftModel();
        Assert.Equal("", ac.AssignedHeadingDisplay);
    }

    [Fact]
    public void HeadingDisplay_ConvertsTrueToMagnetic_ThreeDigit()
    {
        var pos = new LatLon(37.62, -122.38); // SFO area, ~13°E declination
        const double trueHdg = 270.0;
        var ac = new AircraftModel { Heading = new TrueHeading(trueHdg), Position = pos };

        var expected = new TrueHeading(trueHdg).ToMagnetic(MagneticDeclination.GetDeclination(pos)).ToDisplayString();
        Assert.Equal(expected, ac.HeadingDisplay);
        Assert.Equal(3, ac.HeadingDisplay.Length);

        // East declination shifts magnetic well below the true heading, so the display is not "270".
        Assert.NotEqual("270", ac.HeadingDisplay);
    }
}
