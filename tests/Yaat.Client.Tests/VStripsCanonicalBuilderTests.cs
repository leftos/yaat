using Xunit;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Tests;

/// <summary>
/// Pure-function coverage for <see cref="VStripsCanonicalBuilder"/> — every UI
/// action in the vStrips view funnels through one of these builders, so they
/// must match the canonical syntax the server's <c>StripCommandHandler</c>
/// accepts. Keeps exact string comparisons so any drift breaks loudly.
/// </summary>
public class VStripsCanonicalBuilderTests
{
    [Fact]
    public void BuildStripMove_FormatsPositionArgs_OneBasedWire()
    {
        // 0-based rack/index on the inside, emitted as 1-based on the wire.
        Assert.Equal("STRIP Ground 1 1", VStripsCanonicalBuilder.BuildStripMove("Ground", 0, 0));
        Assert.Equal("STRIP Local 2 4", VStripsCanonicalBuilder.BuildStripMove("Local", 1, 3));
    }

    [Fact]
    public void BuildStripMove_NullIndex_OmitsIndexToken()
    {
        // Null index = "append to tail of rack" per CRC bottom-up FIFO; the
        // wire form drops the trailing index token entirely so the server's
        // ResolveStripTokens parses it as absent.
        Assert.Equal("STRIP Ground 1", VStripsCanonicalBuilder.BuildStripMove("Ground", 0, null));
        Assert.Equal("STRIP Local 2", VStripsCanonicalBuilder.BuildStripMove("Local", 1, null));
    }

    [Fact]
    public void BuildStripDeleteOffset_AreStaticVerbs()
    {
        Assert.Equal("STRIPD", VStripsCanonicalBuilder.BuildStripDelete());
        Assert.Equal("STRIPO", VStripsCanonicalBuilder.BuildStripOffset());
    }

    [Fact]
    public void BuildAnnotate_WithText_EmitsAnWithText()
    {
        Assert.Equal("AN 3 RV", VStripsCanonicalBuilder.BuildAnnotate(3, "RV"));
    }

    [Fact]
    public void BuildAnnotate_EmptyOrNullText_EmitsBareAn()
    {
        Assert.Equal("AN 5", VStripsCanonicalBuilder.BuildAnnotate(5, null));
        Assert.Equal("AN 5", VStripsCanonicalBuilder.BuildAnnotate(5, ""));
        Assert.Equal("AN 5", VStripsCanonicalBuilder.BuildAnnotate(5, "  "));
    }

    [Fact]
    public void BuildAnnotate_TrimsText()
    {
        Assert.Equal("AN 1 GATE", VStripsCanonicalBuilder.BuildAnnotate(1, "  GATE  "));
    }

    [Fact]
    public void BuildHalfStripCreate_WithLines_UsesBackslashSeparator()
    {
        // 0-based rack 1 → wire rack 2.
        Assert.Equal("HSC Ground/2 NORDO\\KOAK\\28L", VStripsCanonicalBuilder.BuildHalfStripCreate("Ground", 1, ["NORDO", "KOAK", "28L"]));
    }

    [Fact]
    public void BuildHalfStripCreate_NoLines_OmitsPayload()
    {
        // 0-based rack 0 → wire rack 1.
        Assert.Equal("HSC Local/1", VStripsCanonicalBuilder.BuildHalfStripCreate("Local", 0, []));
    }

    [Fact]
    public void BuildHalfStripAmend_ExpandsKeyThenLines()
    {
        Assert.Equal("HSA NORDO NEW LINE2", VStripsCanonicalBuilder.BuildHalfStripAmend("NORDO", ["NEW", "LINE2"]));
    }

    [Fact]
    public void BuildHalfStripMove_UsesSlashSeparatedDest_OneBasedWire()
    {
        // 0-based rack 1 / index 2 → wire rack 2 / index 3.
        Assert.Equal("HSM NORDO Local/2/3", VStripsCanonicalBuilder.BuildHalfStripMove("NORDO", "Local", 1, 2));
    }

    [Fact]
    public void BuildHalfStripDeleteOffsetSlide_AreKeyed()
    {
        Assert.Equal("HSD NORDO", VStripsCanonicalBuilder.BuildHalfStripDelete("NORDO"));
        Assert.Equal("HSO NORDO", VStripsCanonicalBuilder.BuildHalfStripOffset("NORDO"));
        Assert.Equal("HSS NORDO", VStripsCanonicalBuilder.BuildHalfStripSlide("NORDO"));
    }

    [Theory]
    [InlineData(SeparatorStyle.Handwritten, 'H')]
    [InlineData(SeparatorStyle.White, 'W')]
    [InlineData(SeparatorStyle.Red, 'R')]
    [InlineData(SeparatorStyle.Green, 'G')]
    public void BuildSeparatorCreate_MapsStyleToChar_OneBasedWire(SeparatorStyle style, char styleChar)
    {
        // 0-based rack 0 / index 3 → wire rack 1 / index 4.
        Assert.Equal($"SEP {styleChar} Ground 1 4 HOLD", VStripsCanonicalBuilder.BuildSeparatorCreate(style, "Ground", 0, 3, "HOLD"));
    }

    [Fact]
    public void BuildSeparatorCreate_NoLabel_OmitsTail()
    {
        // 0-based rack 1 / index 0 → wire rack 2 / index 1.
        Assert.Equal("SEP W Local 2 1", VStripsCanonicalBuilder.BuildSeparatorCreate(SeparatorStyle.White, "Local", 1, 0, null));
        Assert.Equal("SEP W Local 2 1", VStripsCanonicalBuilder.BuildSeparatorCreate(SeparatorStyle.White, "Local", 1, 0, "   "));
    }

    [Fact]
    public void BuildSeparatorDelete_LabelWinsOverIndex()
    {
        // When a label is supplied, the index argument is ignored; rack is still 1-based.
        Assert.Equal("SEPD Ground 3 HOLD", VStripsCanonicalBuilder.BuildSeparatorDelete("Ground", 2, "HOLD", 5));
    }

    [Fact]
    public void BuildSeparatorDelete_NoLabel_UsesIndex_OneBasedWire()
    {
        // 0-based rack 1 / index 3 → wire rack 2 / index 4.
        Assert.Equal("SEPD Local 2 4", VStripsCanonicalBuilder.BuildSeparatorDelete("Local", 1, null, 3));
        Assert.Equal("SEPD Local 2 1", VStripsCanonicalBuilder.BuildSeparatorDelete("Local", 1, "", null));
    }

    [Fact]
    public void BuildSeparatorEdit_EmitsAtomicSepeOneBasedWire()
    {
        // Task #17 — single atomic verb replaces the prior delete+create pair.
        // 0-based rack/index convert to 1-based; label is space-joined.
        Assert.Equal("SEPE Ground 2 3 LONG LINE", VStripsCanonicalBuilder.BuildSeparatorEdit("Ground", 1, 2, "LONG LINE"));
        Assert.Equal("SEPE Local 1 1 HOLD", VStripsCanonicalBuilder.BuildSeparatorEdit("Local", 0, 0, "  HOLD  "));
    }

    [Fact]
    public void BuildBlankCreate_NullBay_EmitsBareVerb()
    {
        Assert.Equal("BLANK", VStripsCanonicalBuilder.BuildBlankCreate(null, null, null));
    }

    [Fact]
    public void BuildBlankCreate_WithBay_EmitsFullPosition_OneBasedWire()
    {
        // 0-based rack 1 / index 2 → wire rack 2 / index 3.
        Assert.Equal("BLANK Ground 2 3", VStripsCanonicalBuilder.BuildBlankCreate("Ground", 1, 2));
        // Null rack/index default to 0 then convert 1-based → "1 1".
        Assert.Equal("BLANK Ground 1 1", VStripsCanonicalBuilder.BuildBlankCreate("Ground", null, null));
    }

    [Fact]
    public void BuildBlankDelete_WithRack_EmitsRack_OneBasedWire()
    {
        // 0-based rack 1 → wire rack 2.
        Assert.Equal("BLANKD Ground 2", VStripsCanonicalBuilder.BuildBlankDelete("Ground", 1));
    }

    [Fact]
    public void BuildBlankDelete_WithoutRack_EmitsBayOnly()
    {
        Assert.Equal("BLANKD Ground", VStripsCanonicalBuilder.BuildBlankDelete("Ground", null));
    }
}
