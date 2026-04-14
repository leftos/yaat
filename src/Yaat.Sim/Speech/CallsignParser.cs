namespace Yaat.Sim.Speech;

/// <summary>
/// Bidirectional conversion between spoken callsigns and ICAO-format callsigns.
/// Used by the speech pipeline in both directions:
/// <list type="bullet">
///   <item>
///     <description>
///       <see cref="TryParseLeading"/> / <see cref="TryParseTrailing"/>: transcript → ICAO form.
///       E.g. "southwest one two three climb and maintain 5000" → <c>SWA123</c>.
///     </description>
///   </item>
///   <item>
///     <description>
///       <see cref="IcaoToSpoken"/>: <c>SWA123</c> → "southwest one two three", used to seed
///       Whisper's <c>initial_prompt</c> so transcription is primed for the telephony form.
///     </description>
///   </item>
/// </list>
/// When multiple airlines share a telephony (e.g. VIRGIN → VIR, VOZ), the parser prefers the
/// ICAO whose flight number matches an entry in <c>activeCallsigns</c>. Exact match beats fuzzy
/// match; ambiguity falls through to the first airline in insertion order.
/// </summary>
public static class CallsignParser
{
    // GA aircraft registrations are pronounced letter-by-letter, prefixed with a country word.
    // "November one two three four five" → "N12345" (US). Currently handles November only;
    // other country prefixes (C-, G-, etc.) can be added later.
    private const string NovemberWord = "november";

    public sealed record ParsedCallsign(string IcaoCallsign, int TokensConsumed);

    /// <summary>
    /// Try to parse a callsign from the leading tokens of a transcript.
    /// </summary>
    /// <param name="transcript">
    /// Transcript with digits normalized (see <see cref="AtcNumberParser.NormalizeDigits"/>),
    /// e.g. "southwest 123 climb and maintain 5000".
    /// </param>
    /// <param name="activeCallsigns">
    /// Callsigns of aircraft currently in the scenario, used as a tiebreaker when multiple
    /// airlines share a telephony. Pass an empty list if no context is available.
    /// </param>
    /// <returns>Parsed callsign + number of leading tokens consumed, or null on no match.</returns>
    public static ParsedCallsign? TryParseLeading(string transcript, IReadOnlyCollection<string> activeCallsigns)
    {
        var tokens = Tokenize(transcript);
        return TryParseAt(tokens, startIndex: 0, forward: true, activeCallsigns);
    }

    /// <summary>
    /// Try to parse a callsign from the trailing tokens of a transcript.
    /// Used when pilots put the callsign at the end: "climb and maintain 5000 southwest 123".
    /// </summary>
    public static ParsedCallsign? TryParseTrailing(string transcript, IReadOnlyCollection<string> activeCallsigns)
    {
        var tokens = Tokenize(transcript);
        // Scan backward looking for a telephony start. We try each position from the end,
        // attempting a forward parse from there. The first full match wins.
        for (var i = tokens.Count - 1; i >= 0; i--)
        {
            var match = TryParseAt(tokens, i, forward: true, activeCallsigns);
            if (match is not null)
            {
                // Ensure the parse consumed through to the end of the transcript
                // (no leftover tokens after the callsign).
                if (i + match.TokensConsumed == tokens.Count)
                {
                    return new ParsedCallsign(match.IcaoCallsign, match.TokensConsumed);
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Convert an ICAO-format callsign to its primary spoken ATC form.
    /// <list type="bullet">
    ///   <item>
    ///     <description>
    ///       Airline (3 letters + digits): paired right-to-left flight number. "SWA123" →
    ///       "southwest one twenty three". This matches pilot phrasing on the radio.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <description>
    ///       US GA (N-prefix + digit): full digit-by-digit + NATO phonetic for trailing letters.
    ///       "N12345" → "november one two three four five", "N123BS" → "november one two three bravo sierra".
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <description>
    ///       Foreign GA / unknown: full NATO phonetic. "CPZXA" → "charlie papa zulu xray alpha".
    ///     </description>
    ///   </item>
    /// </list>
    /// For alternate spoken forms (digit-by-digit for airlines, shortened GA, type-based for GA),
    /// see <see cref="GetSpokenVariants"/>.
    /// </summary>
    public static string IcaoToSpoken(string icaoCallsign)
    {
        if (string.IsNullOrWhiteSpace(icaoCallsign))
        {
            return "";
        }

        var upper = icaoCallsign.Trim().ToUpperInvariant();

        // US GA: N + digit, spelled digit-by-digit + NATO phonetic for trailing letters.
        if (IsUsGa(upper))
        {
            return "november " + string.Join(' ', upper[1..].Select(SpellChar));
        }

        // Airline: 3-letter ICAO + digits. Use paired flight number (pilot phrasing).
        if (TrySplitAirline(upper, out var icao, out var flightNumber))
        {
            if (AirlineTelephony.TryGetTelephony(icao, out var telephony))
            {
                return telephony.ToLowerInvariant() + " " + PairedFlightNumberSpoken(flightNumber);
            }
        }

        // Foreign GA / unknown — spell every character in NATO phonetic.
        return string.Join(' ', upper.Select(SpellChar));
    }

    /// <summary>
    /// Return every reasonable spoken form of a callsign for Whisper <c>initial_prompt</c> seeding.
    /// Caller pumps the full list into the prompt so transcription is primed for any form a pilot
    /// might use.
    /// <list type="bullet">
    ///   <item>
    ///     <description>
    ///       Airline: paired flight number + digit-by-digit alternate. "SWA123" →
    ///       ["southwest one twenty three", "southwest one two three"].
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <description>
    ///       US GA: full "november" form + type-based forms (if <paramref name="aircraftType"/>
    ///       is known) + shortened last-3 forms when unambiguous against <paramref name="activeCallsigns"/>.
    ///       "N12345" with C172 → ["november one two three four five", "skyhawk one two three four five",
    ///       "cessna one two three four five", "november three four five", "skyhawk three four five",
    ///       "cessna three four five"].
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <description>
    ///       Foreign GA / unknown: the single NATO phonetic form (no variants).
    ///     </description>
    ///   </item>
    /// </list>
    /// </summary>
    /// <param name="icaoCallsign">ICAO-format callsign (airline or tail number).</param>
    /// <param name="aircraftType">Optional ICAO type designator (e.g. "C172"). Drives type-based variants.</param>
    /// <param name="activeCallsigns">Other active callsigns; used to check if the shortened GA form is unambiguous.</param>
    public static IReadOnlyList<string> GetSpokenVariants(string icaoCallsign, string? aircraftType, IReadOnlyCollection<string> activeCallsigns)
    {
        if (string.IsNullOrWhiteSpace(icaoCallsign))
        {
            return [];
        }

        var upper = icaoCallsign.Trim().ToUpperInvariant();
        var variants = new List<string>(6);

        // US GA path
        if (IsUsGa(upper))
        {
            var tail = upper[1..]; // everything after 'N'
            var fullDigitSpoken = string.Join(' ', tail.Select(SpellChar));

            // Collect the prefixes pilots might use: "november" + type-based names
            var prefixes = new List<string> { "november" };
            foreach (var typeName in AircraftTypeNames.GetSpokenNames(aircraftType))
            {
                if (!prefixes.Contains(typeName))
                {
                    prefixes.Add(typeName);
                }
            }

            // Full forms: prefix + full tail
            foreach (var prefix in prefixes)
            {
                AddIfNew(variants, $"{prefix} {fullDigitSpoken}");
            }

            // Shortened forms: last 3 chars, only if unambiguous and longer than 3
            if (tail.Length > 3 && IsShortenedGaUnambiguous(upper, activeCallsigns))
            {
                var shortTail = tail[^3..];
                var shortSpoken = string.Join(' ', shortTail.Select(SpellChar));
                foreach (var prefix in prefixes)
                {
                    AddIfNew(variants, $"{prefix} {shortSpoken}");
                }
            }
            return variants;
        }

        // Airline path
        if (TrySplitAirline(upper, out var icao, out var flightNumber))
        {
            if (AirlineTelephony.TryGetTelephony(icao, out var telephony))
            {
                var telLower = telephony.ToLowerInvariant();
                AddIfNew(variants, $"{telLower} {PairedFlightNumberSpoken(flightNumber)}");
                AddIfNew(variants, $"{telLower} {string.Join(' ', flightNumber.Select(SpellChar))}");
                return variants;
            }
        }

        // Foreign GA / unknown: single NATO phonetic form.
        AddIfNew(variants, string.Join(' ', upper.Select(SpellChar)));
        return variants;
    }

    private static bool IsUsGa(string upper)
    {
        return upper.Length >= 2 && upper[0] == 'N' && char.IsDigit(upper[1]);
    }

    private static bool TrySplitAirline(string upper, out string icao, out string flightNumber)
    {
        icao = "";
        flightNumber = "";
        if (upper.Length < 4)
        {
            return false;
        }
        if (!char.IsLetter(upper[0]) || !char.IsLetter(upper[1]) || !char.IsLetter(upper[2]))
        {
            return false;
        }
        if (!char.IsDigit(upper[3]))
        {
            return false;
        }
        icao = upper[..3];
        flightNumber = upper[3..];
        return true;
    }

    /// <summary>
    /// Paired spoken form of a flight number string. Trailing non-digit characters (e.g. "SWA123A")
    /// are appended as NATO phonetic: paired digits + " " + spelled letters.
    /// </summary>
    private static string PairedFlightNumberSpoken(string flightNumber)
    {
        // Split into leading digit run and trailing non-digit characters.
        var digitEnd = 0;
        while (digitEnd < flightNumber.Length && char.IsDigit(flightNumber[digitEnd]))
        {
            digitEnd++;
        }
        var digits = flightNumber[..digitEnd];
        var tail = flightNumber[digitEnd..];

        var parts = new List<string>();
        if (digits.Length > 0)
        {
            parts.Add(AtcNumberParser.FlightNumberToPairedWords(int.Parse(digits)));
        }
        if (tail.Length > 0)
        {
            parts.Add(string.Join(' ', tail.Select(SpellChar)));
        }
        return string.Join(' ', parts);
    }

    /// <summary>
    /// Check whether a GA callsign's last 3 characters are unique across other active US GA
    /// callsigns. If no other active N-number shares the same last-3, the shortened form
    /// ("november three four five") is unambiguous.
    /// </summary>
    private static bool IsShortenedGaUnambiguous(string callsign, IReadOnlyCollection<string> activeCallsigns)
    {
        if (callsign.Length < 5)
        {
            return false;
        }
        var last3 = callsign[^3..];
        foreach (var other in activeCallsigns)
        {
            if (string.IsNullOrEmpty(other))
            {
                continue;
            }
            var otherUpper = other.Trim().ToUpperInvariant();
            if (otherUpper == callsign)
            {
                continue;
            }
            if (otherUpper.Length >= 5 && otherUpper[0] == 'N' && otherUpper.EndsWith(last3, StringComparison.Ordinal))
            {
                return false;
            }
        }
        return true;
    }

    private static void AddIfNew(List<string> list, string item)
    {
        if (!string.IsNullOrWhiteSpace(item) && !list.Contains(item))
        {
            list.Add(item);
        }
    }

    // NATO phonetic alphabet — ATC phraseology for individual letters.
    private static readonly Dictionary<char, string> NatoPhonetic = new()
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

    private static string SpellChar(char c)
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
        return NatoPhonetic.TryGetValue(upper, out var word) ? word : upper.ToString();
    }

    private static ParsedCallsign? TryParseAt(List<string> tokens, int startIndex, bool forward, IReadOnlyCollection<string> activeCallsigns)
    {
        _ = forward; // Currently only parses forward; parameter kept for future backwards parsing.
        if (startIndex >= tokens.Count)
        {
            return null;
        }

        // GA: "november <digits> [<letter>]" (fully phonetic) OR "november N9225L" (hybrid —
        // Whisper saw N9225L in its initial_prompt and normalized the tail but kept the word
        // "november" the speaker said in front of it).
        if (string.Equals(tokens[startIndex], NovemberWord, StringComparison.OrdinalIgnoreCase))
        {
            // Phonetic form: consume one or more adjacent digit tokens (e.g. after NormalizeDigits
            // Whisper's partial normalization may produce ["9", "225"] rather than a single
            // "9225"), then optionally consume trailing NATO phonetic letters so
            // "november 9 225 lima" → N9225L and "november 123 bravo sierra" → N123BS.
            var digitStart = startIndex + 1;
            if (digitStart < tokens.Count && IsDigitString(tokens[digitStart]))
            {
                var sb = new System.Text.StringBuilder("N");
                var scan = digitStart;
                while (scan < tokens.Count && IsDigitString(tokens[scan]))
                {
                    sb.Append(tokens[scan]);
                    scan++;
                }
                while (scan < tokens.Count && TryNatoToLetter(tokens[scan], out var letter))
                {
                    sb.Append(letter);
                    scan++;
                }
                var callsign = sb.ToString();
                var resolved = FuzzyResolve(callsign, activeCallsigns);
                return new ParsedCallsign(resolved, scan - startIndex);
            }
            // Hybrid: "november N9225L" where Whisper already emitted the canonical form.
            if (digitStart < tokens.Count && IsUsGaIcaoToken(tokens[digitStart]))
            {
                var callsign = tokens[digitStart].ToUpperInvariant();
                var resolved = FuzzyResolve(callsign, activeCallsigns);
                return new ParsedCallsign(resolved, 2);
            }
            return null;
        }

        // Bare ICAO US GA token (Whisper fully normalized "november niner two two five lima"
        // into the canonical "N9225L" form because its initial_prompt was seeded with the ICAO
        // form). The regex N<digit>[A-Z0-9]* is distinctive enough that a false positive against
        // an English word is impossible, so we accept it regardless of activeCallsigns.
        if (IsUsGaIcaoToken(tokens[startIndex]))
        {
            var callsign = tokens[startIndex].ToUpperInvariant();
            var resolved = FuzzyResolve(callsign, activeCallsigns);
            return new ParsedCallsign(resolved, 1);
        }

        // Bare ICAO airline token (e.g. "SWA123") — only accept when it matches an active
        // callsign, because otherwise random 3-letter+digits tokens in transcripts could
        // produce false positives (e.g. pilots reading back runway + squawk sequences).
        var leadingUpper = tokens[startIndex].ToUpperInvariant();
        if (TrySplitAirline(leadingUpper, out _, out _))
        {
            foreach (var active in activeCallsigns)
            {
                if (string.Equals(active, leadingUpper, StringComparison.OrdinalIgnoreCase))
                {
                    return new ParsedCallsign(active, 1);
                }
            }
        }

        // Airline: try 3-word, 2-word, 1-word telephony prefix (longest-first for greedy match).
        for (var phraseLen = 3; phraseLen >= 1; phraseLen--)
        {
            if (startIndex + phraseLen > tokens.Count)
            {
                continue;
            }
            var phrase = string.Join(' ', tokens.GetRange(startIndex, phraseLen)).ToUpperInvariant();
            if (!AirlineTelephony.TryGetIcaos(phrase, out var candidates) || candidates.Count == 0)
            {
                continue;
            }

            // Flight number: next token must be digits.
            var flightTokenIdx = startIndex + phraseLen;
            if (flightTokenIdx >= tokens.Count || !IsDigitString(tokens[flightTokenIdx]))
            {
                continue;
            }
            var flightNumber = tokens[flightTokenIdx];
            var consumed = phraseLen + 1;

            // Pick the ICAO that matches an active callsign; otherwise first.
            var chosenIcao = candidates[0];
            foreach (var icao in candidates)
            {
                if (activeCallsigns.Contains(icao + flightNumber, StringComparer.OrdinalIgnoreCase))
                {
                    chosenIcao = icao;
                    break;
                }
            }

            var callsign = chosenIcao + flightNumber;
            var resolved = FuzzyResolve(callsign, activeCallsigns);
            return new ParsedCallsign(resolved, consumed);
        }

        return null;
    }

    /// <summary>
    /// If <paramref name="candidate"/> matches an active callsign exactly (case-insensitive),
    /// return the active form. Otherwise return the candidate unchanged. Currently exact-match
    /// only; true fuzzy matching (Levenshtein near-misses) can be added later.
    /// </summary>
    private static string FuzzyResolve(string candidate, IReadOnlyCollection<string> activeCallsigns)
    {
        foreach (var active in activeCallsigns)
        {
            if (string.Equals(active, candidate, StringComparison.OrdinalIgnoreCase))
            {
                return active;
            }
        }
        return candidate;
    }

    // Reverse NATO phonetic map — word → letter. Built once on first access from the forward
    // map below. Used to consume trailing letter tokens in GA callsigns (e.g. "lima" → 'L').
    private static readonly Dictionary<string, char> NatoWordToLetter = BuildNatoReverseMap();

    private static Dictionary<string, char> BuildNatoReverseMap()
    {
        var map = new Dictionary<string, char>(StringComparer.OrdinalIgnoreCase);
        foreach (var (letter, word) in NatoPhonetic)
        {
            map[word] = letter;
        }
        return map;
    }

    /// <summary>
    /// Try to interpret <paramref name="token"/> as a NATO phonetic letter word. "lima" → 'L',
    /// "bravo" → 'B', etc. "november" deliberately returns false because it's also the US GA
    /// prefix word — letting it match here would consume "november" after digits as if it were
    /// a suffix letter, breaking the caller's parse.
    /// </summary>
    private static bool TryNatoToLetter(string token, out char letter)
    {
        if (string.Equals(token, NovemberWord, StringComparison.OrdinalIgnoreCase))
        {
            letter = '\0';
            return false;
        }
        return NatoWordToLetter.TryGetValue(token, out letter);
    }

    /// <summary>
    /// True when <paramref name="token"/> is shaped like a US GA ICAO callsign: leading 'N',
    /// followed by a digit, then any mix of letters/digits. Matches "N9225L", "N12345", "N7AB".
    /// Distinctive enough that no English word collides, so callers can accept it without a
    /// tiebreaker against active callsigns.
    /// </summary>
    private static bool IsUsGaIcaoToken(string token)
    {
        if (token.Length < 2)
        {
            return false;
        }
        if (token[0] != 'n' && token[0] != 'N')
        {
            return false;
        }
        if (!char.IsDigit(token[1]))
        {
            return false;
        }
        for (var i = 2; i < token.Length; i++)
        {
            if (!char.IsLetterOrDigit(token[i]))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsDigitString(string s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return false;
        }
        foreach (var c in s)
        {
            if (c < '0' || c > '9')
            {
                return false;
            }
        }
        return true;
    }

    private static List<string> Tokenize(string transcript)
    {
        var tokens = new List<string>();
        var start = -1;
        for (var i = 0; i < transcript.Length; i++)
        {
            var c = transcript[i];
            var isWord = char.IsLetterOrDigit(c);
            if (isWord)
            {
                if (start == -1)
                {
                    start = i;
                }
            }
            else
            {
                if (start != -1)
                {
                    tokens.Add(transcript[start..i].ToLowerInvariant());
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
}
