using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SherpaOnnx;
using Yaat.Client.Logging;

namespace Yaat.SpeechSandbox;

/// <summary>
/// The fixed-size sample ranges one utterance is fed to a streaming recognizer in: every range
/// <c>chunkSamples</c> long, in order, except a shorter last one.
/// </summary>
public static class StreamingChunks
{
    /// <summary>The ranges covering <paramref name="totalSamples"/> samples in order.</summary>
    /// <param name="totalSamples">How many samples the utterance holds.</param>
    /// <param name="chunkSamples">How many samples every range holds but the last.</param>
    /// <returns>An empty list for an utterance of no samples.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The chunk holds no samples, or the total is negative.</exception>
    public static IReadOnlyList<(int Start, int Length)> Ranges(int totalSamples, int chunkSamples)
    {
        if (chunkSamples <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkSamples), chunkSamples, "a chunk holds at least one sample");
        }
        if (totalSamples < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(totalSamples), totalSamples, "an utterance holds no fewer than zero samples");
        }

        var ranges = new List<(int Start, int Length)>();
        for (int start = 0; start < totalSamples; start += chunkSamples)
        {
            ranges.Add((start, Math.Min(chunkSamples, totalSamples - start)));
        }
        return ranges;
    }
}

/// <summary>
/// Streaming STT probe engine backed by sherpa-onnx's <see cref="OnlineRecognizer"/> with a NeMo
/// streaming transducer export — built for evaluating Nemotron Speech Streaming style models against
/// the Whisper production path. The engine feeds an utterance in fixed-size chunks as push-to-talk
/// capture would, decoding after each one, and times only the wait after the last real sample: the
/// tail padding, the input-finished drain and the result read.
///
/// The model directory must contain a sherpa-onnx NeMo streaming transducer export:
/// <c>encoder[.int8].onnx</c>, <c>decoder[.int8].onnx</c>, <c>joiner[.int8].onnx</c>,
/// <c>tokens.txt</c>. Runs on CPU via the same sherpa-onnx native the Piper TTS path already ships.
/// Decoding is greedy with endpointing off, matching sherpa-onnx's streaming NeMo example, which sets
/// no model type and leaves the feature config at its defaults.
/// </summary>
internal sealed class SherpaStreamingSttEngine(string modelDir) : IDisposable
{
    private static readonly ILogger Log = AppLog.CreateLogger<SherpaStreamingSttEngine>();

    /// <summary>0.3 s of silence sherpa-onnx's <c>streaming-nemotron-c-api.c</c> example pads a tail with.</summary>
    private const double TailPaddingSeconds = 0.3;

    /// <summary>How much audio one feed to the recognizer carries: 1,600 samples at 16 kHz.</summary>
    private const double FeedChunkSeconds = 0.1;

    private readonly string _modelDir = modelDir;
    private OnlineRecognizer? _recognizer;

    public bool IsConfigured =>
        File.Exists(FindModelFile("encoder"))
        && File.Exists(FindModelFile("decoder"))
        && File.Exists(FindModelFile("joiner"))
        && File.Exists(Path.Combine(_modelDir, "tokens.txt"));

    /// <summary>
    /// Transcribes 16 kHz mono Float32 samples, feeding them in <see cref="FeedChunkSeconds"/> slices and
    /// timing only the finalization after the last real sample. Returns a null transcript and 0 ms on empty
    /// input, missing model files, or a load/decode failure. Synchronous because sherpa-onnx's recognizer
    /// API is — callers on a hot path should wrap in Task.Run.
    /// </summary>
    public (string? Text, double FinalizeMs) TranscribeTimed(float[] samples, int sampleRate)
    {
        if (samples.Length == 0)
        {
            return (null, 0);
        }

        try
        {
            OnlineRecognizer? recognizer = EnsureLoaded();
            if (recognizer is null)
            {
                return (null, 0);
            }

            using OnlineStream stream = recognizer.CreateStream();
            int chunkSamples = (int)(sampleRate * FeedChunkSeconds);
            foreach ((int start, int length) in StreamingChunks.Ranges(samples.Length, chunkSamples))
            {
                stream.AcceptWaveform(sampleRate, samples[start..(start + length)]);
                while (recognizer.IsReady(stream))
                {
                    recognizer.Decode(stream);
                }
            }

            var watch = Stopwatch.StartNew();
            // The 0.3 s tail padding the sherpa-onnx streaming-nemotron-c-api.c example feeds.
            stream.AcceptWaveform(sampleRate, new float[(int)(sampleRate * TailPaddingSeconds)]);
            stream.InputFinished();
            while (recognizer.IsReady(stream))
            {
                recognizer.Decode(stream);
            }

            string text = recognizer.GetResult(stream).Text.Trim();
            watch.Stop();
            return (text.Length == 0 ? null : text, watch.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "sherpa-onnx streaming transcription failed for model dir {Dir}", _modelDir);
            return (null, 0);
        }
    }

    private OnlineRecognizer? EnsureLoaded()
    {
        if (_recognizer is not null)
        {
            return _recognizer;
        }

        string encoder = FindModelFile("encoder");
        string decoder = FindModelFile("decoder");
        string joiner = FindModelFile("joiner");
        string tokens = Path.Combine(_modelDir, "tokens.txt");
        if (!File.Exists(encoder) || !File.Exists(decoder) || !File.Exists(joiner) || !File.Exists(tokens))
        {
            Log.LogError("sherpa-onnx streaming model dir {Dir} is missing encoder/decoder/joiner .onnx or tokens.txt", _modelDir);
            return null;
        }

        var config = new OnlineRecognizerConfig();
        config.ModelConfig.Transducer.Encoder = encoder;
        config.ModelConfig.Transducer.Decoder = decoder;
        config.ModelConfig.Transducer.Joiner = joiner;
        config.ModelConfig.Tokens = tokens;
        config.ModelConfig.NumThreads = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);
        config.ModelConfig.Provider = "cpu";
        config.DecodingMethod = "greedy_search";
        config.EnableEndpoint = 0;

        _recognizer = new OnlineRecognizer(config);
        Log.LogInformation("sherpa-onnx NeMo streaming transducer loaded from {Dir}", _modelDir);
        return _recognizer;
    }

    /// <summary>Prefers the int8-quantized file when both quantized and fp32 exports coexist.</summary>
    private string FindModelFile(string stem)
    {
        string int8 = Path.Combine(_modelDir, $"{stem}.int8.onnx");
        return File.Exists(int8) ? int8 : Path.Combine(_modelDir, $"{stem}.onnx");
    }

    public void Dispose()
    {
        _recognizer?.Dispose();
        _recognizer = null;
    }
}
