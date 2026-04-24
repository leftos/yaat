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
    public void BuildStripMove_FormatsPositionArgs_OneBasedSlashWire()
    {
        // 0-based rack/index on the inside, emitted as 1-based slash-compound on the wire.
        Assert.Equal("STRIP Ground/1/1", VStripsCanonicalBuilder.BuildStripMove("Ground", 0, 0));
        Assert.Equal("STRIP Local/2/4", VStripsCanonicalBuilder.BuildStripMove("Local", 1, 3));
    }

    [Fact]
    public void BuildStripMove_NullIndex_OmitsIndexToken()
    {
        // Null index = "append to tail of rack" per CRC bottom-up FIFO; the
        // wire drops the trailing slash+index so the server sees the shorter
        // bay/rack dest-spec and treats it as an append target.
        Assert.Equal("STRIP Ground/1", VStripsCanonicalBuilder.BuildStripMove("Ground", 0, null));
        Assert.Equal("STRIP Local/2", VStripsCanonicalBuilder.BuildStripMove("Local", 1, null));
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
        // 0-based rack 1 → wire rack 2 (1-based).
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
    public void BuildSeparatorCreate_MapsStyleToChar_SlashCompoundWire(SeparatorStyle style, char styleChar)
    {
        // 0-based rack 0 / index 3 → wire rack 1 / index 4, slash-compound.
        Assert.Equal($"SEP {styleChar} Ground/1/4 HOLD", VStripsCanonicalBuilder.BuildSeparatorCreate(style, "Ground", 0, 3, "HOLD"));
    }

    [Fact]
    public void BuildSeparatorCreate_NoLabel_OmitsTail()
    {
        Assert.Equal("SEP W Local/2/1", VStripsCanonicalBuilder.BuildSeparatorCreate(SeparatorStyle.White, "Local", 1, 0, null));
        Assert.Equal("SEP W Local/2/1", VStripsCanonicalBuilder.BuildSeparatorCreate(SeparatorStyle.White, "Local", 1, 0, "   "));
    }

    [Fact]
    public void BuildSeparatorDelete_LabelWinsOverIndex()
    {
        // When a label is supplied, the index argument is ignored; bay/rack is
        // slash-compound with 1-based rack.
        Assert.Equal("SEPD Ground/3 HOLD", VStripsCanonicalBuilder.BuildSeparatorDelete("Ground", 2, "HOLD", 5));
    }

    [Fact]
    public void BuildSeparatorDelete_NoLabel_UsesIndex_SlashCompoundWire()
    {
        // 0-based rack 1 / index 3 → wire rack 2 (slash-compound) + index 4.
        Assert.Equal("SEPD Local/2 4", VStripsCanonicalBuilder.BuildSeparatorDelete("Local", 1, null, 3));
        Assert.Equal("SEPD Local/2 1", VStripsCanonicalBuilder.BuildSeparatorDelete("Local", 1, "", null));
    }

    [Fact]
    public void BuildSeparatorEdit_EmitsAtomicSepeSlashCompoundWire()
    {
        // Atomic SEPE: dest-spec is slash-compound, newLabel is remaining tokens.
        Assert.Equal("SEPE Ground/2/3 LONG LINE", VStripsCanonicalBuilder.BuildSeparatorEdit("Ground", 1, 2, "LONG LINE"));
        Assert.Equal("SEPE Local/1/1 HOLD", VStripsCanonicalBuilder.BuildSeparatorEdit("Local", 0, 0, "  HOLD  "));
    }

    [Fact]
    public void BuildBlankCreate_NullBay_EmitsBareVerb()
    {
        Assert.Equal("BLANK", VStripsCanonicalBuilder.BuildBlankCreate(null, null, null));
    }

    [Fact]
    public void BuildBlankCreate_WithBay_EmitsFullPosition_SlashCompoundWire()
    {
        // 0-based rack 1 / index 2 → wire rack 2 / index 3, slash-compound.
        Assert.Equal("BLANK Ground/2/3", VStripsCanonicalBuilder.BuildBlankCreate("Ground", 1, 2));
        // Null rack/index default to 0 then convert 1-based → "Ground/1/1".
        Assert.Equal("BLANK Ground/1/1", VStripsCanonicalBuilder.BuildBlankCreate("Ground", null, null));
    }

    [Fact]
    public void BuildBlankDelete_WithRack_EmitsRack_SlashCompoundWire()
    {
        // 0-based rack 1 → wire bay/2.
        Assert.Equal("BLANKD Ground/2", VStripsCanonicalBuilder.BuildBlankDelete("Ground", 1));
    }

    [Fact]
    public void BuildBlankDelete_WithoutRack_EmitsBayOnly()
    {
        Assert.Equal("BLANKD Ground", VStripsCanonicalBuilder.BuildBlankDelete("Ground", null));
    }
}
