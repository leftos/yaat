using Xunit;
using Yaat.Sim.Speech;

namespace Yaat.Sim.Tests.Speech;

public class ScenarioCallsignExtractorTests
{
    // --- Labeled quoted (CALLSIGN "..." / CS "...") ---

    [Theory]
    [InlineData("CALLSIGN \"JETLINX\" /V/", "JETLINX")]
    [InlineData("CALLSIGN \"PACK COAST\" /V/", "PACK COAST")]
    [InlineData("callsign \"jetlinx\" /V/", "JETLINX")]
    [InlineData("CS \"FLEX MALTA\"", "FLEX MALTA")]
    [InlineData("cs \"shamrock\"", "SHAMROCK")]
    public void Extract_LabeledQuoted(string remarks, string expected)
    {
        var result = ScenarioCallsignExtractor.Extract(remarks);
        Assert.Single(result);
        Assert.Equal(expected, result[0]);
    }

    // --- Bare quoted ---

    [Theory]
    [InlineData("\"CIRCADIAN\"", "CIRCADIAN")]
    [InlineData("/V/ \"FLEX MALTA\"", "FLEX MALTA")]
    [InlineData("/V/ \"FLEX MALTA\" 1385", "FLEX MALTA")]
    public void Extract_BareQuoted(string remarks, string expected)
    {
        var result = ScenarioCallsignExtractor.Extract(remarks);
        Assert.Single(result);
        Assert.Equal(expected, result[0]);
    }

    // --- Negative cases (bare words, parking data, noise) ---

    [Theory]
    [InlineData("/V/")]
    [InlineData("/V/ GOLDEN GATE")] // Bare word not supported (Phase 1)
    [InlineData("/V/ PARKING F10, AUTO GENERATED")]
    [InlineData("/V/ CALLSING AIRSHARE")] // typo'd label, unquoted — not supported
    [InlineData("")]
    [InlineData("   ")]
    public void Extract_NoMatch_ReturnsEmpty(string remarks)
    {
        Assert.Empty(ScenarioCallsignExtractor.Extract(remarks));
    }

    [Fact]
    public void Extract_NullInput_ReturnsEmpty()
    {
        Assert.Empty(ScenarioCallsignExtractor.Extract(null));
    }

    // --- Multiple / dedupe ---

    [Fact]
    public void Extract_MultipleQuoted_ReturnsAll()
    {
        var result = ScenarioCallsignExtractor.Extract("CALLSIGN \"FOO\" /V/ \"BAR\"");
        Assert.Equal(2, result.Count);
        Assert.Contains("FOO", result);
        Assert.Contains("BAR", result);
    }

    [Fact]
    public void Extract_DuplicateQuoted_Deduped()
    {
        var result = ScenarioCallsignExtractor.Extract("CALLSIGN \"FOO\" \"FOO\"");
        Assert.Single(result);
        Assert.Equal("FOO", result[0]);
    }

    [Fact]
    public void Extract_RejectsNumericQuoted()
    {
        // Quoted strings that look like flight numbers or remarks shouldn't be treated as telephony.
        Assert.Empty(ScenarioCallsignExtractor.Extract("\"1385\""));
    }

    [Fact]
    public void Extract_RejectsOverlongQuoted()
    {
        var longNoise = new string('A', 50);
        Assert.Empty(ScenarioCallsignExtractor.Extract($"\"{longNoise}\""));
    }
}
