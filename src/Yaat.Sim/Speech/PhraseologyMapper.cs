using Yaat.Sim.Commands;

namespace Yaat.Sim.Speech;

/// <summary>
/// Converts a natural-language ATC phraseology transcript into a canonical command string
/// for the YAAT command pipeline. This is the rule-based layer of the hybrid NLU described in
/// <c>docs/plans/speech-recognition.md</c>; if the rules don't match, the LLM fallback in
/// <c>Yaat.Client.Services.LocalLlmCommandMapper</c> takes over.
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

    // "Soft" filler words that are stripped only on a SECOND matching pass, if the first pass
    // didn't match any rule that uses them as a literal. This lets rules that legitimately use
    // these words (e.g. "cleared for takeoff") match on pass 1, while transcripts that use the
    // same words as conversational fillers ("enter right downwind FOR runway 28R") still resolve
    // by stripping the filler and retrying. See <see cref="Map"/> for the two-pass logic.
    private static readonly HashSet<string> SecondPassFillers = new(StringComparer.OrdinalIgnoreCase) { "for" };

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

        // Step 2: tokenize, strip filler, collapse custom fix names. Runway designator collapse
        // already happened inside AtcNumberParser.NormalizeDigits above so the canonical "28R"
        // form is visible to both this rule engine and the LLM fallback in
        // SpeechRecognitionService.MapTranscriptAsync.
        var tokens = Tokenize(normalized);
        tokens = tokens.Where(t => !FillerWords.Contains(t)).ToList();
        if (context.CustomFixPatterns.Count > 0)
        {
            tokens = CollapseCustomFixNames(tokens, context.CustomFixPatterns);
        }

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

        // Step 5: greedy left-to-right longest-match — two-pass over the token list.
        //
        // Pass 1 runs against the tokens as-is, so rules that use SecondPassFillers as literals
        // (e.g. "cleared for takeoff") match cleanly. If pass 1 doesn't end up using any such
        // rule but the input does contain a second-pass filler — the user said something like
        // "enter right downwind for runway 28R", where "for" is conversational filler between
        // two rule literals — pass 2 strips the filler and retries. Pass 2 is preferred when it
        // produces strictly more outputs or consumes strictly more input tokens than pass 1,
        // so a "for"-rule match in pass 1 always beats an equivalent pass 2 result.
        var matchedRulesPass1 = new List<PhraseologyRule>();
        var (outputs, consumedPass1) = MatchTokens(tokens, context, matchedRulesPass1);

        var pass1UsedSecondPassFiller = matchedRulesPass1.Any(r => r.Pattern.Any(p => SecondPassFillers.Contains(p)));
        var inputContainsSecondPassFiller = tokens.Any(t => SecondPassFillers.Contains(t));
        if (inputContainsSecondPassFiller && !pass1UsedSecondPassFiller)
        {
            var strippedTokens = tokens.Where(t => !SecondPassFillers.Contains(t)).ToList();
            var matchedRulesPass2 = new List<PhraseologyRule>();
            var (outputsPass2, consumedPass2) = MatchTokens(strippedTokens, context, matchedRulesPass2);

            // Pass 2 wins on more outputs OR same outputs but more raw tokens consumed. Comparing
            // raw consumed counts is fair here because pass 2's input is a subset of pass 1's
            // (only the filler tokens were removed); if pass 2 consumed more, it must have matched
            // additional non-filler tokens beyond what pass 1 reached.
            if (outputsPass2.Count > outputs.Count || (outputsPass2.Count == outputs.Count && consumedPass2 > consumedPass1))
            {
                outputs = outputsPass2;
            }
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
    /// Greedy left-to-right matching loop. Walks <paramref name="tokens"/> from start to end,
    /// emitting one canonical clause per longest-match. Mutates <paramref name="matchedRules"/>
    /// in place with the rule that produced each output (the two-pass logic in <see cref="Map"/>
    /// uses this to detect whether any matched rule literally referenced a second-pass filler).
    /// Returns the output list and the total number of input tokens consumed by successful
    /// matches (skipped tokens via the no-match advance and connector skip don't count).
    /// </summary>
    private static (List<string> Outputs, int TotalConsumed) MatchTokens(List<string> tokens, MapContext context, List<PhraseologyRule> matchedRules)
    {
        var outputs = new List<string>();
        var totalConsumed = 0;
        var idx = 0;
        while (idx < tokens.Count)
        {
            // "disregard" — controller cancels every instruction issued earlier in the same
            // transmission. Clear whatever we've parsed so far and keep going; anything after
            // "disregard" is the new, active instruction set.
            if (string.Equals(tokens[idx], "disregard", StringComparison.OrdinalIgnoreCase))
            {
                outputs.Clear();
                matchedRules.Clear();
                totalConsumed = 0;
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
            matchedRules.Add(best.Value.Rule);
            totalConsumed += best.Value.Consumed;
            idx += best.Value.Consumed;
        }
        return (outputs, totalConsumed);
    }

    /// <summary>
    /// Find the best-matching rule starting at <paramref name="start"/>. "Best" prefers:
    /// <list type="number">
    ///   <item><description>More tokens consumed (longest match).</description></item>
    ///   <item><description>Fewer captures (more specific literal wins the tie — so <c>squawk vfr → SQVFR</c>
    ///     beats <c>squawk {code} → SQ vfr</c>).</description></item>
    /// </list>
    /// Returns null if no rule matches. The matched rule itself is returned alongside the output
    /// + consumed count so callers (the two-pass loop) can inspect which rule fired without
    /// re-running the matcher.
    /// </summary>
    private static (string Output, int Consumed, PhraseologyRule Rule)? FindLongestMatch(List<string> tokens, int start, MapContext context)
    {
        (string Output, int Consumed, int CaptureCount, PhraseologyRule Rule)? best = null;
        foreach (var rule in PhraseologyRules.All)
        {
            if (TryMatchRule(rule, tokens, start, context, out var consumed, out var output))
            {
                var captureCount = rule.Pattern.Count(p => p.StartsWith('{') && p.EndsWith('}'));
                var candidate = (output, consumed, captureCount, rule);
                if (best is null || consumed > best.Value.Consumed || (consumed == best.Value.Consumed && captureCount < best.Value.CaptureCount))
                {
                    best = candidate;
                }
            }
        }
        return best is null ? null : (best.Value.Output, best.Value.Consumed, best.Value.Rule);
    }

    /// <summary>
    /// Attempt to match a single rule against <paramref name="tokens"/> starting at
    /// <paramref name="start"/>. Optional tokens (<c>literal?</c>) are tried both present and
    /// absent — the first successful path wins. After captures are collected, fix-like ones
    /// are post-processed through <see cref="PhoneticFixMatcher"/> against the context's
    /// programmed fix set, and the final filled template is validated by <see cref="CommandParser"/>
    /// so noisy captures (e.g. <c>CM main</c> from "climb to main aim ...") are rejected.
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
            var filled = FillTemplate(rule.OutputTemplate, captures);
            // Validate the filled canonical via the same parser the terminal input uses. This is
            // the single source of truth for what's a valid command — it checks verb aliases,
            // argument shapes, altitude/heading/speed ranges, runway designators, etc. If the
            // parser rejects it, this rule doesn't match: the greedy engine will either try
            // another rule or advance one token. Catches Whisper mistranscriptions like
            // "climb to main aim flight level tree five zero" where "{alt}" would capture
            // "main" and produce the nonsense canonical "CM main".
            if (!CommandParser.Parse(filled).IsSuccess)
            {
                output = "";
                return false;
            }
            output = filled;
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
        // Tokens are NOT lowercased: AtcNumberParser.NormalizeDigits already emits lowercase
        // input, and the only mixed-case tokens are runway designators (e.g. "28R") that
        // CollapseRunwayDesignators inserted with intentional uppercase suffixes. Re-lowercasing
        // here would clobber those, breaking {rwy} captures that flow through to canonical
        // output. Pattern matching, filler matching, and connector matching all use
        // OrdinalIgnoreCase comparisons so case preservation here is safe.
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
                    tokens.Add(transcript[start..i]);
                    start = -1;
                }
            }
        }
        if (start != -1)
        {
            tokens.Add(transcript[start..]);
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
    /// <summary>
    /// Collapses split runway designator tokens into single canonical tokens: <c>["28", "right"]</c>
    /// → <c>["28R"]</c>, <c>["18", "left"]</c> → <c>["18L"]</c>, <c>["27", "center"]</c> → <c>["27C"]</c>.
    /// Internal so <see cref="AtcNumberParser.NormalizeDigits"/> can call this as part of its
    /// pipeline; that way both the rule engine path and the LLM fallback see the canonical
    /// runway form ("runway 28R") rather than the split form ("runway 28 right").
    /// </summary>
    internal static List<string> CollapseRunwayDesignators(List<string> tokens)
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

    /// <summary>
    /// Greedy longest-match substitution of custom-fix spoken phrases into canonical-alias
    /// tokens. At each position in the transcript, try every registered pattern and substitute
    /// the longest one that matches. Patterns are pre-sorted longest-first by
    /// <c>NavigationDatabase.LoadCustomFixes</c> so the first hit is always the greediest.
    ///
    /// After this pass, the transcript's natural-language phrase ("the runway 30 numbers") has
    /// been replaced with a single token ("OAK30NUM") that downstream <c>{fix}</c> rule captures
    /// treat like any other fix identifier.
    /// </summary>
    internal static List<string> CollapseCustomFixNames(List<string> tokens, IReadOnlyList<CustomFixSpeechPattern> patterns)
    {
        if (patterns.Count == 0)
        {
            return tokens;
        }

        var output = new List<string>(tokens.Count);
        var i = 0;
        while (i < tokens.Count)
        {
            CustomFixSpeechPattern? matched = null;
            foreach (var pattern in patterns)
            {
                if (pattern.Tokens.Count == 0 || i + pattern.Tokens.Count > tokens.Count)
                {
                    continue;
                }

                var allMatch = true;
                for (var k = 0; k < pattern.Tokens.Count; k++)
                {
                    if (!string.Equals(tokens[i + k], pattern.Tokens[k], StringComparison.OrdinalIgnoreCase))
                    {
                        allMatch = false;
                        break;
                    }
                }

                if (allMatch)
                {
                    matched = pattern;
                    break;
                }
            }

            if (matched is not null)
            {
                output.Add(matched.CanonicalAlias);
                i += matched.Tokens.Count;
                continue;
            }

            output.Add(tokens[i]);
            i++;
        }

        return output;
    }
}
