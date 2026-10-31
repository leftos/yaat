using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Yaat.Client.Services;

namespace Yaat.SpeechSandbox;

/// <summary>The STT engine families a <c>--corpus-wer</c> or <c>--eval</c> run can score.</summary>
public enum SttKind
{
    Whisper,
    Sherpa,
    SherpaStreaming,
    Gemini,
    OpenAi,
}

/// <summary>Which device a local STT engine runs on: the engine family's own default, the CPU, or the GPU.</summary>
public enum SttDevice
{
    Default,
    Cpu,
    Gpu,
}

/// <summary>
/// One resolved engine choice: the family, its source — a Whisper model id, URL or <c>.bin</c> file,
/// a sherpa-onnx model directory, or a cloud provider's model id — the device <c>--device</c> asks for, and
/// how a sherpa transducer decodes (<see cref="SherpaDecoding.Greedy"/> for every other family).
/// </summary>
public sealed record SttChoice(SttKind Kind, string Source, SttDevice Device, SherpaDecoding Decoding)
{
    /// <summary>
    /// Only Whisper takes the production biasing prompt; the sherpa transducer exports decode without one,
    /// and the cloud engines send their own fixed instruction.
    /// </summary>
    public bool TakesPrompt => Kind == SttKind.Whisper;
}

/// <summary>The sherpa transducer's two decoders.</summary>
public enum SherpaDecodingMethod
{
    Greedy,
    Beam,
}

/// <summary>How a sherpa transducer decodes a run.</summary>
/// <param name="Method">Greedy search, or modified beam search.</param>
/// <param name="NumPaths">Hypotheses a beam stream returns, 1 through <see cref="MaxBeamPaths"/>; 1 for greedy.</param>
/// <param name="HotwordScore">The recognizer's hotword score; one per run, since the decoder reads it from the config.</param>
public sealed record SherpaDecoding(SherpaDecodingMethod Method, int NumPaths, float HotwordScore)
{
    public const int MaxBeamPaths = 4;

    public static readonly SherpaDecoding Greedy = new(SherpaDecodingMethod.Greedy, 1, SherpaHotwords.DefaultScore);
}

/// <summary>What a transmission's context asks of the recognizer: the Whisper prompt and/or the hotword phrases.</summary>
public sealed record SttBias(string Prompt, IReadOnlyList<string> Hotwords);

/// <summary>One decoded alternative of a clip.</summary>
/// <param name="Text">The transcript, trimmed.</param>
/// <param name="Tokens">The model's tokens; empty for an engine that reports none.</param>
/// <param name="TokenLogProbs">The tokens' log probabilities; empty for an engine that reports none.</param>
/// <param name="Score">The hypothesis's search score; null outside a beam search.</param>
public sealed record SttHypothesis(string Text, IReadOnlyList<string> Tokens, IReadOnlyList<double> TokenLogProbs, double? Score);

/// <summary>One engine's alternatives for a clip, best first, with the latency it measured and whether it came from a cache.</summary>
/// <param name="Entries">The alternatives; empty when the engine returned nothing.</param>
/// <param name="LatencyMs">
/// The engine's own latency in milliseconds; null means the caller's stopwatch around the call is the latency.
/// The streaming engine reports its own finalization time here.
/// </param>
/// <param name="Cached">True only for a cloud cache hit.</param>
public sealed record SttHypotheses(IReadOnlyList<SttHypothesis> Entries, double? LatencyMs, bool Cached)
{
    /// <summary>The best alternative's transcript; null when there is none or it is empty.</summary>
    public string? Text => ((Entries.Count > 0) && (Entries[0].Text.Length > 0)) ? Entries[0].Text : null;

    /// <summary>The result of an engine that returns one plain transcript: no entry for null or empty text.</summary>
    public static SttHypotheses Single(string? text, double? latencyMs, bool cached) =>
        new(string.IsNullOrEmpty(text) ? [] : [new SttHypothesis(text, [], [], null)], latencyMs, cached);
}

/// <summary>One STT engine behind the seam <see cref="CorpusWerRunner"/> scores every arm through.</summary>
internal interface ICorpusSttEngine : IDisposable
{
    /// <summary>Why the engine cannot transcribe, or null when it can.</summary>
    string? SetupError();

    /// <summary>
    /// The alternatives for 16 kHz mono Float32 samples. The bias carries the production biasing prompt,
    /// or empty, and any hotword phrases; an engine ignores what it does not take.
    /// </summary>
    Task<SttHypotheses> TranscribeAsync(float[] samples16k, SttBias bias, CancellationToken ct);

    /// <summary>Loads the model, so the first timed segment pays no load cost.</summary>
    Task WarmUpAsync(CancellationToken ct);
}

/// <summary>Whisper, the production engine, configured with an explicit model source and GPU layer count.</summary>
internal sealed class WhisperCorpusSttEngine(string modelSource, int? gpuLayerCount) : ICorpusSttEngine
{
    private readonly WhisperSttEngine _engine = new(new FixedWhisperRuntimeConfig(modelSource, gpuLayerCount));

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

    public async Task<SttHypotheses> TranscribeAsync(float[] samples16k, SttBias bias, CancellationToken ct)
    {
        string? text = await _engine.TranscribeAsync(samples16k, bias.Prompt, ct).ConfigureAwait(false);
        return SttHypotheses.Single(text, null, false);
    }

    public Task WarmUpAsync(CancellationToken ct) => _engine.PrewarmAsync(ct);

    public void Dispose() => _engine.Dispose();
}

/// <summary>A sherpa-onnx offline transducer export (Parakeet-TDT and its ATC fine-tunes).</summary>
public sealed class SherpaCorpusSttEngine(string modelDir, string provider, SherpaDecoding decoding) : ICorpusSttEngine
{
    private readonly SherpaSttEngine _engine = new(modelDir, provider, decoding);

    public string? SetupError()
    {
        if (!_engine.IsConfigured)
        {
            return $"sherpa model dir {modelDir} has no encoder/decoder/joiner .onnx or tokens.txt";
        }
        return ((decoding.Method == SherpaDecodingMethod.Beam) && !_engine.HasBpeVocab)
            ? $"sherpa model dir {modelDir} has no bpe.vocab, which --decoding beam needs"
            : null;
    }

    public Task<SttHypotheses> TranscribeAsync(float[] samples16k, SttBias bias, CancellationToken ct) =>
        Task.Run(() => new SttHypotheses(_engine.Transcribe(samples16k, AudioCaptureService.SampleRate, bias.Hotwords), null, false), ct);

    /// <summary>Loads the model; a beam run also proves the native can decode it, or throws saying why it cannot.</summary>
    /// <exception cref="InvalidOperationException">The native cannot run this beam search.</exception>
    public Task WarmUpAsync(CancellationToken ct) =>
        (decoding.Method == SherpaDecodingMethod.Beam)
            ? Task.Run(RefuseUnfitNative, ct)
            : Task.Run(() => _engine.Transcribe(new float[AudioCaptureService.SampleRate / 2], AudioCaptureService.SampleRate, []), ct);

    public void Dispose() => _engine.Dispose();

    private void RefuseUnfitNative()
    {
        string? problem = _engine.NativeProblem();
        if (problem is not null)
        {
            throw new InvalidOperationException(problem);
        }
    }
}

/// <summary>A sherpa-onnx streaming transducer export (Nemotron Speech Streaming and its relatives).</summary>
internal sealed class SherpaStreamingCorpusSttEngine(string modelDir, string provider) : ICorpusSttEngine
{
    private readonly SherpaStreamingSttEngine _engine = new(modelDir, provider);

    public string? SetupError() =>
        _engine.IsConfigured ? null : $"sherpa-streaming model dir {modelDir} has no encoder/decoder/joiner .onnx or tokens.txt";

    public Task<SttHypotheses> TranscribeAsync(float[] samples16k, SttBias bias, CancellationToken ct) =>
        Task.Run(
            () =>
            {
                (string? text, double finalizeMs) = _engine.TranscribeTimed(samples16k, AudioCaptureService.SampleRate);
                return SttHypotheses.Single(text, finalizeMs, false);
            },
            ct
        );

    public Task WarmUpAsync(CancellationToken ct) =>
        Task.Run(() => _engine.TranscribeTimed(new float[AudioCaptureService.SampleRate / 2], AudioCaptureService.SampleRate), ct);

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
            SttKind.Whisper => CreateWhisper(choice),
            SttKind.Sherpa => new SherpaCorpusSttEngine(choice.Source, Provider(choice.Device), choice.Decoding),
            SttKind.SherpaStreaming => new SherpaStreamingCorpusSttEngine(choice.Source, Provider(choice.Device)),
            SttKind.Gemini or SttKind.OpenAi => new CloudSttEngine(choice.Kind, choice.Source),
            _ => throw new ArgumentOutOfRangeException(nameof(choice), choice.Kind, "no corpus STT engine for this kind"),
        };

    /// <summary>
    /// The device a run records for its engine: <c>auto</c> for Whisper left to LM-Kit, <c>cpu</c> or <c>gpu</c>
    /// for a local engine, and <c>cloud</c> for a remote one.
    /// </summary>
    public static string DeviceText(SttChoice choice) =>
        choice.Kind switch
        {
            SttKind.Whisper => choice.Device switch
            {
                SttDevice.Cpu => "cpu",
                SttDevice.Gpu => "gpu",
                _ => "auto",
            },
            SttKind.Sherpa or SttKind.SherpaStreaming => (choice.Device == SttDevice.Gpu) ? "gpu" : "cpu",
            SttKind.Gemini or SttKind.OpenAi => "cloud",
            _ => throw new ArgumentOutOfRangeException(nameof(choice), choice.Kind, "no device text for this kind"),
        };

    /// <summary>Whisper for the choice's device, with LM-Kit's GPU backends turned off ahead of a CPU run.</summary>
    private static WhisperCorpusSttEngine CreateWhisper(SttChoice choice)
    {
        if (choice.Device == SttDevice.Cpu)
        {
            DisableLmKitGpuBackends();
        }

        return new WhisperCorpusSttEngine(choice.Source, WhisperGpuLayers(choice.Device));
    }

    /// <summary>
    /// Turns off LM-Kit's GPU backends before its runtime starts, so a CPU Whisper run takes its AVX2
    /// backend: while CUDA or Vulkan is loaded, LM-Kit runs Whisper on one CPU thread. The switches are
    /// process-wide and settle at initialization, which is safe in a corpus run that scores one engine at a
    /// time.
    /// </summary>
    /// <exception cref="InvalidOperationException">LM-Kit's runtime had already started.</exception>
    private static void DisableLmKitGpuBackends()
    {
        if (LMKit.Global.Runtime.IsInitialized)
        {
            throw new InvalidOperationException("LM-Kit's runtime started before --device cpu could turn off its GPU backends");
        }

        LMKit.Global.Runtime.EnableCuda = false;
        LMKit.Global.Runtime.EnableVulkan = false;
    }

    /// <summary>Whisper's GPU layers: every layer on the CPU for a CPU run, LM-Kit's own choice otherwise.</summary>
    private static int? WhisperGpuLayers(SttDevice device) => (device == SttDevice.Cpu) ? 0 : null;

    /// <summary>The sherpa-onnx execution provider: the CUDA build's <c>cuda</c>, or the CPU otherwise.</summary>
    private static string Provider(SttDevice device) => (device == SttDevice.Gpu) ? "cuda" : "cpu";

    /// <summary>
    /// Parses an <c>--stt</c> value, <c>&lt;kind&gt;:&lt;source&gt;</c>, split at the first colon so a
    /// Whisper URL or a sherpa model path keeps every colon after the kind.
    /// </summary>
    /// <param name="value">The flag's value as given.</param>
    /// <param name="choice">The engine choice, when the value parses.</param>
    /// <param name="error">Why the value does not parse, otherwise.</param>
    /// <returns>True when the value names a known kind and a non-empty source.</returns>
    public static bool TryParse(string value, [NotNullWhen(true)] out SttChoice? choice, [NotNullWhen(false)] out string? error)
    {
        choice = null;
        error = null;
        int colon = value.IndexOf(':', StringComparison.Ordinal);
        if ((colon < 0) || (colon == value.Length - 1))
        {
            error = $"--stt needs <kind>:<source>, got '{value}'";
            return false;
        }
        SttKind? kind = KindFromText(value[..colon]);
        if (kind is null)
        {
            error = $"--stt kind must be whisper, sherpa, sherpa-streaming, gemini or openai, got '{value[..colon]}'";
            return false;
        }
        choice = new SttChoice(kind.Value, value[(colon + 1)..], SttDevice.Default, SherpaDecoding.Greedy);
        return true;
    }

    /// <summary>The kind as an <c>--stt</c> value spells it: <c>whisper</c>, <c>sherpa-streaming</c>, <c>openai</c>.</summary>
    public static string KindText(SttKind kind) =>
        kind switch
        {
            SttKind.Whisper => "whisper",
            SttKind.Sherpa => "sherpa",
            SttKind.SherpaStreaming => "sherpa-streaming",
            SttKind.Gemini => "gemini",
            SttKind.OpenAi => "openai",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "no --stt spelling for this kind"),
        };

    private static SttKind? KindFromText(string text)
    {
        foreach (SttKind kind in Enum.GetValues<SttKind>())
        {
            if (KindText(kind) == text)
            {
                return kind;
            }
        }
        return null;
    }

    /// <summary>
    /// How a choice is recorded in results: Whisper's source as a rooted file's name so no machine path
    /// reaches a committed baseline (a URL or a catalog id as given), a sherpa model directory as
    /// <c>sherpa:&lt;dir name&gt;</c> (with <c>+beam&lt;paths&gt;</c> on a beam run) or
    /// <c>sherpa-streaming:&lt;dir name&gt;</c>, and a cloud model as <c>gemini:&lt;model&gt;</c> or
    /// <c>openai:&lt;model&gt;</c>.
    /// </summary>
    public static string RecordedModel(SttChoice choice) =>
        choice.Kind switch
        {
            SttKind.Whisper => WhisperRecordedModel(choice.Source),
            SttKind.Sherpa => "sherpa:" + DirectoryName(choice.Source) + BeamSuffix(choice.Decoding),
            SttKind.SherpaStreaming => "sherpa-streaming:" + DirectoryName(choice.Source),
            SttKind.Gemini => "gemini:" + choice.Source,
            SttKind.OpenAi => "openai:" + choice.Source,
            _ => throw new ArgumentOutOfRangeException(nameof(choice), choice.Kind, "no recorded name for this kind"),
        };

    private static string BeamSuffix(SherpaDecoding decoding) =>
        (decoding.Method == SherpaDecodingMethod.Beam) ? $"+beam{decoding.NumPaths.ToString(CultureInfo.InvariantCulture)}" : string.Empty;

    private static string WhisperRecordedModel(string source)
    {
        bool isUrl = Uri.TryCreate(source, UriKind.Absolute, out Uri? uri) && (uri.Scheme is "http" or "https");
        return (!isUrl && Path.IsPathRooted(source)) ? Path.GetFileName(source) : source;
    }

    private static string DirectoryName(string source) => Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(source)));
}
