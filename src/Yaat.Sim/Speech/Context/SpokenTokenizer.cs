using System.Globalization;
using System.Text;
using Yaat.Sim.Speech;

namespace Yaat.Sim.Speech.Context;

/// <summary>How a token was classified by <see cref="SpokenTokenizer"/>.</summary>
public enum TokenKind
{
    /// <summary>An ordinary spelled word ("southwest", "runway", "mitoe").</summary>
    Word,

    /// <summary>One digit character ("2").</summary>
    Digit,

    /// <summary>A canonical NATO phonetic word ("november").</summary>
    Nato,
}

/// <summary>One normalised token and the whitespace word of the input it came from.</summary>
/// <param name="Text">The normalised token text: a lowercase word, one digit, or a NATO word.</param>
/// <param name="Kind">How the token was classified.</param>
/// <param name="SourceWord">Index of the whitespace-delimited input word the token came from.</param>
public sealed record SpokenToken(string Text, TokenKind Kind, int SourceWord);

/// <summary>
/// One normaliser for transcripts and spoken forms: lowercase, folded accents, no apostrophes,
/// letter and digit runs split apart, spoken numbers expanded to one digit token per digit, and
/// NATO words reduced to the canonical alphabet word.
/// </summary>
public static class SpokenTokenizer
{
    /// <summary>Hesitation sounds that carry no meaning, so they are dropped before scoring.</summary>
    public static IReadOnlySet<string> HesitationFillers { get; } =
        new HashSet<string>(StringComparer.Ordinal) { "uh", "um", "ah", "er", "erm", "hmm" };

    private static readonly IReadOnlyDictionary<string, string> RunwaySuffixWords = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["l"] = "left",
        ["r"] = "right",
        ["c"] = "center",
    };

    /// <summary>Normalises a transcript or a spoken form into tokens whose digits carry their source word.</summary>
    /// <param name="text">The transcript or spoken form.</param>
    /// <returns>The tokens in transcript order.</returns>
    public static IReadOnlyList<SpokenToken> Tokenize(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        return Expand(MergeXray(SplitUnits(text)));
    }

    private static List<Unit> SplitUnits(string text)
    {
        string[] words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var units = new List<Unit>(words.Length);
        for (int i = 0; i < words.Length; i++)
        {
            foreach (string piece in SplitWord(words[i]))
            {
                units.Add(new Unit(piece, i));
            }
        }
        return units;
    }

    private static List<string> SplitWord(string word)
    {
        string folded = FoldAccents(word).ToLowerInvariant();
        string stripped = StripApostrophesAndHyphens(folded);
        var pieces = new List<string>();
        foreach (string piece in SeparateLetterDigitRuns(stripped).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            pieces.Add(piece);
        }
        return pieces;
    }

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

    private static string StripApostrophesAndHyphens(string word)
    {
        var stripped = new StringBuilder(word.Length);
        for (int i = 0; i < word.Length; i++)
        {
            char c = word[i];
            if (c is '\'' or '’' or 'ʼ')
            {
                continue;
            }
            if (c == '-')
            {
                bool betweenLetters = (stripped.Length > 0) && (i + 1 < word.Length) && char.IsLetter(stripped[^1]) && char.IsLetter(word[i + 1]);
                if (!betweenLetters)
                {
                    stripped.Append(' ');
                }
                continue;
            }
            stripped.Append(c);
        }
        return stripped.ToString();
    }

    private static string SeparateLetterDigitRuns(string text)
    {
        var separated = new StringBuilder(text.Length + 8);
        char previous = '\0';
        foreach (char c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                if ((separated.Length > 0) && (char.IsDigit(previous) != char.IsDigit(c)))
                {
                    separated.Append(' ');
                }
                separated.Append(c);
                previous = c;
                continue;
            }
            if ((separated.Length > 0) && (separated[^1] != ' '))
            {
                separated.Append(' ');
            }
            previous = '\0';
        }
        return separated.ToString();
    }

    private static List<Unit> MergeXray(List<Unit> units)
    {
        var merged = new List<Unit>(units.Count);
        int i = 0;
        while (i < units.Count)
        {
            if ((i + 1 < units.Count) && (units[i].Text == "x") && (units[i + 1].Text == "ray"))
            {
                merged.Add(new Unit("xray", units[i].SourceWord));
                i += 2;
                continue;
            }
            merged.Add(units[i]);
            i++;
        }
        return merged;
    }

    private static List<SpokenToken> Expand(List<Unit> units)
    {
        var tokens = new List<SpokenToken>(units.Count);
        int i = 0;
        while (i < units.Count)
        {
            string text = units[i].Text;
            if (HesitationFillers.Contains(text))
            {
                i++;
                continue;
            }

            if (IsAllDigits(text))
            {
                i = EmitDigits(units, i, tokens);
                continue;
            }

            if ((text == "o") && IsBetweenNumberWords(units, i))
            {
                tokens.Add(new SpokenToken("0", TokenKind.Digit, units[i].SourceWord));
                i++;
                continue;
            }

            if (IsSingleLetter(text))
            {
                i = EmitLetterRun(units, i, tokens);
                continue;
            }

            if (AtcNumberParser.TryGetNumberWord(text, out NumberWordKind kind, out int value))
            {
                i = EmitNumberWord(units, i, kind, value, tokens);
                continue;
            }

            if (NatoPhoneticAlphabet.WordToLetter.TryGetValue(text, out char letter))
            {
                tokens.Add(new SpokenToken(NatoPhoneticAlphabet.LetterToWord[char.ToUpperInvariant(letter)], TokenKind.Nato, units[i].SourceWord));
                i++;
                continue;
            }

            tokens.Add(new SpokenToken(text, TokenKind.Word, units[i].SourceWord));
            i++;
        }
        return tokens;
    }

    private static int EmitDigits(List<Unit> units, int start, List<SpokenToken> tokens)
    {
        Unit unit = units[start];
        if ((unit.Text.Length <= 2) && (start + 1 < units.Count) && RunwaySuffixWords.TryGetValue(units[start + 1].Text, out string? suffix))
        {
            AddDigits(tokens, unit.Text, unit.SourceWord);
            tokens.Add(new SpokenToken(suffix, TokenKind.Word, units[start + 1].SourceWord));
            return start + 2;
        }

        AddDigits(tokens, unit.Text, unit.SourceWord);
        return start + 1;
    }

    private static int EmitLetterRun(List<Unit> units, int start, List<SpokenToken> tokens)
    {
        int end = start;
        while ((end < units.Count) && IsSingleLetter(units[end].Text))
        {
            end++;
        }

        if (end - start >= 2)
        {
            var joined = new StringBuilder(end - start);
            for (int i = start; i < end; i++)
            {
                joined.Append(units[i].Text);
            }
            tokens.Add(new SpokenToken(joined.ToString(), TokenKind.Word, units[start].SourceWord));
            return end;
        }

        if (NatoPhoneticAlphabet.LetterToWord.TryGetValue(char.ToUpperInvariant(units[start].Text[0]), out string? word))
        {
            tokens.Add(new SpokenToken(word, TokenKind.Nato, units[start].SourceWord));
        }
        else
        {
            tokens.Add(new SpokenToken(units[start].Text, TokenKind.Word, units[start].SourceWord));
        }
        return start + 1;
    }

    private static int EmitNumberWord(List<Unit> units, int start, NumberWordKind kind, int value, List<SpokenToken> tokens)
    {
        Unit unit = units[start];
        switch (kind)
        {
            case NumberWordKind.Teen:
            case NumberWordKind.Digit:
                AddDigits(tokens, value.ToString(CultureInfo.InvariantCulture), unit.SourceWord);
                return start + 1;

            case NumberWordKind.Oh:
                if (IsBetweenNumberWords(units, start))
                {
                    tokens.Add(new SpokenToken("0", TokenKind.Digit, unit.SourceWord));
                }
                else
                {
                    tokens.Add(new SpokenToken(unit.Text, TokenKind.Word, unit.SourceWord));
                }
                return start + 1;

            case NumberWordKind.Tens:
                if ((start + 1 < units.Count) && TryGetNextDigit(units[start + 1].Text, out int ones))
                {
                    AddDigits(tokens, (value / 10).ToString(CultureInfo.InvariantCulture), unit.SourceWord);
                    AddDigits(tokens, ones.ToString(CultureInfo.InvariantCulture), units[start + 1].SourceWord);
                    return start + 2;
                }
                AddDigits(tokens, value.ToString(CultureInfo.InvariantCulture), unit.SourceWord);
                return start + 1;

            case NumberWordKind.Hundred:
                EmitHundred(units, start, tokens);
                return start + 1;

            case NumberWordKind.Thousand:
                EmitThousand(units, start, tokens);
                return start + 1;

            default:
                tokens.Add(new SpokenToken(unit.Text, TokenKind.Word, unit.SourceWord));
                return start + 1;
        }
    }

    private static void EmitHundred(List<Unit> units, int start, List<SpokenToken> tokens)
    {
        int sourceWord = units[start].SourceWord;
        if ((start + 1 < units.Count) && IsPairWord(units[start + 1].Text))
        {
            return;
        }
        if ((start + 1 < units.Count) && TryGetNextDigit(units[start + 1].Text, out _))
        {
            tokens.Add(new SpokenToken("0", TokenKind.Digit, sourceWord));
            return;
        }
        AddDigits(tokens, "00", sourceWord);
    }

    private static void EmitThousand(List<Unit> units, int start, List<SpokenToken> tokens)
    {
        int sourceWord = units[start].SourceWord;
        bool beforeHundred =
            (start + 2 < units.Count)
            && (IsPairWord(units[start + 1].Text) || TryGetNextDigit(units[start + 1].Text, out _))
            && (units[start + 2].Text == "hundred");
        if (beforeHundred)
        {
            return;
        }
        if ((start + 1 < units.Count) && IsPairWord(units[start + 1].Text))
        {
            tokens.Add(new SpokenToken("0", TokenKind.Digit, sourceWord));
            return;
        }
        if ((start + 1 < units.Count) && TryGetNextDigit(units[start + 1].Text, out _))
        {
            AddDigits(tokens, "00", sourceWord);
            return;
        }
        AddDigits(tokens, "000", sourceWord);
    }

    private static void AddDigits(List<SpokenToken> tokens, string digits, int sourceWord)
    {
        foreach (char c in digits)
        {
            tokens.Add(new SpokenToken(c.ToString(), TokenKind.Digit, sourceWord));
        }
    }

    private static bool IsBetweenNumberWords(List<Unit> units, int index)
    {
        if ((index == 0) || (index + 1 >= units.Count))
        {
            return false;
        }
        return AtcNumberParser.TryGetNumberWord(units[index - 1].Text, out _, out _)
            && AtcNumberParser.TryGetNumberWord(units[index + 1].Text, out _, out _);
    }

    private static bool TryGetNextDigit(string text, out int value)
    {
        if (AtcNumberParser.TryGetNumberWord(text, out NumberWordKind kind, out value) && (kind == NumberWordKind.Digit))
        {
            return true;
        }
        value = 0;
        return false;
    }

    private static bool IsPairWord(string text)
    {
        return AtcNumberParser.TryGetNumberWord(text, out NumberWordKind kind, out _)
            && ((kind == NumberWordKind.Teen) || (kind == NumberWordKind.Tens));
    }

    private static bool IsAllDigits(string text) => (text.Length > 0) && text.All(char.IsDigit);

    private static bool IsSingleLetter(string text) => (text.Length == 1) && char.IsLetter(text[0]);

    private readonly record struct Unit(string Text, int SourceWord);
}
