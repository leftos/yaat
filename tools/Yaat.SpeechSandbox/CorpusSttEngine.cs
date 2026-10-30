using Yaat.Client.Services;

namespace Yaat.SpeechSandbox;

/// <summary>The STT engine families a <c>--corpus-wer</c> run can score.</summary>
public enum SttKind
{
    Whisper,
    Sherpa,
    SherpaStreaming,
}

/// <summary>
/// One resolved engine choice: the family and its source — a Whisper model id, URL or <c>.bin</c> file,
/// or a sherpa-onnx model directory.
/// </summary>
public sealed record SttChoice(SttKind Kind, string Source)
{
    /// <summary>
    /// Only Whisper takes the production biasing prompt; the sherpa transducer exports decode without one.
    /// </summary>
    public bool TakesPrompt => Kind == SttKind.Whisper;
}

/// <summary>One STT engine behind the seam <see cref="CorpusWerRunner"/> scores every arm through.</summary>
internal interface ICorpusSttEngine : IDisposable
{
    /// <summary>Why the engine cannot transcribe, or null when it can.</summary>
    string? SetupError();

    /// <summary>
    /// The transcript of 16 kHz mono Float32 samples, or null when the engine returned nothing. The
    /// prompt is the production biasing prompt, or empty; an engine that takes none ignores it.
    /// </summary>
    Task<string?> TranscribeAsync(float[] samples16k, string prompt, CancellationToken ct);
}

/// <summary>Whisper, the production engine, configured with an explicit model source.</summary>
internal sealed class WhisperCorpusSttEngine(string modelSource) : ICorpusSttEngine
{
    private readonly WhisperSttEngine _engine = new(new FixedWhisperRuntimeConfig(modelSource));

    /// <summary>
    /// Why Whisper cannot transcribe, or null. A rooted model path that does not exist is caught
    /// here, since the engine would otherwise fail every segment and score 100% WER.
    /// </summary>
    public string? SetupError()
    {
        if (!_engine.IsConfigured)
        {
            return "no Whisper model configured — pass --stt whisper:<src>.";
        }
        return (Path.IsPathRooted(modelSource) && !File.Exists(modelSource))
            ? $"Whisper model not configured: the file {modelSource} does not exist."
            : null;
    }

    public Task<string?> TranscribeAsync(float[] samples16k, string prompt, CancellationToken ct) => _engine.TranscribeAsync(samples16k, prompt, ct);

    public void Dispose() => _engine.Dispose();
}

/// <summary>A sherpa-onnx offline transducer export (Parakeet-TDT and its ATC fine-tunes).</summary>
internal sealed class SherpaCorpusSttEngine(string modelDir) : ICorpusSttEngine
{
    private readonly SherpaSttEngine _engine = new(modelDir);

    public string? SetupError() => _engine.IsConfigured ? null : $"sherpa model dir {modelDir} has no encoder/decoder/joiner .onnx or tokens.txt";

    public Task<string?> TranscribeAsync(float[] samples16k, string prompt, CancellationToken ct) =>
        Task.Run(() => _engine.Transcribe(samples16k, AudioCaptureService.SampleRate), ct);

    public void Dispose() => _engine.Dispose();
}

/// <summary>A sherpa-onnx streaming transducer export (Nemotron Speech Streaming and its relatives).</summary>
internal sealed class SherpaStreamingCorpusSttEngine(string modelDir) : ICorpusSttEngine
{
    private readonly SherpaStreamingSttEngine _engine = new(modelDir);

    public string? SetupError() =>
        _engine.IsConfigured ? null : $"sherpa-streaming model dir {modelDir} has no encoder/decoder/joiner .onnx or tokens.txt";

    public Task<string?> TranscribeAsync(float[] samples16k, string prompt, CancellationToken ct) =>
        Task.Run(() => _engine.Transcribe(samples16k, AudioCaptureService.SampleRate), ct);

    public void Dispose() => _engine.Dispose();
}

/// <summary>
/// Builds the engine a run scores through and names it in results. The factory is a plain switch over
/// the kind, so another engine family is one case and one adapter.
/// </summary>
public static class CorpusSttEngines
{
    internal static ICorpusSttEngine Create(SttChoice choice) =>
        choice.Kind switch
        {
            SttKind.Whisper => new WhisperCorpusSttEngine(choice.Source),
            SttKind.Sherpa => new SherpaCorpusSttEngine(choice.Source),
            SttKind.SherpaStreaming => new SherpaStreamingCorpusSttEngine(choice.Source),
            _ => throw new ArgumentOutOfRangeException(nameof(choice), choice.Kind, "no corpus STT engine for this kind"),
        };

    /// <summary>
    /// How a choice is recorded in results: Whisper's source as a rooted file's name so no machine path
    /// reaches a committed baseline (a URL or a catalog id as given), and a sherpa model directory as
    /// <c>sherpa:&lt;dir name&gt;</c> or <c>sherpa-streaming:&lt;dir name&gt;</c>.
    /// </summary>
    public static string RecordedModel(SttChoice choice) =>
        choice.Kind switch
        {
            SttKind.Whisper => WhisperRecordedModel(choice.Source),
            SttKind.Sherpa => "sherpa:" + DirectoryName(choice.Source),
            SttKind.SherpaStreaming => "sherpa-streaming:" + DirectoryName(choice.Source),
            _ => throw new ArgumentOutOfRangeException(nameof(choice), choice.Kind, "no recorded name for this kind"),
        };

    private static string WhisperRecordedModel(string source)
    {
        bool isUrl = Uri.TryCreate(source, UriKind.Absolute, out Uri? uri) && (uri.Scheme is "http" or "https");
        return (!isUrl && Path.IsPathRooted(source)) ? Path.GetFileName(source) : source;
    }

    private static string DirectoryName(string source) => Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(source)));
}
