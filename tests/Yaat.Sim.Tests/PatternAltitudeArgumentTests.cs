using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Testing;

namespace Yaat.Sim.Tests;

public class PatternAltitudeArgumentTests
{
    // -------------------------------------------------------------------------
    // MLT/MRT parsing
    // -------------------------------------------------------------------------

    [Fact]
    public void ParseMLT_NoArg_ReturnsNullRunwayAndAltitude()
    {
        var result = CommandParser.ParseCompound("MLT");
        Assert.True(result.IsSuccess);
        var cmd = Assert.IsType<MakeLeftTrafficCommand>(result.Value!.Blocks[0].Commands[0]);
        Assert.Null(cmd.RunwayId);
        Assert.Null(cmd.Altitude);
    }

    [Fact]
    public void ParseMLT_WithAltitude_ParsesCorrectly()
    {
        var result = CommandParser.ParseCompound("MLT 15");
        Assert.True(result.IsSuccess);
        var cmd = Assert.IsType<MakeLeftTrafficCommand>(result.Value!.Blocks[0].Commands[0]);
        Assert.Null(cmd.RunwayId);
        Assert.Equal(1500, cmd.Altitude);
    }

    [Fact]
    public void ParseMRT_WithRunwayAndAltitude()
    {
        var result = CommandParser.ParseCompound("MRT 28R 20");
        Assert.True(result.IsSuccess);
        var cmd = Assert.IsType<MakeRightTrafficCommand>(result.Value!.Blocks[0].Commands[0]);
        Assert.Equal("28R", cmd.RunwayId);
        Assert.Equal(2000, cmd.Altitude);
    }

    [Fact]
    public void ParseMLT_WithRunway_NoAltitude()
    {
        var result = CommandParser.ParseCompound("MLT 28R");
        Assert.True(result.IsSuccess);
        var cmd = Assert.IsType<MakeLeftTrafficCommand>(result.Value!.Blocks[0].Commands[0]);
        Assert.Equal("28R", cmd.RunwayId);
        Assert.Null(cmd.Altitude);
    }

    [Fact]
    public void ParseMLT_WithRunwayStartingWith0()
    {
        var result = CommandParser.ParseCompound("MLT 09L");
        Assert.True(result.IsSuccess);
        var cmd = Assert.IsType<MakeLeftTrafficCommand>(result.Value!.Blocks[0].Commands[0]);
        Assert.Equal("09L", cmd.RunwayId);
        Assert.Null(cmd.Altitude);
    }

    // -------------------------------------------------------------------------
    // GA MLT/MRT parsing
    // -------------------------------------------------------------------------

    [Fact]
    public void ParseGA_MLT_NoAltitude()
    {
        var result = CommandParser.ParseCompound("GA MLT");
        Assert.True(result.IsSuccess);
        var cmd = Assert.IsType<GoAroundCommand>(result.Value!.Blocks[0].Commands[0]);
        Assert.Equal(PatternDirection.Left, cmd.TrafficPattern);
        Assert.Null(cmd.TargetAltitude);
    }

    [Fact]
    public void ParseGA_MLT_WithAltitude()
    {
        var result = CommandParser.ParseCompound("GA MLT 12");
        Assert.True(result.IsSuccess);
        var cmd = Assert.IsType<GoAroundCommand>(result.Value!.Blocks[0].Commands[0]);
        Assert.Equal(PatternDirection.Left, cmd.TrafficPattern);
        Assert.Equal(1200, cmd.TargetAltitude);
    }

    [Fact]
    public void ParseGA_MRT_WithAltitude()
    {
        var result = CommandParser.ParseCompound("GA MRT 15");
        Assert.True(result.IsSuccess);
        var cmd = Assert.IsType<GoAroundCommand>(result.Value!.Blocks[0].Commands[0]);
        Assert.Equal(PatternDirection.Right, cmd.TrafficPattern);
        Assert.Equal(1500, cmd.TargetAltitude);
    }

    // -------------------------------------------------------------------------
    // CTO MLT/MRT parsing
    // -------------------------------------------------------------------------

    [Fact]
    public void ParseCTO_MLT_NoArgs()
    {
        var result = CommandParser.ParseCompound("CTO MLT");
        Assert.True(result.IsSuccess);
        var cmd = Assert.IsType<ClearedForTakeoffCommand>(result.Value!.Blocks[0].Commands[0]);
        var ct = Assert.IsType<ClosedTrafficDeparture>(cmd.Departure);
        Assert.Equal(PatternDirection.Left, ct.Direction);
        Assert.Null(ct.RunwayId);
        Assert.Null(ct.PatternAltitude);
    }

    [Fact]
    public void ParseCTO_MLT_WithRunwayAndAltitude()
    {
        var result = CommandParser.ParseCompound("CTO MLT 28R 15");
        Assert.True(result.IsSuccess);
        var cmd = Assert.IsType<ClearedForTakeoffCommand>(result.Value!.Blocks[0].Commands[0]);
        var ct = Assert.IsType<ClosedTrafficDeparture>(cmd.Departure);
        Assert.Equal(PatternDirection.Left, ct.Direction);
        Assert.Equal("28R", ct.RunwayId);
        Assert.Equal(1500, ct.PatternAltitude);
    }

    [Fact]
    public void ParseCTO_MRT_WithAltitudeOnly()
    {
        var result = CommandParser.ParseCompound("CTO MRT 15");
        Assert.True(result.IsSuccess);
        var cmd = Assert.IsType<ClearedForTakeoffCommand>(result.Value!.Blocks[0].Commands[0]);
        var ct = Assert.IsType<ClosedTrafficDeparture>(cmd.Departure);
        Assert.Equal(PatternDirection.Right, ct.Direction);
        Assert.Null(ct.RunwayId);
        Assert.Equal(1500, ct.PatternAltitude);
    }

    // -------------------------------------------------------------------------
    // Dispatch sets PatternAltitudeOverrideFt
    // -------------------------------------------------------------------------

    [Fact]
    public void MLT_WithAltitude_SetsPatternAltitudeOverride()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        var runway = TestVnasData.NavigationDb.GetRunway("KOAK", "28L");
        if (runway is null)
        {
            return;
        }

        var ac = new AircraftState
        {
            Callsign = "N123AB",
            AircraftType = "C172",
            Latitude = 37.0,
            Longitude = -122.0,
            TrueHeading = new TrueHeading(100),
            Altitude = 1100,
            IndicatedAirspeed = 90,
            IsOnGround = false,
            Departure = "KOAK",
            Destination = "KOAK",
        };
        ac.Phases = new PhaseList { AssignedRunway = runway };

        var result = PatternCommandHandler.TryChangePatternDirection(ac, PatternDirection.Left, null, 1500);

        Assert.True(result.Success);
        Assert.Equal(1500, ac.PatternAltitudeOverrideFt);
    }
}
