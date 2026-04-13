using Xunit;
using Yaat.Client.Services;

namespace Yaat.Client.Tests;

public class LocalLlmCommandMapperNormalizeTests
{
    [Theory]
    [InlineData("CM 5000", "CM 5000")]
    [InlineData("cm 5000", "CM 5000")]
    [InlineData("CM 5000, FH 270", "CM 5000, FH 270")]
    [InlineData("  FH 270  ", "FH 270")]
    [InlineData("\"CM 5000\"", "CM 5000")]
    [InlineData("`CM 5000`", "CM 5000")]
    [InlineData("Output: CM 5000", "CM 5000")]
    [InlineData("Canonical: CM 5000", "CM 5000")]
    [InlineData("Command: FH 270", "FH 270")]
    [InlineData("CM 5000\nexplanation follows", "CM 5000")]
    [InlineData("CAPP", "CAPP")]
    [InlineData("CAPP ILS28R", "CAPP ILS28R")]
    [InlineData("AT CEPIN CAPP", "AT CEPIN CAPP")]
    [InlineData("SQ 7500", "SQ 7500")]
    public void NormalizeOutput_ValidCanonicalCommands_ReturnsCleaned(string input, string expected)
    {
        Assert.Equal(expected, LocalLlmCommandMapper.NormalizeOutput(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("I think the pilot wants to climb")]
    [InlineData("CM 5000 - this means climb and maintain 5000")]
    [InlineData("lowercase only")]
    [InlineData("123 FH")] // starts with digits — not a verb
    public void NormalizeOutput_InvalidOutput_ReturnsNull(string input)
    {
        Assert.Null(LocalLlmCommandMapper.NormalizeOutput(input));
    }

    [Fact]
    public void NormalizeOutput_StripsMarkdownCodeFence()
    {
        Assert.Equal("CM 5000", LocalLlmCommandMapper.NormalizeOutput("```\nCM 5000\n```"));
    }
}
