using Xunit;
using Yaat.Sim;

namespace Yaat.Sim.Tests;

/// <summary>
/// Covers the four documented CRC FPE altitude forms: NNN, VFR, OTP, VFR/NNN, OTP/NNN.
/// CRC's editor regex permits B and A characters but the documented grammar does not include
/// block (B-prefix) or above (A-prefix) input — those forms are populated server-side (QZ) or by
/// scenario data, not user-typed in the FPE. OTP is VFR rules with a VFR-on-top altitude notation.
/// </summary>
public class FlightPlanAltitudeTests
{
    [Fact]
    public void Parse_Empty_ReturnsVfrNoAltitude()
    {
        var result = FlightPlanAltitude.Parse("");
        Assert.NotNull(result);
        Assert.Equal("VFR", result.Value.Rules);
        Assert.Equal(PlannedAltitude.Vfr(null), result.Value.Altitude);
    }

    [Fact]
    public void Parse_Whitespace_ReturnsVfrNoAltitude()
    {
        var result = FlightPlanAltitude.Parse("   ");
        Assert.NotNull(result);
        Assert.Equal("VFR", result.Value.Rules);
        Assert.Equal(PlannedAltitude.Vfr(null), result.Value.Altitude);
    }

    [Fact]
    public void Parse_BareNumber_ReturnsIfr()
    {
        var result = FlightPlanAltitude.Parse("050");
        Assert.NotNull(result);
        Assert.Equal("IFR", result.Value.Rules);
        Assert.Equal(PlannedAltitude.Ifr(5000), result.Value.Altitude);
    }

    [Fact]
    public void Parse_HighAltitude_ReturnsIfr()
    {
        var result = FlightPlanAltitude.Parse("240");
        Assert.NotNull(result);
        Assert.Equal("IFR", result.Value.Rules);
        Assert.Equal(24000, result.Value.Altitude.CruiseFeet);
    }

    [Fact]
    public void Parse_Vfr_ReturnsVfrNoAltitude()
    {
        var result = FlightPlanAltitude.Parse("VFR");
        Assert.NotNull(result);
        Assert.Equal("VFR", result.Value.Rules);
        Assert.True(result.Value.Altitude.IsVfr);
        Assert.Null(result.Value.Altitude.CruiseFeet);
    }

    [Fact]
    public void Parse_Otp_ReturnsVfrRulesVfrOnTopNotation()
    {
        var result = FlightPlanAltitude.Parse("OTP");
        Assert.NotNull(result);
        Assert.Equal("VFR", result.Value.Rules);
        Assert.True(result.Value.Altitude.IsVfrOnTop);
        Assert.Null(result.Value.Altitude.CruiseFeet);
    }

    [Fact]
    public void Parse_VfrSlashAltitude_ReturnsVfrAlt()
    {
        var result = FlightPlanAltitude.Parse("VFR/065");
        Assert.NotNull(result);
        Assert.Equal("VFR", result.Value.Rules);
        Assert.Equal(PlannedAltitude.Vfr(6500), result.Value.Altitude);
    }

    [Fact]
    public void Parse_OtpSlashAltitude_ReturnsOtpAlt()
    {
        var result = FlightPlanAltitude.Parse("OTP/120");
        Assert.NotNull(result);
        Assert.Equal("VFR", result.Value.Rules);
        Assert.Equal(PlannedAltitude.Otp(12000), result.Value.Altitude);
    }

    [Fact]
    public void Parse_LowercaseInput_NormalizesToUpper()
    {
        var result = FlightPlanAltitude.Parse("vfr/050");
        Assert.NotNull(result);
        Assert.Equal("VFR", result.Value.Rules);
        Assert.Equal(PlannedAltitude.Vfr(5000), result.Value.Altitude);
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("FL240")]
    [InlineData("B080/120")]
    [InlineData("A100")]
    [InlineData("VFR/abc")]
    public void Parse_Unsupported_ReturnsNull(string input)
    {
        Assert.Null(FlightPlanAltitude.Parse(input));
    }

    [Fact]
    public void Format_IfrAltitude_RendersThreeDigits()
    {
        Assert.Equal("050", FlightPlanAltitude.Format(PlannedAltitude.Ifr(5000)));
        Assert.Equal("240", FlightPlanAltitude.Format(PlannedAltitude.Ifr(24000)));
    }

    [Fact]
    public void Format_VfrNoAltitude_RendersBareVfr()
    {
        Assert.Equal("VFR", FlightPlanAltitude.Format(PlannedAltitude.Vfr(null)));
    }

    [Fact]
    public void Format_VfrWithAltitude_RendersVfrSlash()
    {
        Assert.Equal("VFR/055", FlightPlanAltitude.Format(PlannedAltitude.Vfr(5500)));
    }

    [Fact]
    public void Format_OtpNoAltitude_RendersBareOtp()
    {
        Assert.Equal("OTP", FlightPlanAltitude.Format(PlannedAltitude.Otp(null)));
    }

    [Fact]
    public void Format_OtpWithAltitude_RendersOtpSlash()
    {
        Assert.Equal("OTP/120", FlightPlanAltitude.Format(PlannedAltitude.Otp(12000)));
    }

    [Fact]
    public void Format_Block_RendersFloorBCeiling()
    {
        Assert.Equal("200B250", FlightPlanAltitude.Format(PlannedAltitude.Block(20000, 25000)));
    }

    public static TheoryData<PlannedAltitude> RoundTripCases =>
        new()
        {
            PlannedAltitude.Ifr(5000),
            PlannedAltitude.Ifr(24000),
            PlannedAltitude.Vfr(null),
            PlannedAltitude.Vfr(5500),
            PlannedAltitude.Otp(null),
            PlannedAltitude.Otp(12000),
        };

    [Theory]
    [MemberData(nameof(RoundTripCases))]
    public void RoundTrip_FormatThenParse_PreservesState(PlannedAltitude altitude)
    {
        var formatted = FlightPlanAltitude.Format(altitude);
        var parsed = FlightPlanAltitude.Parse(formatted);
        Assert.NotNull(parsed);
        Assert.Equal(altitude, parsed.Value.Altitude);
    }
}
