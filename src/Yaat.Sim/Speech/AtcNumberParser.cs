using System.Text;

namespace Yaat.Sim.Speech;

/// <summary>
/// Converts between spoken ATC number forms and digit strings.
/// Used in two directions by the speech pipeline:
/// <list type="bullet">
///   <item>
///     <description>
///       <see cref="NormalizeDigits"/>: transcript → digit form, consumed by the phraseology rule engine.
///       E.g. "climb and maintain five thousand" → "climb and maintain 5000".
///     </description>
///   </item>
///   <item>
///     <description>
///       <see cref="FlightNumberToWords"/> / <see cref="AltitudeToWords"/>: digit → spoken form,
///       consumed by the Whisper <c>initial_prompt</c> seeder so transcription is primed for the
///       natural-English form pilots actually speak.
///     </description>
///   </item>
/// </list>
/// </summary>
public static class AtcNumberParser
{
    // ATC uses "niner", "tree", "fife" to disambiguate from "nine", "three", "five" on poor
    // radios. Whisper may transcribe either form, so we accept both. We deliberately do NOT add
    // every Whisper mistranscription (e.g. "diner" for "niner") here — the LLM callsign resolver
    // handles disambiguation for noisy transcripts instead.
    private static readonly Dictionary<string, int> DigitWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zero"] = 0,
        ["one"] = 1,
        ["two"] = 2,
        ["three"] = 3,
        ["tree"] = 3,
        ["four"] = 4,
        ["five"] = 5,
        ["fife"] = 5,
        ["six"] = 6,
        ["seven"] = 7,
        ["eight"] = 8,
        ["nine"] = 9,
        ["niner"] = 9,
    };

    private static readonly Dictionary<string, int> TeenWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ten"] = 10,
        ["eleven"] = 11,
        ["twelve"] = 12,
        ["thirteen"] = 13,
        ["fourteen"] = 14,
        ["fifteen"] = 15,
        ["sixteen"] = 16,
        ["seventeen"] = 17,
        ["eighteen"] = 18,
        ["nineteen"] = 19,
    };

    private static readonly Dictionary<string, int> TensWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["twenty"] = 20,
        ["thirty"] = 30,
        ["forty"] = 40,
        ["fifty"] = 50,
        ["sixty"] = 60,
        ["seventy"] = 70,
        ["eighty"] = 80,
        ["ninety"] = 90,
    };

    private static readonly string[] DigitToWord = ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine"];

    /// <summary>
    /// Scan a transcript and replace spoken number phrases with digit-form substrings.
    /// Non-number tokens are passed through unchanged. Lowercase output.
    /// </summary>
    public static string NormalizeDigits(string transcript)
    {
        if (string.IsNullOrWhiteSpace(transcript))
        {
            return transcript ?? "";
        }

        // Strip thousand-separator commas before tokenization. Whisper formats spoken numbers
        // using English conventions ("two thousand" → "2,000") when the model recognizes the word
        // but emits its numeric form. Without this pre-pass the tokenizer splits "2,000" into
        // ["2", "000"] and the rule engine's {alt} capture grabs only the first token, producing
        // "CM 2" instead of "CM 2000". Only commas that have a digit on BOTH sides are removed —
        // sentence punctuation ("hello, world") stays untouched.
        transcript = StripThousandSeparators(transcript);

        var tokens = Tokenize(transcript);
        var output = new List<string>(tokens.Count);

        var i = 0;
        while (i < tokens.Count)
        {
            // "flight level N N N" → NNN00 (e.g. "flight level three five zero" → "35000")
            if (TryReadFlightLevel(tokens, i, out var flValue, out var flConsumed))
            {
                output.Add(flValue.ToString());
                i += flConsumed;
                continue;
            }

            // Digit / compound number run
            if (TryReadNumberRun(tokens, i, out var numberStr, out var numConsumed))
            {
                output.Add(numberStr);
                i += numConsumed;
                continue;
            }

            output.Add(tokens[i]);
            i++;
        }

        // Collapse split runway designators ("28", "right" → "28R") AFTER number normalization
        // so the canonical runway form is visible to both downstream consumers: the rule engine
        // (which needs "28R" to match {rwy} captures) and the LLM fallback (which sees this
        // string as the userPrompt and would otherwise have to guess that "28 right" means
        // "28R"). Lives behind PhraseologyMapper.CollapseRunwayDesignators because that's where
        // the IsDigitString helper is — the cross-file call is fine within Yaat.Sim.
        output = PhraseologyMapper.CollapseRunwayDesignators(output);

        return string.Join(' ', output);
    }

    /// <summary>
    /// Convert a flight number to its spoken ATC form, digit-by-digit.
    /// E.g. 123 → "one two three", 4500 → "four five zero zero".
    /// This is the unambiguous alternate form. For the natural pilot-speak form
    /// (pair-wise English cardinals), see <see cref="FlightNumberToPairedWords"/>.
    /// Used alongside the paired form to seed Whisper's <c>initial_prompt</c>.
    /// </summary>
    public static string FlightNumberToWords(int flightNumber)
    {
        if (flightNumber < 0)
        {
            return "";
        }
        var digits = flightNumber.ToString();
        return string.Join(' ', digits.Select(c => DigitToWord[c - '0']));
    }

    /// <summary>
    /// Convert a flight number to its spoken ATC form, paired right-to-left.
    /// This is the natural form pilots use on the radio for airline flights:
    /// <list type="bullet">
    ///   <item><description>1 digit: "5" → "five"</description></item>
    ///   <item><description>2 digits: "42" → "forty two", "12" → "twelve"</description></item>
    ///   <item><description>3 digits: "123" → "one twenty three" (1 leader + pair 23)</description></item>
    ///   <item><description>4 digits: "1234" → "twelve thirty four" (pair 12 + pair 34)</description></item>
    ///   <item><description>5 digits: "12345" → "one twenty three forty five"</description></item>
    /// </list>
    /// Pairs with a leading zero fall back to digit-by-digit ("05" → "zero five"), since
    /// there is no English cardinal for a zero-leading pair.
    /// </summary>
    public static string FlightNumberToPairedWords(int flightNumber)
    {
        if (flightNumber < 0)
        {
            return "";
        }
        var digits = flightNumber.ToString();
        var parts = new List<string>();
        var i = 0;
        if (digits.Length % 2 == 1)
        {
            parts.Add(DigitToWord[digits[0] - '0']);
            i = 1;
        }
        while (i < digits.Length)
        {
            parts.Add(PairToWords(digits[i], digits[i + 1]));
            i += 2;
        }
        return string.Join(' ', parts);
    }

    private static string PairToWords(char d1, char d2)
    {
        var tens = d1 - '0';
        var ones = d2 - '0';

        // Leading-zero pair: no English cardinal, fall back to digit-by-digit.
        if (tens == 0)
        {
            return DigitToWord[0] + " " + DigitToWord[ones];
        }

        if (tens == 1)
        {
            return ones switch
            {
                0 => "ten",
                1 => "eleven",
                2 => "twelve",
                3 => "thirteen",
                4 => "fourteen",
                5 => "fifteen",
                6 => "sixteen",
                7 => "seventeen",
                8 => "eighteen",
                9 => "nineteen",
                _ => "",
            };
        }

        var tensWord = tens switch
        {
            2 => "twenty",
            3 => "thirty",
            4 => "forty",
            5 => "fifty",
            6 => "sixty",
            7 => "seventy",
            8 => "eighty",
            9 => "ninety",
            _ => "",
        };
        return ones == 0 ? tensWord : tensWord + " " + DigitToWord[ones];
    }

    /// <summary>
    /// Convert an altitude (in feet MSL) to its spoken ATC form.
    /// Below 18000: "five thousand", "eleven thousand five hundred".
    /// At or above 18000: "flight level three five zero".
    /// </summary>
    public static string AltitudeToWords(int altitudeFeet)
    {
        if (altitudeFeet <= 0)
        {
            return "";
        }

        // Flight level form for FL180+. FL is altitudeFeet / 100, spoken digit-by-digit.
        if (altitudeFeet >= 18000 && altitudeFeet % 100 == 0)
        {
            var fl = altitudeFeet / 100;
            var digits = fl.ToString("D3");
            return "flight level " + string.Join(' ', digits.Select(c => DigitToWord[c - '0']));
        }

        // Sub-FL form: N thousand [M hundred]
        var thousands = altitudeFeet / 1000;
        var hundreds = (altitudeFeet % 1000) / 100;
        var parts = new List<string>();
        if (thousands > 0)
        {
            parts.Add(DigitsToSpokenDigits(thousands));
            parts.Add("thousand");
        }
        if (hundreds > 0)
        {
            parts.Add(DigitToWord[hundreds]);
            parts.Add("hundred");
        }
        return parts.Count == 0 ? "" : string.Join(' ', parts);
    }

    private static string DigitsToSpokenDigits(int value)
    {
        return string.Join(' ', value.ToString().Select(c => DigitToWord[c - '0']));
    }

    /// <summary>
    /// Removes any comma that has a digit on both sides — i.e. English thousand-separator
    /// commas like the one in <c>2,000</c>. Walks the string character-by-character so we don't
    /// pull in a regex dependency for what is essentially a one-line transformation. Sentence
    /// commas (<c>"hello, world"</c>) are left alone because they don't have digits on the
    /// adjacent positions.
    /// </summary>
    private static string StripThousandSeparators(string transcript)
    {
        if (transcript.IndexOf(',') < 0)
        {
            return transcript;
        }

        var sb = new StringBuilder(transcript.Length);
        for (var i = 0; i < transcript.Length; i++)
        {
            var c = transcript[i];
            if (c == ',' && i > 0 && i + 1 < transcript.Length && char.IsDigit(transcript[i - 1]) && char.IsDigit(transcript[i + 1]))
            {
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static List<string> Tokenize(string transcript)
    {
        // Split on whitespace + simple punctuation. Commas and periods become token boundaries.
        var tokens = new List<string>();
        var start = -1;
        for (var idx = 0; idx < transcript.Length; idx++)
        {
            var c = transcript[idx];
            var isWord = char.IsLetterOrDigit(c);
            if (isWord)
            {
                if (start == -1)
                {
                    start = idx;
                }
            }
            else
            {
                if (start != -1)
                {
                    tokens.Add(transcript[start..idx].ToLowerInvariant());
                    start = -1;
                }
            }
        }
        if (start != -1)
        {
            tokens.Add(transcript[start..].ToLowerInvariant());
        }
        return tokens;
    }

    private static bool TryReadFlightLevel(List<string> tokens, int start, out int value, out int consumed)
    {
        value = 0;
        consumed = 0;

        if (start + 1 >= tokens.Count)
        {
            return false;
        }
        if (!(tokens[start] == "flight" && tokens[start + 1] == "level"))
        {
            // Allow "fl" shorthand too — some transcripts produce it.
            if (tokens[start] != "fl")
            {
                return false;
            }
        }

        var prefixLen = tokens[start] == "fl" ? 1 : 2;
        if (!TryReadDigitSequence(tokens, start + prefixLen, out var digits, out var digitsConsumed))
        {
            return false;
        }
        if (digits.Length == 0)
        {
            return false;
        }

        value = int.Parse(digits) * 100;
        consumed = prefixLen + digitsConsumed;
        return true;
    }

    private static bool TryReadNumberRun(List<string> tokens, int start, out string result, out int consumed)
    {
        result = "";
        consumed = 0;

        // Read the leading digit / teen / tens sequence into an integer.
        if (!TryReadCompoundValue(tokens, start, out var leadValue, out var leadConsumed))
        {
            return false;
        }

        var i = start + leadConsumed;
        var total = leadValue.Value;
        var used = leadConsumed;

        // Optional "thousand" scale.
        if (i < tokens.Count && tokens[i] == "thousand")
        {
            total *= 1000;
            i++;
            used++;

            // Optional "<compound> hundred" suffix, e.g. "five thousand five hundred" → 5500.
            if (TryReadCompoundValue(tokens, i, out var afterThousand, out var atcConsumed))
            {
                if (i + atcConsumed < tokens.Count && tokens[i + atcConsumed] == "hundred")
                {
                    total += afterThousand.Value * 100;
                    i += atcConsumed + 1;
                    used += atcConsumed + 1;
                }
            }
        }
        else if (i < tokens.Count && tokens[i] == "hundred")
        {
            total *= 100;
            i++;
            used++;
        }
        else if (leadValue.IsPureDigitSequence)
        {
            // Pure digit run with no multiplier: emit the concatenated digits verbatim so
            // "two seven zero" → "270", preserving leading zeros in "zero niner zero" → "090".
            result = leadValue.DigitString;
            consumed = used;
            return true;
        }

        result = total.ToString();
        consumed = used;
        return true;
    }

    /// <summary>
    /// Read a compound value: either a pure digit-word sequence ("two seven zero" → 270),
    /// a teen ("eleven" → 11), or a tens + optional digit ("twenty five" → 25).
    /// </summary>
    private static bool TryReadCompoundValue(List<string> tokens, int start, out CompoundValue result, out int consumed)
    {
        result = default;
        consumed = 0;

        if (start >= tokens.Count)
        {
            return false;
        }

        var first = tokens[start];

        if (TeenWords.TryGetValue(first, out var teen))
        {
            result = new CompoundValue(teen, IsPureDigitSequence: false, DigitString: teen.ToString());
            consumed = 1;
            return true;
        }

        if (TensWords.TryGetValue(first, out var tens))
        {
            // Optional trailing digit: "twenty five" → 25
            if (start + 1 < tokens.Count && DigitWords.TryGetValue(tokens[start + 1], out var d) && d < 10)
            {
                var v = tens + d;
                result = new CompoundValue(v, IsPureDigitSequence: false, DigitString: v.ToString());
                consumed = 2;
                return true;
            }
            result = new CompoundValue(tens, IsPureDigitSequence: false, DigitString: tens.ToString());
            consumed = 1;
            return true;
        }

        if (TryReadDigitSequence(tokens, start, out var digits, out var digitsConsumed))
        {
            var value = digits.Length == 0 ? 0 : int.Parse(digits);
            result = new CompoundValue(value, IsPureDigitSequence: true, DigitString: digits);
            consumed = digitsConsumed;
            return digitsConsumed > 0;
        }

        return false;
    }

    /// <summary>Read 1+ digit words and concatenate them as a literal digit string.</summary>
    private static bool TryReadDigitSequence(List<string> tokens, int start, out string digits, out int consumed)
    {
        var sb = new System.Text.StringBuilder();
        var i = start;
        while (i < tokens.Count && DigitWords.TryGetValue(tokens[i], out var d))
        {
            sb.Append(d);
            i++;
        }
        digits = sb.ToString();
        consumed = i - start;
        return consumed > 0;
    }

    private readonly record struct CompoundValue(int Value, bool IsPureDigitSequence, string DigitString);
}
