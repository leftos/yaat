using System.Security.Cryptography;
using System.Text;
using Yaat.Client.Services;

namespace Yaat.SpeechSandbox;

/// <summary>Who speaks in a corpus segment, read from the segment id's ending.</summary>
public enum CorpusRole
{
    Pilot,
    Controller,
    Mixed,
    Unknown,
}

/// <summary>One utterance of a word error rate corpus: 16 kHz mono samples and the human transcript.</summary>
public sealed record CorpusSegment(string Id, CorpusRole Role, float[] Samples16k, string Reference);

/// <summary>Role lookup and audio conversion shared by the corpus loaders.</summary>
public static class CorpusRoles
{
    private const int WhisperSampleRate = 16000;

    /// <summary>
    /// The role an id's ending names: <c>PIAT</c> mixed, <c>_PI</c> pilot, <c>_AT</c> controller,
    /// anything else unknown. <c>PIAT</c> is checked first, since it also ends a pilot-looking id.
    /// </summary>
    public static CorpusRole FromId(string id)
    {
        if (id.EndsWith("PIAT", StringComparison.Ordinal))
        {
            return CorpusRole.Mixed;
        }
        if (id.EndsWith("_PI", StringComparison.Ordinal))
        {
            return CorpusRole.Pilot;
        }
        return id.EndsWith("_AT", StringComparison.Ordinal) ? CorpusRole.Controller : CorpusRole.Unknown;
    }

    /// <summary>The lowercase label a role carries in results and reports.</summary>
    public static string Label(CorpusRole role) =>
        role switch
        {
            CorpusRole.Pilot => "pilot",
            CorpusRole.Controller => "controller",
            CorpusRole.Mixed => "mixed",
            _ => "unknown",
        };

    /// <summary>
    /// Resamples decoded audio to the 16 kHz Whisper expects; 16 kHz audio comes back unchanged. The
    /// resampler only interpolates, so a rate above 16 kHz (which would alias) or a non-positive one
    /// throws <see cref="InvalidDataException"/> naming <paramref name="id"/>.
    /// </summary>
    public static float[] To16k(Pcm16Audio audio, string id)
    {
        if ((audio.SampleRate <= 0) || (audio.SampleRate > WhisperSampleRate))
        {
            string problem = $"{id}: sample rate {audio.SampleRate} Hz is not supported";
            throw new InvalidDataException($"{problem}; resample to 16 kHz or less first; anti-aliased resampling is YAAT-1559.");
        }
        return PiperSynthesizer.Resample(audio.Samples, audio.SampleRate, WhisperSampleRate);
    }
}

/// <summary>Seeded, reproducible choice of a subset of segment ids.</summary>
public static class CorpusSubset
{
    /// <summary>
    /// Sorts <paramref name="ids"/> ordinally, shuffles them (Fisher-Yates, <c>new Random(seed)</c>),
    /// keeps the first <paramref name="count"/> (every id when null or not below the id count) and
    /// returns them sorted ordinally again, so the same seed always picks the same ids in the same order.
    /// </summary>
    public static IReadOnlyList<string> Select(IReadOnlyList<string> ids, int? count, int seed)
    {
        string[] shuffled = [.. ids.Order(StringComparer.Ordinal)];
        var random = new Random(seed);
        for (int i = shuffled.Length - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
        }
        int take = ((count is int n) && (n < shuffled.Length)) ? n : shuffled.Length;
        return [.. shuffled.Take(take).Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// A lowercase hex SHA-256 identifying which segments a run drew: the UTF-8 bytes of the ids,
    /// sorted ordinally and joined with <c>\n</c>.
    /// </summary>
    public static string Fingerprint(IEnumerable<string> ids)
    {
        string joined = string.Join('\n', ids.Order(StringComparer.Ordinal));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(joined)));
    }
}
