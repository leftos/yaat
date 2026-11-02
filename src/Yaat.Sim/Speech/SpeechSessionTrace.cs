using Yaat.Sim.Speech.Context;

namespace Yaat.Sim.Speech;

/// <summary>Which transcription path produced a session's transcript.</summary>
public enum TranscriptionMode
{
    /// <summary>The single-hypothesis transcription path.</summary>
    Fast,

    /// <summary>The beam n-best path, scored by the context decoder.</summary>
    Accurate,
}

/// <summary>One n-best entry as carried on a session trace: the hypothesis text and its score, with no tokens.</summary>
/// <param name="Text">The hypothesis transcript.</param>
/// <param name="Score">The hypothesis's decode score, or null when the engine reported none.</param>
public sealed record SpeechTraceHypothesis(string Text, double? Score);

/// <summary>
/// Per-pipeline-stage detail captured for a single push-to-talk session. Attached to
/// <c>SpeechSession.Trace</c> by the orchestrator and consumed by the Speech Debug window so a
/// user can post-mortem exactly what each stage produced. Built unconditionally on every session
/// that reached the transcription stage — cost is microseconds of string assembly — and surfaces
/// in the UI only when the user has opted in to sample capture.
/// </summary>
/// <param name="WhisperBiasingPrompt">The free-text prompt fed to Whisper (active callsigns + vocabulary).</param>
/// <param name="RawTranscript">Whisper's verbatim output before any normalization.</param>
/// <param name="CallsignStrippedTranscript">Transcript after digit normalization and callsign extraction — the exact string handed to the rule and LLM mappers.</param>
/// <param name="CallsignExtracted">ICAO callsign recovered from the transcript at the orchestrator layer, or null when none matched.</param>
/// <param name="Rule">Rule-mapper trace — always present. <see cref="RuleMapperTrace.OutputCanonical"/> is null on miss.</param>
/// <param name="Llm">LLM-fallback trace — present only when the rule mapper missed and the LLM mapper ran.</param>
/// <param name="ActiveCallsigns">Callsigns visible to the pipeline at PTT press.</param>
/// <param name="ProgrammedFixes">Fix names known to the relevant aircraft at PTT press.</param>
/// <param name="AvailableRunwaysByAirport">Per-airport runway lists fed to the mappers for {rwy} validation and LLM recovery.</param>
/// <param name="TaxiwayNames">Taxiway-name set fed to <c>NatoLetterNormalizer</c>.</param>
/// <param name="AircraftDestinations">Callsign→destination map fed to the LLM fallback.</param>
/// <param name="Mode">Which transcription path produced the transcript, or null when the trace does not record it.</param>
/// <param name="NBest">The accurate mode's beam n-best list, or null when no beam decode ran.</param>
/// <param name="DecodeState">How the context decoder settled the callsign, or null when the decoder did not run.</param>
/// <param name="BestGuess">The decoder's best-guess callsign when it could not settle one, or null.</param>
/// <param name="DecodeMs">Wall-clock milliseconds the context decode took, or null when it did not run.</param>
/// <param name="DecodeError">The exception message when the decode failed and the press fell back to the fast path, or null.</param>
public sealed record SpeechSessionTrace(
    string WhisperBiasingPrompt,
    string RawTranscript,
    string CallsignStrippedTranscript,
    string? CallsignExtracted,
    RuleMapperTrace Rule,
    LlmMapperTrace? Llm,
    IReadOnlyList<string> ActiveCallsigns,
    IReadOnlyList<string> ProgrammedFixes,
    IReadOnlyDictionary<string, IReadOnlyList<string>> AvailableRunwaysByAirport,
    IReadOnlyList<string> TaxiwayNames,
    IReadOnlyDictionary<string, string> AircraftDestinations,
    TranscriptionMode? Mode,
    IReadOnlyList<SpeechTraceHypothesis>? NBest,
    DecodeState? DecodeState,
    string? BestGuess,
    double? DecodeMs,
    string? DecodeError
);

/// <summary>
/// Rule-mapper outcome for a single transcript. Always emitted by <c>PhraseologyCommandMapper</c>
/// (even when the mapper returned null) so the debug UI can show why a rule miss happened.
/// </summary>
/// <param name="NormalizedTokens">Tokens after filler-strip, NATO-near-miss rewrite, and custom-fix collapse — joined with spaces. Empty when the input was rejected before tokenization.</param>
/// <param name="ConditionPrefix">Canonical condition prefix (e.g. <c>"AT CEPIN"</c> / <c>"LV 5000"</c>) recovered from the leading tokens, or null when none.</param>
/// <param name="MatchedRulePatterns">Pattern descriptors for each clause that produced an output (one entry per matched clause). Empty on miss.</param>
/// <param name="OutputCanonical">The canonical command string the rule engine emitted, or null on miss.</param>
/// <param name="FailureReason">Short human-readable hint describing the miss (e.g. <c>"no rule matched"</c>, <c>"runway not in scenario"</c>). Null on success.</param>
public sealed record RuleMapperTrace(
    string NormalizedTokens,
    string? ConditionPrefix,
    IReadOnlyList<string> MatchedRulePatterns,
    string? OutputCanonical,
    string? FailureReason
)
{
    /// <summary>An empty trace, useful for tests and as a starting point when no rule mapper ran.</summary>
    public static RuleMapperTrace Empty { get; } = new(string.Empty, null, [], null, null);
}

/// <summary>
/// LLM-fallback outcome for a single transcript. Present only when the rule mapper missed and the
/// LLM mapper actually ran (so a session with a rule hit has <c>SpeechSessionTrace.Llm == null</c>).
/// </summary>
/// <param name="SystemPrompt">The static instruction prompt — derived from <c>PhraseologyRules</c>.</param>
/// <param name="UserPrompt">The per-call prompt including the transcript and scenario context.</param>
/// <param name="RawOutput">The model's verbatim output before normalization (empty when the LLM produced nothing).</param>
/// <param name="NormalizedOutput">Canonical command string after grammar-validated post-processing, or null when the raw output failed validation.</param>
/// <param name="FailureReason">Short human-readable hint describing the miss. Null on success.</param>
public sealed record LlmMapperTrace(string SystemPrompt, string UserPrompt, string RawOutput, string? NormalizedOutput, string? FailureReason);
