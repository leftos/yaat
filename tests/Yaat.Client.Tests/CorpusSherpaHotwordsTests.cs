using Xunit;
using Yaat.SpeechSandbox;

namespace Yaat.Client.Tests;

/// <summary>The hotword text a sherpa-onnx stream takes, and the phrase lines a <c>--hotwords-file</c> holds.</summary>
public sealed class CorpusSherpaHotwordsTests
{
    [Fact]
    public void Join_Separates_Phrases_With_A_Slash() => Assert.Equal("a b/c d", SherpaHotwords.Join(["a b", "c d"]));

    [Theory]
    [InlineData(HotwordCase.Title, "SOUTHWEST two", "Southwest Two")]
    [InlineData(HotwordCase.AsIs, "SOUTHWEST two", "SOUTHWEST two")]
    [InlineData(HotwordCase.Lower, "SOUTHWEST two", "southwest two")]
    [InlineData(HotwordCase.Upper, "SOUTHWEST two", "SOUTHWEST TWO")]
    public void Each_Case_Is_Applied_To_The_Phrase(HotwordCase casing, string line, string expected)
    {
        IReadOnlyList<string> phrases = SherpaHotwords.ParseLines([line], casing, out string? error);

        Assert.Null(error);
        Assert.Equal([expected], phrases);
    }

    [Fact]
    public void Blank_Lines_Are_Skipped_And_Inner_Whitespace_Collapsed()
    {
        IReadOnlyList<string> phrases = SherpaHotwords.ParseLines(["", "  alpha \t  bravo  ", "   ", "charlie"], HotwordCase.AsIs, out string? error);

        Assert.Null(error);
        Assert.Equal(["alpha bravo", "charlie"], phrases);
    }

    [Theory]
    [InlineData("a/b")]
    [InlineData("alpha :2.0")]
    [InlineData("#x")]
    [InlineData("@x")]
    public void A_Line_The_Native_Would_Read_As_Syntax_Is_Refused(string line)
    {
        IReadOnlyList<string> phrases = SherpaHotwords.ParseLines(["fine", line], HotwordCase.AsIs, out string? error);

        Assert.Empty(phrases);
        Assert.Equal($"hotword line '{line}' cannot contain '/' or a word starting with ':', '#' or '@'", error);
    }

    [Fact]
    public void A_Colon_Inside_A_Word_Is_Accepted()
    {
        IReadOnlyList<string> phrases = SherpaHotwords.ParseLines(["a:b"], HotwordCase.AsIs, out string? error);

        Assert.Null(error);
        Assert.Equal(["a:b"], phrases);
    }

    [Fact]
    public void All_Blank_Input_Has_No_Phrases()
    {
        IReadOnlyList<string> phrases = SherpaHotwords.ParseLines(["", "  "], HotwordCase.Title, out string? error);

        Assert.Empty(phrases);
        Assert.Equal("the hotwords file has no phrases", error);
    }
}
