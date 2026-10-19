using Xunit;

namespace Yaat.Sim.Tests;

public sealed class AircraftTypeNormalizationTests
{
    [Theory]
    [InlineData("B738", "B738")]
    [InlineData("H/B763/L", "B763")]
    [InlineData("B738/L", "B738")]
    [InlineData("2/C130/G", "C130")]
    [InlineData("2H/F16", "F16")]
    [InlineData(" b738 ", "B738")]
    [InlineData("123/B738", "123")]
    [InlineData("2HX/B738", "2HX")]
    public void NormalizedBaseType_MatchesStripTrimUpper(string raw, string expected)
    {
        Assert.Equal(expected, AircraftState.NormalizedBaseType(raw));
        Assert.Equal(AircraftState.StripTypePrefix(raw).Trim().ToUpperInvariant(), AircraftState.NormalizedBaseType(raw));
    }

    [Fact]
    public void NormalizedBaseType_SecondCall_ReturnsSameValue()
    {
        string first = AircraftState.NormalizedBaseType("H/A306/L");

        // A parallel test class pushing the cache past its cap between the two calls could clear it; today only this
        // class does that, and its methods run one at a time.
        Assert.Same(first, AircraftState.NormalizedBaseType("H/A306/L"));
    }

    [Fact]
    public void NormalizedBaseType_AfterCacheCap_StillCorrect()
    {
        for (int i = 0; i < 5000; i++)
        {
            AircraftState.NormalizedBaseType($"N{i:D4}");
        }

        Assert.True(AircraftState.NormalizedBaseTypeCacheCount <= 4096 + 64);
        Assert.Equal("B738", AircraftState.NormalizedBaseType("B738"));
        Assert.Equal("B763", AircraftState.NormalizedBaseType("H/B763/L"));
    }
}
