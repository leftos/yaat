using System.Security.Cryptography;
using System.Text;
using Yaat.Client.Services;

namespace Yaat.SpeechSandbox;

/// <summary>
/// A word error rate corpus kept as a plain folder: every <c>&lt;id&gt;.wav</c> directly in it (any
/// sample rate, PCM16 mono) beside an <c>&lt;id&gt;.txt</c> holding its UTF-8 transcript.
/// </summary>
public static class LocalCorpus
{
    /// <summary>
    /// Loads the segments <see cref="CorpusSubset.Select"/> picks, in id order, resampled to 16 kHz.
    /// Throws <see cref="InvalidDataException"/> on a folder with no WAVs and
    /// <see cref="FileNotFoundException"/> when any WAV lacks its transcript.
    /// </summary>
    public static IReadOnlyList<CorpusSegment> Load(string dir, int? subset, int seed)
    {
        Dictionary<string, string> wavById = PairedWavs(dir);
        IReadOnlyList<string> ids = CorpusSubset.Select([.. wavById.Keys], subset, seed);
        return [.. ids.Select(id => ToSegment(id, wavById[id]))];
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

    private static CorpusSegment ToSegment(string id, string wavPath)
    {
        float[] samples = CorpusRoles.To16k(WavHeader.ReadPcm16WithRate(wavPath), id);
        return new CorpusSegment(id, CorpusRoles.FromId(id), samples, ReadReference(wavPath));
    }

    private static string ReadReference(string wavPath) => File.ReadAllText(TranscriptPath(wavPath), Encoding.UTF8).Trim();

    private static string TranscriptPath(string wavPath) => Path.ChangeExtension(wavPath, ".txt");
}
