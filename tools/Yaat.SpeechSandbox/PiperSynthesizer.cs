using NAudio.Wave;
using SherpaOnnx;

namespace Yaat.SpeechSandbox;

/// <summary>
/// Headless Piper VITS synthesizer extracted from <see cref="TtsSandboxView"/>. No Avalonia,
/// no playback, no radio FX — just <c>text → (float[] samples, int sampleRate)</c>. Used by the
/// ouroboros harness to deterministically render pilot readbacks to audio for round-trip STT
/// testing.
///
/// Voice pack lookup mirrors the sandbox: walks up from <see cref="AppContext.BaseDirectory"/>
/// for <c>.tmp/voices/vits-piper-en_US-libritts_r-medium/</c>. Override via the constructor.
/// Caller owns lifetime — wrap in <c>using</c>.
/// </summary>
public sealed class PiperSynthesizer : IDisposable
{
    private const string DefaultVoiceRelative = ".tmp/voices/vits-piper-en_US-libritts_r-medium";

    private readonly OfflineTts _tts;
    public string VoiceDir { get; }
    public int SampleRate => _tts.SampleRate;

    public PiperSynthesizer(string voiceDir)
    {
        VoiceDir = voiceDir;
        var dirInfo = new DirectoryInfo(voiceDir);
        if (!dirInfo.Exists)
        {
            throw new DirectoryNotFoundException($"Piper voice dir not found: {voiceDir}");
        }
        var onnxFile = dirInfo.GetFiles("*.onnx").FirstOrDefault() ?? throw new FileNotFoundException($"No .onnx file in voice dir: {voiceDir}");
        var tokensPath = Path.Combine(voiceDir, "tokens.txt");
        if (!File.Exists(tokensPath))
        {
            throw new FileNotFoundException($"tokens.txt missing in voice dir: {voiceDir}");
        }
        var dataDir = Path.Combine(voiceDir, "espeak-ng-data");

        var config = new OfflineTtsConfig();
        config.Model.Vits.Model = onnxFile.FullName;
        config.Model.Vits.Tokens = tokensPath;
        config.Model.Vits.DataDir = dataDir;
        config.Model.Vits.LengthScale = 1.0f;
        config.Model.NumThreads = 2;
        config.Model.Provider = "cpu";
        config.Model.Debug = 0;
        _tts = new OfflineTts(config);
    }

    public void Dispose() => _tts.Dispose();

    /// <summary>
    /// Locate the default Piper voice pack. Searches, in order:
    /// (1) walks up from the executable directory looking for <c>.tmp/voices/...</c> (dev layout);
    /// (2) <c>%LOCALAPPDATA%/yaat/voices/vits-piper-en_US-libritts_r-medium/</c> (the location the
    /// YAAT client installs to via Settings → Speech → TTS).
    /// Returns null if not found — caller should point the user at Settings → Speech → TTS to
    /// install it.
    /// </summary>
    public static string? ResolveDefaultVoiceDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, DefaultVoiceRelative);
            if (IsValidVoiceDir(candidate))
            {
                return candidate;
            }
            dir = dir.Parent;
        }

        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "yaat",
            "voices",
            "vits-piper-en_US-libritts_r-medium"
        );
        return IsValidVoiceDir(appData) ? appData : null;
    }

    private static bool IsValidVoiceDir(string path) => Directory.Exists(path) && new DirectoryInfo(path).GetFiles("*.onnx").Length > 0;

    public readonly record struct SynthResult(float[] Samples, int SampleRate, int LatencyMs);

    /// <summary>
    /// Synthesize <paramref name="text"/> with the given speaker id and speed. Returns the
    /// raw Float32 samples at the model's native sample rate (22050 Hz for LibriTTS-R medium).
    /// </summary>
    public SynthResult Synthesize(string text, int speakerId, float speed = 1.0f)
    {
        var gen = new OfflineTtsGenerationConfig { Sid = speakerId, Speed = speed };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var audio = _tts.GenerateWithConfig(text, gen, null);
        sw.Stop();
        return new SynthResult(audio.Samples, _tts.SampleRate, (int)sw.ElapsedMilliseconds);
    }

    /// <summary>
    /// Linear-interpolation resampler from <paramref name="srcRate"/> to <paramref name="dstRate"/>.
    /// Good enough for ATC speech (no pitch-critical content); deterministic and dependency-free.
    /// When rates already match, returns the input array (no copy).
    /// </summary>
    public static float[] Resample(float[] samples, int srcRate, int dstRate)
    {
        if (srcRate == dstRate || samples.Length == 0)
        {
            return samples;
        }
        var ratio = (double)srcRate / dstRate;
        var outLength = (int)(samples.Length / ratio);
        var output = new float[outLength];
        for (int i = 0; i < outLength; i++)
        {
            var srcPos = i * ratio;
            var idx = (int)srcPos;
            var frac = srcPos - idx;
            var a = samples[idx];
            var b = idx + 1 < samples.Length ? samples[idx + 1] : a;
            output[i] = (float)(a + (b - a) * frac);
        }
        return output;
    }

    /// <summary>
    /// Write Float32 mono samples to a WAV file using NAudio's IEEE-float format. Used for
    /// preserving the unmodified, high-fidelity synth output so a human can listen back when
    /// a round-trip case fails. The Whisper-bound resampled-to-16kHz copy is fed directly to
    /// <c>WhisperSttEngine</c> as a <c>float[]</c> — we do not need to write that one.
    /// </summary>
    public static void WriteWavFloat(string path, float[] samples, int sampleRate)
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);
        using var writer = new WaveFileWriter(path, format);
        writer.WriteSamples(samples, 0, samples.Length);
    }
}
