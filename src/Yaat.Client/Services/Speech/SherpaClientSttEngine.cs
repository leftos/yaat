using System.Diagnostics;

namespace Yaat.Client.Services.Speech;

/// <summary>A <see cref="SherpaSttEngine"/> behind the pipeline's seam: no hotwords, no prompt, its own decode latency.</summary>
public sealed class SherpaClientSttEngine(SherpaSttEngine engine) : ISttEngine
{
    private readonly SherpaSttEngine _engine = engine;

    /// <summary>The engine this adapter decodes with.</summary>
    public SherpaSttEngine Engine => _engine;

    /// <summary>Decodes half a second of silence, which loads the model.</summary>
    /// <exception cref="InvalidOperationException">The model failed to load.</exception>
    public Task WarmUpAsync(CancellationToken ct) =>
        Task.Run(
            () =>
            {
                _ = _engine.Transcribe(new float[AudioCaptureService.SampleRate / 2], AudioCaptureService.SampleRate, string.Empty);
                if (_engine.LoadError is { } loadError)
                {
                    throw new InvalidOperationException(
                        $"The sherpa-onnx engine for {_engine.ModelDir} ({_engine.Decoding.Method}) failed to load: {loadError}"
                    );
                }
            },
            ct
        );

    /// <summary>The engine's alternatives, with the decode's own latency; <paramref name="prompt"/> is ignored.</summary>
    public Task<SttHypotheses> TranscribeAsync(float[] samples16k, string? prompt, CancellationToken ct) =>
        Task.Run(
            () =>
            {
                var sw = Stopwatch.StartNew();
                IReadOnlyList<SttHypothesis> entries = _engine.Transcribe(samples16k, AudioCaptureService.SampleRate, string.Empty);
                sw.Stop();
                return new SttHypotheses(entries, sw.Elapsed.TotalMilliseconds, false);
            },
            ct
        );

    public void Dispose() => _engine.Dispose();
}
