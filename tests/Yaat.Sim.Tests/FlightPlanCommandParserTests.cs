using Xunit;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Tests;

public class FlightPlanCommandParserTests
{
    [Fact]
    public void Apt_ParsesDestination()
    {
        ParseResult<ParsedCommand> result = CommandParser.Parse("APT KSFO");
        ChangeDestinationCommand cmd = Assert.IsType<ChangeDestinationCommand>(result.Value);
        Assert.Equal("KSFO", cmd.Airport);
    }

    [Fact]
    public void Dest_ParsesDestination()
    {
        ParseResult<ParsedCommand> result = CommandParser.Parse("DEST KLAX");
        ChangeDestinationCommand cmd = Assert.IsType<ChangeDestinationCommand>(result.Value);
        Assert.Equal("KLAX", cmd.Airport);
    }

    [Fact]
    public void Apt_LowercaseNormalized()
    {
        ParseResult<ParsedCommand> result = CommandParser.Parse("apt ksfo");
        ChangeDestinationCommand cmd = Assert.IsType<ChangeDestinationCommand>(result.Value);
        Assert.Equal("KSFO", cmd.Airport);
    }

    [Fact]
    public void Apt_NoArg_ReturnsNull() => Assert.Null(CommandParser.Parse("APT").Value);

    [Fact]
    public void Apt_FaaCode_Parses()
    {
        // Parser stays permissive — handler validates against NavigationDatabase.
        ParseResult<ParsedCommand> result = CommandParser.Parse("APT OAK");
        ChangeDestinationCommand cmd = Assert.IsType<ChangeDestinationCommand>(result.Value);
        Assert.Equal("OAK", cmd.Airport);
    }

    [Fact]
    public void Fp_ParsesIfrFlightPlan()
    {
        ParseResult<ParsedCommand> result = CommandParser.Parse("FP B738 220 KBOS SSOXS6 BUZRD KJFK");
        CreateFlightPlanCommand cmd = Assert.IsType<CreateFlightPlanCommand>(result.Value);
        Assert.Equal("IFR", cmd.FlightRules);
        Assert.Equal("B738", cmd.AircraftType);
        Assert.Equal(PlannedAltitude.Ifr(22000), cmd.Altitude);
        Assert.Equal("KBOS SSOXS6 BUZRD KJFK", cmd.Route);
    }

    [Fact]
    public void Vp_ParsesVfrFlightPlan()
    {
        ParseResult<ParsedCommand> result = CommandParser.Parse("VP C172 5500 KOAK DCT KJFK");
        CreateFlightPlanCommand cmd = Assert.IsType<CreateFlightPlanCommand>(result.Value);
        Assert.Equal("VFR", cmd.FlightRules);
        Assert.Equal("C172", cmd.AircraftType);
        Assert.Equal(PlannedAltitude.Vfr(5500), cmd.Altitude);
        Assert.Equal("KOAK DCT KJFK", cmd.Route);
    }

    public static TheoryData<string, string, PlannedAltitude> WholeAltitudeForms =>
        new()
        {
            { "170/SJC/110", "IFR", PlannedAltitude.UntilFix(17000, "SJC", 11000) },
            { "A170", "IFR", PlannedAltitude.Above(17000) },
            { "OTP/170", "OTP", PlannedAltitude.Otp(17000) },
        };

    [Theory]
    [MemberData(nameof(WholeAltitudeForms))]
    public void Fp_KeepsTheWholeAltitude_AndItsCanonicalTextParsesBackTheSame(string altitude, string rules, PlannedAltitude expected)
    {
        CreateFlightPlanCommand cmd = Assert.IsType<CreateFlightPlanCommand>(CommandParser.Parse($"FP B738 {altitude} KOAK KSFO").Value);

        Assert.Equal(rules, cmd.FlightRules);
        Assert.Equal(expected, cmd.Altitude);
        string canonical = CommandDescriber.DescribeCommand(cmd);
        Assert.Equal($"FP B738 {altitude} KOAK KSFO", canonical);
        Assert.Equal(cmd, CommandParser.Parse(canonical).Value);
    }

    public static TheoryData<string, string, PlannedAltitude> NumericAndBareOtpAltitudes =>
        new()
        {
            { "000", "IFR", PlannedAltitude.None },
            { "OTP/000", "OTP", PlannedAltitude.Otp(null) },
            { "055", "IFR", PlannedAltitude.Ifr(5500) },
            { "OTP", "OTP", PlannedAltitude.Otp(null) },
        };

    [Theory]
    [MemberData(nameof(NumericAndBareOtpAltitudes))]
    public void Fp_NumericAndBareOtpAltitudes_RoundTripThroughTheCanonicalText(string altitude, string rules, PlannedAltitude expected)
    {
        CreateFlightPlanCommand cmd = Assert.IsType<CreateFlightPlanCommand>(CommandParser.Parse($"FP B738 {altitude} KOAK KSFO").Value);

        Assert.Equal(rules, cmd.FlightRules);
        Assert.Equal(expected, cmd.Altitude);
        Assert.Equal(cmd, CommandParser.Parse(CommandDescriber.DescribeCommand(cmd)).Value);
    }

    public static TheoryData<string, PlannedAltitude> AbsoluteFeetOffTheHundred =>
        new() { { "35050", PlannedAltitude.Ifr(35050) }, { "OTP/35050", PlannedAltitude.Otp(35050) } };

    [Theory]
    [MemberData(nameof(AbsoluteFeetOffTheHundred))]
    public void Fp_AbsoluteFeetOffTheHundred_RoundTripsThroughTheCanonicalText(string altitude, PlannedAltitude expected)
    {
        CreateFlightPlanCommand cmd = Assert.IsType<CreateFlightPlanCommand>(CommandParser.Parse($"FP B738 {altitude} KOAK KSFO").Value);
        Assert.Equal(expected, cmd.Altitude);

        string canonical = CommandDescriber.DescribeCommand(cmd);

        Assert.Equal($"FP B738 {altitude} KOAK KSFO", canonical);
        CreateFlightPlanCommand reparsed = Assert.IsType<CreateFlightPlanCommand>(CommandParser.Parse(canonical).Value);
        Assert.Equal(expected, reparsed.Altitude);
    }

    [Theory]
    [InlineData("FP B738 VFR/055 KOAK KSFO")] // FP takes no VFR altitude form
    [InlineData("VP C172 A170 KOAK KSFO")] // VP takes only a number of feet
    public void CreateFlightPlan_AltitudeFormTheRulesDoNotTake_IsRefused(string text) => Assert.Null(CommandParser.Parse(text).Value);

    [Fact]
    public void Fp_NoArgs_ReturnsNull() => Assert.Null(CommandParser.Parse("FP").Value);

    [Fact]
    public void Fp_MissingAltitudeAndRoute_ReturnsNull() => Assert.Null(CommandParser.Parse("FP B738").Value);

    [Fact]
    public void Fp_NonNumericAltitude_ReturnsNull() => Assert.Null(CommandParser.Parse("FP B738 ABC ROUTE").Value);

    [Fact]
    public void Remarks_ParsesText()
    {
        ParseResult<ParsedCommand> result = CommandParser.Parse("REMARKS /V/ STUDENT");
        SetRemarksCommand cmd = Assert.IsType<SetRemarksCommand>(result.Value);
        Assert.Equal("/V/ STUDENT", cmd.Text);
    }

    [Fact]
    public void Rem_Alias_ParsesText()
    {
        ParseResult<ParsedCommand> result = CommandParser.Parse("REM /V/ STUDENT PILOT");
        SetRemarksCommand cmd = Assert.IsType<SetRemarksCommand>(result.Value);
        Assert.Equal("/V/ STUDENT PILOT", cmd.Text);
    }

    [Fact]
    public void Remarks_NoArgs_ReturnsNull() => Assert.Null(CommandParser.Parse("REMARKS").Value);

    [Fact]
    public void Fp_MissingRoute_ReturnsNull() =>
        // Only type + altitude, no route
        Assert.Null(CommandParser.Parse("FP B738 220").Value);
}
