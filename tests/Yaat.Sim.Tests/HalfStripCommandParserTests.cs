using Xunit;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Tests;

public class HalfStripCommandParserTests
{
    // ── HSC (create) ─────────────────────────────────────────────

    [Fact]
    public void Hsc_SingleLine_NoRack()
    {
        var result = CommandParser.Parse("HSC OAK/GROUND line1");
        var cmd = Assert.IsType<HalfStripCreateCommand>(result.Value);
        Assert.Equal("OAK", cmd.FacilityId);
        Assert.Equal("GROUND", cmd.BayName);
        Assert.Null(cmd.Rack);
        Assert.Equal(["line1"], cmd.Lines);
    }

    [Fact]
    public void Hsc_MultipleLines_WithRack()
    {
        // Wire rack is 1-based; parser converts to 0-based internal index.
        var result = CommandParser.Parse(@"HSC OAK/GROUND/2 a\b\c");
        var cmd = Assert.IsType<HalfStripCreateCommand>(result.Value);
        Assert.Equal("OAK", cmd.FacilityId);
        Assert.Equal("GROUND", cmd.BayName);
        Assert.Equal(1, cmd.Rack);
        Assert.Equal(["a", "b", "c"], cmd.Lines);
    }

    [Fact]
    public void Hsc_LinesContainingSpaces_PreservesSpaces()
    {
        var result = CommandParser.Parse(@"HSC OAK/LCL VFR pattern\Touch and go");
        var cmd = Assert.IsType<HalfStripCreateCommand>(result.Value);
        Assert.Equal(["VFR pattern", "Touch and go"], cmd.Lines);
    }

    [Fact]
    public void Hsc_TooManyLines_Fails()
    {
        var result = CommandParser.Parse(@"HSC OAK/GROUND a\b\c\d\e\f\g");
        Assert.Null(result.Value);
        Assert.Contains("at most 6 lines", result.Reason);
    }

    [Fact]
    public void Hsc_SixLines_AtCap()
    {
        var result = CommandParser.Parse(@"HSC OAK/GROUND a\b\c\d\e\f");
        var cmd = Assert.IsType<HalfStripCreateCommand>(result.Value);
        Assert.Equal(6, cmd.Lines.Count);
    }

    [Fact]
    public void Hsc_BayOnly_EmptyLines()
    {
        // Aircraft-scoped form: server fills in callsign as line 1.
        var result = CommandParser.Parse("HSC OAK/GROUND");
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
        var result = CommandParser.Parse("HSC OAK/GROUND/-1 line1");
        Assert.Null(result.Value);
        Assert.Contains("invalid rack", result.Reason);
    }

    [Fact]
    public void Hsc_HalfstripcreateAlias_Works()
    {
        var result = CommandParser.Parse("HALFSTRIPCREATE OAK/GROUND line1");
        Assert.IsType<HalfStripCreateCommand>(result.Value);
    }

    [Fact]
    public void Hsc_MultiWordBayName_WithRack_ParsesAsSingleBay()
    {
        // "Ground 1/2" → bay "GROUND 1", rack 1 (0-based). The trailing rack
        // suffix on the second token marks the bay-spec boundary.
        var result = CommandParser.Parse("HSC OAK/Ground 1/2");
        var cmd = Assert.IsType<HalfStripCreateCommand>(result.Value);
        Assert.Equal("GROUND 1", cmd.BayName);
        Assert.Equal(1, cmd.Rack);
        Assert.Empty(cmd.Lines);
    }

    [Fact]
    public void Hsc_MultiWordBayName_WithRack_AndLines()
    {
        var result = CommandParser.Parse(@"HSC OAK/Ground 1/2 a\b\c");
        var cmd = Assert.IsType<HalfStripCreateCommand>(result.Value);
        Assert.Equal("GROUND 1", cmd.BayName);
        Assert.Equal(1, cmd.Rack);
        Assert.Equal(["a", "b", "c"], cmd.Lines);
    }

    [Fact]
    public void Hsc_FirstTokenBay_PreservedWhenNoRackSuffix()
    {
        // Without a `/digits` rack-suffix the historical single-token-bay rule
        // still applies — "VFR pattern" stays as line content, not bay name.
        var result = CommandParser.Parse(@"HSC OAK/LCL VFR pattern\Touch and go");
        var cmd = Assert.IsType<HalfStripCreateCommand>(result.Value);
        Assert.Equal("LCL", cmd.BayName);
        Assert.Equal(["VFR pattern", "Touch and go"], cmd.Lines);
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
        // Per disambiguation rule (no '/' in the head → not a bay spec)
        var result = CommandParser.Parse("HSA key");
        var cmd = Assert.IsType<HalfStripAmendCommand>(result.Value);
        Assert.Null(cmd.BayName);
        Assert.Equal(["key"], cmd.Tokens);
    }

    // ── HSA — explicit bay form ───────────────────────────────

    [Fact]
    public void Hsa_ExplicitBay_KeyAndPayload()
    {
        var result = CommandParser.Parse(@"HSA OAK/GROUND key\new1\new2");
        var cmd = Assert.IsType<HalfStripAmendCommand>(result.Value);
        Assert.Equal("OAK", cmd.FacilityId);
        Assert.Equal("GROUND", cmd.BayName);
        Assert.Null(cmd.Rack);
        Assert.Equal(["key", "new1", "new2"], cmd.Tokens);
    }

    [Fact]
    public void Hsa_ExplicitBayWithRack()
    {
        // Wire rack 2 → 0-based internal rack 1.
        var result = CommandParser.Parse(@"HSA OAK/GROUND/2 key\new1");
        var cmd = Assert.IsType<HalfStripAmendCommand>(result.Value);
        Assert.Equal("GROUND", cmd.BayName);
        Assert.Equal(1, cmd.Rack);
        Assert.Equal(["key", "new1"], cmd.Tokens);
    }

    [Fact]
    public void Hsa_ExplicitBay_TwoTokens_NoBackslash()
    {
        // The head carries a '/' and no '\' → it is a facility-qualified bay spec
        var result = CommandParser.Parse("HSA OAK/GROUND key");
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
        var result = CommandParser.Parse("HSD OAK/GROUND key");
        var cmd = Assert.IsType<HalfStripDeleteCommand>(result.Value);
        Assert.Equal("GROUND", cmd.BayName);
        Assert.Equal(["key"], cmd.Tokens);
    }

    [Fact]
    public void Hsd_ExplicitBayWithRack_WithKey()
    {
        // Wire rack 2 → 0-based internal rack 1.
        var result = CommandParser.Parse("HSD OAK/GROUND/2 key");
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

    // ── Strip-id form (HSTRIP_<guid>) ──────────────────────────
    // Empty half-strips have no first-line text, so the embedded vStrips UI
    // falls back to emitting the strip's id (HSTRIP_<guid>) as the lookup
    // key. Both the parser and server must recognize this form, mirroring
    // the existing SEP_/BLANK_ id-prefix handling.

    [Fact]
    public void Hsd_StripIdForm_TreatsAsLookupKey_NotBay()
    {
        // The HSTRIP_ prefix always wins over the bay-spec peel: a strip id is
        // never a bay name, even if one ever contained a '/'.
        var result = CommandParser.Parse("HSD HSTRIP_abc123 unused");
        Assert.Null(result.Value);
        Assert.Contains("at most one", result.Reason);
    }

    [Fact]
    public void Hsd_StripIdForm_SingleToken_KeepsId()
    {
        var result = CommandParser.Parse("HSD HSTRIP_abc123");
        var cmd = Assert.IsType<HalfStripDeleteCommand>(result.Value);
        Assert.Null(cmd.BayName);
        Assert.Equal(["HSTRIP_abc123"], cmd.Tokens);
    }

    [Fact]
    public void Hsa_StripIdForm_TreatsAsLookupKey_NotBay()
    {
        // HSA HSTRIP_abc123 line1\line2 must parse as key + payload, not as
        // bay HSTRIP_abc123 + body.
        var result = CommandParser.Parse(@"HSA HSTRIP_abc123 line1\line2");
        var cmd = Assert.IsType<HalfStripAmendCommand>(result.Value);
        Assert.Null(cmd.BayName);
        Assert.Equal(["HSTRIP_abc123", "line1", "line2"], cmd.Tokens);
    }

    [Fact]
    public void Hso_StripIdForm_TreatsAsLookupKey_NotBay()
    {
        // HSO with two tokens (first = bay) shouldn't peel HSTRIP_xxx as a bay.
        // Parser only accepts up to "[bay] [key]" so HSTRIP_xxx alone is fine,
        // but HSTRIP_xxx + some-other-bay-spec isn't a real call site — the
        // single-token id form is what the embedded UI emits.
        var result = CommandParser.Parse("HSO HSTRIP_abc123");
        var cmd = Assert.IsType<HalfStripOffsetCommand>(result.Value);
        Assert.Null(cmd.BayName);
        Assert.Equal("HSTRIP_abc123", cmd.LookupKey);
    }

    [Fact]
    public void Hss_StripIdForm_TreatsAsLookupKey_NotBay()
    {
        var result = CommandParser.Parse("HSS HSTRIP_abc123");
        var cmd = Assert.IsType<HalfStripSlideCommand>(result.Value);
        Assert.Null(cmd.BayName);
        Assert.Equal("HSTRIP_abc123", cmd.LookupKey);
    }
}
