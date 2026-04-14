using System.Text;
using LMKit.Media.Audio;
using LMKit.Model;
using LMKit.Speech;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
// LM-Kit nests DeviceConfiguration inside the LM class; we don't need it explicitly here (Whisper
// models always use LM-Kit's auto-default device config), but the loading constructors take it.
using DeviceConfiguration = LMKit.Model.LM.DeviceConfiguration;

namespace Yaat.Client.Services;

/// <summary>
/// Wraps LM-Kit's <see cref="SpeechToText"/> for push-to-talk transcription. Takes 16 kHz mono
/// Float32 PCM samples (as emitted by <see cref="AudioCaptureService"/>), wraps them in a minimal
/// in-memory WAV container via <see cref="WavHeader.WritePcm16"/>, and feeds them to a
/// <see cref="SpeechToText"/> engine backed by an LM-Kit Whisper model.
///
/// The Whisper <see cref="LM"/> is loaded lazily on first <see cref="TranscribeAsync"/> call
/// using the model identifier configured in <see cref="UserPreferences.WhisperModelSize"/> (e.g.
/// <c>whisper-base</c>, <c>whisper-large-turbo3</c>). Subsequent calls reuse the same model
/// handle. The <see cref="SpeechToText"/> instance is also cached because it's a thin wrapper;
/// only the <see cref="SpeechToText.Prompt"/> property is updated per-call to reflect the current
/// biasing prompt (active callsigns + ATC vocabulary).
///
/// Returns null when:
/// - the input sample array is empty,
/// - the configured Whisper model identifier is missing or invalid,
/// - LM-Kit fails to load the model or throws during inference,
/// - the transcript is empty or matches the noise-marker heuristic.
/// </summary>
public sealed class WhisperSttEngine : IDisposable
{
    private static readonly ILogger Log = AppLog.CreateLogger<WhisperSttEngine>();

    private readonly UserPreferences _preferences;
    private readonly SemaphoreSlim _transcribeLock = new(1, 1);

    // Cache the LM (model weights + native handles) and the SpeechToText engine that wraps it.
    // Both are reused across PTT presses; only Prompt is updated per-call. The cache key is the
    // model identifier — when the user changes WhisperModelSize via Settings, the next call
    // detects the mismatch and rebuilds.
    private LM? _model;
    private SpeechToText? _stt;
    private string? _loadedModelSource;

    public WhisperSttEngine(UserPreferences preferences)
    {
        _preferences = preferences;
    }

    /// <summary>
    /// True when the configured Whisper model identifier is non-empty. We trust LM-Kit to
    /// resolve the identifier (curated ID like <c>whisper-base</c>, file path, or URI) at load
    /// time — if it's invalid, the load attempt fails and we return null from
    /// <see cref="TranscribeAsync"/>.
    /// </summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_preferences.WhisperModelSize);

    /// <summary>
    /// Transcribes a block of 16 kHz mono PCM Float32 samples using the configured Whisper model.
    /// <paramref name="initialPrompt"/> is a free-text hint that biases recognition toward
    /// expected words — typically the static ATC vocabulary (NATO alphabet, phonetic numbers,
    /// phraseology literals) plus any active scenario callsigns. Pass an empty string to skip
    /// biasing. Returns the concatenated transcript text, or null on empty audio, missing model,
    /// or noise-marker output. Whether the user has speech recognition enabled is the
    /// orchestrator's concern (<see cref="SpeechRecognitionService"/>) — this engine just
    /// transcribes what it's given.
    /// </summary>
    public async Task<string?> TranscribeAsync(float[] samples, string initialPrompt, CancellationToken ct)
    {
        if (samples.Length == 0 || !IsConfigured)
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
            var stt = EnsureLoaded();
            if (stt is null)
            {
                return null;
            }

            // Update the biasing prompt for this call. The Prompt property is mutable on
            // SpeechToText so we can flip it per PTT without rebuilding the engine. Empty string
            // disables biasing for this call.
            stt.Prompt = initialPrompt ?? string.Empty;

            // Convert float[] to in-memory WAV bytes via the existing WavHeader helper, then
            // construct a WaveFile for LM-Kit. We use the in-memory byte[] constructor (rather
            // than writing a temp file) so PTT latency stays under audio-duration overhead.
            var wavStream = WavHeader.WritePcm16(samples, AudioCaptureService.SampleRate);
            using var waveFile = new WaveFile(wavStream.ToArray());

            var sb = new StringBuilder();
            void OnSegment(object? _, SpeechToText.OnNewSegmentEventArgs e)
            {
                // Use Segment.Text, NOT Segment.ToString(). ToString() formats the segment with a
                // metadata prefix like "[00:00:00 → 00:00:04] (68.7%, lang=en)  <text>" which is
                // useful for the Scratch probe console dump but completely breaks downstream
                // parsing — the rule engine can't match a clause that starts with timestamps,
                // and the LLM under grammar constraint can't emit a valid command from it either.
                sb.Append(e.Segment.Text);
            }
            stt.OnNewSegment += OnSegment;
            try
            {
                _ = await stt.TranscribeAsync(waveFile, language: "en", ct).ConfigureAwait(false);
            }
            finally
            {
                stt.OnNewSegment -= OnSegment;
            }

            var text = sb.ToString().Trim();
            Log.LogDebug("Whisper transcript: {Text}", text);
            if (text.Length == 0 || IsNoiseMarker(text))
            {
                return null;
            }
            return text;
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

    /// <summary>
    /// Loads the Whisper model and runs a short silence transcription so the first real PTT
    /// press doesn't stall on multi-second model load + graph setup. Idempotent — subsequent
    /// calls return immediately when the model is already loaded for the current preferences.
    /// No-op when the model identifier is empty. The orchestrator (<see cref="SpeechRecognitionService"/>)
    /// decides whether prewarm should run at all based on the user's speech-enabled preference.
    /// Exceptions are logged and swallowed — prewarm failures must not crash startup.
    /// </summary>
    public async Task PrewarmAsync(CancellationToken ct)
    {
        if (!IsConfigured)
        {
            return;
        }

        try
        {
            await _transcribeLock.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            var stt = EnsureLoaded();
            if (stt is null)
            {
                return;
            }

            // Run a tiny silence transcription to prime the decoder graph + GPU kernels so the
            // first real PTT press has no measurable load overhead. Whisper emits no segments
            // on silence, so this is essentially pure graph warmup.
            var silence = new float[AudioCaptureService.SampleRate / 2]; // 0.5s
            var wavStream = WavHeader.WritePcm16(silence, AudioCaptureService.SampleRate);
            using var waveFile = new WaveFile(wavStream.ToArray());
            _ = await stt.TranscribeAsync(waveFile, language: "en", ct).ConfigureAwait(false);

            Log.LogInformation("Whisper prewarm complete");
        }
        catch (OperationCanceledException)
        {
            Log.LogInformation("Whisper prewarm cancelled");
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "Whisper prewarm failed; lazy-load will retry on first PTT");
        }
        finally
        {
            _transcribeLock.Release();
        }
    }

    /// <summary>
    /// Whisper emits literal placeholder tokens like <c>[BLANK_AUDIO]</c>, <c>[MUSIC]</c>,
    /// <c>[SOUND]</c>, <c>[INAUDIBLE]</c>, or <c>(silence)</c> when an audio segment contains
    /// no speech. These are informational markers from the decoder, not user utterances —
    /// treat them as empty transcripts so the mapping pipeline (rule engine + LLM fallback)
    /// doesn't waste cycles and log output on silence. Heuristic: a trimmed transcript that is
    /// entirely wrapped in matching brackets or parentheses is a marker, not speech. LM-Kit's
    /// SuppressHallucinations + SuppressNonSpeechTokens cover most of this internally, but we
    /// keep the filter as defence-in-depth in case a marker leaks through.
    /// </summary>
    private static bool IsNoiseMarker(string text)
    {
        if (text.Length < 2)
        {
            return false;
        }

        var first = text[0];
        var last = text[^1];
        return (first == '[' && last == ']') || (first == '(' && last == ')');
    }

    private SpeechToText? EnsureLoaded()
    {
        var source = _preferences.WhisperModelSize;
        if (string.IsNullOrWhiteSpace(source))
        {
            Log.LogWarning("Whisper model identifier is empty");
            return null;
        }

        if (_stt is not null && _loadedModelSource == source)
        {
            return _stt;
        }

        DisposeHandles();

        try
        {
            Log.LogInformation("Loading LM-Kit Whisper model {Source}", source);

            // Source dispatch matches LocalLlmService: rooted file path → file constructor;
            // http/https URI → URI constructor (auto-downloads); bare string → LoadFromModelID
            // (LM-Kit's curated catalog like "whisper-base", "whisper-large-turbo3"). Whisper
            // models use LM-Kit's auto-default DeviceConfiguration (null) — there's no
            // user-tunable layer count for STT, the engine picks based on available VRAM.
            DeviceConfiguration? deviceConfig = null;
            if (Path.IsPathRooted(source) && File.Exists(source))
            {
                _model = new LM(source, deviceConfig, loadingOptions: null, loadingProgress: null);
            }
            else if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            {
                _model = new LM(uri, storagePath: null, deviceConfig, loadingOptions: null, downloadingProgress: null, loadingProgress: null);
            }
            else
            {
                _model = LM.LoadFromModelID(
                    source,
                    storagePath: null,
                    deviceConfiguration: deviceConfig,
                    loadingOptions: null,
                    downloadingProgress: null,
                    loadingProgress: null
                );
            }

            // PTT mode: VAD off so Transcribe processes the entire captured buffer as one shot
            // (no chunking, no silence-gating). SuppressHallucinations + SuppressNonSpeechTokens
            // cut down on [BLANK_AUDIO] / [MUSIC] / repeated-token hallucinations the base
            // Whisper models are prone to.
            _stt = new SpeechToText(_model)
            {
                EnableVoiceActivityDetection = false,
                SuppressHallucinations = true,
                SuppressNonSpeechTokens = true,
            };
            _loadedModelSource = source;
            return _stt;
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Failed to load LM-Kit Whisper model {Source}", source);
            DisposeHandles();
            return null;
        }
    }

    private void DisposeHandles()
    {
        try
        {
            _model?.Dispose();
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "Error disposing LM-Kit Whisper model");
        }

        _stt = null;
        _model = null;
        _loadedModelSource = null;
    }

    public void Dispose()
    {
        DisposeHandles();
        _transcribeLock.Dispose();
    }
}

/// <summary>
/// Writes a minimal 44-byte RIFF/WAV header followed by 16-bit signed little-endian PCM samples
/// into a <see cref="MemoryStream"/>. Used by <see cref="WhisperSttEngine"/> to hand captured
/// PortAudio Float32 samples to LM-Kit via <see cref="LMKit.Media.Audio.WaveFile"/>.
///
/// LM-Kit's <see cref="LMKit.Media.Audio.WaveFile"/> accepts byte[] of a complete WAV file, so
/// we pack a header + int16 PCM in memory rather than writing a temp file. Standard int16 PCM
/// matches what whisper.cpp's own ffmpeg helper produces, so the conversion cost (one multiply +
/// clamp per sample) is negligible relative to inference and avoids any disk IO on the hot path.
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

    /// <summary>
    /// Inverse of <see cref="WritePcm16"/>: reads a minimal RIFF/WAV file (16-bit signed mono
    /// PCM, any sample rate) from disk and returns the samples as Float32 in the [-1, 1] range.
    /// Used by the speech sandbox tool to round-trip recorded clips across runs without needing
    /// a third-party audio library. Throws <see cref="InvalidDataException"/> if the file isn't
    /// a recognizable PCM mono WAV.
    /// </summary>
    public static float[] ReadPcm16(string path)
    {
        using var fs = File.OpenRead(path);
        using var reader = new BinaryReader(fs, Encoding.ASCII, leaveOpen: false);

        if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "RIFF")
        {
            throw new InvalidDataException($"Not a RIFF file: {path}");
        }
        _ = reader.ReadInt32(); // total chunk size — we don't trust it, just read until EOF
        if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "WAVE")
        {
            throw new InvalidDataException($"Not a WAVE file: {path}");
        }

        // Walk subchunks until we find both fmt and data. Real-world WAVs sometimes have extra
        // chunks (LIST, INFO) between fmt and data, so don't assume layout.
        ushort channels = 0;
        ushort bitsPerSample = 0;
        ushort formatTag = 0;
        byte[]? pcmBytes = null;

        while (fs.Position < fs.Length - 8)
        {
            var chunkId = Encoding.ASCII.GetString(reader.ReadBytes(4));
            var chunkSize = reader.ReadInt32();
            if (chunkId == "fmt ")
            {
                formatTag = reader.ReadUInt16();
                channels = reader.ReadUInt16();
                _ = reader.ReadInt32(); // sample rate
                _ = reader.ReadInt32(); // byte rate
                _ = reader.ReadUInt16(); // block align
                bitsPerSample = reader.ReadUInt16();
                // Skip any extra fmt bytes (extensible WAVs).
                if (chunkSize > 16)
                {
                    fs.Seek(chunkSize - 16, SeekOrigin.Current);
                }
            }
            else if (chunkId == "data")
            {
                pcmBytes = reader.ReadBytes(chunkSize);
            }
            else
            {
                // Unknown chunk — skip it. Chunks are word-aligned, so round up odd sizes.
                fs.Seek(chunkSize + (chunkSize & 1), SeekOrigin.Current);
            }
        }

        if (pcmBytes is null || formatTag == 0)
        {
            throw new InvalidDataException($"WAV is missing fmt or data chunk: {path}");
        }
        if (formatTag != 1 || channels != 1 || bitsPerSample != 16)
        {
            throw new InvalidDataException($"WAV must be PCM mono 16-bit, got format={formatTag} channels={channels} bits={bitsPerSample}: {path}");
        }

        var sampleCount = pcmBytes.Length / sizeof(short);
        var samples = new float[sampleCount];
        for (var i = 0; i < sampleCount; i++)
        {
            // Read little-endian int16 and rescale to [-1, 1]. Symmetric inverse of the
            // multiply-by-short.MaxValue + clamp in WritePcm16.
            var lo = pcmBytes[i * 2];
            var hi = (sbyte)pcmBytes[i * 2 + 1];
            var value = (short)((hi << 8) | lo);
            samples[i] = value / (float)short.MaxValue;
        }
        return samples;
    }
}
