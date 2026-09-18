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
        Assert.Equal(22000, cmd.CruiseAltitude);
        Assert.Equal("KBOS SSOXS6 BUZRD KJFK", cmd.Route);
    }

    [Fact]
    public void Vp_ParsesVfrFlightPlan()
    {
        ParseResult<ParsedCommand> result = CommandParser.Parse("VP C172 5500 KOAK DCT KJFK");
        CreateFlightPlanCommand cmd = Assert.IsType<CreateFlightPlanCommand>(result.Value);
        Assert.Equal("VFR", cmd.FlightRules);
        Assert.Equal("C172", cmd.AircraftType);
        Assert.Equal(5500, cmd.CruiseAltitude);
        Assert.Equal("KOAK DCT KJFK", cmd.Route);
    }

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
