using Xunit;
using Yaat.Sim;

namespace Yaat.Sim.Tests;

/// <summary>
/// <see cref="AircraftFlightPlan.FormatSpeedField"/> writes ERAM field 05 the way AM SPD enters it: <c>SC</c> wins over a
/// Mach number, a Mach number is <c>M</c> and three digits of hundredths, knots are bare digits, and no speed is empty.
/// </summary>
public class AircraftFlightPlanSpeedFieldTests
{
    [Theory]
    [InlineData(0, null, true, "SC")]
    [InlineData(0, 78, true, "SC")]
    [InlineData(0, 78, false, "M078")]
    [InlineData(0, 100, false, "M100")]
    [InlineData(0, 5, false, "M005")]
    [InlineData(450, null, false, "450")]
    [InlineData(0, null, false, "")]
    public void FormatSpeedField_WritesTheEramText(int knots, int? mach, bool classified, string expected) =>
        Assert.Equal(expected, AircraftFlightPlan.FormatSpeedField(knots, mach, classified));
}
