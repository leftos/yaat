using Microsoft.Extensions.Logging;
using SherpaOnnx;
using Yaat.Client.Logging;

namespace Yaat.SpeechSandbox;

/// <summary>
/// Offline STT probe engine backed by sherpa-onnx's <see cref="OfflineRecognizer"/> with a
/// NeMo transducer export — built for evaluating NVIDIA Parakeet-TDT against the Whisper
/// production path (<c>--eval --parakeet &lt;model-dir&gt;</c> / <c>--sherpa-stt</c>).
/// Sandbox-only for now: promotion into Yaat.Client happens only if eval results justify a
/// second production STT engine.
///
/// The model directory must contain a sherpa-onnx NeMo transducer export
/// (e.g. <c>csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8</c>):
/// <c>encoder[.int8].onnx</c>, <c>decoder[.int8].onnx</c>, <c>joiner[.int8].onnx</c>,
/// <c>tokens.txt</c>. Runs on CPU via the same sherpa-onnx native the Piper TTS path already
/// ships. Decoding is greedy; hotword biasing needs <c>modified_beam_search</c> plus the BPE
/// vocab file, which the published int8 export does not include — regenerate it from the NeMo
/// tokenizer before wiring hotwords.
/// </summary>
internal sealed class SherpaSttEngine : IDisposable
{
    private static readonly ILogger Log = AppLog.CreateLogger<SherpaSttEngine>();

    private readonly string _modelDir;
    private OfflineRecognizer? _recognizer;

    public SherpaSttEngine(string modelDir)
    {
        _modelDir = modelDir;
    }

    public bool IsConfigured => File.Exists(FindModelFile("encoder")) && File.Exists(Path.Combine(_modelDir, "tokens.txt"));

    /// <summary>
    /// Transcribes 16 kHz mono Float32 samples. Returns null on empty input, missing model
    /// files, or a load/decode failure. Synchronous because sherpa-onnx's offline API is —
    /// callers on a hot path should wrap in Task.Run.
    /// </summary>
    public string? Transcribe(float[] samples, int sampleRate)
    {
        if (samples.Length == 0)
        {
            return null;
        }

        try
        {
            var recognizer = EnsureLoaded();
            if (recognizer is null)
            {
                return null;
            }

            using var stream = recognizer.CreateStream();
            stream.AcceptWaveform(sampleRate, samples);
            recognizer.Decode(stream);
            var text = stream.Result.Text.Trim();
            return text.Length == 0 ? null : text;
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "sherpa-onnx transcription failed for model dir {Dir}", _modelDir);
            return null;
        }
    }

    private OfflineRecognizer? EnsureLoaded()
    {
        if (_recognizer is not null)
        {
            return _recognizer;
        }

        var encoder = FindModelFile("encoder");
        var decoder = FindModelFile("decoder");
        var joiner = FindModelFile("joiner");
        var tokens = Path.Combine(_modelDir, "tokens.txt");
        if (!File.Exists(encoder) || !File.Exists(decoder) || !File.Exists(joiner) || !File.Exists(tokens))
        {
            Log.LogError("sherpa-onnx model dir {Dir} is missing encoder/decoder/joiner .onnx or tokens.txt", _modelDir);
            return null;
        }

        var config = new OfflineRecognizerConfig();
        config.ModelConfig.Transducer.Encoder = encoder;
        config.ModelConfig.Transducer.Decoder = decoder;
        config.ModelConfig.Transducer.Joiner = joiner;
        config.ModelConfig.Tokens = tokens;
        config.ModelConfig.ModelType = "nemo_transducer";
        config.ModelConfig.NumThreads = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);
        config.ModelConfig.Provider = "cpu";
        config.DecodingMethod = "greedy_search";

        _recognizer = new OfflineRecognizer(config);
        Log.LogInformation("sherpa-onnx NeMo transducer loaded from {Dir}", _modelDir);
        return _recognizer;
    }

    /// <summary>Prefers the int8-quantized file when both quantized and fp32 exports coexist.</summary>
    private string FindModelFile(string stem)
    {
        var int8 = Path.Combine(_modelDir, $"{stem}.int8.onnx");
        return File.Exists(int8) ? int8 : Path.Combine(_modelDir, $"{stem}.onnx");
    }

    public void Dispose()
    {
        _recognizer?.Dispose();
        _recognizer = null;
    }
}
