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
    [InlineData("H/A306/L", "A306", "L")]
    [InlineData("2/C130/G", "C130", "G")]
    [InlineData("C172/G", "C172", "G")]
    [InlineData("H/A306", "A306", null)]
    [InlineData("A306", "A306", null)]
    [InlineData("C182/L-DOV/C", "C182", "L-DOV/C")]
    public void SplitTypeAndSuffix_SplitsAfterAnyTypePrefix(string raw, string expectedType, string? expectedSuffix)
    {
        (string? type, string? suffix) = FlightPlanNormalization.SplitTypeAndSuffix(raw);

        Assert.Equal(expectedType, type);
        Assert.Equal(expectedSuffix, suffix);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void SplitTypeAndSuffix_NullOrEmpty_ReturnsInputAndNoSuffix(string? raw)
    {
        (string? type, string? suffix) = FlightPlanNormalization.SplitTypeAndSuffix(raw);

        Assert.Equal(raw, type);
        Assert.Null(suffix);
    }

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
}
