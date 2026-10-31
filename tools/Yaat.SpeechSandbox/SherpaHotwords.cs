using System.Globalization;

namespace Yaat.SpeechSandbox;

/// <summary>How <c>--hotword-case</c> rewrites a phrase before it reaches the recognizer's BPE encoder.</summary>
public enum HotwordCase
{
    AsIs,
    Title,
    Lower,
    Upper,
}

/// <summary>The hotword text a sherpa-onnx stream takes, and the phrase lines that feed it.</summary>
public static class SherpaHotwords
{
    public const float DefaultScore = 1.5f;

    /// <summary>The stream's hotword text: the native separates phrases with <c>/</c>.</summary>
    public static string Join(IReadOnlyList<string> phrases) => string.Join('/', phrases);

    /// <summary>
    /// The phrases of a hotwords file: each line trimmed, inner whitespace collapsed and the case applied,
    /// blank lines skipped. The native reads <c>/</c> and a word's leading <c>:</c>, <c>#</c> or <c>@</c> as
    /// syntax, so a line carrying one is refused rather than decoded as something else.
    /// </summary>
    /// <param name="lines">The file's lines.</param>
    /// <param name="casing">The case to apply.</param>
    /// <param name="error">Why the lines cannot be used; null when they can.</param>
    /// <returns>The phrases; empty when <paramref name="error"/> is set.</returns>
    public static IReadOnlyList<string> ParseLines(IEnumerable<string> lines, HotwordCase casing, out string? error)
    {
        error = null;
        var phrases = new List<string>();
        foreach (string line in lines)
        {
            string[] words = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0)
            {
                continue;
            }
            if (line.Contains('/', StringComparison.Ordinal) || words.Any(w => w[0] is ':' or '#' or '@'))
            {
                error = $"hotword line '{line.Trim()}' cannot contain '/' or a word starting with ':', '#' or '@'";
                return [];
            }
            phrases.Add(string.Join(' ', words.Select(w => ApplyCase(w, casing))));
        }

        if (phrases.Count == 0)
        {
            error = "the hotwords file has no phrases";
        }
        return phrases;
    }

    private static string ApplyCase(string word, HotwordCase casing) =>
        casing switch
        {
            HotwordCase.AsIs => word,
            HotwordCase.Title => char.ToUpperInvariant(word[0]) + word[1..].ToLower(CultureInfo.InvariantCulture),
            HotwordCase.Lower => word.ToLower(CultureInfo.InvariantCulture),
            HotwordCase.Upper => word.ToUpper(CultureInfo.InvariantCulture),
            _ => throw new ArgumentOutOfRangeException(nameof(casing), casing, "no such hotword case"),
        };
}
