using System.Text;
using Yaat.Sim.Speech;

namespace Yaat.Sim.Speech.Context;

/// <summary>
/// Scores a heard token span against an entity's spoken form: a name part compared as one word,
/// a code part compared token by token, and the two multiplied.
/// </summary>
public static class SpokenFormScore
{
    /// <summary>
    /// Similarity of two words: 1 when equal; 0 for a digit mismatch, two different NATO words, or
    /// a word shorter than <see cref="DecoderSettings.MinFuzzyChars"/>; otherwise the weighted sum
    /// of the raw and the phonetic Levenshtein similarities.
    /// </summary>
    /// <param name="a">The first word.</param>
    /// <param name="b">The second word.</param>
    /// <param name="mode">Exact matching returns 1 or 0 only.</param>
    /// <param name="settings">The thresholds the score is measured against.</param>
    /// <returns>The similarity in [0, 1].</returns>
    public static double WordScore(string a, string b, MatchMode mode, DecoderSettings settings)
    {
        if (string.Equals(a, b, StringComparison.Ordinal))
        {
            return 1.0;
        }
        if (mode == MatchMode.ExactOnly)
        {
            return 0.0;
        }
        if (IsDigitToken(a) || IsDigitToken(b))
        {
            return 0.0;
        }
        if (IsNatoWord(a) && IsNatoWord(b))
        {
            return 0.0;
        }

        int maxLength = Math.Max(a.Length, b.Length);
        if (maxLength < settings.MinFuzzyChars)
        {
            return 0.0;
        }

        double raw = 1.0 - ((double)PhoneticFixMatcher.Levenshtein(a, b) / maxLength);
        string phoneticA = VoicingFold(PhoneticFixMatcher.Phonetize(a));
        string phoneticB = VoicingFold(PhoneticFixMatcher.Phonetize(b));
        int maxPhoneticLength = Math.Max(phoneticA.Length, phoneticB.Length);
        double phonetic = maxPhoneticLength == 0 ? 1.0 : 1.0 - ((double)PhoneticFixMatcher.Levenshtein(phoneticA, phoneticB) / maxPhoneticLength);
        return (settings.WRaw * raw) + ((1.0 - settings.WRaw) * phonetic);
    }

    /// <summary>
    /// Scores a heard span against a spoken form: the name part as one word, the code part token by
    /// token, multiplied together and by the form's prior. A part either side lacks counts 1.
    /// </summary>
    /// <param name="heard">The tokens of the heard span.</param>
    /// <param name="form">The tokens of the spoken form.</param>
    /// <param name="prior">The form's prior.</param>
    /// <param name="mode">Exact matching returns 1 or 0 only.</param>
    /// <param name="settings">The thresholds the score is measured against.</param>
    /// <returns>The span score in [0, 1].</returns>
    public static double ScoreSpan(
        IReadOnlyList<SpokenToken> heard,
        IReadOnlyList<SpokenToken> form,
        double prior,
        MatchMode mode,
        DecoderSettings settings
    )
    {
        (IReadOnlyList<SpokenToken> Name, IReadOnlyList<SpokenToken> Code) parts = SplitHeard(heard, form);
        return NameScore(parts.Name, form, mode, settings) * CodeScore(parts.Code, form, mode, settings) * prior;
    }

    /// <summary>
    /// Splits a heard span into the part compared with the form's name and the part compared with its code. The first heard
    /// words, as many as the form's name has, are name; heard words beyond those are near misses of NATO tokens when the form
    /// has NATO tokens, and stay name when it has none.
    /// </summary>
    /// <param name="heard">The tokens of the heard span.</param>
    /// <param name="form">The tokens of the spoken form.</param>
    /// <returns>The heard name tokens and the heard code tokens, each in token order.</returns>
    public static (IReadOnlyList<SpokenToken> Name, IReadOnlyList<SpokenToken> Code) SplitHeard(
        IReadOnlyList<SpokenToken> heard,
        IReadOnlyList<SpokenToken> form
    )
    {
        int formNameCount = form.Count(token => token.Kind == TokenKind.Word);
        bool surplusIsCode = form.Any(token => token.Kind == TokenKind.Nato);
        var name = new List<SpokenToken>();
        var code = new List<SpokenToken>();
        foreach (SpokenToken token in heard)
        {
            bool isName = (token.Kind == TokenKind.Word) && ((name.Count < formNameCount) || !surplusIsCode);
            (isName ? name : code).Add(token);
        }
        return (name, code);
    }

    private static double NameScore(IReadOnlyList<SpokenToken> heardName, IReadOnlyList<SpokenToken> form, MatchMode mode, DecoderSettings settings)
    {
        List<string> heardWords = [.. heardName.Select(token => token.Text)];
        List<string> formWords = [.. form.Where(token => !IsCodeToken(token)).Select(token => token.Text)];
        if (!SameDiscriminators(heardWords, formWords) || HasForeignLiteral(heardWords, formWords))
        {
            return 0.0;
        }
        if ((heardWords.Count == 0) || (formWords.Count == 0))
        {
            return 1.0;
        }
        return WordScore(string.Concat(heardWords), string.Concat(formWords), mode, settings);
    }

    /// <summary>One short differing word would vanish inside a long concatenated name, so these words must match exactly.</summary>
    private static bool SameDiscriminators(List<string> heard, List<string> form)
    {
        IEnumerable<string> heardWords = heard.Where(ContextPhraseWords.DiscriminatorWords.Contains).Order(StringComparer.Ordinal);
        IEnumerable<string> formWords = form.Where(ContextPhraseWords.DiscriminatorWords.Contains).Order(StringComparer.Ordinal);
        return heardWords.SequenceEqual(formWords);
    }

    private static bool HasForeignLiteral(List<string> heard, List<string> form) =>
        heard.Any(word =>
            (NatoNearMissResolver.RuleLiteralWords.Contains(word) || ContextPhraseWords.FacilityWords.Contains(word)) && !form.Contains(word)
        );

    private static double CodeScore(IReadOnlyList<SpokenToken> heardCode, IReadOnlyList<SpokenToken> form, MatchMode mode, DecoderSettings settings)
    {
        List<SpokenToken> formCode = [.. form.Where(IsCodeToken)];
        if ((heardCode.Count == 0) || (formCode.Count == 0))
        {
            return 1.0;
        }
        if ((formCode.Count == form.Count) && !CodeOnlyFormHeard(heardCode, formCode, mode, settings))
        {
            return 0.0;
        }
        return AlignmentScore(heardCode, formCode, mode, settings);
    }

    /// <summary>
    /// Whether a form names an aircraft by itself: it has a word, or starts with "november", or starts with a NATO word then a
    /// digit ("delta 7 7 2"). Every other callsign form is a bare number or letters.
    /// </summary>
    /// <param name="form">The tokens of the spoken form.</param>
    /// <returns>Whether the form is prefixed.</returns>
    public static bool IsPrefixedForm(IReadOnlyList<SpokenToken> form)
    {
        bool hasWord = form.Any(token => token.Kind == TokenKind.Word);
        return hasWord || StartsWithLetterPrefix(form);
    }

    private static bool StartsWithLetterPrefix(IReadOnlyList<SpokenToken> form)
    {
        bool startsNovember = (form.Count > 0) && (form[0].Text == "november");
        bool letterThenDigit = (form.Count > 1) && (form[0].Kind == TokenKind.Nato) && (form[1].Kind == TokenKind.Digit);
        return startsNovember || letterThenDigit;
    }

    /// <summary>A letter prefix has to be heard: digits alone are any number, however well they align with the rest.</summary>
    private static bool CodeOnlyFormHeard(IReadOnlyList<SpokenToken> heard, List<SpokenToken> form, MatchMode mode, DecoderSettings settings)
    {
        bool prefixMissing = StartsWithLetterPrefix(form) && (heard[0].Text != form[0].Text);
        return !prefixMissing && HeardWordsStandInPlace(heard, form, mode, settings);
    }

    /// <summary>
    /// In a form with no words, a heard word can only be a near miss of the NATO token at its own position, past the first:
    /// a word before the digits is what was said in place of the prefix, not a mishearing of it.
    /// </summary>
    private static bool HeardWordsStandInPlace(IReadOnlyList<SpokenToken> heard, List<SpokenToken> form, MatchMode mode, DecoderSettings settings)
    {
        for (int i = 0; i < heard.Count; i++)
        {
            if (heard[i].Kind != TokenKind.Word)
            {
                continue;
            }
            if ((i == 0) || (heard.Count != form.Count) || (WordScore(heard[i].Text, form[i].Text, mode, settings) < settings.NatoCodeAccept))
            {
                return false;
            }
        }
        return true;
    }

    private static double AlignmentScore(IReadOnlyList<SpokenToken> heard, IReadOnlyList<SpokenToken> form, MatchMode mode, DecoderSettings settings)
    {
        double[] previous = new double[form.Count + 1];
        double[] current = new double[form.Count + 1];
        for (int i = 1; i <= heard.Count; i++)
        {
            current[0] = 0.0;
            for (int j = 1; j <= form.Count; j++)
            {
                double aligned = previous[j - 1] + TokenSimilarity(heard[i - 1], form[j - 1], mode, settings);
                current[j] = Math.Max(Math.Max(current[j - 1], previous[j]), aligned);
            }
            (previous, current) = (current, previous);
        }
        return previous[form.Count] / Math.Max(heard.Count, form.Count);
    }

    private static double TokenSimilarity(SpokenToken heard, SpokenToken form, MatchMode mode, DecoderSettings settings)
    {
        double score = WordScore(heard.Text, form.Text, mode, settings);
        bool wordAgainstNato =
            ((heard.Kind == TokenKind.Word) && (form.Kind == TokenKind.Nato)) || ((heard.Kind == TokenKind.Nato) && (form.Kind == TokenKind.Word));
        if (wordAgainstNato)
        {
            return score >= settings.NatoCodeAccept ? score : 0.0;
        }
        return score;
    }

    private static bool IsCodeToken(SpokenToken token) => (token.Kind == TokenKind.Digit) || (token.Kind == TokenKind.Nato);

    private static string VoicingFold(string phonetized)
    {
        var folded = new StringBuilder(phonetized.Length);
        foreach (char c in phonetized)
        {
            folded.Append(FoldVoicing(c));
        }
        return folded.ToString();
    }

    private static char FoldVoicing(char c) =>
        c switch
        {
            'Z' => 'S',
            'V' => 'F',
            'D' => 'T',
            'B' => 'P',
            'G' => 'K',
            _ => c,
        };

    private static bool IsDigitToken(string text) => (text.Length > 0) && text.All(char.IsDigit);

    private static bool IsNatoWord(string text) => NatoPhoneticAlphabet.WordSet.Contains(text);
}
