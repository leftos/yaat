using Xunit;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Tests;

/// <summary>
/// DA's <c>.P</c> flight-rules indicator is VFR-on-top (stars.md:772), not plain VFR
/// (leftos/yaat#383). The canonical DA parser must carry it as "OTP" so
/// <see cref="FlightPlanAltitude.FromRulesAndFeet"/> files the OTP notation, and the describer
/// must render it back as <c>.P</c> so recordings replay losslessly.
/// </summary>
public class AbbreviatedFlightPlanOtpTests
{
    [Fact]
    public void Parse_DotP_YieldsOtpFlightRules()
    {
        var result = CommandParser.ParseCompound("DA C172 055 .P");

        Assert.True(result.IsSuccess, result.Reason);
        var cmd = Assert.IsType<CreateAbbreviatedFlightPlanCommand>(result.Value!.Blocks[0].Commands[0]);
        Assert.Equal("OTP", cmd.FlightRules);
    }

    [Fact]
    public void Describe_OtpFlightRules_RendersDotP()
    {
        var cmd = new CreateAbbreviatedFlightPlanCommand(null, null, null, "C172", 5500, "OTP");

        var canonical = CommandDescriber.DescribeCommand(cmd);

        Assert.Contains(".P", canonical);
        Assert.DoesNotContain(".V", canonical);
    }

    [Fact]
    public void Canonical_OtpRoundTrips()
    {
        var cmd = new CreateAbbreviatedFlightPlanCommand(null, null, null, "C172", 5500, "OTP");
        var canonical = CommandDescriber.DescribeCommand(cmd);

        var reparsed = CommandParser.ParseCompound(canonical);

        Assert.True(reparsed.IsSuccess, reparsed.Reason);
        var reparsedCmd = Assert.IsType<CreateAbbreviatedFlightPlanCommand>(reparsed.Value!.Blocks[0].Commands[0]);
        Assert.Equal("OTP", reparsedCmd.FlightRules);
    }
}
