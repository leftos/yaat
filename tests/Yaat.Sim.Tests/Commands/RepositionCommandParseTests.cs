using Xunit;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Tests.Commands;

/// <summary>
/// Canonical-form round-trip for the Track Reposition commands. These are dispatched by the CRC
/// Track Reposition handler and recorded as <c>RPOSLOC</c>/<c>RPOSMOVE</c>; replay re-parses the
/// recorded text, so the parser must reconstruct the same <see cref="ParsedCommand"/>.
/// </summary>
public class RepositionCommandParseTests
{
    [Fact]
    public void ParsesRposLoc()
    {
        var result = CommandParser.Parse("RPOSLOC N123 37.5 -122.3");

        Assert.True(result.IsSuccess, result.Reason);
        var cmd = Assert.IsType<RepositionToLocationCommand>(result.Value);
        Assert.Equal("N123", cmd.Callsign);
        Assert.Equal(37.5, cmd.Latitude, 6);
        Assert.Equal(-122.3, cmd.Longitude, 6);
    }

    [Fact]
    public void ParsesRposMove()
    {
        var result = CommandParser.Parse("RPOSMOVE N1 N2");

        Assert.True(result.IsSuccess, result.Reason);
        var cmd = Assert.IsType<RepositionMoveCommand>(result.Value);
        Assert.Equal("N1", cmd.FromCallsign);
        Assert.Equal("N2", cmd.ToCallsign);
    }

    [Fact]
    public void RposLoc_MissingCoordinates_Fails()
    {
        Assert.False(CommandParser.Parse("RPOSLOC N123").IsSuccess);
    }

    [Fact]
    public void RposMove_MissingTarget_Fails()
    {
        Assert.False(CommandParser.Parse("RPOSMOVE N1").IsSuccess);
    }
}
