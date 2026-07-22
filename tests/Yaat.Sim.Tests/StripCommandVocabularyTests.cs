using Xunit;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Tests;

/// <summary>
/// Coverage for the Commit A vStrips-clone vocabulary additions:
/// STRIPD, STRIPO, HSM, HSO, HSS, SEP, SEPD, BLANK, BLANKD. The core STRIP
/// extension (rack/index) is covered in <see cref="StripCommandParserTests"/>
/// alongside the existing happy-path tests.
/// </summary>
public class StripCommandVocabularyTests
{
    // ── HSM (half-strip move) ─────────────────────────────────────
    // The parser is intentionally dumb for HSM: it just emits raw tokens.
    // Bay names can be multi-word ("Local 1"), so the destination spec spans
    // multiple whitespace tokens and disambiguating "src-bay vs lookup-key vs
    // dest-bay-name-prefix" requires the bay registry — only the handler has it.

    [Fact]
    public void Hsm_DestBayOnly_AircraftScoped()
    {
        var result = CommandParser.Parse("HSM Local");
        var cmd = Assert.IsType<HalfStripMoveCommand>(result.Value);
        Assert.Equal(["Local"], cmd.Tokens);
    }

    [Fact]
    public void Hsm_DestBayWithRack()
    {
        var result = CommandParser.Parse("HSM Local/2");
        var cmd = Assert.IsType<HalfStripMoveCommand>(result.Value);
        Assert.Equal(["Local/2"], cmd.Tokens);
    }

    [Fact]
    public void Hsm_DestBayWithRackAndIndex()
    {
        var result = CommandParser.Parse("HSM Local/2/3");
        var cmd = Assert.IsType<HalfStripMoveCommand>(result.Value);
        Assert.Equal(["Local/2/3"], cmd.Tokens);
    }

    [Fact]
    public void Hsm_GlobalKey_Dest()
    {
        var result = CommandParser.Parse("HSM KEY1 Local");
        var cmd = Assert.IsType<HalfStripMoveCommand>(result.Value);
        Assert.Equal(["KEY1", "Local"], cmd.Tokens);
    }

    [Fact]
    public void Hsm_ExplicitSourceBay_Key_Dest()
    {
        var result = CommandParser.Parse("HSM Ground/2 KEY1 Local/3/1");
        var cmd = Assert.IsType<HalfStripMoveCommand>(result.Value);
        Assert.Equal(["Ground/2", "KEY1", "Local/3/1"], cmd.Tokens);
    }

    [Fact]
    public void Hsm_MultiWordDestBay_PreservesAllTokens()
    {
        // Drag-and-drop emits this exact wire when the dest is "Local 1".
        // The handler resolves the multi-word bay against the registry.
        var result = CommandParser.Parse("HSM N569SX Local 1/1/2");
        var cmd = Assert.IsType<HalfStripMoveCommand>(result.Value);
        Assert.Equal(["N569SX", "Local", "1/1/2"], cmd.Tokens);
    }

    [Fact]
    public void Hsm_HstripIdForm_MultiWordDestBay()
    {
        // Empty half-strips have no first-line text, so the UI emits the
        // strip's id as the lookup key.
        var result = CommandParser.Parse("HSM HSTRIP_abc123 Local 1/1/2");
        var cmd = Assert.IsType<HalfStripMoveCommand>(result.Value);
        Assert.Equal(["HSTRIP_abc123", "Local", "1/1/2"], cmd.Tokens);
    }

    [Fact]
    public void Hsm_NoArg_Fails()
    {
        var result = CommandParser.Parse("HSM");
        Assert.Null(result.Value);
    }

    // ── HSO (half-strip offset toggle) ────────────────────────────

    [Fact]
    public void Hso_NoArg_AircraftScoped()
    {
        var result = CommandParser.Parse("HSO");
        var cmd = Assert.IsType<HalfStripOffsetCommand>(result.Value);
        Assert.Null(cmd.BayName);
        Assert.Null(cmd.Rack);
        Assert.Null(cmd.LookupKey);
    }

    [Fact]
    public void Hso_Key_GlobalLookup()
    {
        var result = CommandParser.Parse("HSO KEY1");
        var cmd = Assert.IsType<HalfStripOffsetCommand>(result.Value);
        Assert.Null(cmd.BayName);
        Assert.Equal("KEY1", cmd.LookupKey);
    }

    [Fact]
    public void Hso_BayAndKey()
    {
        // Wire rack 2 → 0-based internal rack 1.
        var result = CommandParser.Parse("HSO OAK/Ground/2 KEY1");
        var cmd = Assert.IsType<HalfStripOffsetCommand>(result.Value);
        Assert.Equal("OAK", cmd.FacilityId);
        Assert.Equal("GROUND", cmd.BayName);
        Assert.Equal(1, cmd.Rack);
        Assert.Equal("KEY1", cmd.LookupKey);
    }

    [Fact]
    public void Hso_TooManyTokens_Fails()
    {
        var result = CommandParser.Parse("HSO Ground KEY1 EXTRA");
        Assert.Null(result.Value);
        Assert.Contains("at most", result.Reason);
    }

    // ── HSS (half-strip slide, toggles left/right) ────────────────

    [Fact]
    public void Hss_NoArg_AircraftScoped()
    {
        var result = CommandParser.Parse("HSS");
        var cmd = Assert.IsType<HalfStripSlideCommand>(result.Value);
        Assert.Null(cmd.BayName);
        Assert.Null(cmd.LookupKey);
    }

    [Fact]
    public void Hss_Key_GlobalLookup()
    {
        var result = CommandParser.Parse("HSS KEY1");
        var cmd = Assert.IsType<HalfStripSlideCommand>(result.Value);
        Assert.Equal("KEY1", cmd.LookupKey);
    }

    [Fact]
    public void Hss_BayAndKey()
    {
        var result = CommandParser.Parse("HSS OAK/Ground KEY1");
        var cmd = Assert.IsType<HalfStripSlideCommand>(result.Value);
        Assert.Equal("OAK", cmd.FacilityId);
        Assert.Equal("GROUND", cmd.BayName);
        Assert.Equal("KEY1", cmd.LookupKey);
    }

    // ── SEP (separator create) ────────────────────────────────────

    [Fact]
    public void Sep_Handwritten_BayOnly()
    {
        var result = CommandParser.Parse("SEP H Ground");
        var cmd = Assert.IsType<SeparatorCreateCommand>(result.Value);
        Assert.Equal(SeparatorStyle.Handwritten, cmd.Style);
        Assert.Equal(["Ground"], cmd.Tokens);
    }

    [Fact]
    public void Sep_White_BayRackIndexLabel()
    {
        var result = CommandParser.Parse("SEP W Ground 1 2 ARRIVALS");
        var cmd = Assert.IsType<SeparatorCreateCommand>(result.Value);
        Assert.Equal(SeparatorStyle.White, cmd.Style);
        Assert.Equal(["Ground", "1", "2", "ARRIVALS"], cmd.Tokens);
    }

    [Fact]
    public void Sep_Red_ViaAlias()
    {
        var result = CommandParser.Parse("SEP RED Local");
        var cmd = Assert.IsType<SeparatorCreateCommand>(result.Value);
        Assert.Equal(SeparatorStyle.Red, cmd.Style);
    }

    [Fact]
    public void Sep_Green_ViaAlias()
    {
        var result = CommandParser.Parse("SEP GREEN Local");
        var cmd = Assert.IsType<SeparatorCreateCommand>(result.Value);
        Assert.Equal(SeparatorStyle.Green, cmd.Style);
    }

    [Fact]
    public void Sep_InvalidStyle_Fails()
    {
        var result = CommandParser.Parse("SEP X Ground");
        Assert.Null(result.Value);
        Assert.Contains("invalid separator style", result.Reason);
    }

    [Fact]
    public void Sep_StyleOnly_NoBay_Fails()
    {
        var result = CommandParser.Parse("SEP H");
        Assert.Null(result.Value);
        Assert.Contains("bay name", result.Reason);
    }

    [Fact]
    public void Sep_NoArg_Fails()
    {
        var result = CommandParser.Parse("SEP");
        Assert.Null(result.Value);
    }

    // ── SEPD (separator delete) ───────────────────────────────────

    [Fact]
    public void Sepd_BayAndLabel()
    {
        var result = CommandParser.Parse("SEPD Ground ARRIVALS");
        var cmd = Assert.IsType<SeparatorDeleteCommand>(result.Value);
        Assert.Equal(["Ground", "ARRIVALS"], cmd.Tokens);
    }

    [Fact]
    public void Sepd_BayOnly_DoesNotFailAtParseLevel()
    {
        // Server handler may require a label-or-position; parser just forwards tokens.
        var result = CommandParser.Parse("SEPD Ground");
        var cmd = Assert.IsType<SeparatorDeleteCommand>(result.Value);
        Assert.Equal(["Ground"], cmd.Tokens);
    }

    [Fact]
    public void Sepd_NoArg_Fails()
    {
        var result = CommandParser.Parse("SEPD");
        Assert.Null(result.Value);
    }

    // ── BLANK (create) ────────────────────────────────────────────

    [Fact]
    public void Blank_NoArg_PrinterQueue()
    {
        // BLANK with no args creates a blank in the printer queue (matches vStrips
        // "Request Blank Strip").
        var result = CommandParser.Parse("BLANK");
        var cmd = Assert.IsType<BlankCreateCommand>(result.Value);
        Assert.Empty(cmd.Tokens);
    }

    [Fact]
    public void Blank_Bay()
    {
        var result = CommandParser.Parse("BLANK Ground");
        var cmd = Assert.IsType<BlankCreateCommand>(result.Value);
        Assert.Equal(["Ground"], cmd.Tokens);
    }

    [Fact]
    public void Blank_BayRackIndex()
    {
        var result = CommandParser.Parse("BLANK Ground 1 2");
        var cmd = Assert.IsType<BlankCreateCommand>(result.Value);
        Assert.Equal(["Ground", "1", "2"], cmd.Tokens);
    }

    // ── BLANKD (delete; blanks are fungible) ──────────────────────

    [Fact]
    public void Blankd_Bay()
    {
        var result = CommandParser.Parse("BLANKD Ground");
        var cmd = Assert.IsType<BlankDeleteCommand>(result.Value);
        Assert.Equal(["Ground"], cmd.Tokens);
    }

    [Fact]
    public void Blankd_BayAndRack()
    {
        var result = CommandParser.Parse("BLANKD Ground 1");
        var cmd = Assert.IsType<BlankDeleteCommand>(result.Value);
        Assert.Equal(["Ground", "1"], cmd.Tokens);
    }

    [Fact]
    public void Blankd_NoArg_Fails()
    {
        var result = CommandParser.Parse("BLANKD");
        Assert.Null(result.Value);
    }

    // ── Registry presence (completeness enforcement) ──────────────

    [Fact]
    public void AllNewVerbs_RegistryContains()
    {
        // Every CanonicalCommandType must exist in the registry; this test fails
        // loudly if anything was missed. The project-wide completeness test enforces
        // this for the whole enum but we verify the new verbs specifically here.
        var types = new[]
        {
            CanonicalCommandType.StripMove,
            CanonicalCommandType.StripDelete,
            CanonicalCommandType.StripOffset,
            CanonicalCommandType.HalfStripMove,
            CanonicalCommandType.HalfStripOffset,
            CanonicalCommandType.HalfStripSlide,
            CanonicalCommandType.SeparatorCreate,
            CanonicalCommandType.SeparatorDelete,
            CanonicalCommandType.BlankCreate,
            CanonicalCommandType.BlankDelete,
        };

        foreach (var type in types)
        {
            Assert.True(CommandRegistry.All.ContainsKey(type), $"Missing registry entry for {type}");
        }
    }

    // ── Canonical round-trip (describer) ──────────────────────────

    [Fact]
    public void Canonical_StripMove_BayOnly()
    {
        var cmd = new StripMoveCommand(["Ground"]);
        Assert.Equal("STRIP Ground", CommandDescriber.DescribeCommand(cmd));
    }

    [Fact]
    public void Canonical_StripMove_BayRackIndex()
    {
        var cmd = new StripMoveCommand(["Ground", "1", "2"]);
        Assert.Equal("STRIP Ground 1 2", CommandDescriber.DescribeCommand(cmd));
    }

    [Fact]
    public void Canonical_StripDelete()
    {
        Assert.Equal("STRIPD", CommandDescriber.DescribeCommand(new StripDeleteCommand()));
    }

    [Fact]
    public void Canonical_StripOffset()
    {
        Assert.Equal("STRIPO", CommandDescriber.DescribeCommand(new StripOffsetCommand()));
    }

    [Fact]
    public void Canonical_Hsm_AllTokens()
    {
        var cmd = new HalfStripMoveCommand(["GROUND/1", "KEY1", "LOCAL/2/1"]);
        Assert.Equal("HSM GROUND/1 KEY1 LOCAL/2/1", CommandDescriber.DescribeCommand(cmd));
    }

    [Fact]
    public void Canonical_Hsm_DestOnly()
    {
        var cmd = new HalfStripMoveCommand(["LOCAL"]);
        Assert.Equal("HSM LOCAL", CommandDescriber.DescribeCommand(cmd));
    }

    [Fact]
    public void Canonical_Hsm_MultiWordDestBay_PreservesSpaces()
    {
        // The wire form for a multi-word destination is exactly what the UI
        // emits via VStripsCanonicalBuilder.BuildHalfStripMove("KEY","OAK","Local 1",0,1).
        var cmd = new HalfStripMoveCommand(["KEY1", "OAK/Local", "1/1/2"]);
        Assert.Equal("HSM KEY1 OAK/Local 1/1/2", CommandDescriber.DescribeCommand(cmd));
    }

    [Fact]
    public void Canonical_Hso_AircraftScoped()
    {
        Assert.Equal("HSO", CommandDescriber.DescribeCommand(new HalfStripOffsetCommand(null, null, null, null)));
    }

    [Fact]
    public void Canonical_Hss_KeyOnly()
    {
        Assert.Equal("HSS KEY1", CommandDescriber.DescribeCommand(new HalfStripSlideCommand(null, null, null, "KEY1")));
    }

    [Fact]
    public void Canonical_Sep_Handwritten_Label()
    {
        var cmd = new SeparatorCreateCommand(SeparatorStyle.Handwritten, ["Ground", "1", "2", "NOTE"]);
        Assert.Equal("SEP H Ground 1 2 NOTE", CommandDescriber.DescribeCommand(cmd));
    }

    [Fact]
    public void Canonical_Sep_White_BayOnly()
    {
        var cmd = new SeparatorCreateCommand(SeparatorStyle.White, ["Ground"]);
        Assert.Equal("SEP W Ground", CommandDescriber.DescribeCommand(cmd));
    }

    [Fact]
    public void Canonical_Sepd_BayLabel()
    {
        var cmd = new SeparatorDeleteCommand(["Ground", "ARRIVALS"]);
        Assert.Equal("SEPD Ground ARRIVALS", CommandDescriber.DescribeCommand(cmd));
    }

    [Fact]
    public void Canonical_Blank_PrinterQueue()
    {
        Assert.Equal("BLANK", CommandDescriber.DescribeCommand(new BlankCreateCommand([])));
    }

    [Fact]
    public void Canonical_Blank_Bay()
    {
        Assert.Equal("BLANK Ground", CommandDescriber.DescribeCommand(new BlankCreateCommand(["Ground"])));
    }

    [Fact]
    public void Canonical_Blankd_Bay()
    {
        Assert.Equal("BLANKD Ground", CommandDescriber.DescribeCommand(new BlankDeleteCommand(["Ground"])));
    }
}
