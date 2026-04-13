using System.Text;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Speech;

namespace Yaat.Client.Services;

/// <summary>
/// <see cref="ISpeechCommandMapper"/> backed by a local GGUF LLM via <see cref="LocalLlmService"/>.
/// The speech pipeline in Phase 7 invokes this as a fallback when <see cref="PhraseologyCommandMapper"/>
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

    public async Task<MapResult?> MapAsync(string transcript, MapContext context, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(transcript))
        {
            return null;
        }

        var userPrompt = BuildUserPrompt(transcript, context);
        var raw = await _llm.GenerateAsync(_systemPrompt.Value, userPrompt, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var canonical = NormalizeOutput(raw);
        if (canonical is null)
        {
            Log.LogDebug("LLM output did not parse as canonical command: {Raw}", raw);
            return null;
        }

        // Phase 2 rule matches are cheap enough that if the LLM echoes a rule-shaped string, we trust
        // it. We don't have a callsign extraction step here — Phase 7 will re-run CallsignParser on
        // the transcript for the LLM path if needed.
        Log.LogInformation("LLM fallback produced canonical command: {Canonical}", canonical);
        return new MapResult(null, canonical, 1);
    }

    private static string BuildUserPrompt(string transcript, MapContext context)
    {
        var sb = new StringBuilder();
        sb.Append("Transcript: ").AppendLine(transcript);
        if (context.ActiveCallsigns.Count > 0)
        {
            sb.Append("Active callsigns: ").AppendLine(string.Join(", ", context.ActiveCallsigns));
        }

        if (context.ProgrammedFixes.Count > 0)
        {
            sb.Append("Programmed fixes: ").AppendLine(string.Join(", ", context.ProgrammedFixes));
        }

        sb.AppendLine("Output only the canonical command string, nothing else.");
        return sb.ToString();
    }

    private static string BuildSystemPrompt()
    {
        // Compact reference: canonical verb → category → sample arg. One line per command.
        var sb = new StringBuilder();
        sb.AppendLine("You are an air traffic control command parser for the YAAT simulator.");
        sb.AppendLine("Your job: convert a spoken ATC instruction into YAAT canonical command syntax.");
        sb.AppendLine();
        sb.AppendLine("Output rules:");
        sb.AppendLine("- Output ONLY the canonical command string, nothing else (no explanation, no quotes).");
        sb.AppendLine("- Multiple commands in one utterance are comma-separated: 'CM 5000, FH 270'.");
        sb.AppendLine("- Condition prefixes use 'AT <FIX>' or 'LV <ALT>' prefixed to the clauses, e.g. 'AT CEPIN CAPP'.");
        sb.AppendLine("- Altitudes in feet (5000), flight levels converted (FL350 → 35000).");
        sb.AppendLine("- Headings as 3-digit degrees (270, 090).");
        sb.AppendLine("- Return an empty response if the transcript is not a recognizable instruction.");
        sb.AppendLine();
        sb.AppendLine("Canonical commands (verb — description):");
        foreach (var def in CommandRegistry.All.Values.OrderBy(d => d.Type.ToString(), StringComparer.Ordinal))
        {
            var aliases = def.DefaultAliases.Length > 0 ? def.DefaultAliases[0] : def.Type.ToString();
            sb.Append(aliases).Append(" — ").Append(def.Label);
            if (def.ArgMode != ArgMode.None && !string.IsNullOrEmpty(def.SampleArg))
            {
                sb.Append(" (arg: ").Append(def.SampleArg).Append(')');
            }

            sb.AppendLine();
        }

        sb.AppendLine();
        sb.AppendLine("Examples:");
        sb.AppendLine("Transcript: 'climb and maintain five thousand' → CM 5000");
        sb.AppendLine("Transcript: 'turn right heading two seven zero' → TR 270");
        sb.AppendLine("Transcript: 'direct to CEPIN' → DCT CEPIN");
        sb.AppendLine("Transcript: 'at CEPIN cleared approach' → AT CEPIN CAPP");
        sb.AppendLine("Transcript: 'squawk seven five zero zero' → SQ 7500");
        return sb.ToString();
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
