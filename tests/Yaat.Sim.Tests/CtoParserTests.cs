using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;

namespace Yaat.Sim.Tests;

public class CtoParserTests : IDisposable
{
    private readonly IDisposable _scope;

    public CtoParserTests()
    {
        _scope = NavigationDatabase.ScopedOverride(NavigationDatabase.ForTesting());
    }

    public void Dispose()
    {
        _scope.Dispose();
    }

    [Fact]
    public void BareCto_ParsesAsDefaultDeparture()
    {
        var cmd = CommandParser.Parse("CTO");
        var cto = Assert.IsType<ClearedForTakeoffCommand>(cmd.Value);
        Assert.IsType<DefaultDeparture>(cto.Departure);
        Assert.Null(cto.AssignedAltitude);
    }

    [Fact]
    public void Cto_BareNumber_IsHeading()
    {
        var cmd = CommandParser.Parse("CTO 050");
        var cto = Assert.IsType<ClearedForTakeoffCommand>(cmd.Value);
        var fh = Assert.IsType<FlyHeadingDeparture>(cto.Departure);
        Assert.Equal(50, fh.MagneticHeading.Degrees);
        Assert.Null(fh.Direction);
        Assert.Null(cto.AssignedAltitude);
    }

    [Fact]
    public void Cto_BareNumber_WithAltitude()
    {
        var cmd = CommandParser.Parse("CTO 060 250");
        var cto = Assert.IsType<ClearedForTakeoffCommand>(cmd.Value);
        var fh = Assert.IsType<FlyHeadingDeparture>(cto.Departure);
        Assert.Equal(60, fh.MagneticHeading.Degrees);
        Assert.Null(fh.Direction);
        Assert.Equal(25000, cto.AssignedAltitude);
    }

    [Fact]
    public void Cto_360_IsHeading360()
    {
        var cmd = CommandParser.Parse("CTO 360");
        var cto = Assert.IsType<ClearedForTakeoffCommand>(cmd.Value);
        var fh = Assert.IsType<FlyHeadingDeparture>(cto.Departure);
        // MagneticHeading normalizes [0, 360) so 360 → 0 degrees; display int is 360
        Assert.Equal(360, fh.MagneticHeading.ToDisplayInt());
        Assert.Null(cto.AssignedAltitude);
    }

    [Fact]
    public void Cto_Mrc_RightCrosswind()
    {
        var cmd = CommandParser.Parse("CTO MRC");
        var cto = Assert.IsType<ClearedForTakeoffCommand>(cmd.Value);
        var rel = Assert.IsType<RelativeTurnDeparture>(cto.Departure);
        Assert.Equal(90, rel.Degrees);
        Assert.Equal(TurnDirection.Right, rel.Direction);
    }

    [Fact]
    public void Cto_Mrc_WithAlt()
    {
        var cmd = CommandParser.Parse("CTO MRC 014");
        var cto = Assert.IsType<ClearedForTakeoffCommand>(cmd.Value);
        var rel = Assert.IsType<RelativeTurnDeparture>(cto.Departure);
        Assert.Equal(90, rel.Degrees);
        Assert.Equal(TurnDirection.Right, rel.Direction);
        Assert.Equal(1400, cto.AssignedAltitude);
    }

    [Fact]
    public void Cto_Mrd_RightDownwind()
    {
        var cmd = CommandParser.Parse("CTO MRD");
        var cto = Assert.IsType<ClearedForTakeoffCommand>(cmd.Value);
        var rel = Assert.IsType<RelativeTurnDeparture>(cto.Departure);
        Assert.Equal(180, rel.Degrees);
        Assert.Equal(TurnDirection.Right, rel.Direction);
    }

    [Fact]
    public void Cto_Mr270_ArbitraryRightTurn()
    {
        var cmd = CommandParser.Parse("CTO MR270");
        var cto = Assert.IsType<ClearedForTakeoffCommand>(cmd.Value);
        var rel = Assert.IsType<RelativeTurnDeparture>(cto.Departure);
        Assert.Equal(270, rel.Degrees);
        Assert.Equal(TurnDirection.Right, rel.Direction);
    }

    [Fact]
    public void Cto_Mr45_WithAlt()
    {
        var cmd = CommandParser.Parse("CTO MR45 050");
        var cto = Assert.IsType<ClearedForTakeoffCommand>(cmd.Value);
        var rel = Assert.IsType<RelativeTurnDeparture>(cto.Departure);
        Assert.Equal(45, rel.Degrees);
        Assert.Equal(TurnDirection.Right, rel.Direction);
        Assert.Equal(5000, cto.AssignedAltitude);
    }

    [Fact]
    public void Cto_Mlc_LeftCrosswind()
    {
        var cmd = CommandParser.Parse("CTO MLC");
        var cto = Assert.IsType<ClearedForTakeoffCommand>(cmd.Value);
        var rel = Assert.IsType<RelativeTurnDeparture>(cto.Departure);
        Assert.Equal(90, rel.Degrees);
        Assert.Equal(TurnDirection.Left, rel.Direction);
    }

    [Fact]
    public void Cto_Mld_LeftDownwind()
    {
        var cmd = CommandParser.Parse("CTO MLD");
        var cto = Assert.IsType<ClearedForTakeoffCommand>(cmd.Value);
        var rel = Assert.IsType<RelativeTurnDeparture>(cto.Departure);
        Assert.Equal(180, rel.Degrees);
        Assert.Equal(TurnDirection.Left, rel.Direction);
    }

    [Fact]
    public void Cto_Ml270_ArbitraryLeftTurn()
    {
        var cmd = CommandParser.Parse("CTO ML270");
        var cto = Assert.IsType<ClearedForTakeoffCommand>(cmd.Value);
        var rel = Assert.IsType<RelativeTurnDeparture>(cto.Departure);
        Assert.Equal(270, rel.Degrees);
        Assert.Equal(TurnDirection.Left, rel.Direction);
    }

    [Fact]
    public void Cto_Mrh_RunwayHeading()
    {
        var cmd = CommandParser.Parse("CTO MRH");
        var cto = Assert.IsType<ClearedForTakeoffCommand>(cmd.Value);
        Assert.IsType<RunwayHeadingDeparture>(cto.Departure);
    }

    [Fact]
    public void Cto_Mso_RunwayHeadingAlias()
    {
        var cmd = CommandParser.Parse("CTO MSO");
        var cto = Assert.IsType<ClearedForTakeoffCommand>(cmd.Value);
        Assert.IsType<RunwayHeadingDeparture>(cto.Departure);
    }

    [Fact]
    public void Cto_Rh_RunwayHeading()
    {
        var cmd = CommandParser.Parse("CTO RH");
        var cto = Assert.IsType<ClearedForTakeoffCommand>(cmd.Value);
        Assert.IsType<RunwayHeadingDeparture>(cto.Departure);
    }

    [Fact]
    public void Cto_Rh_WithAlt()
    {
        var cmd = CommandParser.Parse("CTO RH 050");
        var cto = Assert.IsType<ClearedForTakeoffCommand>(cmd.Value);
        Assert.IsType<RunwayHeadingDeparture>(cto.Departure);
        Assert.Equal(5000, cto.AssignedAltitude);
    }

    [Fact]
    public void Cto_H270_FlyHeading()
    {
        var cmd = CommandParser.Parse("CTO H270");
        var cto = Assert.IsType<ClearedForTakeoffCommand>(cmd.Value);
        var fh = Assert.IsType<FlyHeadingDeparture>(cto.Departure);
        Assert.Equal(270, fh.MagneticHeading.Degrees);
        Assert.Null(fh.Direction);
    }

    [Fact]
    public void Cto_Rh270_TurnRightHeading()
    {
        var cmd = CommandParser.Parse("CTO RH270");
        var cto = Assert.IsType<ClearedForTakeoffCommand>(cmd.Value);
        var fh = Assert.IsType<FlyHeadingDeparture>(cto.Departure);
        Assert.Equal(270, fh.MagneticHeading.Degrees);
        Assert.Equal(TurnDirection.Right, fh.Direction);
    }

    [Fact]
    public void Cto_Lh270_TurnLeftHeading()
    {
        var cmd = CommandParser.Parse("CTO LH270");
        var cto = Assert.IsType<ClearedForTakeoffCommand>(cmd.Value);
        var fh = Assert.IsType<FlyHeadingDeparture>(cto.Departure);
        Assert.Equal(270, fh.MagneticHeading.Degrees);
        Assert.Equal(TurnDirection.Left, fh.Direction);
    }

    [Fact]
    public void Cto_Lh270_WithAlt()
    {
        var cmd = CommandParser.Parse("CTO LH270 014");
        var cto = Assert.IsType<ClearedForTakeoffCommand>(cmd.Value);
        var fh = Assert.IsType<FlyHeadingDeparture>(cto.Departure);
        Assert.Equal(270, fh.MagneticHeading.Degrees);
        Assert.Equal(TurnDirection.Left, fh.Direction);
        Assert.Equal(1400, cto.AssignedAltitude);
    }

    [Fact]
    public void Cto_Rt270_TurnRightHeading()
    {
        var cmd = CommandParser.Parse("CTO RT270");
        var cto = Assert.IsType<ClearedForTakeoffCommand>(cmd.Value);
        var fh = Assert.IsType<FlyHeadingDeparture>(cto.Departure);
        Assert.Equal(270, fh.MagneticHeading.Degrees);
        Assert.Equal(TurnDirection.Right, fh.Direction);
    }

    [Fact]
    public void Cto_Lt270_TurnLeftHeading()
    {
        var cmd = CommandParser.Parse("CTO LT270");
        var cto = Assert.IsType<ClearedForTakeoffCommand>(cmd.Value);
        var fh = Assert.IsType<FlyHeadingDeparture>(cto.Departure);
        Assert.Equal(270, fh.MagneticHeading.Degrees);
        Assert.Equal(TurnDirection.Left, fh.Direction);
    }

    [Fact]
    public void Cto_Oc_OnCourse()
    {
        var cmd = CommandParser.Parse("CTO OC");
        var cto = Assert.IsType<ClearedForTakeoffCommand>(cmd.Value);
        Assert.IsType<OnCourseDeparture>(cto.Departure);
    }

    [Fact]
    public void Cto_Oc_WithAlt()
    {
        var cmd = CommandParser.Parse("CTO OC 050");
        var cto = Assert.IsType<ClearedForTakeoffCommand>(cmd.Value);
        Assert.IsType<OnCourseDeparture>(cto.Departure);
        Assert.Equal(5000, cto.AssignedAltitude);
    }

    [Fact]
    public void Cto_Dct_DirectFix()
    {
        _scope.Dispose();
        using var _ = NavigationDatabase.ScopedOverride(
            NavigationDatabase.ForTesting(fixes: new Dictionary<string, (double Lat, double Lon)> { ["SUNOL"] = (37.5, -121.8) })
        );
        var cmd = CommandParser.Parse("CTO DCT SUNOL");
        var cto = Assert.IsType<ClearedForTakeoffCommand>(cmd.Value);
        var dfd = Assert.IsType<DirectFixDeparture>(cto.Departure);
        Assert.Equal("SUNOL", dfd.FixName);
        Assert.Equal(37.5, dfd.Lat, 1);
        Assert.Equal(-121.8, dfd.Lon, 1);
    }

    [Fact]
    public void Cto_Dct_WithAlt()
    {
        _scope.Dispose();
        using var _ = NavigationDatabase.ScopedOverride(
            NavigationDatabase.ForTesting(fixes: new Dictionary<string, (double Lat, double Lon)> { ["SUNOL"] = (37.5, -121.8) })
        );
        var cmd = CommandParser.Parse("CTO DCT SUNOL 050");
        var cto = Assert.IsType<ClearedForTakeoffCommand>(cmd.Value);
        var dfd = Assert.IsType<DirectFixDeparture>(cto.Departure);
        Assert.Equal("SUNOL", dfd.FixName);
        Assert.Equal(5000, cto.AssignedAltitude);
    }

    [Fact]
    public void Cto_Mrt_RightClosedTraffic()
    {
        var cmd = CommandParser.Parse("CTO MRT");
        var cto = Assert.IsType<ClearedForTakeoffCommand>(cmd.Value);
        var ct = Assert.IsType<ClosedTrafficDeparture>(cto.Departure);
        Assert.Equal(PatternDirection.Right, ct.Direction);
    }

    [Fact]
    public void Cto_Mlt_LeftClosedTraffic()
    {
        var cmd = CommandParser.Parse("CTO MLT");
        var cto = Assert.IsType<ClearedForTakeoffCommand>(cmd.Value);
        var ct = Assert.IsType<ClosedTrafficDeparture>(cto.Departure);
        Assert.Equal(PatternDirection.Left, ct.Direction);
    }

    [Fact]
    public void Cto_Mrh_WithAlt()
    {
        var cmd = CommandParser.Parse("CTO MRH 050");
        var cto = Assert.IsType<ClearedForTakeoffCommand>(cmd.Value);
        Assert.IsType<RunwayHeadingDeparture>(cto.Departure);
        Assert.Equal(5000, cto.AssignedAltitude);
    }

    // Cross-runway closed traffic

    [Fact]
    public void Cto_Mrt_WithRunway_ParsesRunwayId()
    {
        var cmd = CommandParser.Parse("CTO MRT 28R");
        var cto = Assert.IsType<ClearedForTakeoffCommand>(cmd.Value);
        var ct = Assert.IsType<ClosedTrafficDeparture>(cto.Departure);
        Assert.Equal(PatternDirection.Right, ct.Direction);
        Assert.Equal("28R", ct.RunwayId);
        Assert.Null(cto.AssignedAltitude);
    }

    [Fact]
    public void Cto_Mlt_WithRunway_ParsesRunwayId()
    {
        var cmd = CommandParser.Parse("CTO MLT 28L");
        var cto = Assert.IsType<ClearedForTakeoffCommand>(cmd.Value);
        var ct = Assert.IsType<ClosedTrafficDeparture>(cto.Departure);
        Assert.Equal(PatternDirection.Left, ct.Direction);
        Assert.Equal("28L", ct.RunwayId);
    }

    [Fact]
    public void Cto_Mrt_NoRunway_RunwayIdIsNull()
    {
        var cmd = CommandParser.Parse("CTO MRT");
        var cto = Assert.IsType<ClearedForTakeoffCommand>(cmd.Value);
        var ct = Assert.IsType<ClosedTrafficDeparture>(cto.Departure);
        Assert.Null(ct.RunwayId);
    }
}
