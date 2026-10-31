using System.Text.Json;
using Xunit;
using Yaat.SpeechSandbox;

namespace Yaat.Client.Tests;

/// <summary>
/// What the sandbox reads from sherpa-onnx's result JSON: the n-best a beam search fills, the top-level
/// result greedy leaves it with, and the missing key that identifies the stock native.
/// </summary>
public sealed class CorpusSherpaResultTests
{
    private const string BeamJson = """
        {"lang": "", "emotion": "", "event": "", "text": " alpha bravo", "timestamps": [0.10, 0.50], "durations": [0.10, 0.20],
        "tokens":["▁alpha", "▁bravo"], "ys_log_probs": [-0.100000, -0.200000], "words": [], "hypotheses": [
        {"text": " alpha bravo", "timestamps": [0.10, 0.50], "durations": [0.10, 0.20], "tokens": ["▁alpha", "▁bravo"],
        "ys_log_probs": [-0.100000, -0.200000], "score": -1.250000},
        {"text": "alpha brave ", "timestamps": [0.10, 0.50], "durations": [0.10, 0.20], "tokens": ["▁alpha", "▁bra", "ve"],
        "ys_log_probs": [-0.100000, -0.900000, -0.300000], "score": -2.500000}]}
        """;

    private const string GreedyJson = """
        {"text": " alpha bravo ", "tokens":["▁alpha", "▁bravo"], "ys_log_probs": [-0.100000, -0.200000], "words": [], "hypotheses": []}
        """;

    private const string EmptyJson = """
        {"text": " ", "tokens":[], "ys_log_probs": [], "words": [], "hypotheses": []}
        """;

    private const string StockJson = """
        {"text": " alpha bravo ", "tokens":["▁alpha", "▁bravo"], "ys_log_probs": [-0.100000, -0.200000], "words": []}
        """;

    [Fact]
    public void A_Beam_Result_Gives_Every_Hypothesis_In_Order()
    {
        IReadOnlyList<SttHypothesis> entries = SherpaResultJson.Parse(BeamJson);

        Assert.Equal(2, entries.Count);
        Assert.Equal("alpha bravo", entries[0].Text);
        Assert.Equal(["▁alpha", "▁bravo"], entries[0].Tokens);
        Assert.Equal([-0.1, -0.2], entries[0].TokenLogProbs);
        Assert.Equal(-1.25, entries[0].Score);
        Assert.Equal("alpha brave", entries[1].Text);
        Assert.Equal(["▁alpha", "▁bra", "ve"], entries[1].Tokens);
        Assert.Equal([-0.1, -0.9, -0.3], entries[1].TokenLogProbs);
        Assert.Equal(-2.5, entries[1].Score);
    }

    [Theory]
    [InlineData(GreedyJson)]
    [InlineData(StockJson)]
    public void A_Result_Without_Hypotheses_Gives_One_Entry_From_The_Top_Level(string json)
    {
        SttHypothesis entry = Assert.Single(SherpaResultJson.Parse(json));

        Assert.Equal("alpha bravo", entry.Text);
        Assert.Equal(["▁alpha", "▁bravo"], entry.Tokens);
        Assert.Equal([-0.1, -0.2], entry.TokenLogProbs);
        Assert.Null(entry.Score);
    }

    [Fact]
    public void An_Empty_Result_Gives_No_Entries() => Assert.Empty(SherpaResultJson.Parse(EmptyJson));

    [Theory]
    [InlineData(BeamJson, true)]
    [InlineData(GreedyJson, true)]
    [InlineData(StockJson, false)]
    public void Only_The_Hypotheses_Key_Marks_A_Native_With_Nbest(string json, bool expected) =>
        Assert.Equal(expected, SherpaResultJson.CarriesNbest(json));

    [Fact]
    public void Malformed_Json_Throws() => Assert.ThrowsAny<JsonException>(() => SherpaResultJson.Parse("{\"text\": -inf"));

    [Fact]
    public void A_Hypothesis_Without_A_Score_Throws() =>
        Assert.Throws<KeyNotFoundException>(() =>
            SherpaResultJson.Parse(
                """{"text": "a", "tokens": [], "ys_log_probs": [], "hypotheses": [{"text": "a", "tokens": [], "ys_log_probs": []}]}"""
            )
        );

    [Fact]
    public void No_Entries_Have_No_Text() => Assert.Null(new SttHypotheses([], null, false).Text);

    [Fact]
    public void An_Empty_First_Entry_Has_No_Text()
    {
        var hypotheses = new SttHypotheses([new SttHypothesis(string.Empty, [], [], null), new SttHypothesis("alpha", [], [], null)], null, false);

        Assert.Null(hypotheses.Text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Single_Of_No_Text_Has_No_Entries(string? text) => Assert.Empty(SttHypotheses.Single(text, null, false).Entries);

    [Fact]
    public void Single_Keeps_The_Text_Latency_And_Cache_Flag()
    {
        var hypotheses = SttHypotheses.Single("alpha", 12.5, true);

        SttHypothesis entry = Assert.Single(hypotheses.Entries);
        Assert.Equal("alpha", hypotheses.Text);
        Assert.Empty(entry.Tokens);
        Assert.Empty(entry.TokenLogProbs);
        Assert.Null(entry.Score);
        Assert.Equal(12.5, hypotheses.LatencyMs);
        Assert.True(hypotheses.Cached);
    }
}
