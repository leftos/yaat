using Xunit;
using Yaat.Sim.Pilot;

namespace Yaat.Sim.Tests.Pilot;

/// <summary>
/// Unit tests for <see cref="PilotSayBuilder"/> spoken-form formatters. AIM references:
///   - 4-2-8 (digit-by-digit number speech)
///   - 4-2-9 (altitudes — group form below FL180, "flight level X" at/above)
///   - 4-2-10 (magnetic headings as three spoken digits)
///   - 4-2-11 (Mach as "point X Y", airspeed in spoken digits + "knots")
///   - 4-2-7  (phonetic alphabet for letter suffixes)
/// </summary>
public class PilotSayBuilderTests
{
    [Theory]
    [InlineData(0, "zero")]
    [InlineData(1, "one")]
    [InlineData(9, "niner")]
    [InlineData(10, "one zero")]
    [InlineData(123, "one two three")]
    [InlineData(250, "two five zero")]
    public void SpokenDigits_ReadsDigitByDigit(int value, string expected)
    {
        Assert.Equal(expected, PilotSayBuilder.SpokenDigits(value));
    }

    [Theory]
    [InlineData(360, "three six zero")]
    [InlineData(1, "zero zero one")]
    [InlineData(90, "zero niner zero")]
    [InlineData(270, "two seven zero")]
    public void SpokenHeading_PreservesLeadingZeros(int hdg, string expected)
    {
        Assert.Equal(expected, PilotSayBuilder.SpokenHeading(hdg));
    }

    [Theory]
    [InlineData(0, "zero")]
    [InlineData(500, "five hundred")]
    [InlineData(5000, "five thousand")]
    [InlineData(5300, "five thousand three hundred")]
    [InlineData(8000, "eight thousand")]
    [InlineData(17900, "one seven thousand niner hundred")]
    [InlineData(18000, "flight level one eight zero")]
    [InlineData(25000, "flight level two five zero")]
    [InlineData(35000, "flight level three five zero")]
    public void SpokenAltitude_GroupFormBelowFL180_FlightLevelAbove(int alt, string expected)
    {
        Assert.Equal(expected, PilotSayBuilder.SpokenAltitude(alt));
    }

    [Theory]
    [InlineData(0.78, "point seven eight")]
    [InlineData(0.65, "point six five")]
    [InlineData(0.8, "point eight zero")]
    public void SpokenMach_DropsLeadingZero(double mach, string expected)
    {
        Assert.Equal(expected, PilotSayBuilder.SpokenMach(mach));
    }

    [Theory]
    [InlineData("I19L", "ILS one niner left")]
    [InlineData("I28R", "ILS two eight right")]
    [InlineData("I19C", "ILS one niner center")]
    [InlineData("R28L", "RNAV two eight left")]
    [InlineData("V19", "VOR one niner")]
    [InlineData("V09", "VOR zero niner")]
    [InlineData("IY28L", "ILS Yankee two eight left")]
    [InlineData("IZ19R", "ILS Zulu one niner right")]
    [InlineData("R09", "RNAV zero niner")]
    [InlineData("L05", "LOC zero five")]
    [InlineData("N28", "NDB two eight")]
    public void SpokenApproach_ExpandsType_PhoneticizesSuffix_SpellsRunway(string id, string expected)
    {
        Assert.Equal(expected, PilotSayBuilder.SpokenApproach(id));
    }
}
