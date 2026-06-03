using Xunit;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Tests;

/// <summary>
/// Parser + canonical round-trip coverage for the per-taxiway turn-direction hints (issue #172 W7):
/// a leading <c>&gt;</c> (right) / <c>&lt;</c> (left) glyph on a taxiway token records a hint aligned
/// by index with <see cref="TaxiCommand.Path"/>, and the canonical form re-emits the glyph so it
/// round-trips through the server's parser.
/// </summary>
public class GroundCommandParserTurnHintTests
{
    [Fact]
    public void ParseTaxi_GlyphPrefixes_RecordHintsAlignedToPath()
    {
        var result = GroundCommandParser.ParseTaxi(">A B <C");

        Assert.True(result.IsSuccess);
        var taxi = Assert.IsType<TaxiCommand>(result.Value);

        Assert.Equal(["A", "B", "C"], taxi.Path);
        Assert.Equal([TurnDirection.Right, null, TurnDirection.Left], taxi.PathTurnHints);
    }

    [Fact]
    public void ParseTaxi_NoGlyph_LeavesHintsNull()
    {
        var result = GroundCommandParser.ParseTaxi("A B C");

        Assert.True(result.IsSuccess);
        var taxi = Assert.IsType<TaxiCommand>(result.Value);

        Assert.Null(taxi.PathTurnHints);
    }

    [Fact]
    public void ParseTaxi_GlyphOnNumberedTaxiway_StripsGlyphKeepsName()
    {
        var result = GroundCommandParser.ParseTaxi("<B7 A");

        Assert.True(result.IsSuccess);
        var taxi = Assert.IsType<TaxiCommand>(result.Value);

        Assert.Equal(["B7", "A"], taxi.Path);
        Assert.Equal([TurnDirection.Left, null], taxi.PathTurnHints);
    }

    [Fact]
    public void ParseTaxi_HintsDoNotLeakIntoHoldShortsOrCross()
    {
        var result = GroundCommandParser.ParseTaxi(">A B HS 28R CROSS 01L");

        Assert.True(result.IsSuccess);
        var taxi = Assert.IsType<TaxiCommand>(result.Value);

        Assert.Equal(["A", "B"], taxi.Path);
        Assert.Equal([TurnDirection.Right, null], taxi.PathTurnHints);
        Assert.Equal(["28R"], taxi.HoldShorts);
        Assert.Equal(["01L"], taxi.CrossRunways);
    }

    [Fact]
    public void ParseTaxi_TrailingRunwayRemoval_KeepsHintAlignment()
    {
        // "<A B 28R" → 28R is detected as the destination runway and dropped from Path; the
        // parallel hint list must drop its last entry too so it stays index-aligned with Path.
        var result = GroundCommandParser.ParseTaxi("<A B 28R");

        Assert.True(result.IsSuccess);
        var taxi = Assert.IsType<TaxiCommand>(result.Value);

        Assert.Equal(["A", "B"], taxi.Path);
        Assert.Equal("28R", taxi.DestinationRunway);
        Assert.Equal([TurnDirection.Left, null], taxi.PathTurnHints);
    }

    [Fact]
    public void DescribeCommand_ReEmitsGlyphs_AndReParsesToSameHints()
    {
        var parsed = Assert.IsType<TaxiCommand>(GroundCommandParser.ParseTaxi(">A B <C").Value);

        string canonical = CommandDescriber.DescribeCommand(parsed);
        Assert.Equal("TAXI >A B <C", canonical);

        // Strip the verb and re-parse the argument — the glyphs survive the canonical round-trip.
        var reparsed = Assert.IsType<TaxiCommand>(GroundCommandParser.ParseTaxi(canonical["TAXI ".Length..]).Value);
        Assert.Equal(parsed.Path, reparsed.Path);
        Assert.Equal(parsed.PathTurnHints, reparsed.PathTurnHints);
    }
}
