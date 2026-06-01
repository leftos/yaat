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

    public void Dispose()
    {
        _scope.Dispose();
    }

    [Fact]
    public void BareCtopp_ParsesAsPresentPositionHover_DefaultAltitude()
    {
        var cmd = CommandParser.Parse("CTOPP");
        var ctopp = Assert.IsType<ClearedTakeoffPresentCommand>(cmd.Value);
        var hover = Assert.IsType<PresentPositionHoverDeparture>(ctopp.Departure);
        Assert.Equal(25, hover.HoverAltitudeAglFt);
        Assert.Null(ctopp.AssignedAltitude);
    }

    [Fact]
    public void Ctopp_PlusAgl_HundredsShorthand()
    {
        var cmd = CommandParser.Parse("CTOPP +002");
        var ctopp = Assert.IsType<ClearedTakeoffPresentCommand>(cmd.Value);
        var hover = Assert.IsType<PresentPositionHoverDeparture>(ctopp.Departure);
        Assert.Equal(200, hover.HoverAltitudeAglFt);
        Assert.Null(ctopp.AssignedAltitude);
    }

    [Fact]
    public void Ctopp_PlusAgl_DefaultHundred()
    {
        var cmd = CommandParser.Parse("CTOPP +001");
        var ctopp = Assert.IsType<ClearedTakeoffPresentCommand>(cmd.Value);
        var hover = Assert.IsType<PresentPositionHoverDeparture>(ctopp.Departure);
        Assert.Equal(100, hover.HoverAltitudeAglFt);
    }

    [Fact]
    public void Ctopp_PlusAgl_LiteralFeetAboveThousand()
    {
        var cmd = CommandParser.Parse("CTOPP +1500");
        var ctopp = Assert.IsType<ClearedTakeoffPresentCommand>(cmd.Value);
        var hover = Assert.IsType<PresentPositionHoverDeparture>(ctopp.Departure);
        Assert.Equal(1500, hover.HoverAltitudeAglFt);
    }

    [Fact]
    public void Ctopp_PlusMalformed_Rejected()
    {
        var cmd = CommandParser.Parse("CTOPP +");
        Assert.False(cmd.IsSuccess);
        Assert.Contains("CTOPP", cmd.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Ctopp_BareNumber_IsHeading()
    {
        var cmd = CommandParser.Parse("CTOPP 340");
        var ctopp = Assert.IsType<ClearedTakeoffPresentCommand>(cmd.Value);
        var fh = Assert.IsType<FlyHeadingDeparture>(ctopp.Departure);
        Assert.Equal(340, fh.MagneticHeading.Degrees);
        Assert.Null(fh.Direction);
        Assert.Null(ctopp.AssignedAltitude);
    }

    [Fact]
    public void Ctopp_BareNumber_WithAltitude()
    {
        var cmd = CommandParser.Parse("CTOPP 340 015");
        var ctopp = Assert.IsType<ClearedTakeoffPresentCommand>(cmd.Value);
        var fh = Assert.IsType<FlyHeadingDeparture>(ctopp.Departure);
        Assert.Equal(340, fh.MagneticHeading.Degrees);
        Assert.Equal(1500, ctopp.AssignedAltitude);
    }

    [Fact]
    public void Ctopp_LT270_TurnLeftHeading()
    {
        var cmd = CommandParser.Parse("CTOPP LT270");
        var ctopp = Assert.IsType<ClearedTakeoffPresentCommand>(cmd.Value);
        var fh = Assert.IsType<FlyHeadingDeparture>(ctopp.Departure);
        Assert.Equal(270, fh.MagneticHeading.Degrees);
        Assert.Equal(TurnDirection.Left, fh.Direction);
    }

    [Fact]
    public void Ctopp_RT090_WithAlt()
    {
        var cmd = CommandParser.Parse("CTOPP RT090 050");
        var ctopp = Assert.IsType<ClearedTakeoffPresentCommand>(cmd.Value);
        var fh = Assert.IsType<FlyHeadingDeparture>(ctopp.Departure);
        Assert.Equal(90, fh.MagneticHeading.Degrees);
        Assert.Equal(TurnDirection.Right, fh.Direction);
        Assert.Equal(5000, ctopp.AssignedAltitude);
    }

    [Fact]
    public void Ctopp_LH270_TurnLeftHeading()
    {
        var cmd = CommandParser.Parse("CTOPP LH270");
        var ctopp = Assert.IsType<ClearedTakeoffPresentCommand>(cmd.Value);
        var fh = Assert.IsType<FlyHeadingDeparture>(ctopp.Departure);
        Assert.Equal(270, fh.MagneticHeading.Degrees);
        Assert.Equal(TurnDirection.Left, fh.Direction);
    }

    [Fact]
    public void Ctopp_H180_FlyHeading()
    {
        var cmd = CommandParser.Parse("CTOPP H180");
        var ctopp = Assert.IsType<ClearedTakeoffPresentCommand>(cmd.Value);
        var fh = Assert.IsType<FlyHeadingDeparture>(ctopp.Departure);
        Assert.Equal(180, fh.MagneticHeading.Degrees);
        Assert.Null(fh.Direction);
    }

    [Fact]
    public void Ctopp_Oc_OnCourse()
    {
        var cmd = CommandParser.Parse("CTOPP OC");
        var ctopp = Assert.IsType<ClearedTakeoffPresentCommand>(cmd.Value);
        Assert.IsType<OnCourseDeparture>(ctopp.Departure);
        Assert.Null(ctopp.AssignedAltitude);
    }

    [Fact]
    public void Ctopp_Oc_WithAlt()
    {
        var cmd = CommandParser.Parse("CTOPP OC 050");
        var ctopp = Assert.IsType<ClearedTakeoffPresentCommand>(cmd.Value);
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
        var cmd = CommandParser.Parse("CTOPP DCT SUNOL");
        var ctopp = Assert.IsType<ClearedTakeoffPresentCommand>(cmd.Value);
        var dfd = Assert.IsType<DirectFixDeparture>(ctopp.Departure);
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
        var cmd = CommandParser.Parse("CTOPP TLDCT SUNOL");
        var ctopp = Assert.IsType<ClearedTakeoffPresentCommand>(cmd.Value);
        var dfd = Assert.IsType<DirectFixDeparture>(ctopp.Departure);
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
        var cmd = CommandParser.Parse("CTOPP TRDCT SUNOL 040");
        var ctopp = Assert.IsType<ClearedTakeoffPresentCommand>(cmd.Value);
        var dfd = Assert.IsType<DirectFixDeparture>(ctopp.Departure);
        Assert.Equal("SUNOL", dfd.FixName);
        Assert.Equal(TurnDirection.Right, dfd.Direction);
        Assert.Equal(4000, ctopp.AssignedAltitude);
    }

    [Fact]
    public void Ctopp_Rh_Rejected()
    {
        var cmd = CommandParser.Parse("CTOPP RH");
        Assert.False(cmd.IsSuccess);
        Assert.Contains("CTOPP", cmd.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Ctopp_Mlt_Rejected()
    {
        var cmd = CommandParser.Parse("CTOPP MLT");
        Assert.False(cmd.IsSuccess);
        Assert.Contains("CTOPP", cmd.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Ctopp_Mrt_Rejected()
    {
        var cmd = CommandParser.Parse("CTOPP MRT");
        Assert.False(cmd.IsSuccess);
        Assert.Contains("CTOPP", cmd.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Ctopp_Mrc_Rejected()
    {
        var cmd = CommandParser.Parse("CTOPP MRC");
        Assert.False(cmd.IsSuccess);
        Assert.Contains("CTOPP", cmd.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Ctopp_Mr270_Rejected()
    {
        var cmd = CommandParser.Parse("CTOPP MR270");
        Assert.False(cmd.IsSuccess);
        Assert.Contains("CTOPP", cmd.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Ctopp_Mld_Rejected()
    {
        var cmd = CommandParser.Parse("CTOPP MLD");
        Assert.False(cmd.IsSuccess);
        Assert.Contains("CTOPP", cmd.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Ctopp_Mso_Rejected()
    {
        var cmd = CommandParser.Parse("CTOPP MSO");
        Assert.False(cmd.IsSuccess);
        Assert.Contains("CTOPP", cmd.Reason!, StringComparison.OrdinalIgnoreCase);
    }
}
