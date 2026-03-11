using Xunit;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Tests;

public class FlightPlanCommandParserTests
{
    [Fact]
    public void Apt_ParsesDestination()
    {
        var result = CommandParser.Parse("APT KSFO");
        var cmd = Assert.IsType<ChangeDestinationCommand>(result);
        Assert.Equal("KSFO", cmd.Airport);
    }

    [Fact]
    public void Dest_ParsesDestination()
    {
        var result = CommandParser.Parse("DEST KLAX");
        var cmd = Assert.IsType<ChangeDestinationCommand>(result);
        Assert.Equal("KLAX", cmd.Airport);
    }

    [Fact]
    public void Apt_LowercaseNormalized()
    {
        var result = CommandParser.Parse("apt ksfo");
        var cmd = Assert.IsType<ChangeDestinationCommand>(result);
        Assert.Equal("KSFO", cmd.Airport);
    }

    [Fact]
    public void Apt_NoArg_ReturnsNull()
    {
        Assert.Null(CommandParser.Parse("APT"));
    }

    [Fact]
    public void Fp_ParsesIfrFlightPlan()
    {
        var result = CommandParser.Parse("FP B738 220 KBOS SSOXS6 BUZRD KJFK");
        var cmd = Assert.IsType<CreateFlightPlanCommand>(result);
        Assert.Equal("IFR", cmd.FlightRules);
        Assert.Equal("B738", cmd.AircraftType);
        Assert.Equal(22000, cmd.CruiseAltitude);
        Assert.Equal("KBOS SSOXS6 BUZRD KJFK", cmd.Route);
    }

    [Fact]
    public void Vp_ParsesVfrFlightPlan()
    {
        var result = CommandParser.Parse("VP C172 5500 KOAK DCT KJFK");
        var cmd = Assert.IsType<CreateFlightPlanCommand>(result);
        Assert.Equal("VFR", cmd.FlightRules);
        Assert.Equal("C172", cmd.AircraftType);
        Assert.Equal(5500, cmd.CruiseAltitude);
        Assert.Equal("KOAK DCT KJFK", cmd.Route);
    }

    [Fact]
    public void Fp_NoArgs_ReturnsNull()
    {
        Assert.Null(CommandParser.Parse("FP"));
    }

    [Fact]
    public void Fp_MissingAltitudeAndRoute_ReturnsNull()
    {
        Assert.Null(CommandParser.Parse("FP B738"));
    }

    [Fact]
    public void Fp_NonNumericAltitude_ReturnsNull()
    {
        Assert.Null(CommandParser.Parse("FP B738 ABC ROUTE"));
    }

    [Fact]
    public void Remarks_ParsesText()
    {
        var result = CommandParser.Parse("REMARKS /V/ STUDENT");
        var cmd = Assert.IsType<SetRemarksCommand>(result);
        Assert.Equal("/V/ STUDENT", cmd.Text);
    }

    [Fact]
    public void Rem_Alias_ParsesText()
    {
        var result = CommandParser.Parse("REM /V/ STUDENT PILOT");
        var cmd = Assert.IsType<SetRemarksCommand>(result);
        Assert.Equal("/V/ STUDENT PILOT", cmd.Text);
    }

    [Fact]
    public void Remarks_NoArgs_ReturnsNull()
    {
        Assert.Null(CommandParser.Parse("REMARKS"));
    }

    [Fact]
    public void Fp_MissingRoute_ReturnsNull()
    {
        // Only type + altitude, no route
        Assert.Null(CommandParser.Parse("FP B738 220"));
    }
}
