using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Yaat.Client.Services;

namespace Yaat.SpeechSandbox;

/// <summary>
/// A word error rate corpus kept as a plain folder: every <c>&lt;id&gt;.wav</c> directly in it (any
/// sample rate, PCM16 mono) beside an <c>&lt;id&gt;.txt</c> holding its UTF-8 transcript, and optionally
/// a <c>decisions.json</c> mapping each id to who checked its transcript.
/// </summary>
public static class LocalCorpus
{
    private const string DecisionsFileName = "decisions.json";

    /// <summary>
    /// Loads the segments <see cref="CorpusSubset.Select"/> picks, in id order, resampled to 16 kHz. Their
    /// feed comes from the id and their provenance from the folder's <c>decisions.json</c> when there is
    /// one. Throws <see cref="InvalidDataException"/> on a folder with no WAVs, on a decision that is not
    /// <c>owner</c>, <c>agreed</c> or <c>claude</c>, and on a loaded clip with no decision, and
    /// <see cref="FileNotFoundException"/> when any WAV lacks its transcript.
    /// </summary>
    public static IReadOnlyList<CorpusSegment> Load(string dir, int? subset, int seed)
    {
        Dictionary<string, string> wavById = PairedWavs(dir);
        IReadOnlyList<string> ids = CorpusSubset.Select([.. wavById.Keys], subset, seed);
        Dictionary<string, string>? decisions = ReadDecisions(dir);
        return [.. ids.Select(id => ToSegment(id, wavById[id], Provenance(decisions, id, dir)))];
    }

    /// <summary>
    /// A lowercase hex SHA-256 identifying the whole folder (before any subset): for each id in
    /// ordinal order, the UTF-8 bytes of <c>id + "\n" + reference + "\n"</c> followed by the raw
    /// SHA-256 digest of that WAV file's bytes.
    /// </summary>
    public static string Fingerprint(string dir)
    {
        Dictionary<string, string> wavById = PairedWavs(dir);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string id in wavById.Keys.Order(StringComparer.Ordinal))
        {
            string wavPath = wavById[id];
            hash.AppendData(Encoding.UTF8.GetBytes(id + "\n" + ReadReference(wavPath) + "\n"));
            hash.AppendData(SHA256.HashData(File.ReadAllBytes(wavPath)));
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>Every WAV directly in <paramref name="dir"/> by id, each checked to have its transcript beside it.</summary>
    private static Dictionary<string, string> PairedWavs(string dir)
    {
        string[] wavs =
        [
            .. Directory
                .EnumerateFiles(dir, "*.wav", SearchOption.TopDirectoryOnly)
                .Where(p => string.Equals(Path.GetExtension(p), ".wav", StringComparison.OrdinalIgnoreCase)),
        ];
        if (wavs.Length == 0)
        {
            throw new InvalidDataException($"No .wav files directly in {dir}.");
        }
        foreach (string wav in wavs)
        {
            string txt = TranscriptPath(wav);
            if (!File.Exists(txt))
            {
                throw new FileNotFoundException($"No transcript {txt} beside {wav}.", txt);
            }
        }
        return wavs.ToDictionary(p => Path.GetFileNameWithoutExtension(p), StringComparer.Ordinal);
    }

    private static CorpusSegment ToSegment(string id, string wavPath, string? provenance)
    {
        float[] samples = CorpusRoles.To16k(WavHeader.ReadPcm16WithRate(wavPath), id);
        return new CorpusSegment(id, CorpusRoles.FromId(id), samples, ReadReference(wavPath), CorpusRoles.FeedFromId(id), provenance);
    }

    /// <summary>The folder's decisions by id, or null when it carries no decisions file.</summary>
    private static Dictionary<string, string>? ReadDecisions(string dir)
    {
        string path = Path.Combine(dir, DecisionsFileName);
        if (!File.Exists(path))
        {
            return null;
        }
        Dictionary<string, string> decisions =
            JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path, Encoding.UTF8))
            ?? throw new InvalidDataException($"{path} is empty or not a JSON object of id to provenance.");
        return new Dictionary<string, string>(decisions, StringComparer.Ordinal);
    }

    /// <summary>One clip's provenance: null without a decisions file, the checked value when there is one.</summary>
    private static string? Provenance(Dictionary<string, string>? decisions, string id, string dir)
    {
        if (decisions is null)
        {
            return null;
        }
        string path = Path.Combine(dir, DecisionsFileName);
        if (!decisions.TryGetValue(id, out string? value))
        {
            throw new InvalidDataException($"{id}: no decision in {path}.");
        }
        if (value is not ("owner" or "agreed" or "claude"))
        {
            throw new InvalidDataException($"{id}: {path} says '{value}', which is not owner, agreed or claude.");
        }
        return value;
    }

    private static string ReadReference(string wavPath) => File.ReadAllText(TranscriptPath(wavPath), Encoding.UTF8).Trim();

    private static string TranscriptPath(string wavPath) => Path.ChangeExtension(wavPath, ".txt");
}
