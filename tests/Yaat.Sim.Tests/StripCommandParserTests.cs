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
        Assert.Equal("3", cmd.Box);
        Assert.Equal("RV", cmd.Text);
    }

    [Fact]
    public void Box_ParsesBoxAndText()
    {
        var result = CommandParser.Parse("BOX 5 ATIS");
        var cmd = Assert.IsType<StripAnnotateCommand>(result.Value);
        Assert.Equal("5", cmd.Box);
        Assert.Equal("ATIS", cmd.Text);
    }

    [Fact]
    public void Annotate_ParsesBoxAndText()
    {
        var result = CommandParser.Parse("ANNOTATE 1 CLR");
        var cmd = Assert.IsType<StripAnnotateCommand>(result.Value);
        Assert.Equal("1", cmd.Box);
        Assert.Equal("CLR", cmd.Text);
    }

    [Fact]
    public void An_BoxOnly_ClearsBox()
    {
        var result = CommandParser.Parse("AN 3");
        var cmd = Assert.IsType<StripAnnotateCommand>(result.Value);
        Assert.Equal("3", cmd.Box);
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
        Assert.Equal("1", cmd.Box);
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
        Assert.Equal("9", cmd.Box);
        Assert.Equal("GATE", cmd.Text);
    }

    [Fact]
    public void An_TextWithSpaces_PreservesFullText()
    {
        var result = CommandParser.Parse("AN 2 TWR HOLD");
        var cmd = Assert.IsType<StripAnnotateCommand>(result.Value);
        Assert.Equal("2", cmd.Box);
        Assert.Equal("TWR HOLD", cmd.Text);
    }

    [Fact]
    public void An_Box10_MapsToBox1()
    {
        var result = CommandParser.Parse("AN 10 CLR");
        var cmd = Assert.IsType<StripAnnotateCommand>(result.Value);
        Assert.Equal("1", cmd.Box);
        Assert.Equal("CLR", cmd.Text);
    }

    [Fact]
    public void An_Box18_MapsToBox9()
    {
        var result = CommandParser.Parse("AN 18 GATE");
        var cmd = Assert.IsType<StripAnnotateCommand>(result.Value);
        Assert.Equal("9", cmd.Box);
        Assert.Equal("GATE", cmd.Text);
    }

    [Fact]
    public void An_Box19_ReturnsNull()
    {
        var result = CommandParser.Parse("AN 19 X");
        Assert.Null(result.Value);
    }

    [Fact]
    public void An_Box8a_ParsesAsLiteral()
    {
        // 8a and 8b are freeform annotation placeholders below field 8 in the
        // middle column (col 3 rows 2/3). They map to FieldValues[19]/[20]
        // on the server, outside the 1-9 / 10-18 grid range.
        var result = CommandParser.Parse("AN 8a ENR");
        var cmd = Assert.IsType<StripAnnotateCommand>(result.Value);
        Assert.Equal("8a", cmd.Box);
        Assert.Equal("ENR", cmd.Text);
    }

    [Fact]
    public void An_Box8B_UpperCase_NormalizesToLower()
    {
        var result = CommandParser.Parse("AN 8B DLY");
        var cmd = Assert.IsType<StripAnnotateCommand>(result.Value);
        Assert.Equal("8b", cmd.Box);
        Assert.Equal("DLY", cmd.Text);
    }

    [Fact]
    public void An_InvalidBoxToken_ReturnsNull()
    {
        var result = CommandParser.Parse("AN 9c X");
        Assert.Null(result.Value);
    }

    // ── Id-form (STRIP_<id> prefix) coverage ──────────────────────
    //
    // Strip-tab and CRC-translator paths emit the id form so a scanned copy
    // (STRIP_{callsign}_{shortGuid}) can be addressed independently of its
    // originator. Terminal users keep the bare/callsign-keyed forms.

    [Fact]
    public void StripD_StripIdForm_ParsesId()
    {
        var result = CommandParser.Parse("STRIPD STRIP_UAL100");
        var cmd = Assert.IsType<StripDeleteCommand>(result.Value);
        Assert.Equal("STRIP_UAL100", cmd.StripId);
    }

    [Fact]
    public void StripD_NoArg_LeavesStripIdNull()
    {
        var result = CommandParser.Parse("STRIPD");
        var cmd = Assert.IsType<StripDeleteCommand>(result.Value);
        Assert.Null(cmd.StripId);
    }

    [Fact]
    public void StripD_StripIdForm_HandlesScannedCopySuffix()
    {
        var result = CommandParser.Parse("STRIPD STRIP_UAL100_a1b2c3d4");
        var cmd = Assert.IsType<StripDeleteCommand>(result.Value);
        Assert.Equal("STRIP_UAL100_a1b2c3d4", cmd.StripId);
    }

    [Fact]
    public void StripD_ArrivalIdForm_ParsesId()
    {
        // Arrival strips are keyed ARRIVAL_{callsign}; the arrival printer's
        // Delete button addresses them by id (issue #278).
        var result = CommandParser.Parse("STRIPD ARRIVAL_UAL100");
        var cmd = Assert.IsType<StripDeleteCommand>(result.Value);
        Assert.Equal("ARRIVAL_UAL100", cmd.StripId);
    }

    [Fact]
    public void StripO_ArrivalIdForm_ParsesId()
    {
        var result = CommandParser.Parse("STRIPO ARRIVAL_UAL100");
        var cmd = Assert.IsType<StripOffsetCommand>(result.Value);
        Assert.Equal("ARRIVAL_UAL100", cmd.StripId);
    }

    [Fact]
    public void StripD_StripIdForm_RejectsExtraTokens()
    {
        // Extra tokens are user error; the handler can't disambiguate so
        // reject at parse time. STRIPD is at most "STRIPD STRIP_<id>".
        var result = CommandParser.Parse("STRIPD STRIP_UAL100 garbage");
        Assert.Null(result.Value);
    }

    [Fact]
    public void StripO_StripIdForm_ParsesId()
    {
        var result = CommandParser.Parse("STRIPO STRIP_UAL100_a1b2c3d4");
        var cmd = Assert.IsType<StripOffsetCommand>(result.Value);
        Assert.Equal("STRIP_UAL100_a1b2c3d4", cmd.StripId);
    }

    [Fact]
    public void StripO_NoArg_LeavesStripIdNull()
    {
        var result = CommandParser.Parse("STRIPO");
        var cmd = Assert.IsType<StripOffsetCommand>(result.Value);
        Assert.Null(cmd.StripId);
    }

    [Fact]
    public void An_StripIdForm_PeelsIdAndParsesBox()
    {
        var result = CommandParser.Parse("AN STRIP_UAL100 3 RV");
        var cmd = Assert.IsType<StripAnnotateCommand>(result.Value);
        Assert.Equal("STRIP_UAL100", cmd.StripId);
        Assert.Equal("3", cmd.Box);
        Assert.Equal("RV", cmd.Text);
    }

    [Fact]
    public void An_StripIdForm_BoxOnly_ClearsBox()
    {
        var result = CommandParser.Parse("AN STRIP_UAL100 5");
        var cmd = Assert.IsType<StripAnnotateCommand>(result.Value);
        Assert.Equal("STRIP_UAL100", cmd.StripId);
        Assert.Equal("5", cmd.Box);
        Assert.Null(cmd.Text);
    }

    [Fact]
    public void An_StripIdForm_8a_PreservesSuffixCanonical()
    {
        var result = CommandParser.Parse("AN STRIP_UAL100 8a ENR");
        var cmd = Assert.IsType<StripAnnotateCommand>(result.Value);
        Assert.Equal("STRIP_UAL100", cmd.StripId);
        Assert.Equal("8a", cmd.Box);
        Assert.Equal("ENR", cmd.Text);
    }

    [Fact]
    public void An_StripIdOnly_ReturnsNull()
    {
        var result = CommandParser.Parse("AN STRIP_UAL100");
        Assert.Null(result.Value);
    }

    [Fact]
    public void Strip_StripIdForm_KeepsTokensIntact()
    {
        // Parser passes raw tokens to the handler; STRIP_<id> peel happens
        // server-side so the parser stays bay-agnostic.
        var result = CommandParser.Parse("STRIP STRIP_UAL100_a1b2c3d4 OAK/Local 1");
        var cmd = Assert.IsType<StripMoveCommand>(result.Value);
        Assert.Equal(["STRIP_UAL100_a1b2c3d4", "OAK/Local", "1"], cmd.Tokens);
    }

    // ── Facility-qualified dest-spec (TryParseStripDest) ──────────

    [Theory]
    [InlineData("OAK/GROUND", "OAK", "GROUND", null, null)]
    [InlineData("OAK/GROUND/2", "OAK", "GROUND", 1, null)]
    [InlineData("OAK/GROUND/2/3", "OAK", "GROUND", 1, 2)]
    // Facility and bay are upper-cased; a bay named like a facility still parses.
    [InlineData("nct/nct/1/1", "NCT", "NCT", 0, 0)]
    public void TryParseStripDest_SplitsFacilityBayRackIndex(string spec, string facility, string bay, int? rack, int? index)
    {
        Assert.True(CommandParser.TryParseStripDest(spec, out var parsedFacility, out var parsedBay, out var parsedRack, out var parsedIndex, out _));
        Assert.Equal(facility, parsedFacility);
        Assert.Equal(bay, parsedBay);
        Assert.Equal(rack, parsedRack);
        Assert.Equal(index, parsedIndex);
    }

    [Fact]
    public void TryParseStripDest_UnqualifiedBay_IsRejectedWithGuidance()
    {
        // Bay names are only unique within a facility, so the segment is required
        // rather than inferred — the error names the shape the user should type.
        Assert.False(CommandParser.TryParseStripDest("GROUND", out _, out _, out _, out _, out var error));
        Assert.Contains("FACILITY/BAY", error);
    }

    [Theory]
    [InlineData("OAK/GROUND/2/3/4")]
    [InlineData("OAK//2")]
    [InlineData("/GROUND/2")]
    [InlineData("OAK/GROUND/0")]
    [InlineData("OAK/GROUND/1/0")]
    public void TryParseStripDest_RejectsMalformedSpecs(string spec)
    {
        Assert.False(CommandParser.TryParseStripDest(spec, out _, out _, out _, out _, out _));
    }
}
