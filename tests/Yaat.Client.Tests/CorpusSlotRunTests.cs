using Xunit;
using Yaat.Sim.Speech.Context;
using Yaat.Sim.Testing;
using Yaat.SpeechSandbox;

namespace Yaat.Client.Tests;

/// <summary>
/// The <c>--slots</c> run's per-clip work: how a sherpa hypothesis becomes a decoder hypothesis, how the context decoder runs
/// over a clip's n-best with and without resolution, and the set-up errors a corpus folder without its gold raises.
/// </summary>
public sealed class CorpusSlotRunTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("corpus-slot-run-").FullName;

    public CorpusSlotRunTests() => TestVnasData.EnsureInitialized();

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void ToDecoderHypothesis_LeadingSpaceBecomesWordMark()
    {
        var hypothesis = CorpusSlotRun.ToDecoderHypothesis(new SttHypothesis("number", [" num", "ber"], [-0.25, -0.5], -1.5));

        Assert.Equal("number", hypothesis.Text);
        Assert.Equal([new TokenLogProb("▁num", -0.25), new TokenLogProb("ber", -0.5)], hypothesis.Tokens);
        Assert.Equal(-1.5, hypothesis.AcousticScore);
    }

    [Fact]
    public void ToDecoderHypothesis_NoTokens_NullTokens()
    {
        var hypothesis = CorpusSlotRun.ToDecoderHypothesis(new SttHypothesis("number", [], [], null));

        Assert.Null(hypothesis.Tokens);
        Assert.Null(hypothesis.AcousticScore);
    }

    [Fact]
    public void ToDecoderHypothesis_CountMismatch_Throws()
    {
        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            CorpusSlotRun.ToDecoderHypothesis(new SttHypothesis("number", [" num", "ber"], [-0.25], null))
        );

        Assert.Contains("2", error.Message, StringComparison.Ordinal);
        Assert.Contains("1", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Decode_NoTranscript_NoCallsign()
    {
        DecodeResult result = CorpusSlotRun.Decode(new SttHypotheses([], null, false), Context(), true, DecoderSettings.Default);

        Assert.Equal(DecodeState.NoCallsign, result.State);
        Assert.Null(result.Callsign);
        Assert.Empty(result.Entities);
        Assert.Equal(string.Empty, result.RewrittenTranscript);
    }

    [Fact]
    public void Decode_ResolveOff_FirstHypothesisExactOnly()
    {
        DecodeResult result = CorpusSlotRun.Decode(Transcript(), Context(), false, DecoderSettings.Default);

        Assert.NotEqual(DecodeState.Resolved, result.State);
    }

    [Fact]
    public void Decode_ResolveOn_PicksResolvedHypothesis()
    {
        DecodeResult result = CorpusSlotRun.Decode(Transcript(), Context(), true, DecoderSettings.Default);

        Assert.Equal(DecodeState.Resolved, result.State);
        Assert.Equal("SWA2495", result.Callsign?.Entity.Value);
        Assert.Equal(1, result.HypothesisIndex);
    }

    [Fact]
    public void TryCreate_NoRoomsFile_Error()
    {
        File.WriteAllText(Path.Combine(_dir, CorpusSlots.FileName), """{ "koak-gnd_seg0001": [] }""");

        var run = CorpusSlotRun.TryCreate(_dir, [Segment("koak-gnd_seg0001")], Options(), out string? error);

        Assert.Null(run);
        Assert.Equal($"--slots needs rooms.json in {_dir}", error);
    }

    [Fact]
    public void TryCreate_ClipMissingFromSlots_Error()
    {
        File.WriteAllText(
            Path.Combine(_dir, CorpusRooms.FileName),
            """{ "feeds": { "koak-gnd": { "airport": "KOAK", "facility": "gnd", "seed": 1 } } }"""
        );
        File.WriteAllText(Path.Combine(_dir, CorpusSlots.FileName), """{ "koak-gnd_seg0001": [] }""");

        var run = CorpusSlotRun.TryCreate(_dir, [Segment("koak-gnd_seg0001"), Segment("koak-gnd_seg0002")], Options(), out string? error);

        Assert.Null(run);
        Assert.Equal("koak-gnd_seg0002 has no entry in slots.json", error);
    }

    private static TransmissionContext Context() =>
        new(
            "KOAK",
            [
                new RoomAircraft(
                    ContextEntityFactory.Callsign("SWA2495", null, new Dictionary<string, string>(), ["SWA2495"], DecoderSettings.Default)!,
                    []
                ),
            ],
            []
        );

    private static SttHypotheses Transcript() =>
        new(
            [new SttHypothesis("southwest two four nine six", [], [], null), new SttHypothesis("southwest two four nine five", [], [], null)],
            null,
            false
        );

    private static CorpusSegment Segment(string id) => new(id, CorpusRole.Unknown, [], "reference", "koak-gnd", null);

    private static CorpusSlotOptions Options() => new(false, null, 300, HotwordCase.Title);
}
