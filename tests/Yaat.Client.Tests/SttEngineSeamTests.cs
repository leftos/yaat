using Xunit;
using Yaat.Client.Services;
using Yaat.Client.Services.Speech;
using Yaat.Sim.Speech;

namespace Yaat.Client.Tests;

/// <summary>
/// The speech pipeline's STT seam: an <see cref="ISttEngine"/>'s best hypothesis is the transcript the
/// rule mapper turns into a callsign and a canonical command.
/// </summary>
public sealed class SttEngineSeamTests
{
    [Fact]
    public async Task The_Engines_Best_Hypothesis_Maps_To_A_Command()
    {
        using var engine = new FixedSttEngine(
            new SttHypotheses(
                [
                    new SttHypothesis("november three four six golf turn left heading three one zero", [], [], null),
                    new SttHypothesis("november three four six golf turn left heading three one two", [], [], null),
                ],
                null,
                false
            )
        );
        var ctx = new SpeechContext(["N346G"], [], string.Empty);

        SttHypotheses hyps = await engine.TranscribeAsync([0.1f, 0.2f], ctx.WhisperInitialPrompt, CancellationToken.None);
        (TranscriptMapResult result, _, _) = await SpeechRecognitionService.MapTranscriptWithTraceAsync(
            hyps.Text!,
            ctx,
            new PhraseologyCommandMapper(),
            null,
            null,
            CancellationToken.None
        );

        Assert.Equal("N346G", result.Callsign);
        Assert.Equal("TL 310", result.Canonical);
    }

    private sealed class FixedSttEngine(SttHypotheses hypotheses) : ISttEngine
    {
        public TranscriptionMode Mode => TranscriptionMode.Fast;

        public Task WarmUpAsync(CancellationToken ct) => Task.CompletedTask;

        public Task<SttHypotheses> TranscribeAsync(float[] samples16k, string? prompt, CancellationToken ct) => Task.FromResult(hypotheses);

        public void Dispose() { }
    }
}
