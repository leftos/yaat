using System.Text;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;

namespace Yaat.Client.Services;

/// <summary>
/// Uses the local LLM to disambiguate a spoken callsign from a noisy Whisper transcript when
/// the rule-based <see cref="Yaat.Sim.Speech.CallsignParser"/> can't extract one. The rule
/// parser handles clean inputs fast; this resolver is the fallback for when Whisper produces
/// mishears like "diner" (niner), "common" (climb and), or hybrid forms the regex-style parser
/// can't cover without enumerating every variant.
///
/// The model is given the list of currently-active callsigns and the raw transcript, and
/// instructed to return exactly one ICAO callsign from the list or the literal string
/// <c>NONE</c>. The output is validated against the active list — anything else returns null,
/// so the resolver can never produce a phantom callsign that doesn't exist in the scenario.
/// </summary>
public sealed class LocalLlmCallsignResolver
{
    private static readonly ILogger Log = AppLog.CreateLogger<LocalLlmCallsignResolver>();

    /// <summary>
    /// Default system prompt used by the production resolver. Exposed so the sandbox tool can
    /// read it as a starting point for prompt iteration, then pass an edited version back via
    /// the custom-prompt constructor.
    /// </summary>
    public const string DefaultSystemPrompt =
        "You are an ATC speech disambiguator. Given a list of active aircraft callsigns and a "
        + "transcript that may contain speech-to-text errors, identify the ICAO callsign from "
        + "the list that the speaker most likely referred to.\n"
        + "\n"
        + "Rules:\n"
        + "- Output ONLY the ICAO callsign exactly as it appears in the active list.\n"
        + "- No quotes, no prose, no explanation.\n"
        + "- If no callsign from the list fits the transcript, output NONE.\n"
        + "- Common Whisper errors to compensate for: 'diner'/'dinner' for 'niner' (9), merged "
        + "words like 'common' for 'climb and', missing or duplicated digits.";

    private readonly LocalLlmService _llm;
    private readonly string _systemPrompt;

    public LocalLlmCallsignResolver(LocalLlmService llm)
    {
        _llm = llm;
        _systemPrompt = DefaultSystemPrompt;
    }

    /// <summary>
    /// Constructs a resolver with an explicit system prompt — used by the speech sandbox tool.
    /// Production callers use the single-arg constructor which takes <see cref="DefaultSystemPrompt"/>.
    /// </summary>
    public LocalLlmCallsignResolver(LocalLlmService llm, string customSystemPrompt)
    {
        _llm = llm;
        _systemPrompt = customSystemPrompt;
    }

    /// <summary>
    /// Ask the LLM which active callsign the transcript refers to. Returns the ICAO form when
    /// the model's output exactly matches one of the active callsigns (case-insensitive),
    /// otherwise null. Null is also returned when the LLM is disabled, the active list is
    /// empty, inference throws, or the model returns NONE / garbage.
    /// </summary>
    public async Task<string?> ResolveAsync(string transcript, IReadOnlyCollection<string> activeCallsigns, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(transcript) || activeCallsigns.Count == 0)
        {
            return null;
        }

        var userPrompt = BuildUserPrompt(transcript, activeCallsigns);
        // No grammar — the resolver's output is freeform "ICAO callsign or NONE", validated against
        // the active list afterward by ValidateAgainstActive. Constraining the grammar to the
        // active-callsign alternation would be theoretically tighter but adds zero safety
        // (validation already prevents phantom callsigns) and would require building a fresh
        // grammar on every call from a fresh callsign list — not worth the complexity.
        var raw = await _llm.GenerateAsync(_systemPrompt, userPrompt, gbnfGrammar: null, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var resolved = ValidateAgainstActive(raw, activeCallsigns);
        if (resolved is null)
        {
            Log.LogDebug("LLM callsign resolver output did not match any active callsign: {Raw}", raw);
        }
        else
        {
            Log.LogInformation("LLM callsign resolver mapped transcript to: {Callsign}", resolved);
        }
        return resolved;
    }

    private static string BuildUserPrompt(string transcript, IReadOnlyCollection<string> activeCallsigns)
    {
        var sb = new StringBuilder();
        sb.Append("Active callsigns: ").AppendLine(string.Join(", ", activeCallsigns));
        sb.Append("Transcript: ").AppendLine(transcript);
        sb.AppendLine("Callsign:");
        return sb.ToString();
    }

    /// <summary>
    /// Strips markdown noise and returns the first token in the LLM output that exactly matches
    /// an active callsign. Explicit handling for the literal "NONE" response (return null).
    /// </summary>
    internal static string? ValidateAgainstActive(string raw, IReadOnlyCollection<string> activeCallsigns)
    {
        var trimmed = raw.Trim().Trim('`', '"', '\'').Trim();

        // Keep only the first line — small models sometimes spill extra lines after the answer.
        var newlineIdx = trimmed.IndexOfAny(['\n', '\r']);
        if (newlineIdx > 0)
        {
            trimmed = trimmed[..newlineIdx].Trim();
        }

        if (trimmed.Length == 0 || string.Equals(trimmed, "NONE", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // Match the first whitespace-delimited token against the active list — lets us tolerate
        // models that append stray trailing text despite the instruction not to.
        var firstToken = trimmed.Split([' ', '\t', ',', '.'], 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (firstToken.Length == 0)
        {
            return null;
        }

        var candidate = firstToken[0];
        foreach (var active in activeCallsigns)
        {
            if (string.Equals(active, candidate, StringComparison.OrdinalIgnoreCase))
            {
                return active;
            }
        }
        return null;
    }
}
