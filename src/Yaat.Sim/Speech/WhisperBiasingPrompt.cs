using System.Text;

namespace Yaat.Sim.Speech;

/// <summary>
/// Builds the static Whisper <c>initial_prompt</c> string YAAT feeds to the speech recognizer
/// to bias decoding toward ATC vocabulary. The prompt is the union of three deterministic
/// vocabulary sets:
/// <list type="number">
///   <item><description>The full ICAO/NATO phonetic alphabet (alpha … zulu).</description></item>
///   <item><description>Phonetic number forms used in ATC (zero/one/two/three/tree/four/fower/five/fife/six/seven/eight/niner) plus magnitude words (hundred, thousand, point, decimal, flight, level, altitude, knots).</description></item>
///   <item><description>Every distinct literal pattern token from <see cref="PhraseologyRules.All"/>, which together cover every word the rule engine knows how to map to a canonical command.</description></item>
/// </list>
///
/// The result is cached as a single space-joined alphabetized string, well under whisper.cpp's
/// 224-token decoder context cap, and identical across PTT presses. We deliberately do NOT mix
/// in dynamic per-PTT context (active scenario callsigns, programmed fixes) here — the static
/// vocabulary is sufficient because:
/// <list type="bullet">
///   <item><description>The NATO alphabet covers any callsign character. The N346G probe showed
///     <c>whisper-large-turbo3</c> recognizing tail numbers cleanly with no per-callsign biasing.</description></item>
///   <item><description>Avoiding per-PTT recomputation removes the 224-token budget concern and
///     drops a class of potential prompt truncation bugs.</description></item>
///   <item><description>The static prompt can be composed once into <see cref="Default"/> and
///     reused across every PTT press for the life of the process.</description></item>
/// </list>
/// </summary>
public static class WhisperBiasingPrompt
{
    private static readonly Lazy<string> DefaultLazy = new(Build);

    /// <summary>Cached default biasing prompt. Computed once on first access.</summary>
    public static string Default => DefaultLazy.Value;

    /// <summary>
    /// NATO / ICAO phonetic alphabet — the canonical spoken letter forms used in aviation
    /// callsigns and clearance read-back. Sequence preserved for predictability.
    /// </summary>
    private static readonly string[] NatoAlphabet =
    [
        "alpha",
        "bravo",
        "charlie",
        "delta",
        "echo",
        "foxtrot",
        "golf",
        "hotel",
        "india",
        "juliet",
        "kilo",
        "lima",
        "mike",
        "november",
        "oscar",
        "papa",
        "quebec",
        "romeo",
        "sierra",
        "tango",
        "uniform",
        "victor",
        "whiskey",
        "xray",
        "yankee",
        "zulu",
    ];

    /// <summary>
    /// Phonetic / spoken number forms from FAA 7110.65 4-2-9 ("Numbers Usage"). Includes ATC
    /// variants (<c>tree</c> for 3, <c>fower</c> for 4, <c>fife</c> for 5, <c>niner</c> for 9)
    /// alongside the standard English forms because pilots and controllers use both — Whisper
    /// needs to recognize either spelling. Magnitude words ("hundred", "thousand") and altitude
    /// vocabulary ("flight level", "feet") cover the surrounding clearance phraseology.
    ///
    /// We also include the bare digit characters and concrete altitude / heading values so the
    /// model is biased toward emitting digit forms ("346 GOLF") rather than phonetic-word forms
    /// ("three four six golf") when given strong audio. Without the digit forms in the prompt,
    /// whisper-large-turbo3 over-corrects toward word forms because the entire biasing
    /// vocabulary lives in the word side of the joint distribution. AtcNumberParser.NormalizeDigits
    /// handles both downstream, but digit form is the preferred wire format because it skips a
    /// normalization pass.
    /// </summary>
    private static readonly string[] PhoneticNumbers =
    [
        "zero",
        "one",
        "two",
        "three",
        "tree",
        "four",
        "fower",
        "five",
        "fife",
        "six",
        "seven",
        "eight",
        "nine",
        "niner",
        "ten",
        "eleven",
        "twelve",
        "thirteen",
        "fourteen",
        "fifteen",
        "sixteen",
        "seventeen",
        "eighteen",
        "nineteen",
        "twenty",
        "thirty",
        "forty",
        "fifty",
        "sixty",
        "seventy",
        "eighty",
        "ninety",
        "hundred",
        "thousand",
        "point",
        "decimal",
        "flight",
        "level",
        "altitude",
        "feet",
        "knots",
        // Bare digits — bias the model toward digit-form output rather than phonetic-word form.
        "0",
        "1",
        "2",
        "3",
        "4",
        "5",
        "6",
        "7",
        "8",
        "9",
        // Concrete altitude / heading / speed examples in the canonical wire form. Seeing these
        // in the prompt cues the model to produce e.g. "270" for "two seven zero" instead of
        // "two seven zero" verbatim.
        "090",
        "180",
        "270",
        "360",
        "1000",
        "2000",
        "3000",
        "4000",
        "5000",
        "6000",
        "7000",
        "8000",
        "9000",
        "10000",
        "FL180",
        "FL250",
        "FL350",
    ];

    /// <summary>
    /// Builds the prompt by merging the NATO alphabet, phonetic number set, and every distinct
    /// literal token in <see cref="PhraseologyRules.All"/>. Capture-group placeholders
    /// (<c>{name}</c>) are excluded — they're regex-style holes, not vocabulary words. Trailing
    /// <c>?</c> markers from optional tokens are stripped so we bias for the underlying word.
    /// </summary>
    public static string Build()
    {
        // StringComparer.OrdinalIgnoreCase de-duplicates "Climb"/"climb"/"CLIMB" into one entry.
        var vocab = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var word in NatoAlphabet)
        {
            vocab.Add(word);
        }
        foreach (var word in PhoneticNumbers)
        {
            vocab.Add(word);
        }

        foreach (var rule in PhraseologyRules.All)
        {
            foreach (var token in rule.Pattern)
            {
                // Skip capture groups — they're {name} placeholders, not vocabulary.
                if (token.StartsWith('{') && token.EndsWith('}'))
                {
                    continue;
                }

                // Strip the optional-marker suffix so "and?" → "and".
                var literal = token.EndsWith('?') ? token[..^1] : token;
                if (literal.Length > 0)
                {
                    vocab.Add(literal);
                }
            }
        }

        // Single space-joined string — Whisper's initial_prompt is a free-form text seed, not a
        // structured token list. Whisper tokenizes the prompt itself when it loads; word-level
        // separation by spaces is the standard form (matches whisper.cpp's example prompts and
        // the form Whisper.net's WithPrompt expected).
        var sb = new StringBuilder(capacity: vocab.Count * 8);
        var first = true;
        foreach (var word in vocab)
        {
            if (!first)
            {
                sb.Append(' ');
            }
            sb.Append(word);
            first = false;
        }
        return sb.ToString();
    }
}
