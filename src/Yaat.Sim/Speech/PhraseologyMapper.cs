namespace Yaat.Sim.Speech;

/// <summary>
/// Converts a natural-language ATC phraseology transcript into a canonical command string
/// for the YAAT command pipeline. This is the rule-based layer of the hybrid NLU described in
/// <c>docs/plans/speech-recognition.md</c>; if the rules don't match, a future Phase 4 LLM
/// fallback can take over.
/// </summary>
/// <remarks>
/// Pipeline per transcript:
/// <list type="number">
///   <item><description>Normalize spoken numbers via <see cref="AtcNumberParser.NormalizeDigits"/>.</description></item>
///   <item><description>Tokenize and strip filler words ("uh", "please", "sir").</description></item>
///   <item><description>Extract callsign from the leading or trailing tokens via <see cref="CallsignParser"/>.</description></item>
///   <item><description>Extract condition prefix ("at <c>{fix}</c>", "when level at <c>{alt}</c>").</description></item>
///   <item><description>Greedy left-to-right longest-match against <see cref="PhraseologyRules.All"/>.</description></item>
///   <item><description>Skip compound connectors ("and", "then") between matched clauses.</description></item>
/// </list>
/// </remarks>
public static class PhraseologyMapper
{
    /// <summary>Context passed by the caller to aid matching.</summary>
    /// <param name="ActiveCallsigns">
    /// Callsigns currently in the scenario — used by <see cref="CallsignParser"/> to disambiguate
    /// shared-telephony airlines (e.g. VIRGIN → VIR vs VOZ).
    /// </param>
    /// <param name="ProgrammedFixes">
    /// Fixes known to the relevant aircraft; reserved for Phase 3 (<c>PhoneticFixMatcher</c>) but
    /// accepted here so the public surface is stable.
    /// </param>
    public sealed record MapContext(IReadOnlyCollection<string> ActiveCallsigns, IReadOnlyCollection<string> ProgrammedFixes)
    {
        /// <summary>Empty context — used when the caller has no scenario state.</summary>
        public static MapContext Empty { get; } = new([], []);
    }

    /// <summary>Result of mapping a transcript. Null return means no part of the transcript matched.</summary>
    /// <param name="Callsign">Extracted ICAO callsign (e.g. "SWA123"), or null if none found.</param>
    /// <param name="CanonicalCommand">Comma-separated canonical commands (e.g. "CM 5000, FH 270").</param>
    /// <param name="MatchedRuleCount">How many clauses matched a rule. Useful as a confidence signal.</param>
    public sealed record MapResult(string? Callsign, string CanonicalCommand, int MatchedRuleCount);

    // Filler words stripped before matching. Kept conservative so real command words stay intact.
    private static readonly HashSet<string> FillerWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "uh",
        "um",
        "er",
        "please",
        "sir",
        "maam",
        "ma'am",
        "okay",
        "ok",
        "alright",
        "thanks",
        "thank",
        "you",
    };

    // Connector words skipped between clauses in compound commands.
    private static readonly HashSet<string> CompoundConnectors = new(StringComparer.OrdinalIgnoreCase) { "and", "then", "also" };

    // Capture names that should be post-processed through PhoneticFixMatcher — these are
    // the navigation fix names Whisper most often mistranscribes as English words.
    private static readonly HashSet<string> FixLikeCaptureNames = new(StringComparer.OrdinalIgnoreCase) { "fix", "current" };

    /// <summary>
    /// Map a transcript to a canonical YAAT command. Returns null when no rule matched any part
    /// of the transcript.
    /// </summary>
    public static MapResult? Map(string transcript, MapContext context)
    {
        if (string.IsNullOrWhiteSpace(transcript))
        {
            return null;
        }

        // Step 1: normalize spoken numbers to digits.
        var normalized = AtcNumberParser.NormalizeDigits(transcript);

        // Step 2: tokenize, strip filler, collapse runway designators.
        var tokens = Tokenize(normalized);
        tokens = tokens.Where(t => !FillerWords.Contains(t)).ToList();
        tokens = CollapseRunwayDesignators(tokens);
        if (tokens.Count == 0)
        {
            return null;
        }

        // Step 3: extract callsign from leading or trailing tokens.
        var callsign = ExtractCallsign(tokens, context.ActiveCallsigns, out var callsignStart, out var callsignEnd);
        if (callsign is not null)
        {
            // Remove the callsign tokens from the list.
            tokens = RemoveRange(tokens, callsignStart, callsignEnd - callsignStart);
        }

        // Step 4: extract condition prefix ("at {fix}", "when level at {alt}", "when {condition}").
        var conditionPrefix = ExtractConditionPrefix(tokens, out var conditionConsumed);
        if (conditionPrefix is not null)
        {
            tokens = tokens.Skip(conditionConsumed).ToList();
        }

        // Step 5: greedy left-to-right longest-match.
        var outputs = new List<string>();
        var idx = 0;
        while (idx < tokens.Count)
        {
            // "disregard" — controller cancels every instruction issued earlier in the same
            // transmission. Clear whatever we've parsed so far and keep going; anything after
            // "disregard" is the new, active instruction set.
            if (string.Equals(tokens[idx], "disregard", StringComparison.OrdinalIgnoreCase))
            {
                outputs.Clear();
                idx++;
                continue;
            }

            // Skip compound connectors between clauses.
            if (CompoundConnectors.Contains(tokens[idx]))
            {
                idx++;
                continue;
            }

            var best = FindLongestMatch(tokens, idx, context);
            if (best is null)
            {
                // No rule matches here — advance one token and keep trying.
                idx++;
                continue;
            }

            outputs.Add(best.Value.Output);
            idx += best.Value.Consumed;
        }

        if (outputs.Count == 0)
        {
            return null;
        }

        var canonical = string.Join(", ", outputs);
        if (conditionPrefix is not null)
        {
            canonical = conditionPrefix + " " + canonical;
        }

        return new MapResult(callsign, canonical, outputs.Count);
    }

    /// <summary>
    /// Find the best-matching rule starting at <paramref name="start"/>. "Best" prefers:
    /// <list type="number">
    ///   <item><description>More tokens consumed (longest match).</description></item>
    ///   <item><description>Fewer captures (more specific literal wins the tie — so <c>squawk vfr → SQVFR</c>
    ///     beats <c>squawk {code} → SQ vfr</c>).</description></item>
    /// </list>
    /// Returns null if no rule matches.
    /// </summary>
    private static (string Output, int Consumed)? FindLongestMatch(List<string> tokens, int start, MapContext context)
    {
        (string Output, int Consumed, int CaptureCount)? best = null;
        foreach (var rule in PhraseologyRules.All)
        {
            if (TryMatchRule(rule, tokens, start, context, out var consumed, out var output))
            {
                var captureCount = rule.Pattern.Count(p => p.StartsWith('{') && p.EndsWith('}'));
                var candidate = (output, consumed, captureCount);
                if (best is null || consumed > best.Value.Consumed || (consumed == best.Value.Consumed && captureCount < best.Value.CaptureCount))
                {
                    best = candidate;
                }
            }
        }
        return best is null ? null : (best.Value.Output, best.Value.Consumed);
    }

    /// <summary>
    /// Attempt to match a single rule against <paramref name="tokens"/> starting at
    /// <paramref name="start"/>. Optional tokens (<c>literal?</c>) are tried both present and
    /// absent — the first successful path wins. After captures are collected, fix-like ones
    /// are post-processed through <see cref="PhoneticFixMatcher"/> against the context's
    /// programmed fix set.
    /// </summary>
    private static bool TryMatchRule(PhraseologyRule rule, List<string> tokens, int start, MapContext context, out int consumed, out string output)
    {
        var captures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (TryMatchPattern(rule.Pattern, 0, tokens, start, captures, out consumed))
        {
            // Post-pass: correct fix-like captures using the phonetic matcher. Runs only for
            // capture names we know represent fix references (e.g. {fix}, {current}).
            foreach (var name in FixLikeCaptureNames)
            {
                if (captures.TryGetValue(name, out var rawValue))
                {
                    var matched = PhoneticFixMatcher.TryMatch(rawValue, context.ProgrammedFixes);
                    if (matched is not null)
                    {
                        captures[name] = matched;
                    }
                }
            }
            output = FillTemplate(rule.OutputTemplate, captures);
            return true;
        }
        output = "";
        return false;
    }

    /// <summary>
    /// Recursive pattern matcher. Handles optional-token branching by trying both present-and-absent
    /// paths. Depth is bounded by pattern length, so recursion is cheap.
    /// </summary>
    private static bool TryMatchPattern(
        string[] pattern,
        int patternIdx,
        List<string> tokens,
        int tokenIdx,
        Dictionary<string, string> captures,
        out int consumed
    )
    {
        consumed = 0;
        var startTokenIdx = tokenIdx;

        while (patternIdx < pattern.Length)
        {
            var p = pattern[patternIdx];

            // Capture group {name}
            if (p.StartsWith('{') && p.EndsWith('}'))
            {
                if (tokenIdx >= tokens.Count)
                {
                    return false;
                }
                var name = p[1..^1];
                captures[name] = tokens[tokenIdx];
                tokenIdx++;
                patternIdx++;
                continue;
            }

            // Optional literal: literal?
            if (p.EndsWith('?'))
            {
                var literal = p[..^1];
                // Try matching the optional token present first. If it matches, we consume
                // it; if not, we skip it. In both branches we recurse on the remaining pattern.
                if (tokenIdx < tokens.Count && string.Equals(tokens[tokenIdx], literal, StringComparison.OrdinalIgnoreCase))
                {
                    // Present branch
                    var captureSnapshot = new Dictionary<string, string>(captures, StringComparer.OrdinalIgnoreCase);
                    if (TryMatchPattern(pattern, patternIdx + 1, tokens, tokenIdx + 1, captureSnapshot, out var subConsumed))
                    {
                        foreach (var kvp in captureSnapshot)
                        {
                            captures[kvp.Key] = kvp.Value;
                        }
                        consumed = tokenIdx + 1 + subConsumed - startTokenIdx;
                        return true;
                    }
                }
                // Absent branch — fall through to next pattern token without consuming.
                patternIdx++;
                continue;
            }

            // Required literal
            if (tokenIdx >= tokens.Count || !string.Equals(tokens[tokenIdx], p, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            tokenIdx++;
            patternIdx++;
        }

        consumed = tokenIdx - startTokenIdx;
        return true;
    }

    private static string FillTemplate(string template, Dictionary<string, string> captures)
    {
        var result = template;
        foreach (var kvp in captures)
        {
            result = result.Replace("{" + kvp.Key + "}", kvp.Value, StringComparison.OrdinalIgnoreCase);
        }
        return result;
    }

    /// <summary>
    /// Try to find a callsign in either the leading or trailing tokens. Returns the ICAO form
    /// and the token index range (exclusive end) to remove from the working token list.
    /// </summary>
    private static string? ExtractCallsign(List<string> tokens, IReadOnlyCollection<string> activeCallsigns, out int start, out int end)
    {
        start = 0;
        end = 0;

        // Reassemble the token list as a space-separated string so CallsignParser can tokenize
        // it the same way it does standalone transcripts. This keeps both extractors in sync.
        var joined = string.Join(' ', tokens);
        var leading = CallsignParser.TryParseLeading(joined, activeCallsigns);
        if (leading is not null)
        {
            start = 0;
            end = leading.TokensConsumed;
            return leading.IcaoCallsign;
        }

        var trailing = CallsignParser.TryParseTrailing(joined, activeCallsigns);
        if (trailing is not null)
        {
            start = tokens.Count - trailing.TokensConsumed;
            end = tokens.Count;
            return trailing.IcaoCallsign;
        }

        return null;
    }

    /// <summary>
    /// Parse condition prefixes at the start of the token list. Supported:
    /// <list type="bullet">
    ///   <item><description><c>at {fix}</c> → <c>AT {fix}</c></description></item>
    ///   <item><description><c>when level at? {alt}</c> → <c>LV {alt}</c></description></item>
    /// </list>
    /// </summary>
    private static string? ExtractConditionPrefix(List<string> tokens, out int consumed)
    {
        consumed = 0;
        if (tokens.Count < 2)
        {
            return null;
        }

        // "at {fix}" — but be careful: "at" is a short, common word and we want to avoid false
        // matches in non-condition contexts. Only match if the next token is not a digit string
        // (digit strings signal a normalized number, which would mean "at 5000" = level condition
        // not fix condition — handled by the other branch).
        if (string.Equals(tokens[0], "at", StringComparison.OrdinalIgnoreCase))
        {
            var next = tokens[1];
            if (next.Length > 0 && !char.IsDigit(next[0]))
            {
                consumed = 2;
                return $"AT {next.ToUpperInvariant()}";
            }
        }

        // "when level {alt}" or "when level at {alt}"
        if (
            tokens.Count >= 3
            && string.Equals(tokens[0], "when", StringComparison.OrdinalIgnoreCase)
            && string.Equals(tokens[1], "level", StringComparison.OrdinalIgnoreCase)
        )
        {
            var altIdx = 2;
            if (altIdx < tokens.Count && string.Equals(tokens[altIdx], "at", StringComparison.OrdinalIgnoreCase))
            {
                altIdx++;
            }
            if (altIdx < tokens.Count && IsDigitString(tokens[altIdx]))
            {
                consumed = altIdx + 1;
                return $"LV {tokens[altIdx]}";
            }
        }

        return null;
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
            if (char.IsLetterOrDigit(c))
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

    private static List<string> RemoveRange(List<string> tokens, int start, int count)
    {
        var copy = new List<string>(tokens.Count - count);
        for (var i = 0; i < tokens.Count; i++)
        {
            if (i < start || i >= start + count)
            {
                copy.Add(tokens[i]);
            }
        }
        return copy;
    }

    /// <summary>
    /// Collapse "NN left/right/center" into "NNL/NNR/NNC" so rules can capture a single runway
    /// designator token. Also handles "NN l" / "NN r" / "NN c" short forms.
    /// </summary>
    private static List<string> CollapseRunwayDesignators(List<string> tokens)
    {
        var output = new List<string>(tokens.Count);
        var i = 0;
        while (i < tokens.Count)
        {
            if (i + 1 < tokens.Count && IsDigitString(tokens[i]))
            {
                var next = tokens[i + 1];
                char? suffix = next switch
                {
                    "left" or "l" => 'L',
                    "right" or "r" => 'R',
                    "center" or "centre" or "c" => 'C',
                    _ => null,
                };
                if (suffix is not null)
                {
                    output.Add(tokens[i] + suffix);
                    i += 2;
                    continue;
                }
            }
            output.Add(tokens[i]);
            i++;
        }
        return output;
    }
}
