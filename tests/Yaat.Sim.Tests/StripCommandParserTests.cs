using Xunit;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Tests;

public class StripCommandParserTests
{
    [Fact]
    public void Strip_ParsesBayName()
    {
        var result = CommandParser.Parse("STRIP Ground");
        var cmd = Assert.IsType<StripMoveCommand>(result.Value);
        Assert.Equal(["Ground"], cmd.Tokens);
    }

    [Fact]
    public void Strip_NoArg_ReturnsNull()
    {
        var result = CommandParser.Parse("STRIP");
        Assert.Null(result.Value);
    }

    [Fact]
    public void Strip_ParsesBayAndRack()
    {
        var result = CommandParser.Parse("STRIP Ground 1");
        var cmd = Assert.IsType<StripMoveCommand>(result.Value);
        Assert.Equal(["Ground", "1"], cmd.Tokens);
    }

    [Fact]
    public void Strip_ParsesBayRackAndIndex()
    {
        var result = CommandParser.Parse("STRIP Ground 1 2");
        var cmd = Assert.IsType<StripMoveCommand>(result.Value);
        Assert.Equal(["Ground", "1", "2"], cmd.Tokens);
    }

    [Fact]
    public void Strip_PreservesMultiTokenBayForHandlerGreedyMatch()
    {
        // Parser doesn't know about accessible bays; it just tokenizes.
        // The server-side handler peels the longest bay-name prefix, so
        // "STRIP Ground 1 1 2" could resolve to bay='Ground 1' rack=1 index=2.
        var result = CommandParser.Parse("STRIP Ground 1 1 2");
        var cmd = Assert.IsType<StripMoveCommand>(result.Value);
        Assert.Equal(["Ground", "1", "1", "2"], cmd.Tokens);
    }

    [Fact]
    public void StripD_NoArg_Succeeds()
    {
        var result = CommandParser.Parse("STRIPD");
        Assert.IsType<StripDeleteCommand>(result.Value);
    }

    [Fact]
    public void StripO_NoArg_Succeeds()
    {
        var result = CommandParser.Parse("STRIPO");
        Assert.IsType<StripOffsetCommand>(result.Value);
    }

    [Fact]
    public void An_ParsesBoxAndText()
    {
        var result = CommandParser.Parse("AN 3 RV");
        var cmd = Assert.IsType<StripAnnotateCommand>(result.Value);
        Assert.Equal(3, cmd.Box);
        Assert.Equal("RV", cmd.Text);
    }

    [Fact]
    public void Box_ParsesBoxAndText()
    {
        var result = CommandParser.Parse("BOX 5 ATIS");
        var cmd = Assert.IsType<StripAnnotateCommand>(result.Value);
        Assert.Equal(5, cmd.Box);
        Assert.Equal("ATIS", cmd.Text);
    }

    [Fact]
    public void Annotate_ParsesBoxAndText()
    {
        var result = CommandParser.Parse("ANNOTATE 1 CLR");
        var cmd = Assert.IsType<StripAnnotateCommand>(result.Value);
        Assert.Equal(1, cmd.Box);
        Assert.Equal("CLR", cmd.Text);
    }

    [Fact]
    public void An_BoxOnly_ClearsBox()
    {
        var result = CommandParser.Parse("AN 3");
        var cmd = Assert.IsType<StripAnnotateCommand>(result.Value);
        Assert.Equal(3, cmd.Box);
        Assert.Null(cmd.Text);
    }

    [Fact]
    public void An_BoxZero_ReturnsNull()
    {
        var result = CommandParser.Parse("AN 0 X");
        Assert.Null(result.Value);
    }

    [Fact]
    public void An_Box10_IsValidAlias()
    {
        var result = CommandParser.Parse("AN 10 X");
        var cmd = Assert.IsType<StripAnnotateCommand>(result.Value);
        Assert.Equal(1, cmd.Box);
        Assert.Equal("X", cmd.Text);
    }

    [Fact]
    public void An_NoArg_ReturnsNull()
    {
        var result = CommandParser.Parse("AN");
        Assert.Null(result.Value);
    }

    [Fact]
    public void An_Box9_Succeeds()
    {
        var result = CommandParser.Parse("AN 9 GATE");
        var cmd = Assert.IsType<StripAnnotateCommand>(result.Value);
        Assert.Equal(9, cmd.Box);
        Assert.Equal("GATE", cmd.Text);
    }

    [Fact]
    public void An_TextWithSpaces_PreservesFullText()
    {
        var result = CommandParser.Parse("AN 2 TWR HOLD");
        var cmd = Assert.IsType<StripAnnotateCommand>(result.Value);
        Assert.Equal(2, cmd.Box);
        Assert.Equal("TWR HOLD", cmd.Text);
    }

    [Fact]
    public void An_Box10_MapsToBox1()
    {
        var result = CommandParser.Parse("AN 10 CLR");
        var cmd = Assert.IsType<StripAnnotateCommand>(result.Value);
        Assert.Equal(1, cmd.Box);
        Assert.Equal("CLR", cmd.Text);
    }

    [Fact]
    public void An_Box18_MapsToBox9()
    {
        var result = CommandParser.Parse("AN 18 GATE");
        var cmd = Assert.IsType<StripAnnotateCommand>(result.Value);
        Assert.Equal(9, cmd.Box);
        Assert.Equal("GATE", cmd.Text);
    }

    [Fact]
    public void An_Box19_ReturnsNull()
    {
        var result = CommandParser.Parse("AN 19 X");
        Assert.Null(result.Value);
    }
}
