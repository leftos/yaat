using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Yaat.SpeechSandbox;

/// <summary>
/// Puts both sides of a word error rate comparison into one comparable form: lowercase, punctuation
/// removed, every number spelled digit by digit, and the spellings UWB-ATCC writes ("alfa",
/// "juliett") aligned with the ones Whisper emits ("alpha", "juliet"). Deliberately separate from
/// <c>AtcNumberParser.NormalizeDigits</c>, which collapses words to digits for the command mapper.
/// </summary>
public static partial class WerTextNormalizer
{
    private static readonly FrozenDictionary<string, string> Aliases = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["fl"] = "flight level",
        ["alpha"] = "alfa",
        ["juliet"] = "juliett",
        ["niner"] = "nine",
        ["tree"] = "three",
        ["fife"] = "five",
        ["okay"] = "ok",
    }.ToFrozenDictionary();

    /// <summary>Two-word spellings joined into the one word the other side may write.</summary>
    private static readonly FrozenDictionary<(string First, string Second), string> Compounds = new Dictionary<(string, string), string>
    {
        [("x", "ray")] = "xray",
        [("stand", "by")] = "standby",
        [("push", "back")] = "pushback",
        [("take", "off")] = "takeoff",
    }.ToFrozenDictionary();

    private static readonly string[] DigitNames = ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine"];

    /// <summary>Normalizes one transcript so that a reference and a hypothesis compare word for word.</summary>
    public static string Normalize(string text)
    {
        string normalized = DecimalPoint().Replace(FoldAccents(text).ToLowerInvariant(), " decimal ");
        normalized = Apostrophe().Replace(normalized, string.Empty);
        normalized = HyphenBetweenLetters().Replace(normalized, string.Empty);
        normalized = NotAlphanumeric().Replace(normalized, " ");
        normalized = LetterDigitBoundary().Replace(normalized, " ");

        string[] tokens = normalized.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var words = new List<string>(tokens.Length);
        for (int i = 0; i < tokens.Length; i++)
        {
            if ((i + 1 < tokens.Length) && Compounds.TryGetValue((tokens[i], tokens[i + 1]), out string? compound))
            {
                words.Add(compound);
                i++;
                continue;
            }
            words.Add(MapToken(tokens[i]));
        }
        return string.Join(' ', words);
    }

    /// <summary>Decomposes accented letters and drops the combining marks, so <c>ě</c> becomes <c>e</c>.</summary>
    private static string FoldAccents(string text)
    {
        string decomposed = text.Normalize(NormalizationForm.FormD);
        var folded = new StringBuilder(decomposed.Length);
        foreach (char c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                folded.Append(c);
            }
        }
        return folded.ToString();
    }

    private static string MapToken(string token) => IsAllDigits(token) ? SpellDigits(token) : Aliases.GetValueOrDefault(token, token);

    private static bool IsAllDigits(string token) => token.All(char.IsAsciiDigit);

    private static string SpellDigits(string token) => string.Join(' ', token.Select(digit => DigitNames[digit - '0']));

    [GeneratedRegex(@"(?<=\d)\.(?=\d)")]
    private static partial Regex DecimalPoint();

    [GeneratedRegex("['’]")]
    private static partial Regex Apostrophe();

    [GeneratedRegex(@"(?<=[a-z])-(?=[a-z])")]
    private static partial Regex HyphenBetweenLetters();

    [GeneratedRegex(@"[^a-z0-9]")]
    private static partial Regex NotAlphanumeric();

    [GeneratedRegex(@"(?<=[a-z])(?=[0-9])|(?<=[0-9])(?=[a-z])")]
    private static partial Regex LetterDigitBoundary();
}
