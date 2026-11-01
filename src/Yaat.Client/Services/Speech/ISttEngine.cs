namespace Yaat.Client.Services.Speech;

/// <summary>A speech-to-text engine the push-to-talk pipeline transcribes through.</summary>
public interface ISttEngine : IDisposable
{
    /// <summary>Loads the model, so the first transmission pays no load cost.</summary>
    Task WarmUpAsync(CancellationToken ct);

    /// <summary>The alternatives for 16 kHz mono Float32 samples, best first; no entries when the engine heard nothing.</summary>
    /// <param name="samples16k">The audio.</param>
    /// <param name="prompt">The biasing prompt, for an engine that takes one (Whisper); every other engine ignores it.</param>
    /// <param name="ct">Cancels the transcription.</param>
    Task<SttHypotheses> TranscribeAsync(float[] samples16k, string? prompt, CancellationToken ct);
}
