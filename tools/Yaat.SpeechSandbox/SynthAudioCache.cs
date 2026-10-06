using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Yaat.Sim;

namespace Yaat.SpeechSandbox;

/// <summary>Every input that changes the rendered samples, so a cache key changes when any one does.</summary>
public sealed record SynthAudioKeyInputs
{
    /// <summary>Voice-pack identity from <see cref="PiperSynthesizer.VoiceIdentity"/>.</summary>
    public required string VoiceIdentity { get; init; }

    /// <summary>The exact text spoken.</summary>
    public required string Text { get; init; }

    /// <summary>Piper multi-speaker id.</summary>
    public required int SpeakerId { get; init; }

    /// <summary>Piper speaking-rate multiplier.</summary>
    public required float Speed { get; init; }

    /// <summary>VITS length scale the synthesizer is configured with.</summary>
    public required float LengthScale { get; init; }

    /// <summary>Sample rate of the cached (resampled and padded) samples.</summary>
    public required int SampleRate { get; init; }

    /// <summary>Leading silence padding in milliseconds.</summary>
    public required int LeadingSilenceMs { get; init; }

    /// <summary>Trailing silence padding in milliseconds.</summary>
    public required int TrailingSilenceMs { get; init; }
}

/// <summary>
/// Content-addressed cache of synthesized Piper audio. Piper's sampling differs between processes —
/// the same text, speaker and speed render different samples on every run — so the synthetic corpus
/// pins each case's audio with this cache. Keying each rendered sample set by its exact synthesis
/// inputs makes a seed render byte-identical WAVs every time.
///
/// The key (<see cref="ComputeKey"/>) is a SHA-256 hex over the pipeline version, the voice-pack
/// identity and the synthesis inputs. Entries are IEEE-float mono WAVs under the cache root;
/// <see cref="PutOrGetExisting"/> writes a temp file and renames it into place so a crash never
/// leaves a partial entry and the first writer wins, while <see cref="TryGet"/> reports a corrupt
/// entry as a logged, deleted miss rather than crashing the run.
/// </summary>
/// <param name="root">Directory the entries are written under (tests point it at a temp directory).</param>
public sealed class SynthAudioCache(string root)
{
    /// <summary>Bump when the synthesize → resample → pad pipeline changes, so stale entries miss.</summary>
    public const string PipelineVersion = "1";

    private static readonly ILogger Log = SimLog.CreateLogger("SynthAudioCache");

    private const int WavHeaderBytes = 44;
    private const int FmtChunkBytes = 16;
    private const ushort IeeeFloatFormat = 3;
    private const int BitsPerSample = 32;
    private const int MaxRenameAttempts = 8;
    private const int RenameRetryStepMs = 25;

    /// <summary>The per-user cache directory the sandbox uses by default.</summary>
    public static string DefaultRoot => YaatPaths.Combine("cache", "synth-audio");

    /// <summary>
    /// SHA-256 hex over every input that changes the rendered samples. Fields are length-prefixed so
    /// no text can collide with a neighbouring field.
    /// </summary>
    public static string ComputeKey(SynthAudioKeyInputs inputs)
    {
        var material = new StringBuilder();
        AppendField(material, PipelineVersion);
        AppendField(material, inputs.VoiceIdentity);
        AppendField(material, inputs.Text);
        AppendField(material, inputs.SpeakerId.ToString(CultureInfo.InvariantCulture));
        AppendField(material, inputs.Speed.ToString("R", CultureInfo.InvariantCulture));
        AppendField(material, inputs.LengthScale.ToString("R", CultureInfo.InvariantCulture));
        AppendField(material, inputs.SampleRate.ToString(CultureInfo.InvariantCulture));
        AppendField(material, inputs.LeadingSilenceMs.ToString(CultureInfo.InvariantCulture));
        AppendField(material, inputs.TrailingSilenceMs.ToString(CultureInfo.InvariantCulture));
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(material.ToString()));
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    private static void AppendField(StringBuilder material, string field) => material.Append(field.Length).Append(':').Append(field).Append('\n');

    /// <summary>Returns the cached samples for <paramref name="key"/>, or false when absent or unreadable.</summary>
    public bool TryGet(string key, out float[] samples)
    {
        string path = PathFor(key);
        if (!File.Exists(path))
        {
            samples = [];
            return false;
        }

        try
        {
            samples = ReadWav(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ReportUnreadable(path, ex, corruptConsequence: "re-synthesizing", ioConsequence: "treating it as a miss");
            samples = [];
            return false;
        }
    }

    /// <summary>
    /// Stores <paramref name="samples"/> under <paramref name="key"/> and returns the samples the
    /// cache now holds. The first writer wins: a write to an existing entry leaves the stored samples
    /// untouched and returns them instead.
    /// </summary>
    public float[] PutOrGetExisting(string key, float[] samples, int sampleRate)
    {
        Directory.CreateDirectory(root);
        string path = PathFor(key);
        string tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllBytes(tempPath, BuildWav(samples, sampleRate));
        try
        {
            MoveIntoPlace(tempPath, path);
            return samples;
        }
        catch (IOException)
        {
            Log.LogDebug("Synth-audio cache entry {Path} already exists; keeping the stored samples", path);
            return ReadExistingOrFallBack(path, samples);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                TryDelete(tempPath);
            }
        }
    }

    private string PathFor(string key) => Path.Combine(root, key + ".wav");

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.LogWarning(ex, "Failed to delete unreadable synth-audio cache entry {Path}", path);
        }
    }

    /// <summary>Renames the temp file onto the entry, retrying a transient lock while the entry is absent.</summary>
    private static void MoveIntoPlace(string tempPath, string path)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(tempPath, path);
                return;
            }
            catch (Exception ex) when ((ex is UnauthorizedAccessException or IOException) && (attempt < MaxRenameAttempts) && !File.Exists(path))
            {
                Log.LogDebug(ex, "Writing synth-audio cache entry {Path} failed on attempt {Attempt}; retrying", path, attempt);
                Thread.Sleep(RenameRetryStepMs * attempt);
            }
        }
    }

    /// <summary>
    /// Reads the entry that won the write race. On a read failure it applies the same policy as
    /// <see cref="TryGet"/> — log, warn on stderr, delete only a corrupt entry — and returns the
    /// synthesized samples instead, so an unreadable entry can never end a corpus run.
    /// </summary>
    private static float[] ReadExistingOrFallBack(string path, float[] fallback)
    {
        try
        {
            return ReadWav(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ReportUnreadable(path, ex, corruptConsequence: "using the synthesized samples", ioConsequence: "using the synthesized samples");
            return fallback;
        }
    }

    /// <summary>
    /// Reports an entry that could not be read: a warning to the log and a <c>WARN:</c> line to
    /// stderr. A format corruption is deleted — it can only be replaced — while a transient I/O error
    /// leaves the entry in place for the next run.
    /// </summary>
    /// <param name="path">The entry that could not be read.</param>
    /// <param name="ex">The read failure.</param>
    /// <param name="corruptConsequence">What the caller does instead when the entry's format is corrupt.</param>
    /// <param name="ioConsequence">What the caller does instead when the entry is only unreadable right now.</param>
    private static void ReportUnreadable(string path, Exception ex, string corruptConsequence, string ioConsequence)
    {
        if (ex is InvalidDataException or EndOfStreamException)
        {
            Log.LogWarning(ex, "Synth-audio cache entry {Path} is not a readable WAV; deleting it and {Consequence}", path, corruptConsequence);
            Console.Error.WriteLine($"WARN: {path} is not a readable synth-audio cache entry; deleting it and {corruptConsequence}");
            TryDelete(path);
            return;
        }

        Log.LogWarning(ex, "Synth-audio cache entry {Path} could not be read; {Consequence}", path, ioConsequence);
        Console.Error.WriteLine($"WARN: {path} could not be read; {ioConsequence}");
    }

    private static byte[] BuildWav(float[] samples, int sampleRate)
    {
        const int channels = 1;
        int blockAlign = channels * (BitsPerSample / 8);
        int dataSize = samples.Length * sizeof(float);
        byte[] bytes = new byte[WavHeaderBytes + dataSize];
        using var stream = new MemoryStream(bytes);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);

        writer.Write("RIFF"u8.ToArray());
        writer.Write(36 + dataSize);
        writer.Write("WAVE"u8.ToArray());

        writer.Write("fmt "u8.ToArray());
        writer.Write(FmtChunkBytes);
        writer.Write(IeeeFloatFormat);
        writer.Write((ushort)channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * blockAlign);
        writer.Write((ushort)blockAlign);
        writer.Write((ushort)BitsPerSample);

        writer.Write("data"u8.ToArray());
        writer.Write(dataSize);
        foreach (float sample in samples)
        {
            writer.Write(sample);
        }
        writer.Flush();
        return bytes;
    }

    private static float[] ReadWav(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.Length < WavHeaderBytes)
        {
            throw new InvalidDataException($"Synth-audio cache entry is shorter than a WAV header: {path}");
        }

        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
        RequireChunk(reader, "RIFF", path);
        _ = reader.ReadInt32();
        RequireChunk(reader, "WAVE", path);
        RequireChunk(reader, "fmt ", path);
        int fmtSize = reader.ReadInt32();
        if ((fmtSize < FmtChunkBytes) || (fmtSize > RemainingBytes(stream, bytes)))
        {
            throw new InvalidDataException($"Synth-audio cache entry declares an implausible fmt size {fmtSize}: {path}");
        }
        ushort formatTag = reader.ReadUInt16();
        ushort channels = reader.ReadUInt16();
        _ = reader.ReadInt32();
        _ = reader.ReadInt32();
        _ = reader.ReadUInt16();
        ushort bitsPerSample = reader.ReadUInt16();
        if (fmtSize > FmtChunkBytes)
        {
            reader.ReadBytes(fmtSize - FmtChunkBytes);
        }
        RequireChunk(reader, "data", path);
        int dataSize = reader.ReadInt32();

        int remaining = RemainingBytes(stream, bytes);
        bool wellFormed =
            (formatTag == IeeeFloatFormat)
            && (channels == 1)
            && (bitsPerSample == BitsPerSample)
            && (dataSize == remaining)
            && ((dataSize % sizeof(float)) == 0);
        if (!wellFormed)
        {
            throw new InvalidDataException($"Synth-audio cache entry is not a well-formed IEEE-float mono WAV: {path}");
        }

        float[] samples = new float[dataSize / sizeof(float)];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = reader.ReadSingle();
        }
        return samples;
    }

    private static int RemainingBytes(MemoryStream stream, byte[] bytes) => bytes.Length - (int)stream.Position;

    private static void RequireChunk(BinaryReader reader, string expected, string path)
    {
        if (Encoding.ASCII.GetString(reader.ReadBytes(expected.Length)) != expected)
        {
            throw new InvalidDataException($"Synth-audio cache entry is missing its {expected} chunk: {path}");
        }
    }
}
