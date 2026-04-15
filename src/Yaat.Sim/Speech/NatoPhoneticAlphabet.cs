namespace Yaat.Sim.Speech;

/// <summary>
/// Single canonical source for the ICAO / NATO phonetic alphabet used by the speech pipeline.
/// Exposes both directions of the mapping (letter ↔ word), an A-Z ordered word list, and a
/// case-insensitive word set for O(1) exact-match checks. Every other Speech/ class
/// (<see cref="CallsignParser"/>, <see cref="NatoLetterNormalizer"/>,
/// <see cref="NatoNearMissResolver"/>, <see cref="WhisperBiasingPrompt"/>) consumes these
/// maps instead of defining its own copy.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="WhisperBiasingPrompt"/> additionally defines a scrambled order (to break the
/// sequential-alphabet prior in whisper-large-turbo3) — that order is a local concern of the
/// biasing prompt and is NOT exposed here. The <see cref="Words"/> list returned from this
/// class is always A-Z ordered; callers that need a different order can permute it locally.
/// </para>
/// </remarks>
public static class NatoPhoneticAlphabet
{
    /// <summary>A-Z ordered letter → NATO word map. "alpha", "bravo", …, "zulu".</summary>
    public static readonly IReadOnlyDictionary<char, string> LetterToWord = new Dictionary<char, string>
    {
        ['A'] = "alpha",
        ['B'] = "bravo",
        ['C'] = "charlie",
        ['D'] = "delta",
        ['E'] = "echo",
        ['F'] = "foxtrot",
        ['G'] = "golf",
        ['H'] = "hotel",
        ['I'] = "india",
        ['J'] = "juliet",
        ['K'] = "kilo",
        ['L'] = "lima",
        ['M'] = "mike",
        ['N'] = "november",
        ['O'] = "oscar",
        ['P'] = "papa",
        ['Q'] = "quebec",
        ['R'] = "romeo",
        ['S'] = "sierra",
        ['T'] = "tango",
        ['U'] = "uniform",
        ['V'] = "victor",
        ['W'] = "whiskey",
        ['X'] = "xray",
        ['Y'] = "yankee",
        ['Z'] = "zulu",
    };

    /// <summary>
    /// Case-insensitive reverse map: NATO word → letter. Lookup is O(1); the dictionary is
    /// built once at class-load time.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, char> WordToLetter = BuildReverseMap();

    /// <summary>
    /// The 26 NATO words in A-Z order. Stable across calls — callers can read this as a
    /// <c>IReadOnlyList</c> without worrying about mutation.
    /// </summary>
    public static readonly IReadOnlyList<string> Words = LetterToWord.Values.ToList();

    /// <summary>
    /// Case-insensitive set of the 26 NATO words. Intended for O(1) "is this token a NATO
    /// word?" checks without having to call <see cref="TryGetLetter"/> just for the
    /// boolean answer.
    /// </summary>
    public static readonly IReadOnlySet<string> WordSet = new HashSet<string>(Words, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Try to interpret <paramref name="word"/> as a NATO phonetic word and return its
    /// letter. Case-insensitive. Returns false for non-NATO tokens.
    /// </summary>
    public static bool TryGetLetter(string word, out char letter) => WordToLetter.TryGetValue(word, out letter);

    /// <summary>
    /// Try to get the NATO phonetic word for <paramref name="letter"/>. Accepts both upper
    /// and lower case input.
    /// </summary>
    public static bool TryGetWord(char letter, out string word)
    {
        var upper = char.ToUpperInvariant(letter);
        return LetterToWord.TryGetValue(upper, out word!);
    }

    /// <summary>
    /// Spells a single character into its spoken form: NATO phonetic for letters, spoken digit
    /// form for '0'-'9' ("zero" … "nine"). Returns the character's uppercase string for anything
    /// else (punctuation, non-ASCII). Used by <see cref="CallsignParser.IcaoToSpoken"/> and
    /// related spoken-form helpers.
    /// </summary>
    public static string SpellChar(char c)
    {
        if (c >= '0' && c <= '9')
        {
            return (c - '0') switch
            {
                0 => "zero",
                1 => "one",
                2 => "two",
                3 => "three",
                4 => "four",
                5 => "five",
                6 => "six",
                7 => "seven",
                8 => "eight",
                9 => "nine",
                _ => c.ToString(),
            };
        }
        var upper = char.ToUpperInvariant(c);
        return LetterToWord.TryGetValue(upper, out var word) ? word : upper.ToString();
    }

    private static Dictionary<string, char> BuildReverseMap()
    {
        var map = new Dictionary<string, char>(StringComparer.OrdinalIgnoreCase);
        foreach (var (letter, word) in LetterToWord)
        {
            map[word] = letter;
        }
        return map;
    }
}
