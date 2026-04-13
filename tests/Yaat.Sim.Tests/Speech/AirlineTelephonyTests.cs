using Xunit;
using Yaat.Sim.Speech;

namespace Yaat.Sim.Tests.Speech;

public class AirlineTelephonyTests
{
    [Fact]
    public void Count_IsAtLeastFiveThousand()
    {
        Assert.True(AirlineTelephony.Count >= 5000, $"Expected ≥5000 airlines, got {AirlineTelephony.Count}");
    }

    [Theory]
    [InlineData("AAL", "AMERICAN")]
    [InlineData("UAL", "UNITED")]
    [InlineData("DAL", "DELTA")]
    [InlineData("SWA", "SOUTHWEST")]
    [InlineData("ASA", "ALASKA")] // patched via OVERRIDES (upstream has "Inc.")
    [InlineData("AVA", "AVIANCA")] // patched via OVERRIDES (upstream has "S.A.")
    [InlineData("JBU", "JETBLUE")]
    [InlineData("SKW", "SKYWEST")]
    [InlineData("BAW", "SPEEDBIRD")]
    [InlineData("ACA", "AIR CANADA")]
    public void TryGetTelephony_KnownIcao_ReturnsExpected(string icao, string expectedTelephony)
    {
        Assert.True(AirlineTelephony.TryGetTelephony(icao, out var telephony));
        Assert.Equal(expectedTelephony, telephony);
    }

    [Fact]
    public void TryGetTelephony_CaseInsensitive()
    {
        Assert.True(AirlineTelephony.TryGetTelephony("swa", out var telephony));
        Assert.Equal("SOUTHWEST", telephony);
    }

    [Fact]
    public void TryGetTelephony_UnknownIcao_ReturnsFalse()
    {
        // QZX is not in the OpenFlights dataset (ZZZ is — Zabaykalskii Airlines).
        Assert.False(AirlineTelephony.TryGetTelephony("QZX", out _));
    }

    [Fact]
    public void TryGetTelephony_EmptyOrNull_ReturnsFalse()
    {
        Assert.False(AirlineTelephony.TryGetTelephony("", out _));
        Assert.False(AirlineTelephony.TryGetTelephony("   ", out _));
    }

    [Fact]
    public void TryGetIcaos_UniqueCallsign_ReturnsSingleIcao()
    {
        Assert.True(AirlineTelephony.TryGetIcaos("AMERICAN", out var icaos));
        Assert.Contains("AAL", icaos);
    }

    [Fact]
    public void TryGetIcaos_SharedCallsign_ReturnsMultipleIcaos()
    {
        // VIRGIN is shared between Virgin Atlantic (VIR) and Virgin Australia (VOZ), both active.
        Assert.True(AirlineTelephony.TryGetIcaos("VIRGIN", out var icaos));
        Assert.True(icaos.Count >= 2, $"Expected ≥2 ICAOs for VIRGIN, got {icaos.Count}");
        Assert.Contains("VIR", icaos);
        Assert.Contains("VOZ", icaos);
    }

    [Fact]
    public void TryGetIcaos_CaseInsensitive()
    {
        Assert.True(AirlineTelephony.TryGetIcaos("southwest", out var icaos));
        Assert.Contains("SWA", icaos);
    }

    [Fact]
    public void TryGetIcaos_UnknownTelephony_ReturnsFalseAndEmpty()
    {
        Assert.False(AirlineTelephony.TryGetIcaos("NOSUCHAIRLINE", out var icaos));
        Assert.Empty(icaos);
    }
}
