using System.Text;
using Microsoft.Extensions.Logging;
using Whisper.net;
using Yaat.Client.Logging;

namespace Yaat.Client.Services;

/// <summary>
/// Wraps Whisper.net for push-to-talk transcription. Takes 16 kHz mono Float32 PCM samples
/// (as emitted by <see cref="AudioCaptureService"/>), wraps them in a minimal in-memory WAV
/// container, and feeds them to a <see cref="WhisperProcessor"/>.
///
/// The <see cref="WhisperFactory"/> is loaded lazily on first <see cref="TranscribeAsync"/> call
/// using whatever Whisper model file <see cref="ModelManager"/> points at (based on
/// <see cref="UserPreferences.WhisperModelSize"/>). Subsequent calls reuse the same factory.
///
/// Processors are created per-call with the current initial prompt hints so the pipeline's context
/// (active callsigns, programmed fixes) can change between PTT presses without rebuilding the model.
///
/// Returns null when:
/// - speech recognition is disabled,
/// - the configured Whisper model file is missing,
/// - the input sample array is empty,
/// - Whisper.net fails to load the model or throws during inference.
/// </summary>
public sealed class WhisperSttEngine : IDisposable
{
    private static readonly ILogger Log = AppLog.CreateLogger<WhisperSttEngine>();

    private readonly UserPreferences _preferences;
    private readonly ModelManager _modelManager;
    private readonly SemaphoreSlim _transcribeLock = new(1, 1);

    private WhisperFactory? _factory;
    private string? _loadedModelPath;

    public WhisperSttEngine(UserPreferences preferences, ModelManager modelManager)
    {
        _preferences = preferences;
        _modelManager = modelManager;
    }

    /// <summary>
    /// True when the configured Whisper model file exists on disk. Does not imply the factory is
    /// loaded yet — that happens lazily on first <see cref="TranscribeAsync"/> call.
    /// </summary>
    public bool IsConfigured => File.Exists(_modelManager.GetWhisperPath(_preferences.WhisperModelSize));

    /// <summary>
    /// Transcribes a block of 16 kHz mono PCM Float32 samples using the configured Whisper model.
    /// <paramref name="initialPrompt"/> is a free-text hint that gets prepended to the Whisper
    /// decoder to bias recognition toward expected words — typically active callsigns and
    /// programmed fix names. Pass an empty string to skip.
    /// Returns the concatenated transcript text, or null on failure / no audio / disabled.
    /// </summary>
    public async Task<string?> TranscribeAsync(float[] samples, string initialPrompt, CancellationToken ct)
    {
        if (!_preferences.SpeechEnabled || samples.Length == 0 || !IsConfigured)
        {
            return null;
        }

        try
        {
            await _transcribeLock.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        try
        {
            var factory = EnsureLoaded();
            if (factory is null)
            {
                return null;
            }

            var builder = factory.CreateBuilder().WithLanguage("en");
            if (!string.IsNullOrWhiteSpace(initialPrompt))
            {
                builder = builder.WithPrompt(initialPrompt);
            }

            using var processor = builder.Build();
            using var wavStream = WavHeader.WritePcm16(samples, AudioCaptureService.SampleRate);

            var sb = new StringBuilder();
            await foreach (var segment in processor.ProcessAsync(wavStream, ct).ConfigureAwait(false))
            {
                sb.Append(segment.Text);
            }

            var text = sb.ToString().Trim();
            Log.LogDebug("Whisper transcript: {Text}", text);
            return text.Length == 0 ? null : text;
        }
        catch (OperationCanceledException)
        {
            Log.LogInformation("Whisper transcription cancelled");
            throw;
        }
        catch (Exception ex)
        {
            // Previously this was swallowed and returned null, which SpeechRecognitionService
            // mis-interpreted as "empty transcript" — the session never made it into the debug
            // history and the mic status went back to Idle instead of Error. Propagate the
            // exception so the pipeline records the failure visibly.
            Log.LogError(ex, "Whisper transcription failed");
            throw;
        }
        finally
        {
            _transcribeLock.Release();
        }
    }

    private WhisperFactory? EnsureLoaded()
    {
        var path = _modelManager.GetWhisperPath(_preferences.WhisperModelSize);
        if (!File.Exists(path))
        {
            Log.LogWarning("Whisper model not found at {Path}", path);
            return null;
        }

        if (_factory is not null && _loadedModelPath == path)
        {
            return _factory;
        }

        DisposeFactory();

        try
        {
            Log.LogInformation("Loading Whisper model from {Path}", path);
            _factory = WhisperFactory.FromPath(path);
            _loadedModelPath = path;
            return _factory;
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Failed to load Whisper model from {Path}", path);
            DisposeFactory();
            return null;
        }
    }

    private void DisposeFactory()
    {
        try
        {
            _factory?.Dispose();
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "Error disposing WhisperFactory");
        }

        _factory = null;
        _loadedModelPath = null;
    }

    public void Dispose()
    {
        DisposeFactory();
        _transcribeLock.Dispose();
    }
}

/// <summary>
/// Writes a minimal 44-byte RIFF/WAV header followed by 16-bit signed little-endian PCM samples
/// into a <see cref="MemoryStream"/>. Used by <see cref="WhisperSttEngine"/> to hand captured
/// PortAudio Float32 samples to Whisper.net via <c>ProcessAsync</c>.
///
/// Whisper.net's <c>WaveParser</c> is strict about the WAV format it accepts — IEEE Float32
/// streams throw <c>CorruptedWaveException</c> on parse. Writing standard int16 PCM is the
/// universally-supported path and matches what whisper.cpp's own ffmpeg helper produces, so the
/// conversion cost (one multiply + clamp per sample) is worth the compatibility.
/// </summary>
internal static class WavHeader
{
    public static MemoryStream WritePcm16(float[] samples, int sampleRate)
    {
        const int channels = 1;
        const int bitsPerSample = 16;
        const ushort formatPcm = 1; // WAVE_FORMAT_PCM
        var byteRate = sampleRate * channels * (bitsPerSample / 8);
        var blockAlign = (ushort)(channels * (bitsPerSample / 8));
        var dataSize = samples.Length * sizeof(short);

        var stream = new MemoryStream(44 + dataSize);
        var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);

        // RIFF header
        writer.Write("RIFF"u8.ToArray());
        writer.Write(36 + dataSize); // total chunk size = header (36 bytes after "RIFF<size>") + data
        writer.Write("WAVE"u8.ToArray());

        // fmt subchunk (16 bytes, PCM)
        writer.Write("fmt "u8.ToArray());
        writer.Write(16);
        writer.Write(formatPcm);
        writer.Write((ushort)channels);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write(blockAlign);
        writer.Write((ushort)bitsPerSample);

        // data subchunk
        writer.Write("data"u8.ToArray());
        writer.Write(dataSize);

        // Convert Float32 [-1.0, 1.0] → Int16 [-32767, 32767], clamping to avoid wrap on overflow.
        // short.MaxValue (32767) is used rather than 32768 because the negative extreme of int16
        // is -32768 and multiplying -1.0 by 32768 would overflow when stored back as short.
        for (int i = 0; i < samples.Length; i++)
        {
            var clamped = Math.Clamp(samples[i], -1.0f, 1.0f);
            writer.Write((short)(clamped * short.MaxValue));
        }

        writer.Flush();
        stream.Position = 0;
        return stream;
    }
}
