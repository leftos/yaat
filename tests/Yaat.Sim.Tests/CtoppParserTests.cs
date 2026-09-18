using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;

namespace Yaat.Sim.Tests;

public class CtoppParserTests : IDisposable
{
    private IDisposable _scope;

    public CtoppParserTests()
    {
        _scope = NavigationDatabase.ScopedOverride(NavigationDatabase.ForTesting());
    }

    public void Dispose() => _scope.Dispose();

    [Fact]
    public void BareCtopp_ParsesAsPresentPositionHover_DefaultAltitude()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("CTOPP");
        ClearedTakeoffPresentCommand ctopp = Assert.IsType<ClearedTakeoffPresentCommand>(cmd.Value);
        PresentPositionHoverDeparture hover = Assert.IsType<PresentPositionHoverDeparture>(ctopp.Departure);
        Assert.Equal(25, hover.HoverAltitudeAglFt);
        Assert.Null(ctopp.AssignedAltitude);
    }

    [Fact]
    public void Ctopp_PlusAgl_HundredsShorthand()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("CTOPP +002");
        ClearedTakeoffPresentCommand ctopp = Assert.IsType<ClearedTakeoffPresentCommand>(cmd.Value);
        PresentPositionHoverDeparture hover = Assert.IsType<PresentPositionHoverDeparture>(ctopp.Departure);
        Assert.Equal(200, hover.HoverAltitudeAglFt);
        Assert.Null(ctopp.AssignedAltitude);
    }

    [Fact]
    public void Ctopp_PlusAgl_DefaultHundred()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("CTOPP +001");
        ClearedTakeoffPresentCommand ctopp = Assert.IsType<ClearedTakeoffPresentCommand>(cmd.Value);
        PresentPositionHoverDeparture hover = Assert.IsType<PresentPositionHoverDeparture>(ctopp.Departure);
        Assert.Equal(100, hover.HoverAltitudeAglFt);
    }

    [Fact]
    public void Ctopp_PlusAgl_LiteralFeetAboveThousand()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("CTOPP +1500");
        ClearedTakeoffPresentCommand ctopp = Assert.IsType<ClearedTakeoffPresentCommand>(cmd.Value);
        PresentPositionHoverDeparture hover = Assert.IsType<PresentPositionHoverDeparture>(ctopp.Departure);
        Assert.Equal(1500, hover.HoverAltitudeAglFt);
    }

    [Fact]
    public void Ctopp_PlusMalformed_Rejected()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("CTOPP +");
        Assert.False(cmd.IsSuccess);
        Assert.Contains("CTOPP", cmd.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Ctopp_BareNumber_IsHeading()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("CTOPP 340");
        ClearedTakeoffPresentCommand ctopp = Assert.IsType<ClearedTakeoffPresentCommand>(cmd.Value);
        FlyHeadingDeparture fh = Assert.IsType<FlyHeadingDeparture>(ctopp.Departure);
        Assert.Equal(340, fh.MagneticHeading.Degrees);
        Assert.Null(fh.Direction);
        Assert.Null(ctopp.AssignedAltitude);
    }

    [Fact]
    public void Ctopp_BareNumber_WithAltitude()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("CTOPP 340 015");
        ClearedTakeoffPresentCommand ctopp = Assert.IsType<ClearedTakeoffPresentCommand>(cmd.Value);
        FlyHeadingDeparture fh = Assert.IsType<FlyHeadingDeparture>(ctopp.Departure);
        Assert.Equal(340, fh.MagneticHeading.Degrees);
        Assert.Equal(1500, ctopp.AssignedAltitude);
    }

    [Fact]
    public void Ctopp_LT270_TurnLeftHeading()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("CTOPP LT270");
        ClearedTakeoffPresentCommand ctopp = Assert.IsType<ClearedTakeoffPresentCommand>(cmd.Value);
        FlyHeadingDeparture fh = Assert.IsType<FlyHeadingDeparture>(ctopp.Departure);
        Assert.Equal(270, fh.MagneticHeading.Degrees);
        Assert.Equal(TurnDirection.Left, fh.Direction);
    }

    [Fact]
    public void Ctopp_RT090_WithAlt()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("CTOPP RT090 050");
        ClearedTakeoffPresentCommand ctopp = Assert.IsType<ClearedTakeoffPresentCommand>(cmd.Value);
        FlyHeadingDeparture fh = Assert.IsType<FlyHeadingDeparture>(ctopp.Departure);
        Assert.Equal(90, fh.MagneticHeading.Degrees);
        Assert.Equal(TurnDirection.Right, fh.Direction);
        Assert.Equal(5000, ctopp.AssignedAltitude);
    }

    [Fact]
    public void Ctopp_LH270_TurnLeftHeading()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("CTOPP LH270");
        ClearedTakeoffPresentCommand ctopp = Assert.IsType<ClearedTakeoffPresentCommand>(cmd.Value);
        FlyHeadingDeparture fh = Assert.IsType<FlyHeadingDeparture>(ctopp.Departure);
        Assert.Equal(270, fh.MagneticHeading.Degrees);
        Assert.Equal(TurnDirection.Left, fh.Direction);
    }

    [Fact]
    public void Ctopp_H180_FlyHeading()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("CTOPP H180");
        ClearedTakeoffPresentCommand ctopp = Assert.IsType<ClearedTakeoffPresentCommand>(cmd.Value);
        FlyHeadingDeparture fh = Assert.IsType<FlyHeadingDeparture>(ctopp.Departure);
        Assert.Equal(180, fh.MagneticHeading.Degrees);
        Assert.Null(fh.Direction);
    }

    [Fact]
    public void Ctopp_Oc_OnCourse()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("CTOPP OC");
        ClearedTakeoffPresentCommand ctopp = Assert.IsType<ClearedTakeoffPresentCommand>(cmd.Value);
        Assert.IsType<OnCourseDeparture>(ctopp.Departure);
        Assert.Null(ctopp.AssignedAltitude);
    }

    [Fact]
    public void Ctopp_Oc_WithAlt()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("CTOPP OC 050");
        ClearedTakeoffPresentCommand ctopp = Assert.IsType<ClearedTakeoffPresentCommand>(cmd.Value);
        Assert.IsType<OnCourseDeparture>(ctopp.Departure);
        Assert.Equal(5000, ctopp.AssignedAltitude);
    }

    [Fact]
    public void Ctopp_Dct_DirectFix()
    {
        _scope.Dispose();
        _scope = NavigationDatabase.ScopedOverride(
            NavigationDatabase.ForTesting(fixes: new Dictionary<string, (double Lat, double Lon)> { ["SUNOL"] = (37.5, -121.8) })
        );
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("CTOPP DCT SUNOL");
        ClearedTakeoffPresentCommand ctopp = Assert.IsType<ClearedTakeoffPresentCommand>(cmd.Value);
        DirectFixDeparture dfd = Assert.IsType<DirectFixDeparture>(ctopp.Departure);
        Assert.Equal("SUNOL", dfd.FixName);
        Assert.Null(dfd.Direction);
    }

    [Fact]
    public void Ctopp_Tldct_DirectFix_LeftTurn()
    {
        _scope.Dispose();
        _scope = NavigationDatabase.ScopedOverride(
            NavigationDatabase.ForTesting(fixes: new Dictionary<string, (double Lat, double Lon)> { ["SUNOL"] = (37.5, -121.8) })
        );
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("CTOPP TLDCT SUNOL");
        ClearedTakeoffPresentCommand ctopp = Assert.IsType<ClearedTakeoffPresentCommand>(cmd.Value);
        DirectFixDeparture dfd = Assert.IsType<DirectFixDeparture>(ctopp.Departure);
        Assert.Equal("SUNOL", dfd.FixName);
        Assert.Equal(TurnDirection.Left, dfd.Direction);
    }

    [Fact]
    public void Ctopp_Trdct_DirectFix_RightTurn_WithAlt()
    {
        _scope.Dispose();
        _scope = NavigationDatabase.ScopedOverride(
            NavigationDatabase.ForTesting(fixes: new Dictionary<string, (double Lat, double Lon)> { ["SUNOL"] = (37.5, -121.8) })
        );
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("CTOPP TRDCT SUNOL 040");
        ClearedTakeoffPresentCommand ctopp = Assert.IsType<ClearedTakeoffPresentCommand>(cmd.Value);
        DirectFixDeparture dfd = Assert.IsType<DirectFixDeparture>(ctopp.Departure);
        Assert.Equal("SUNOL", dfd.FixName);
        Assert.Equal(TurnDirection.Right, dfd.Direction);
        Assert.Equal(4000, ctopp.AssignedAltitude);
    }

    [Fact]
    public void Ctopp_Rh_Rejected()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("CTOPP RH");
        Assert.False(cmd.IsSuccess);
        Assert.Contains("CTOPP", cmd.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Ctopp_Mlt_Rejected()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("CTOPP MLT");
        Assert.False(cmd.IsSuccess);
        Assert.Contains("CTOPP", cmd.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Ctopp_Mrt_Rejected()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("CTOPP MRT");
        Assert.False(cmd.IsSuccess);
        Assert.Contains("CTOPP", cmd.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Ctopp_Mrc_Rejected()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("CTOPP MRC");
        Assert.False(cmd.IsSuccess);
        Assert.Contains("CTOPP", cmd.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Ctopp_Mr270_Rejected()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("CTOPP MR270");
        Assert.False(cmd.IsSuccess);
        Assert.Contains("CTOPP", cmd.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Ctopp_Mld_Rejected()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("CTOPP MLD");
        Assert.False(cmd.IsSuccess);
        Assert.Contains("CTOPP", cmd.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Ctopp_Mso_Rejected()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("CTOPP MSO");
        Assert.False(cmd.IsSuccess);
        Assert.Contains("CTOPP", cmd.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    // Strict trailing-argument validation — unparseable/extra tokens must be rejected.

    [Fact]
    public void Ctopp_Oc_TrailingJunk_Fails()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("CTOPP OC JUNK");
        Assert.False(cmd.IsSuccess);
    }

    [Fact]
    public void Ctopp_BareHeading_ExtraToken_Fails()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("CTOPP 270 050 EXTRA");
        Assert.False(cmd.IsSuccess);
    }

    [Fact]
    public void Ctopp_Dct_NoFix_Fails()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("CTOPP DCT");
        Assert.False(cmd.IsSuccess);
    }
}
