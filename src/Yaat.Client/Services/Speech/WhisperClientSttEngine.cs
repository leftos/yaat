namespace Yaat.Client.Services.Speech;

/// <summary>
/// A <see cref="WhisperSttEngine"/> behind the pipeline's seam: its one transcript as a single hypothesis.
/// The adapter does not own the engine: disposing it leaves the engine to whoever created it.
/// </summary>
public sealed class WhisperClientSttEngine(WhisperSttEngine engine) : ISttEngine
{
    private readonly WhisperSttEngine _engine = engine;

    public Task WarmUpAsync(CancellationToken ct) => _engine.PrewarmAsync(ct);

    /// <summary>Whisper's transcript, biased by <paramref name="prompt"/>; no entries for a null or empty transcript.</summary>
    public async Task<SttHypotheses> TranscribeAsync(float[] samples16k, string? prompt, CancellationToken ct)
    {
        string? text = await _engine.TranscribeAsync(samples16k, prompt ?? string.Empty, ct).ConfigureAwait(false);
        return SttHypotheses.Single(text, null, false);
    }

    /// <summary>Does nothing: the engine's creator disposes it.</summary>
    public void Dispose() { }
}
