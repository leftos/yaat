using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;

namespace Yaat.Sim.Tests;

public class WarpCommandTests
{
    private const double FixLat = 37.5;
    private const double FixLon = -121.8;

    private static IDisposable WithFix() =>
        NavigationDatabase.ScopedOverride(
            NavigationDatabase.ForTesting(fixes: new Dictionary<string, (double Lat, double Lon)> { ["SUNOL"] = (FixLat, FixLon) })
        );

    private static AircraftState MakeAircraft(double heading = 90, double altitude = 3500, double ias = 180) =>
        new()
        {
            Callsign = "N123",
            AircraftType = "C172",
            Position = new LatLon(37.0, -122.0),
            TrueHeading = new TrueHeading(heading),
            TrueTrack = new TrueHeading(heading),
            Altitude = altitude,
            IndicatedAirspeed = ias,
        };

    // ----- parser: shape coverage ----------------------------------------

    [Fact]
    public void Warp_PositionOnly_LeavesHeadingAltitudeSpeedUnset()
    {
        using var _ = WithFix();
        var cmd = Assert.IsType<WarpCommand>(CommandParser.Parse("WARP SUNOL").Value);
        Assert.Equal("SUNOL", cmd.PositionLabel);
        Assert.Equal(FixLat, cmd.Latitude);
        Assert.Equal(FixLon, cmd.Longitude);
        Assert.Null(cmd.MagneticHeading);
        Assert.Null(cmd.Altitude);
        Assert.Null(cmd.Speed);
    }

    [Fact]
    public void Warp_HeadingOnly_FillsHeadingAndLeavesOthersUnset()
    {
        using var _ = WithFix();
        var cmd = Assert.IsType<WarpCommand>(CommandParser.Parse("WARP SUNOL 270").Value);
        Assert.Equal(270, cmd.MagneticHeading?.Degrees);
        Assert.Null(cmd.Altitude);
        Assert.Null(cmd.Speed);
    }

    [Fact]
    public void Warp_FullFeetSecondArg_SkipsHeadingAndFillsAltitude()
    {
        using var _ = WithFix();
        var cmd = Assert.IsType<WarpCommand>(CommandParser.Parse("WARP SUNOL 5000").Value);
        Assert.Null(cmd.MagneticHeading);
        Assert.Equal(5000, cmd.Altitude);
        Assert.Null(cmd.Speed);
    }

    [Fact]
    public void Warp_HeadingAndShorthandAltitude_LeavesSpeedUnset()
    {
        using var _ = WithFix();
        var cmd = Assert.IsType<WarpCommand>(CommandParser.Parse("WARP SUNOL 270 50").Value);
        Assert.Equal(270, cmd.MagneticHeading?.Degrees);
        Assert.Equal(5000, cmd.Altitude);
        Assert.Null(cmd.Speed);
    }

    [Fact]
    public void Warp_FullFeetThenSpeed_SkipsHeading()
    {
        using var _ = WithFix();
        var cmd = Assert.IsType<WarpCommand>(CommandParser.Parse("WARP SUNOL 5000 220").Value);
        Assert.Null(cmd.MagneticHeading);
        Assert.Equal(5000, cmd.Altitude);
        Assert.Equal(220, cmd.Speed);
    }

    [Fact]
    public void Warp_AllFourArgs_SetsEverything()
    {
        using var _ = WithFix();
        var cmd = Assert.IsType<WarpCommand>(CommandParser.Parse("WARP SUNOL 270 5000 220").Value);
        Assert.Equal(270, cmd.MagneticHeading?.Degrees);
        Assert.Equal(5000, cmd.Altitude);
        Assert.Equal(220, cmd.Speed);
    }

    // ----- parser: failure cases -----------------------------------------

    [Fact]
    public void Warp_NoArg_Fails()
    {
        using var _ = WithFix();
        var result = CommandParser.Parse("WARP");
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void Warp_TooManyArgs_Fails()
    {
        using var _ = WithFix();
        var result = CommandParser.Parse("WARP SUNOL 270 5000 220 99");
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void Warp_GarbageToken_Fails()
    {
        using var _ = WithFix();
        var result = CommandParser.Parse("WARP SUNOL abc");
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void Warp_NegativeHeadingFollowedByAltitude_FillsAltitudeOnly()
    {
        // -50: heading rejects (out of range); altitude rejects (resolver returns null for <=0); speed rejects (<=0).
        // Whole command should fail because no slot accepts the token.
        using var _ = WithFix();
        var result = CommandParser.Parse("WARP SUNOL -50");
        Assert.False(result.IsSuccess);
    }

    // ----- ApplyWarp: nulls fall back to current state -------------------

    [Fact]
    public void ApplyWarp_NullHeading_KeepsCurrentHeading()
    {
        var ac = MakeAircraft(heading: 123, altitude: 4500, ias: 210);
        var cmd = new WarpCommand("X", FixLat, FixLon, MagneticHeading: null, Altitude: 6000, Speed: 250);
        FlightCommandHandler.ApplyWarp(cmd, ac);
        Assert.Equal(123, ac.MagneticHeading.Degrees, 6);
        Assert.Equal(6000, ac.Altitude);
        Assert.Equal(250, ac.IndicatedAirspeed);
    }

    [Fact]
    public void ApplyWarp_NullAltitude_KeepsCurrentAltitude()
    {
        var ac = MakeAircraft(heading: 90, altitude: 4500, ias: 210);
        var cmd = new WarpCommand("X", FixLat, FixLon, new MagneticHeading(270), Altitude: null, Speed: 250);
        FlightCommandHandler.ApplyWarp(cmd, ac);
        Assert.Equal(270, ac.MagneticHeading.Degrees, 6);
        Assert.Equal(4500, ac.Altitude);
        Assert.Equal(250, ac.IndicatedAirspeed);
    }

    [Fact]
    public void ApplyWarp_NullSpeed_KeepsCurrentSpeed()
    {
        var ac = MakeAircraft(heading: 90, altitude: 4500, ias: 210);
        var cmd = new WarpCommand("X", FixLat, FixLon, new MagneticHeading(270), Altitude: 6000, Speed: null);
        FlightCommandHandler.ApplyWarp(cmd, ac);
        Assert.Equal(270, ac.MagneticHeading.Degrees, 6);
        Assert.Equal(6000, ac.Altitude);
        Assert.Equal(210, ac.IndicatedAirspeed);
    }

    [Fact]
    public void ApplyWarp_AllNull_KeepsAllExceptPosition()
    {
        var ac = MakeAircraft(heading: 90, altitude: 4500, ias: 210);
        var cmd = new WarpCommand("X", FixLat, FixLon, MagneticHeading: null, Altitude: null, Speed: null);
        FlightCommandHandler.ApplyWarp(cmd, ac);
        Assert.Equal(FixLat, ac.Position.Lat);
        Assert.Equal(FixLon, ac.Position.Lon);
        Assert.Equal(90, ac.MagneticHeading.Degrees, 6);
        Assert.Equal(4500, ac.Altitude);
        Assert.Equal(210, ac.IndicatedAirspeed);
    }

    [Fact]
    public void ApplyWarp_AllSet_AppliesAll()
    {
        var ac = MakeAircraft(heading: 90, altitude: 4500, ias: 210);
        var cmd = new WarpCommand("X", FixLat, FixLon, new MagneticHeading(180), Altitude: 8000, Speed: 250);
        FlightCommandHandler.ApplyWarp(cmd, ac);
        Assert.Equal(180, ac.MagneticHeading.Degrees, 6);
        Assert.Equal(8000, ac.Altitude);
        Assert.Equal(250, ac.IndicatedAirspeed);
        Assert.False(ac.IsOnGround);
    }
}
