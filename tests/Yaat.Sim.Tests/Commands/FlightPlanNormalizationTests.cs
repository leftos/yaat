using Xunit;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Tests.Commands;

/// <summary>
/// Type/suffix splitting shared by every flight-plan create and amend path (issue #463). A scenario files the type with
/// its wake prefix ("H/A306/L") and CRC's editor echoes that string back unchanged, so the split must drop the prefix
/// rather than store "H" as the type. A bare type carries no suffix: the equipment suffix is a separate field, and only
/// the Sim's new-plan filing defaults it to /A.
/// </summary>
public class FlightPlanNormalizationTests
{
    [Theory]
    [InlineData("B738", null, null, "B738", null)]
    [InlineData("B738/L", null, null, "B738", "L")]
    [InlineData("H/B763/L", null, 'H', "B763", "L")]
    [InlineData("2/F16", 2, null, "F16", null)]
    [InlineData("2H/F16", 2, 'H', "F16", null)]
    [InlineData("2H/F16/L", 2, 'H', "F16", "L")]
    [InlineData("12/F18/G", 12, null, "F18", "G")]
    [InlineData("J/A388/L", null, 'J', "A388", "L")]
    [InlineData("C172/", null, null, "C172", null)]
    [InlineData("C182/L-DOV/C", null, null, "C182", "L-DOV/C")]
    [InlineData("123/F16", null, null, "123", "F16")]
    [InlineData("2HX/F16", null, null, "2HX", "F16")]
    [InlineData("2h/f16", 2, 'H', "F16", null)]
    [InlineData("h/b763/l", null, 'H', "B763", "L")]
    [InlineData("b763/l", null, null, "B763", "L")]
    public void SplitTypeAndSuffix_SplitsElementATypeAndSuffix(
        string raw,
        int? expectedCount,
        char? expectedIndicator,
        string expectedType,
        string? expectedSuffix
    )
    {
        FiledAircraftType split = FlightPlanNormalization.SplitTypeAndSuffix(raw);

        Assert.Equal(new FiledAircraftType(expectedCount, expectedIndicator, expectedType, expectedSuffix), split);
    }

    [Fact]
    public void SplitTypeAndSuffix_Null_ReturnsNull() => Assert.Null(FlightPlanNormalization.SplitTypeAndSuffix(null));

    [Fact]
    public void SplitTypeAndSuffix_Empty_ReturnsEmptyTypeAndNothingElse() =>
        Assert.Equal(new FiledAircraftType(null, null, "", null), FlightPlanNormalization.SplitTypeAndSuffix(""));

    [Fact]
    public void StripTypePrefix_CountAndIndicator_ReturnsTheType() => Assert.Equal("F16", AircraftState.StripTypePrefix("2H/F16"));

    [Theory]
    [InlineData("2H", true)]
    [InlineData("12H", true)]
    [InlineData("H", true)]
    [InlineData("2h", true)]
    [InlineData("123", false)]
    [InlineData("2HX", false)]
    [InlineData("", false)]
    [InlineData("123H", false)]
    public void IsTypePrefix_AcceptsElementAOnly(string segment, bool expected) => Assert.Equal(expected, AircraftState.IsTypePrefix(segment));

    [Fact]
    public void ResolveTypeAndSuffix_WakePrefixedEcho_KeepsBaseTypeAndFaaSuffix()
    {
        (string? type, string? suffix) = FlightPlanNormalization.ResolveTypeAndSuffix("H/A306/L", "L");

        Assert.Equal("A306", type);
        Assert.Equal("L", suffix);
    }

    [Fact]
    public void ResolveTypeAndSuffix_BareTypeAndBlankFaaSuffix_LeavesSuffixUnset()
    {
        (string? type, string? suffix) = FlightPlanNormalization.ResolveTypeAndSuffix("A306", "");

        Assert.Equal("A306", type);
        Assert.Null(suffix);
    }

    [Fact]
    public void ResolveTypeAndSuffix_LowerCaseFaaSuffix_ReturnsItUpperCase()
    {
        (string? type, string? suffix) = FlightPlanNormalization.ResolveTypeAndSuffix("b738", "l");

        Assert.Equal("B738", type);
        Assert.Equal("L", suffix);
    }
}
