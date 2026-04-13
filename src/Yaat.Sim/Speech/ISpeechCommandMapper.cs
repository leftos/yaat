namespace Yaat.Sim.Speech;

/// <summary>
/// Context passed to a speech command mapper to aid matching.
/// </summary>
/// <param name="ActiveCallsigns">
/// Callsigns currently in the scenario — used by <see cref="CallsignParser"/> to disambiguate
/// shared-telephony airlines (e.g. VIRGIN → VIR vs VOZ).
/// </param>
/// <param name="ProgrammedFixes">
/// Fixes known to the relevant aircraft; used by <see cref="PhoneticFixMatcher"/> to correct
/// Whisper mistranscriptions of fix names.
/// </param>
public sealed record MapContext(IReadOnlyCollection<string> ActiveCallsigns, IReadOnlyCollection<string> ProgrammedFixes)
{
    /// <summary>Empty context — used when the caller has no scenario state.</summary>
    public static MapContext Empty { get; } = new([], []);
}

/// <summary>
/// Result of mapping a transcript to a canonical command. Null return (from the mapper) means
/// no part of the transcript was understood.
/// </summary>
/// <param name="Callsign">Extracted ICAO callsign (e.g. "SWA123"), or null if none found.</param>
/// <param name="CanonicalCommand">Comma-separated canonical commands (e.g. "CM 5000, FH 270").</param>
/// <param name="MatchedRuleCount">How many clauses matched a rule. Useful as a confidence signal; LLM mappers report 1.</param>
public sealed record MapResult(string? Callsign, string CanonicalCommand, int MatchedRuleCount);

/// <summary>
/// Maps a natural-language ATC transcript to a canonical YAAT command string. Implementations:
/// <list type="bullet">
///   <item><description><see cref="PhraseologyCommandMapper"/> — rule-based, synchronous, 163 7110.65 patterns.</description></item>
///   <item><description><c>Yaat.Client.Services.LocalLlmCommandMapper</c> — local GGUF LLM fallback via LLamaSharp.</description></item>
/// </list>
/// Mappers return null when the transcript doesn't map cleanly. The speech pipeline in Phase 7 tries
/// the rule-based mapper first and only falls through to the LLM when it returns null.
/// </summary>
public interface ISpeechCommandMapper
{
    /// <summary>
    /// Maps a transcript to a canonical command. Returns null when no match was produced.
    /// </summary>
    Task<MapResult?> MapAsync(string transcript, MapContext context, CancellationToken ct);
}
