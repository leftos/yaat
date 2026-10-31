using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using SherpaOnnx;
using Yaat.Client.Logging;
using Yaat.Client.Services;

namespace Yaat.SpeechSandbox;

/// <summary>
/// Offline STT probe engine backed by sherpa-onnx's C API with a NeMo transducer export — built for
/// evaluating NVIDIA Parakeet-TDT against the Whisper production path
/// (<c>--eval --stt sherpa:&lt;dir&gt;</c> / <c>--corpus-wer --stt sherpa:&lt;dir&gt;</c>). Sandbox-only for
/// now: promotion into Yaat.Client happens only if eval results justify a second production STT engine.
///
/// The model directory must contain a sherpa-onnx NeMo transducer export
/// (e.g. <c>csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8</c>):
/// <c>encoder[.int8].onnx</c>, <c>decoder[.int8].onnx</c>, <c>joiner[.int8].onnx</c>,
/// <c>tokens.txt</c>. Runs on the sherpa-onnx execution <paramref name="provider"/> — <c>cpu</c> with
/// the native the Piper TTS path already ships, or <c>cuda</c> when the CUDA build's DLLs are in place.
///
/// Greedy and <c>modified_beam_search</c> share one code path, and every result is read from the C API's
/// JSON. A beam run also needs <c>bpe.vocab</c> in the model directory, takes per-stream hotwords and returns
/// an n-best list. The NuGet recognizer keeps its handle private, so hotword streams go through the imports
/// below. The stock native has no n-best and drops speech in TDT beam search, so a beam run needs the native
/// <c>New-SherpaNativeSandbox.ps1</c> overlays.
/// </summary>
internal sealed class SherpaSttEngine(string modelDir, string provider, SherpaDecoding decoding) : IDisposable
{
    private const string NativeLibrary = "sherpa-onnx-c-api";

    private static readonly ILogger Log = AppLog.CreateLogger<SherpaSttEngine>();

    private readonly string _modelDir = modelDir;
    private readonly bool _isBeam = decoding.Method == SherpaDecodingMethod.Beam;
    private IntPtr _recognizer;

    public bool IsConfigured => File.Exists(FindModelFile("encoder")) && File.Exists(Path.Combine(_modelDir, "tokens.txt"));

    public bool HasBpeVocab => File.Exists(BpeVocabPath);

    private string BpeVocabPath => Path.Combine(_modelDir, "bpe.vocab");

    /// <summary>
    /// Transcribes 16 kHz mono Float32 samples into its alternatives, best first. Empty on empty input,
    /// missing model files, or a load, decode or JSON failure. Synchronous because sherpa-onnx's offline API
    /// is — callers on a hot path should wrap in Task.Run.
    /// </summary>
    /// <param name="samples">The audio.</param>
    /// <param name="sampleRate">The audio's sample rate.</param>
    /// <param name="hotwords">Phrases to bias this stream toward; empty for none.</param>
    public IReadOnlyList<SttHypothesis> Transcribe(float[] samples, int sampleRate, IReadOnlyList<string> hotwords)
    {
        if (samples.Length == 0)
        {
            return [];
        }

        try
        {
            string? json = DecodeToJson(samples, sampleRate, hotwords);
            return (json is null) ? [] : SherpaResultJson.Parse(json);
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "sherpa-onnx transcription failed for model dir {Dir}", _modelDir);
            return [];
        }
    }

    /// <summary>
    /// Why this native cannot run the beam search, or null when it can or the run is greedy. Decodes half a
    /// second of silence and looks for the n-best key the stock native does not write, since that native
    /// also predates the fix for TDT beam search dropping speech.
    /// </summary>
    public string? NativeProblem()
    {
        if (!_isBeam)
        {
            return null;
        }

        string? json = DecodeToJson(new float[AudioCaptureService.SampleRate / 2], AudioCaptureService.SampleRate, []);
        if (json is null)
        {
            return $"sherpa model {_modelDir} failed to load";
        }
        return SherpaResultJson.CarriesNbest(json)
            ? null
            : "this sherpa-onnx native has no n-best and drops speech in TDT beam search; run the sandbox from New-SherpaNativeSandbox.ps1";
    }

    public void Dispose()
    {
        if (_recognizer != IntPtr.Zero)
        {
            SherpaOnnxDestroyOfflineRecognizer(_recognizer);
            _recognizer = IntPtr.Zero;
        }
    }

    /// <summary>Decodes one stream; null (after logging why) when the recognizer or a native object could not be made.</summary>
    private string? DecodeToJson(float[] samples, int sampleRate, IReadOnlyList<string> hotwords)
    {
        IntPtr recognizer = EnsureLoaded();
        if (recognizer == IntPtr.Zero)
        {
            return null;
        }

        IntPtr handle =
            (hotwords.Count > 0)
                ? SherpaOnnxCreateOfflineStreamWithHotwords(recognizer, SherpaHotwords.Join(hotwords))
                : SherpaOnnxCreateOfflineStream(recognizer);
        if (handle == IntPtr.Zero)
        {
            Log.LogError("sherpa-onnx could not create a stream for model dir {Dir}", _modelDir);
            return null;
        }

        using var stream = new OfflineStream(handle);
        if (_isBeam)
        {
            stream.SetOption("num_return_paths", decoding.NumPaths.ToString(CultureInfo.InvariantCulture));
        }
        stream.AcceptWaveform(sampleRate, samples);
        SherpaOnnxDecodeOfflineStream(recognizer, stream.Handle);
        return ReadResultJson(stream);
    }

    /// <summary>The stream's result; the native writes UTF-8 and the tokens carry non-ASCII word markers.</summary>
    private string? ReadResultJson(OfflineStream stream)
    {
        IntPtr json = SherpaOnnxGetOfflineStreamResultAsJson(stream.Handle);
        if (json == IntPtr.Zero)
        {
            Log.LogError("sherpa-onnx returned no result JSON for model dir {Dir}", _modelDir);
            return null;
        }

        try
        {
            return Marshal.PtrToStringUTF8(json);
        }
        finally
        {
            SherpaOnnxDestroyOfflineStreamResultJson(json);
        }
    }

    private IntPtr EnsureLoaded()
    {
        if (_recognizer != IntPtr.Zero)
        {
            return _recognizer;
        }

        string encoder = FindModelFile("encoder");
        string decoder = FindModelFile("decoder");
        string joiner = FindModelFile("joiner");
        string tokens = Path.Combine(_modelDir, "tokens.txt");
        if (!File.Exists(encoder) || !File.Exists(decoder) || !File.Exists(joiner) || !File.Exists(tokens))
        {
            Log.LogError("sherpa-onnx model dir {Dir} is missing encoder/decoder/joiner .onnx or tokens.txt", _modelDir);
            return IntPtr.Zero;
        }

        var config = new OfflineRecognizerConfig();
        config.ModelConfig.Transducer.Encoder = encoder;
        config.ModelConfig.Transducer.Decoder = decoder;
        config.ModelConfig.Transducer.Joiner = joiner;
        config.ModelConfig.Tokens = tokens;
        config.ModelConfig.ModelType = "nemo_transducer";
        config.ModelConfig.NumThreads = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);
        config.ModelConfig.Provider = provider;
        config.DecodingMethod = _isBeam ? "modified_beam_search" : "greedy_search";
        config.MaxActivePaths = SherpaDecoding.MaxBeamPaths;
        if (_isBeam)
        {
            config.ModelConfig.ModelingUnit = "bpe";
            config.ModelConfig.BpeVocab = BpeVocabPath;
            config.HotwordsScore = decoding.HotwordScore;
        }

        _recognizer = SherpaOnnxCreateOfflineRecognizer(ref config);
        if (_recognizer == IntPtr.Zero)
        {
            Log.LogError("sherpa-onnx rejected the recognizer config for model dir {Dir}", _modelDir);
            return IntPtr.Zero;
        }

        Log.LogInformation("sherpa-onnx NeMo transducer loaded from {Dir} on {Provider} ({Method})", _modelDir, provider, decoding.Method);
        return _recognizer;
    }

    /// <summary>Prefers the int8-quantized file when both quantized and fp32 exports coexist.</summary>
    private string FindModelFile(string stem)
    {
        string int8 = Path.Combine(_modelDir, $"{stem}.int8.onnx");
        return File.Exists(int8) ? int8 : Path.Combine(_modelDir, $"{stem}.onnx");
    }

    [DllImport(NativeLibrary)]
    private static extern IntPtr SherpaOnnxCreateOfflineRecognizer(ref OfflineRecognizerConfig config);

    [DllImport(NativeLibrary)]
    private static extern void SherpaOnnxDestroyOfflineRecognizer(IntPtr recognizer);

    [DllImport(NativeLibrary)]
    private static extern IntPtr SherpaOnnxCreateOfflineStream(IntPtr recognizer);

    [DllImport(NativeLibrary)]
    private static extern IntPtr SherpaOnnxCreateOfflineStreamWithHotwords(IntPtr recognizer, [MarshalAs(UnmanagedType.LPUTF8Str)] string hotwords);

    [DllImport(NativeLibrary)]
    private static extern void SherpaOnnxDecodeOfflineStream(IntPtr recognizer, IntPtr stream);

    [DllImport(NativeLibrary)]
    private static extern IntPtr SherpaOnnxGetOfflineStreamResultAsJson(IntPtr stream);

    [DllImport(NativeLibrary)]
    private static extern void SherpaOnnxDestroyOfflineStreamResultJson(IntPtr json);
}
