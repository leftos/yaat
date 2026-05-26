using System.Text;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Speech;

namespace Yaat.Client.Services;

/// <summary>
/// <see cref="ISpeechCommandMapper"/> backed by a local GGUF LLM via <see cref="LocalLlmService"/>.
/// The speech pipeline invokes this as a fallback when <see cref="PhraseologyCommandMapper"/>
/// returns null (i.e. the rule engine didn't understand the transcript).
///
/// Returns null when:
/// - speech is disabled,
/// - no GGUF is configured,
/// - the LLM produces output that doesn't parse as a plausible canonical command.
/// </summary>
public sealed class LocalLlmCommandMapper : ISpeechCommandMapper
{
    private static readonly ILogger Log = AppLog.CreateLogger<LocalLlmCommandMapper>();

    // Condition prefixes the phraseology engine produces — "AT FIX ..." / "LV ALT ...". We strip
    // these from a clause before validating the verb so the fallback can produce prefixed output too.
    private static readonly HashSet<string> ConditionPrefixes = new(StringComparer.OrdinalIgnoreCase) { "AT", "LV" };

    private readonly LocalLlmService _llm;
    private readonly Lazy<string> _systemPrompt;

    public LocalLlmCommandMapper(LocalLlmService llm)
    {
        _llm = llm;
        _systemPrompt = new Lazy<string>(BuildSystemPrompt);
    }

    /// <summary>
    /// Constructs a mapper with an explicit system prompt — used by the speech sandbox tool to
    /// iterate on prompt phrasing without rebuilding the mapper class. Production callers use
    /// the single-arg constructor which derives the prompt from <see cref="PhraseologyRules"/>.
    /// </summary>
    public LocalLlmCommandMapper(LocalLlmService llm, string customSystemPrompt)
    {
        _llm = llm;
        _systemPrompt = new Lazy<string>(() => customSystemPrompt);
    }

    /// <summary>
    /// Returns the default system prompt the production mapper uses. Exposed so the sandbox tool
    /// can fetch the current prompt as the starting point for iteration.
    /// </summary>
    public static string GetDefaultSystemPrompt() => BuildSystemPrompt();

    public async Task<MapResult?> MapAsync(string transcript, MapContext context, CancellationToken ct)
    {
        var (result, _) = await MapWithTraceAsync(transcript, context, ct).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Trace-collecting variant of <see cref="MapAsync"/>. Always returns a populated
    /// <see cref="LlmMapperTrace"/> capturing the exact system prompt, per-call user prompt, raw
    /// model output, and post-normalization canonical (or a failure reason on miss). The speech
    /// debug pipeline calls this directly so the in-client debug window can surface what the LLM
    /// actually saw and produced for any session that fell through the rule mapper.
    /// </summary>
    public async Task<(MapResult? Result, LlmMapperTrace Trace)> MapWithTraceAsync(string transcript, MapContext context, CancellationToken ct)
    {
        var systemPrompt = _systemPrompt.Value;

        if (string.IsNullOrWhiteSpace(transcript))
        {
            return (null, new LlmMapperTrace(systemPrompt, string.Empty, string.Empty, null, "empty transcript"));
        }

        var userPrompt = BuildUserPrompt(transcript, context);
        // Constrain generation with the canonical-command GBNF derived from CommandRegistry.
        // The grammar guarantees the model can only emit syntactically valid clauses ("CM 5000",
        // "AT CEPIN CAPP ILS28R", etc.) so NormalizeOutput's verb/charset checks become a cheap
        // defence-in-depth instead of the only line of defence. NEW commands automatically expand
        // the grammar because CanonicalCommandGrammar enumerates the registry at runtime.
        var raw = await _llm.GenerateAsync(systemPrompt, userPrompt, CanonicalCommandGrammar.Default, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return (null, new LlmMapperTrace(systemPrompt, userPrompt, raw ?? string.Empty, null, "LLM produced empty output"));
        }

        var canonical = NormalizeOutput(raw);
        if (canonical is null)
        {
            Log.LogDebug("LLM output did not parse as canonical command: {Raw}", raw);
            return (null, new LlmMapperTrace(systemPrompt, userPrompt, raw, null, "output failed canonical validation"));
        }

        // Rule engine matches are cheap enough that if the LLM echoes a rule-shaped string, we
        // trust it. We don't have a callsign extraction step here — the speech pipeline re-runs
        // CallsignParser on the transcript for the LLM path if needed.
        Log.LogInformation("LLM fallback produced canonical command: {Canonical}", canonical);
        return (new MapResult(null, canonical, 1), new LlmMapperTrace(systemPrompt, userPrompt, raw, canonical, null));
    }

    /// <summary>
    /// Builds the per-call user prompt the way <see cref="MapAsync"/> does. Exposed as
    /// <c>internal</c> so the speech sandbox and integration tests can dump the exact prompt
    /// the LLM would receive — paste it into <c>tools/Yaat.SpeechSandbox</c>'s probe UI to
    /// iterate on prompt phrasing without rebuilding the production class.
    /// </summary>
    internal static string BuildUserPromptForDebug(string transcript, MapContext context) => BuildUserPrompt(transcript, context);

    /// <summary>
    /// Run the LLM with explicit pre-built system and user prompts, bypassing
    /// <see cref="BuildUserPrompt"/>. Used by the speech sandbox when the user has manually edited
    /// the user prompt textbox and wants to test that exact prompt instead of regenerating it
    /// from the input fields. Result still flows through <see cref="NormalizeOutput"/> and the
    /// canonical-command grammar so the sandbox sees the same post-processing the production
    /// path applies. Returns null on the same conditions as <see cref="MapAsync"/>: empty
    /// generation, normalize-rejected output, or LLM unavailable.
    /// </summary>
    internal async Task<MapResult?> MapWithPromptsAsync(string systemPrompt, string userPrompt, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(userPrompt))
        {
            return null;
        }

        var raw = await _llm.GenerateAsync(systemPrompt, userPrompt, CanonicalCommandGrammar.Default, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var canonical = NormalizeOutput(raw);
        if (canonical is null)
        {
            Log.LogDebug("LLM output did not parse as canonical command (override prompt path): {Raw}", raw);
            return null;
        }

        return new MapResult(null, canonical, 1);
    }

    private static string BuildUserPrompt(string transcript, MapContext context)
    {
        // A flat "Active callsigns:" line is deliberately NOT included here — the small instruct
        // model echoed callsigns as "AT N314GT AT N346G ..." condition-prefixed clauses when it
        // saw a bare list. The callsign → destination map below is safer because the arrow gives
        // the model a clear "this is metadata, not a command" framing, and the destination column
        // is what enables runway recovery (the model can pick the right airport's runway list
        // when the transcript only has a partial/noisy callsign).
        var sb = new StringBuilder();
        sb.Append("Transcript: ").AppendLine(transcript);

        if (context.ProgrammedFixes.Count > 0)
        {
            sb.Append("Programmed fixes: ").AppendLine(string.Join(", ", context.ProgrammedFixes));
        }

        // Available runways grouped by airport: lets the model recover a misheard runway like
        // "288" → "28R" by snapping to a real designator at the relevant airport. Compact format
        // ("KOAK: 28R 28L 10R 10L 30 12 33 15") keeps the prompt small even when several airports
        // are in scope. The directive line is critical — without it, greedy decoding on small
        // models just echoes the misheard runway from the transcript verbatim instead of snapping
        // to the closest real runway. Tested 2026-04-14 against gemma4:e4b: without the directive,
        // "288" → "ERD 288"; with it, "288" → "ERD 28R".
        if (context.AvailableRunways.Count > 0)
        {
            sb.AppendLine("Available runways:");
            foreach (var (airport, runways) in context.AvailableRunways)
            {
                sb.Append("  ").Append(airport).Append(": ").AppendLine(string.Join(' ', runways));
            }
            sb.AppendLine(
                "If the transcript mentions a runway that is not in the list above, replace it with the closest matching runway from the list."
            );
        }

        // Active aircraft → destination so the model can pair an in-transcript callsign with the
        // right airport's runway list above. The arrow notation matches the framing the instruct
        // models tend to read as "lookup table" rather than "instruction list".
        if (context.AircraftDestinations.Count > 0)
        {
            sb.AppendLine("Active aircraft (callsign -> destination):");
            foreach (var (cs, dest) in context.AircraftDestinations)
            {
                sb.Append("  ").Append(cs).Append(" -> ").AppendLine(dest);
            }
        }

        sb.AppendLine("Output only the canonical command string, nothing else.");
        return sb.ToString();
    }

    private static string BuildSystemPrompt()
    {
        // Compact reference: canonical verb → category → sample arg. One line per command.
        // Derive the prompt body directly from PhraseologyRules.All. Each canonical output gets
        // one line listing all its natural-language spoken variants, which is exactly the signal
        // a small instruct model needs to discriminate between similar commands. An earlier
        // version of this prompt dumped CommandRegistry alphabetically with labels — the model
        // picked wrong verbs (CM vs DM, FH vs FPH, RD vs SPD) because the label text didn't line
        // up with the spoken phrasing. Rule-derived is both smaller AND more accurate.
        //
        // ⚠️ SMALL-MODEL PROMPT SENSITIVITY: on Qwen2.5-1.5B Q4_K_M with Temperature=0, a single
        // character change in this prompt flipped "fly heading two seven zero" from "FH 270" to
        // "FPH" (verified by the opt-in integration suite). Before making cosmetic edits here
        // (rephrasing, punctuation, reordering), run LocalLlmPipelineIntegrationTests against the
        // recommended test model and confirm all cases still pass exact-match. A future
        // iteration can make this robust to prompt variants by using a larger / better-instructed
        // model.
        var sb = new StringBuilder();
        sb.AppendLine("You convert spoken ATC instructions into YAAT canonical commands.");
        sb.AppendLine();
        sb.AppendLine("Rules:");
        sb.AppendLine("- Output ONLY the canonical command. No quotes, no explanation, no prose.");
        sb.AppendLine("- Multiple commands: comma-separated, e.g. 'CM 5000, FH 270'.");
        sb.AppendLine("- Condition prefix: 'AT <FIX>' or 'LV <ALT>' before the clause, e.g. 'AT CEPIN CAPP'.");
        sb.AppendLine("- Altitudes in feet (5000), flight levels converted (FL350 -> 35000).");
        sb.AppendLine("- Headings as 3-digit degrees (270, 090).");
        sb.AppendLine("- If the transcript is NOT a recognizable instruction, output nothing.");
        sb.AppendLine();
        sb.AppendLine("Canonical commands (spoken phrasings -> canonical):");
        sb.AppendLine();

        // Group rules by canonical output so all synonyms for one command appear on one line.
        // Keep rule insertion order (Heading, Alt/Speed, Nav, Tower, Approach, Pattern, Hold,
        // Helicopter, Transponder, Ground, Broadcast) — that's the order PhraseologyRules.Build()
        // constructs them, and it's a reasonable category grouping for the model to read.
        var grouped = PhraseologyRules
            .All.GroupBy(r => r.OutputTemplate, StringComparer.Ordinal)
            .Select(g =>
                (
                    Output: g.Key,
                    // Strip optional-literal "?" and variadic "..." markers from the rendered
                    // pattern. The small instruct model doesn't need to know the matcher's
                    // internal semantics — "{path...}" and "{path}" both tell it "some fix-like
                    // argument slot". Leaving "..." in the prompt risks confusing the model
                    // into emitting the literal ellipsis in its output.
                    Patterns: g.Select(r => string.Join(' ', r.Pattern.Select(RenderPatternToken))).Distinct(StringComparer.Ordinal).ToList()
                )
            );

        foreach (var (output, patterns) in grouped)
        {
            sb.Append(output).Append(": ").AppendLine(string.Join(" / ", patterns));
        }

        return sb.ToString();
    }

    private static string RenderPatternToken(string token)
    {
        // Variadic capture {name...} renders as plain {name}.
        if (token.StartsWith('{') && token.EndsWith("...}"))
        {
            return string.Concat("{", token.AsSpan(1, token.Length - 5), "}");
        }
        // Optional literal "of?" renders as plain "of".
        return token.TrimEnd('?');
    }

    /// <summary>
    /// Cleans up LLM output and validates that it looks like a canonical command string.
    /// Strips markdown code fences, quotes, leading "Output:" style labels, and whitespace, then
    /// splits into comma-separated clauses and verifies each clause starts with a known canonical
    /// verb from <see cref="CommandRegistry.AliasToCanonicType"/>. Condition prefixes ("AT FIX",
    /// "LV ALT") are recognized and the validator looks at the verb that follows.
    /// Returns null if any clause fails validation.
    /// </summary>
    internal static string? NormalizeOutput(string raw)
    {
        var trimmed = raw.Trim();

        // Strip code fences (```...```) and inline backticks.
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            trimmed = trimmed.Trim('`').Trim();
            var firstNewline = trimmed.IndexOf('\n');
            if (firstNewline > 0 && trimmed[..firstNewline].All(static c => char.IsLetter(c)))
            {
                trimmed = trimmed[(firstNewline + 1)..].Trim();
            }
        }

        trimmed = trimmed.Trim('`', '"', '\'').Trim();

        // Drop leading "Output:" / "Canonical:" / "Command:" labels if the model chose to include them.
        foreach (var prefix in new[] { "Output:", "Canonical:", "Command:", "Answer:" })
        {
            if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed[prefix.Length..].Trim();
                break;
            }
        }

        // Keep only the first line — models sometimes spill chain-of-thought after the answer.
        var newlineIdx = trimmed.IndexOfAny(['\n', '\r']);
        if (newlineIdx > 0)
        {
            trimmed = trimmed[..newlineIdx].Trim();
        }

        if (trimmed.Length == 0)
        {
            return null;
        }

        trimmed = trimmed.ToUpperInvariant();

        // Validate: each comma-separated clause must start with a known canonical verb, optionally
        // after a condition prefix ("AT <FIX>" / "LV <ALT>"). We don't validate the args themselves —
        // the command dispatcher will reject nonsense args when the user hits Enter.
        var clauses = trimmed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (clauses.Length == 0)
        {
            return null;
        }

        foreach (var clause in clauses)
        {
            if (!ClauseStartsWithKnownVerb(clause))
            {
                return null;
            }
        }

        return string.Join(", ", clauses);
    }

    private static bool ClauseStartsWithKnownVerb(string clause)
    {
        var tokens = clause.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return false;
        }

        // Real canonical clauses top out around 4 tokens (e.g. "AT CEPIN CAPP ILS28R"). Anything
        // longer is almost certainly natural-language explanation bleeding through — reject it.
        if (tokens.Length > MaxClauseTokens)
        {
            return false;
        }

        // Reject any token containing non-command characters — the LLM sometimes emits prose with
        // dashes, parentheses, etc. Canonical tokens are alphanumerics plus a small set of specials.
        foreach (var token in tokens)
        {
            if (!IsCanonicalToken(token))
            {
                return false;
            }
        }

        // Skip an optional condition prefix: "AT <fix>" or "LV <alt>".
        var verbIdx = 0;
        if (ConditionPrefixes.Contains(tokens[0]) && tokens.Length >= 3)
        {
            verbIdx = 2;
        }

        return verbIdx < tokens.Length && CommandRegistry.AliasToCanonicType.ContainsKey(tokens[verbIdx]);
    }

    private const int MaxClauseTokens = 5;

    private static bool IsCanonicalToken(string token)
    {
        foreach (var c in token)
        {
            // Allow uppercase letters, digits, and the small set of punctuation used in real
            // canonical args: '+' / '-' / '.' / '/' (e.g. KOAK+010, ILS28R, 28L/28R).
            if (!(char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c) || c is '+' or '-' or '.' or '/'))
            {
                return false;
            }
        }

        return token.Length > 0;
    }
}
