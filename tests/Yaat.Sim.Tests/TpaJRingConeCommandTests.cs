using Xunit;
using Yaat.Sim;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Tests;

/// <summary>
/// Unit coverage for the instructor TPA J-Ring / Cone commands: parsing the size argument
/// (J-Ring radius / Cone length in nm, 1-30 NM range per CRC), canonical describers, the
/// TrackEngine handlers that write <c>AircraftStarsState.TpaType</c> + <c>TpaSize</c>, and the
/// snapshot round-trip. These overlays render on YAAT's own radar; they never reach the student's CRC.
/// </summary>
public class TpaJRingConeCommandTests
{
    private const int TpaJRing = 1;
    private const int TpaCone = 2;

    [Fact]
    public void JRing_WithRadius_ParsesEnableAndSize()
    {
        var cmd = Assert.IsType<JRingCommand>(CommandParser.Parse("JRING 3").Value);
        Assert.True(cmd.Enable);
        Assert.Equal(3.0, cmd.Size);
    }

    [Fact]
    public void JRing_Bare_ParsesClear()
    {
        var cmd = Assert.IsType<JRingCommand>(CommandParser.Parse("JRING").Value);
        Assert.False(cmd.Enable);
        Assert.Null(cmd.Size);
    }

    [Fact]
    public void Cone_WithLength_ParsesEnableAndFractionalSize()
    {
        var cmd = Assert.IsType<ConeCommand>(CommandParser.Parse("CONE 5.5").Value);
        Assert.True(cmd.Enable);
        Assert.Equal(5.5, cmd.Size);
    }

    [Fact]
    public void Cone_Bare_ParsesClear()
    {
        var cmd = Assert.IsType<ConeCommand>(CommandParser.Parse("CONE").Value);
        Assert.False(cmd.Enable);
        Assert.Null(cmd.Size);
    }

    [Theory]
    [InlineData("JRING 0")]
    [InlineData("JRING 0.5")]
    [InlineData("JRING 31")]
    [InlineData("CONE 0")]
    [InlineData("CONE 30.1")]
    [InlineData("JRING abc")]
    public void TpaSizeOutOfRange_Fails(string input)
    {
        Assert.False(CommandParser.Parse(input).IsSuccess);
    }

    [Theory]
    [InlineData("JRING 1")]
    [InlineData("JRING 30")]
    [InlineData("CONE 1")]
    [InlineData("CONE 30")]
    public void TpaSizeAtRangeBounds_Succeeds(string input)
    {
        Assert.True(CommandParser.Parse(input).IsSuccess);
    }

    [Fact]
    public void Canonical_RoundTrips()
    {
        Assert.Equal("JRING 3", CommandDescriber.DescribeCommand(new JRingCommand(true, 3.0)));
        Assert.Equal("JRING 3.5", CommandDescriber.DescribeCommand(new JRingCommand(true, 3.5)));
        Assert.Equal("JRING", CommandDescriber.DescribeCommand(new JRingCommand(false, null)));
        Assert.Equal("CONE 5", CommandDescriber.DescribeCommand(new ConeCommand(true, 5.0)));
        Assert.Equal("CONE", CommandDescriber.DescribeCommand(new ConeCommand(false, null)));
    }

    [Fact]
    public void MapsToCanonicalType()
    {
        Assert.Equal(CanonicalCommandType.JRing, CommandDescriber.ToCanonicalType(new JRingCommand(true, 3.0)));
        Assert.Equal(CanonicalCommandType.Cone, CommandDescriber.ToCanonicalType(new ConeCommand(true, 3.0)));
    }

    [Fact]
    public void HandleJRing_SetsTypeAndSize()
    {
        var ac = new AircraftState { Callsign = "AAL100", AircraftType = "B738" };

        TrackEngine.HandleJRing(ac, enable: true, size: 4.0);

        Assert.Equal(TpaJRing, ac.Stars.TpaType);
        Assert.Equal(4.0, ac.Stars.TpaSize);
    }

    [Fact]
    public void HandleCone_SetsTypeAndSize()
    {
        var ac = new AircraftState { Callsign = "AAL100", AircraftType = "B738" };

        TrackEngine.HandleCone(ac, enable: true, size: 6.0);

        Assert.Equal(TpaCone, ac.Stars.TpaType);
        Assert.Equal(6.0, ac.Stars.TpaSize);
    }

    [Fact]
    public void HandleJRing_Clear_ResetsTypeAndSize()
    {
        var ac = new AircraftState { Callsign = "AAL100", AircraftType = "B738" };
        TrackEngine.HandleJRing(ac, enable: true, size: 4.0);

        TrackEngine.HandleJRing(ac, enable: false, size: null);

        Assert.Null(ac.Stars.TpaType);
        Assert.Equal(0.0, ac.Stars.TpaSize);
    }

    [Fact]
    public void TpaState_SurvivesSnapshotRoundTrip()
    {
        var ac = new AircraftState { Callsign = "AAL100", AircraftType = "B738" };
        TrackEngine.HandleCone(ac, enable: true, size: 7.0);

        var restored = AircraftState.FromSnapshot(ac.ToSnapshot(), groundLayout: null);

        Assert.Equal(TpaCone, restored.Stars.TpaType);
        Assert.Equal(7.0, restored.Stars.TpaSize);
    }
}
