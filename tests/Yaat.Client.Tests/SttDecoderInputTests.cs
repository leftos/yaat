using Xunit;
using Yaat.Client.Services.Speech;
using Yaat.Sim.Speech.Context;

namespace Yaat.Client.Tests;

/// <summary>
/// A recognized hypothesis as the context decoder takes it: sherpa's leading-space pieces become the word mark, and an entry
/// that reported no tokens carries none.
/// </summary>
public sealed class SttDecoderInputTests
{
    [Fact]
    public void ToDecoderHypothesis_LeadingSpaceBecomesWordMark()
    {
        var hypothesis = SttDecoderInput.ToDecoderHypothesis(new SttHypothesis("number", [" num", "ber"], [-0.25, -0.5], -1.5));

        Assert.Equal("number", hypothesis.Text);
        Assert.Equal([new TokenLogProb("▁num", -0.25), new TokenLogProb("ber", -0.5)], hypothesis.Tokens);
        Assert.Equal(-1.5, hypothesis.AcousticScore);
    }

    [Fact]
    public void ToDecoderHypothesis_NoTokens_NullTokens()
    {
        var hypothesis = SttDecoderInput.ToDecoderHypothesis(new SttHypothesis("number", [], [], null));

        Assert.Null(hypothesis.Tokens);
        Assert.Null(hypothesis.AcousticScore);
    }

    [Fact]
    public void ToDecoderHypothesis_CountMismatch_Throws()
    {
        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            SttDecoderInput.ToDecoderHypothesis(new SttHypothesis("number", [" num", "ber"], [-0.25], null))
        );

        Assert.Equal("A hypothesis has 2 tokens but 1 token log-probabilities: 'number'.", error.Message);
    }
}
