using System.Globalization;
using Microsoft.Extensions.Logging;
using SherpaOnnx;
using Yaat.Client.Logging;

namespace Yaat.Client.Services.Speech;

/// <summary>
/// Offline STT engine backed by sherpa-onnx's C API with a NeMo transducer export (NVIDIA Parakeet-TDT and
/// its ATC fine-tunes).
///
/// The model directory must contain a sherpa-onnx NeMo transducer export
/// (e.g. <c>csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8</c>):
/// <c>encoder[.int8].onnx</c>, <c>decoder[.int8].onnx</c>, <c>joiner[.int8].onnx</c>,
/// <c>tokens.txt</c>. Runs on the sherpa-onnx execution <paramref name="provider"/> — <c>cpu</c> with
/// the native the Piper TTS path already ships, or <c>cuda</c> when the CUDA build's DLLs are in place.
///
/// Greedy and <c>modified_beam_search</c> share one code path, and every result is read from the C API's
/// JSON. A beam run also needs <c>bpe.vocab</c> in the model directory, takes per-stream hotwords and returns
/// an n-best list. Every native call goes through <paramref name="api"/>, since the NuGet recognizer keeps its
/// handle private. The stock native has no n-best and drops speech in TDT beam search, so a beam run needs the
/// native the speech sandbox's <c>New-SherpaNativeSandbox.ps1</c> overlays.
///
/// Safe to call from several threads: loads, decodes and <see cref="Dispose"/> are serialised on one lock, so at
/// most one recognizer is made and none is destroyed under a running decode. After <see cref="Dispose"/> the
/// engine makes no native call at all.
/// </summary>
public sealed class SherpaSttEngine(string modelDir, string provider, SherpaDecoding decoding, ISherpaOfflineApi api) : IDisposable
{
    private static readonly ILogger Log = AppLog.CreateLogger<SherpaSttEngine>();

    private readonly string _modelDir = modelDir;
    private readonly bool _isBeam = decoding.Method == SherpaDecodingMethod.Beam;
    private readonly Lock _gate = new();
    private IntPtr _recognizer;
    private bool _disposed;
    private volatile string? _loadError;

    /// <summary>The folder the model is loaded from.</summary>
    public string ModelDir => _modelDir;

    /// <summary>Why the last attempt to load the model failed; null before any attempt and after a successful load.</summary>
    public string? LoadError => _loadError;

    /// <summary>True once <see cref="Dispose"/> has run.</summary>
    public bool IsDisposed
    {
        get
        {
            lock (_gate)
            {
                return _disposed;
            }
        }
    }

    /// <summary>How the engine decodes: greedy, or beam with its paths and hotword score.</summary>
    public SherpaDecoding Decoding { get; } = decoding;

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
    /// <param name="hotwords">The stream's hotword text, phrases already joined in the C API's form; empty for none.</param>
    public IReadOnlyList<SttHypothesis> Transcribe(float[] samples, int sampleRate, string hotwords)
    {
        if (samples.Length == 0)
        {
            return [];
        }

        try
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    Log.LogWarning("sherpa-onnx engine for {Dir} was disposed; transcription skipped", _modelDir);
                    return [];
                }

                string? json = DecodeToJson(samples, sampleRate, hotwords);
                return (json is null) ? [] : SherpaResultJson.Parse(json);
            }
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

        lock (_gate)
        {
            if (_disposed)
            {
                return $"sherpa model {_modelDir} was disposed";
            }

            string? json = DecodeToJson(new float[AudioCaptureService.SampleRate / 2], AudioCaptureService.SampleRate, string.Empty);
            if (json is null)
            {
                return $"sherpa model {_modelDir} failed to load";
            }
            return SherpaResultJson.CarriesNbest(json)
                ? null
                : "this sherpa-onnx native has no n-best and drops speech in TDT beam search; run the sandbox from New-SherpaNativeSandbox.ps1";
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (_recognizer != IntPtr.Zero)
            {
                api.DestroyRecognizer(_recognizer);
                _recognizer = IntPtr.Zero;
            }

            _disposed = true;
        }
    }

    /// <summary>
    /// Decodes one stream; null (after logging why) when the recognizer or a native object could not be made. Only ever
    /// runs with the gate held.
    /// </summary>
    private string? DecodeToJson(float[] samples, int sampleRate, string hotwords)
    {
        IntPtr recognizer = EnsureLoaded();
        if (recognizer == IntPtr.Zero)
        {
            return null;
        }

        IntPtr stream = api.CreateStream(recognizer, hotwords);
        if (stream == IntPtr.Zero)
        {
            Log.LogError("sherpa-onnx could not create a stream for model dir {Dir}", _modelDir);
            return null;
        }

        try
        {
            if (_isBeam)
            {
                api.SetStreamOption(stream, "num_return_paths", Decoding.NumPaths.ToString(CultureInfo.InvariantCulture));
            }
            api.AcceptWaveform(stream, sampleRate, samples);
            api.Decode(recognizer, stream);
            string? json = api.ResultJson(stream);
            if (json is null)
            {
                Log.LogError("sherpa-onnx returned no result JSON for model dir {Dir}", _modelDir);
            }
            return json;
        }
        finally
        {
            api.DestroyStream(stream);
        }
    }

    /// <summary>The recognizer, created on first use. Only ever runs with the gate held, so it is created at most once.</summary>
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
            _loadError = $"the model folder {_modelDir} is missing encoder/decoder/joiner .onnx or tokens.txt";
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
            config.HotwordsScore = Decoding.HotwordScore;
        }

        try
        {
            _recognizer = api.CreateRecognizer(ref config);
        }
        catch (Exception ex)
        {
            _loadError = $"sherpa-onnx failed to create the recognizer for {_modelDir}: {ex.Message}";
            throw;
        }
        if (_recognizer == IntPtr.Zero)
        {
            _loadError = $"sherpa-onnx rejected the recognizer config for {_modelDir}";
            Log.LogError("sherpa-onnx rejected the recognizer config for model dir {Dir}", _modelDir);
            return IntPtr.Zero;
        }

        _loadError = null;
        Log.LogInformation("sherpa-onnx NeMo transducer loaded from {Dir} on {Provider} ({Method})", _modelDir, provider, Decoding.Method);
        return _recognizer;
    }

    /// <summary>Prefers the int8-quantized file when both quantized and fp32 exports coexist.</summary>
    private string FindModelFile(string stem)
    {
        string int8 = Path.Combine(_modelDir, $"{stem}.int8.onnx");
        return File.Exists(int8) ? int8 : Path.Combine(_modelDir, $"{stem}.onnx");
    }
}
