using Xunit;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Tests;

public class HalfStripCommandParserTests
{
    // ── HSC (create) ─────────────────────────────────────────────

    [Fact]
    public void Hsc_SingleLine_NoRack()
    {
        var result = CommandParser.Parse("HSC GROUND line1");
        var cmd = Assert.IsType<HalfStripCreateCommand>(result.Value);
        Assert.Equal("GROUND", cmd.BayName);
        Assert.Null(cmd.Rack);
        Assert.Equal(["line1"], cmd.Lines);
    }

    [Fact]
    public void Hsc_MultipleLines_WithRack()
    {
        var result = CommandParser.Parse(@"HSC GROUND/1 a\b\c");
        var cmd = Assert.IsType<HalfStripCreateCommand>(result.Value);
        Assert.Equal("GROUND", cmd.BayName);
        Assert.Equal(1, cmd.Rack);
        Assert.Equal(["a", "b", "c"], cmd.Lines);
    }

    [Fact]
    public void Hsc_LinesContainingSpaces_PreservesSpaces()
    {
        var result = CommandParser.Parse(@"HSC LCL VFR pattern\Touch and go");
        var cmd = Assert.IsType<HalfStripCreateCommand>(result.Value);
        Assert.Equal(["VFR pattern", "Touch and go"], cmd.Lines);
    }

    [Fact]
    public void Hsc_TooManyLines_Fails()
    {
        var result = CommandParser.Parse(@"HSC GROUND a\b\c\d\e\f\g");
        Assert.Null(result.Value);
        Assert.Contains("at most 6 lines", result.Reason);
    }

    [Fact]
    public void Hsc_SixLines_AtCap()
    {
        var result = CommandParser.Parse(@"HSC GROUND a\b\c\d\e\f");
        var cmd = Assert.IsType<HalfStripCreateCommand>(result.Value);
        Assert.Equal(6, cmd.Lines.Count);
    }

    [Fact]
    public void Hsc_BayOnly_EmptyLines()
    {
        // Aircraft-scoped form: server fills in callsign as line 1.
        var result = CommandParser.Parse("HSC GROUND");
        var cmd = Assert.IsType<HalfStripCreateCommand>(result.Value);
        Assert.Equal("GROUND", cmd.BayName);
        Assert.Empty(cmd.Lines);
    }

    [Fact]
    public void Hsc_NoArg_Fails()
    {
        var result = CommandParser.Parse("HSC");
        Assert.Null(result.Value);
    }

    [Fact]
    public void Hsc_NegativeRack_Fails()
    {
        var result = CommandParser.Parse("HSC GROUND/-1 line1");
        Assert.Null(result.Value);
        Assert.Contains("invalid rack", result.Reason);
    }

    [Fact]
    public void Hsc_HalfstripcreateAlias_Works()
    {
        var result = CommandParser.Parse("HALFSTRIPCREATE GROUND line1");
        Assert.IsType<HalfStripCreateCommand>(result.Value);
    }

    // ── HSA (amend) — auto-search form ─────────────────────────

    [Fact]
    public void Hsa_NoBay_KeyAndPayload()
    {
        var result = CommandParser.Parse(@"HSA key\new1\new2");
        var cmd = Assert.IsType<HalfStripAmendCommand>(result.Value);
        Assert.Null(cmd.BayName);
        Assert.Null(cmd.Rack);
        Assert.Equal(["key", "new1", "new2"], cmd.Tokens);
    }

    [Fact]
    public void Hsa_NoArgs_EmptyTokens()
    {
        var result = CommandParser.Parse("HSA");
        var cmd = Assert.IsType<HalfStripAmendCommand>(result.Value);
        Assert.Null(cmd.BayName);
        Assert.Empty(cmd.Tokens);
    }

    [Fact]
    public void Hsa_SingleToken_TreatedAsKey_NotBay()
    {
        // Per disambiguation rule (single whitespace-token, no follow-up → not a bay)
        var result = CommandParser.Parse("HSA key");
        var cmd = Assert.IsType<HalfStripAmendCommand>(result.Value);
        Assert.Null(cmd.BayName);
        Assert.Equal(["key"], cmd.Tokens);
    }

    // ── HSA — explicit bay form ───────────────────────────────

    [Fact]
    public void Hsa_ExplicitBay_KeyAndPayload()
    {
        var result = CommandParser.Parse(@"HSA GROUND key\new1\new2");
        var cmd = Assert.IsType<HalfStripAmendCommand>(result.Value);
        Assert.Equal("GROUND", cmd.BayName);
        Assert.Null(cmd.Rack);
        Assert.Equal(["key", "new1", "new2"], cmd.Tokens);
    }

    [Fact]
    public void Hsa_ExplicitBayWithRack()
    {
        var result = CommandParser.Parse(@"HSA GROUND/2 key\new1");
        var cmd = Assert.IsType<HalfStripAmendCommand>(result.Value);
        Assert.Equal("GROUND", cmd.BayName);
        Assert.Equal(2, cmd.Rack);
        Assert.Equal(["key", "new1"], cmd.Tokens);
    }

    [Fact]
    public void Hsa_ExplicitBay_TwoTokens_NoBackslash()
    {
        // GROUND has no backslash and there's a follow-up token → GROUND is the bay
        var result = CommandParser.Parse("HSA GROUND key");
        var cmd = Assert.IsType<HalfStripAmendCommand>(result.Value);
        Assert.Equal("GROUND", cmd.BayName);
        Assert.Equal(["key"], cmd.Tokens);
    }

    [Fact]
    public void Hsa_TooManyTokens_Fails()
    {
        // 8 tokens (1 key + 7 lines) exceeds the 1+6 cap
        var result = CommandParser.Parse(@"HSA k\a\b\c\d\e\f\g");
        Assert.Null(result.Value);
        Assert.Contains("at most", result.Reason);
    }

    [Fact]
    public void Hsa_HalfstripamendAlias_Works()
    {
        var result = CommandParser.Parse(@"HALFSTRIPAMEND key\new");
        Assert.IsType<HalfStripAmendCommand>(result.Value);
    }

    // ── HSD (delete) — auto-search form ────────────────────────

    [Fact]
    public void Hsd_SingleKey_NoBay()
    {
        var result = CommandParser.Parse("HSD key");
        var cmd = Assert.IsType<HalfStripDeleteCommand>(result.Value);
        Assert.Null(cmd.BayName);
        Assert.Equal(["key"], cmd.Tokens);
    }

    [Fact]
    public void Hsd_NoArgs_EmptyTokens()
    {
        var result = CommandParser.Parse("HSD");
        var cmd = Assert.IsType<HalfStripDeleteCommand>(result.Value);
        Assert.Null(cmd.BayName);
        Assert.Empty(cmd.Tokens);
    }

    // ── HSD — explicit bay form ────────────────────────────────

    [Fact]
    public void Hsd_ExplicitBay_WithKey()
    {
        var result = CommandParser.Parse("HSD GROUND key");
        var cmd = Assert.IsType<HalfStripDeleteCommand>(result.Value);
        Assert.Equal("GROUND", cmd.BayName);
        Assert.Equal(["key"], cmd.Tokens);
    }

    [Fact]
    public void Hsd_ExplicitBayWithRack_WithKey()
    {
        var result = CommandParser.Parse("HSD GROUND/1 key");
        var cmd = Assert.IsType<HalfStripDeleteCommand>(result.Value);
        Assert.Equal("GROUND", cmd.BayName);
        Assert.Equal(1, cmd.Rack);
        Assert.Equal(["key"], cmd.Tokens);
    }

    [Fact]
    public void Hsd_TooManyTokens_Fails()
    {
        var result = CommandParser.Parse(@"HSD k1\k2");
        Assert.Null(result.Value);
        Assert.Contains("at most one", result.Reason);
    }

    [Fact]
    public void Hsd_HalfstripdelAlias_Works()
    {
        var result = CommandParser.Parse("HALFSTRIPDEL key");
        Assert.IsType<HalfStripDeleteCommand>(result.Value);
    }
}
