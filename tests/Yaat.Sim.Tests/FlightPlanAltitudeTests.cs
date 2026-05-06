using Xunit;
using Yaat.Sim;

namespace Yaat.Sim.Tests;

/// <summary>
/// Covers the four documented CRC FPE altitude forms: NNN, VFR, OTP, VFR/NNN, OTP/NNN.
/// CRC's editor regex permits B and A characters but the documented grammar does not include
/// block (B-prefix) or above (A-prefix) input — those forms are populated server-side or by
/// scenario data, not user-typed in the FPE.
/// </summary>
public class FlightPlanAltitudeTests
{
    [Fact]
    public void Parse_Empty_ReturnsVfrZero()
    {
        var result = FlightPlanAltitude.Parse("");
        Assert.NotNull(result);
        Assert.Equal("VFR", result.Value.FlightRules);
        Assert.Equal(0, result.Value.CruiseAltitude);
    }

    [Fact]
    public void Parse_Whitespace_ReturnsVfrZero()
    {
        var result = FlightPlanAltitude.Parse("   ");
        Assert.NotNull(result);
        Assert.Equal("VFR", result.Value.FlightRules);
        Assert.Equal(0, result.Value.CruiseAltitude);
    }

    [Fact]
    public void Parse_BareNumber_ReturnsIfr()
    {
        var result = FlightPlanAltitude.Parse("050");
        Assert.NotNull(result);
        Assert.Equal("IFR", result.Value.FlightRules);
        Assert.Equal(5000, result.Value.CruiseAltitude);
    }

    [Fact]
    public void Parse_HighAltitude_ReturnsIfr()
    {
        var result = FlightPlanAltitude.Parse("240");
        Assert.NotNull(result);
        Assert.Equal("IFR", result.Value.FlightRules);
        Assert.Equal(24000, result.Value.CruiseAltitude);
    }

    [Fact]
    public void Parse_Vfr_ReturnsVfrZero()
    {
        var result = FlightPlanAltitude.Parse("VFR");
        Assert.NotNull(result);
        Assert.Equal("VFR", result.Value.FlightRules);
        Assert.Equal(0, result.Value.CruiseAltitude);
    }

    [Fact]
    public void Parse_Otp_ReturnsOtpZero()
    {
        var result = FlightPlanAltitude.Parse("OTP");
        Assert.NotNull(result);
        Assert.Equal("OTP", result.Value.FlightRules);
        Assert.Equal(0, result.Value.CruiseAltitude);
    }

    [Fact]
    public void Parse_VfrSlashAltitude_ReturnsVfrAlt()
    {
        var result = FlightPlanAltitude.Parse("VFR/065");
        Assert.NotNull(result);
        Assert.Equal("VFR", result.Value.FlightRules);
        Assert.Equal(6500, result.Value.CruiseAltitude);
    }

    [Fact]
    public void Parse_OtpSlashAltitude_ReturnsOtpAlt()
    {
        var result = FlightPlanAltitude.Parse("OTP/120");
        Assert.NotNull(result);
        Assert.Equal("OTP", result.Value.FlightRules);
        Assert.Equal(12000, result.Value.CruiseAltitude);
    }

    [Fact]
    public void Parse_LowercaseInput_NormalizesToUpper()
    {
        var result = FlightPlanAltitude.Parse("vfr/050");
        Assert.NotNull(result);
        Assert.Equal("VFR", result.Value.FlightRules);
        Assert.Equal(5000, result.Value.CruiseAltitude);
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
        Assert.Equal("050", FlightPlanAltitude.Format("IFR", 5000));
        Assert.Equal("240", FlightPlanAltitude.Format("IFR", 24000));
    }

    [Fact]
    public void Format_VfrZero_RendersBareVfr()
    {
        Assert.Equal("VFR", FlightPlanAltitude.Format("VFR", 0));
    }

    [Fact]
    public void Format_VfrWithAltitude_RendersVfrSlash()
    {
        Assert.Equal("VFR/055", FlightPlanAltitude.Format("VFR", 5500));
    }

    [Fact]
    public void Format_OtpZero_RendersBareOtp()
    {
        Assert.Equal("OTP", FlightPlanAltitude.Format("OTP", 0));
    }

    [Fact]
    public void Format_OtpWithAltitude_RendersOtpSlash()
    {
        Assert.Equal("OTP/120", FlightPlanAltitude.Format("OTP", 12000));
    }

    [Theory]
    [InlineData("IFR", 5000)]
    [InlineData("IFR", 24000)]
    [InlineData("VFR", 0)]
    [InlineData("VFR", 5500)]
    [InlineData("OTP", 0)]
    [InlineData("OTP", 12000)]
    public void RoundTrip_FormatThenParse_PreservesState(string flightRules, int cruiseAltitude)
    {
        var formatted = FlightPlanAltitude.Format(flightRules, cruiseAltitude);
        var parsed = FlightPlanAltitude.Parse(formatted);
        Assert.NotNull(parsed);
        Assert.Equal(flightRules, parsed.Value.FlightRules);
        Assert.Equal(cruiseAltitude, parsed.Value.CruiseAltitude);
    }
}
