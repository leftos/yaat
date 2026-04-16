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
    public void BuildStripMove_FormatsPositionArgs()
    {
        Assert.Equal("STRIP Ground 0 0", VStripsCanonicalBuilder.BuildStripMove("Ground", 0, 0));
        Assert.Equal("STRIP Local 1 3", VStripsCanonicalBuilder.BuildStripMove("Local", 1, 3));
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
        Assert.Equal("HSC Ground/1 NORDO\\KOAK\\28L", VStripsCanonicalBuilder.BuildHalfStripCreate("Ground", 1, ["NORDO", "KOAK", "28L"]));
    }

    [Fact]
    public void BuildHalfStripCreate_NoLines_OmitsPayload()
    {
        Assert.Equal("HSC Local/0", VStripsCanonicalBuilder.BuildHalfStripCreate("Local", 0, []));
    }

    [Fact]
    public void BuildHalfStripAmend_ExpandsKeyThenLines()
    {
        Assert.Equal("HSA NORDO NEW LINE2", VStripsCanonicalBuilder.BuildHalfStripAmend("NORDO", ["NEW", "LINE2"]));
    }

    [Fact]
    public void BuildHalfStripMove_UsesSlashSeparatedDest()
    {
        Assert.Equal("HSM NORDO Local/1/2", VStripsCanonicalBuilder.BuildHalfStripMove("NORDO", "Local", 1, 2));
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
    public void BuildSeparatorCreate_MapsStyleToChar(SeparatorStyle style, char styleChar)
    {
        Assert.Equal($"SEP {styleChar} Ground 0 3 HOLD", VStripsCanonicalBuilder.BuildSeparatorCreate(style, "Ground", 0, 3, "HOLD"));
    }

    [Fact]
    public void BuildSeparatorCreate_NoLabel_OmitsTail()
    {
        Assert.Equal("SEP W Local 1 0", VStripsCanonicalBuilder.BuildSeparatorCreate(SeparatorStyle.White, "Local", 1, 0, null));
        Assert.Equal("SEP W Local 1 0", VStripsCanonicalBuilder.BuildSeparatorCreate(SeparatorStyle.White, "Local", 1, 0, "   "));
    }

    [Fact]
    public void BuildSeparatorDelete_LabelWinsOverIndex()
    {
        Assert.Equal("SEPD Ground 2 HOLD", VStripsCanonicalBuilder.BuildSeparatorDelete("Ground", 2, "HOLD", 5));
    }

    [Fact]
    public void BuildSeparatorDelete_NoLabel_UsesIndex()
    {
        Assert.Equal("SEPD Local 1 3", VStripsCanonicalBuilder.BuildSeparatorDelete("Local", 1, null, 3));
        Assert.Equal("SEPD Local 1 0", VStripsCanonicalBuilder.BuildSeparatorDelete("Local", 1, "", null));
    }

    [Fact]
    public void BuildBlankCreate_NullBay_EmitsBareVerb()
    {
        Assert.Equal("BLANK", VStripsCanonicalBuilder.BuildBlankCreate(null, null, null));
    }

    [Fact]
    public void BuildBlankCreate_WithBay_EmitsFullPosition()
    {
        Assert.Equal("BLANK Ground 1 2", VStripsCanonicalBuilder.BuildBlankCreate("Ground", 1, 2));
        Assert.Equal("BLANK Ground 0 0", VStripsCanonicalBuilder.BuildBlankCreate("Ground", null, null));
    }

    [Fact]
    public void BuildBlankDelete_WithRack_EmitsRack()
    {
        Assert.Equal("BLANKD Ground 1", VStripsCanonicalBuilder.BuildBlankDelete("Ground", 1));
    }

    [Fact]
    public void BuildBlankDelete_WithoutRack_EmitsBayOnly()
    {
        Assert.Equal("BLANKD Ground", VStripsCanonicalBuilder.BuildBlankDelete("Ground", null));
    }
}
