using Xunit;
using Yaat.Client.Services;

namespace Yaat.Client.Tests;

/// <summary>
/// Unit tests for <see cref="LocalLlmCallsignResolver.ValidateAgainstActive"/> — the output
/// guard that ensures the LLM can't hallucinate a callsign that isn't in the active list.
/// </summary>
public class LocalLlmCallsignResolverTests
{
    private static readonly string[] Active = ["N9225L", "SWA123", "UAL456"];

    [Theory]
    [InlineData("N9225L")]
    [InlineData("n9225l")] // case-insensitive
    [InlineData("SWA123")]
    [InlineData("UAL456")]
    public void ValidateAgainstActive_ExactMatch_ReturnsCallsign(string raw)
    {
        var result = LocalLlmCallsignResolver.ValidateAgainstActive(raw, Active);
        Assert.Contains(result, Active, StringComparer.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("`N9225L`")] // backticks
    [InlineData("\"SWA123\"")] // quotes
    [InlineData("  UAL456  ")] // whitespace
    [InlineData("N9225L\nextra explanation")] // trailing lines
    [InlineData("N9225L, explanation follows")] // trailing comma text
    public void ValidateAgainstActive_StripsNoise_ReturnsCallsign(string raw)
    {
        var result = LocalLlmCallsignResolver.ValidateAgainstActive(raw, Active);
        Assert.NotNull(result);
        Assert.Contains(result!, Active, StringComparer.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("NONE")]
    [InlineData("none")]
    [InlineData("None")]
    public void ValidateAgainstActive_None_ReturnsNull(string raw)
    {
        Assert.Null(LocalLlmCallsignResolver.ValidateAgainstActive(raw, Active));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("DAL999")] // not in active list
    [InlineData("I think it's N9225L")] // first token not a callsign
    [InlineData("N")] // partial
    [InlineData("garbage")]
    public void ValidateAgainstActive_InvalidOrAbsent_ReturnsNull(string raw)
    {
        Assert.Null(LocalLlmCallsignResolver.ValidateAgainstActive(raw, Active));
    }

    [Fact]
    public void ValidateAgainstActive_EmptyActiveList_ReturnsNull()
    {
        Assert.Null(LocalLlmCallsignResolver.ValidateAgainstActive("N9225L", []));
    }
}
