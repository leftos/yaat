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

    /// <summary>
    /// Speech-recognition patterns for custom fixes (from <c>NavigationDatabase.CustomFixSpeechPatterns</c>).
    /// <see cref="PhraseologyMapper"/> collapses multi-token matches into single canonical-alias
    /// tokens before rule matching runs, so natural-language references like "the runway 30
    /// numbers" end up behaving identically to a direct fix name. Defaults to empty so existing
    /// callers don't need to populate it; new callers use the with-expression or object
    /// initializer to add patterns.
    /// </summary>
    public IReadOnlyList<CustomFixSpeechPattern> CustomFixPatterns { get; init; } = [];

    /// <summary>
    /// Airport code → list of runway designators (e.g. <c>"KOAK" → ["28R","28L","10R","10L","30","12","33","15"]</c>).
    /// Pulled from <c>NavigationDatabase.GetRunways</c> for the destinations + departures of every active aircraft
    /// in the scenario. <see cref="PhraseologyMapper"/> uses this to validate <c>{rwy}</c> captures so misheard
    /// runways (Whisper "288" instead of "28R") fail the rule and fall through to the LLM fallback.
    /// The LLM fallback also reads this to recover the intended runway from scenario knowledge.
    /// Empty when no scenario state is available — both validators skip their checks in that case.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> AvailableRunways { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// ICAO callsign → destination airport code (e.g. <c>"N9225L" → "KOAK"</c>). Built from
    /// <c>AircraftState.Destination</c> across the active scenario. The LLM fallback uses this to
    /// correlate the in-transcript callsign with the right airport's runway list when recovering
    /// from a Whisper mistranscription.
    /// </summary>
    public IReadOnlyDictionary<string, string> AircraftDestinations { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Set of taxiway names (uppercase, case-insensitive) for the airport currently relevant to
    /// the aircraft being commanded. Used by <see cref="NatoLetterNormalizer"/> to disambiguate
    /// multi-letter taxiway names ("tango echo" → "TE") from adjacent single-letter taxiways
    /// ("T E"). Empty when no scenario / ground-layout state is available — the normalizer
    /// falls back to single-letter splits in that case. Populated from
    /// <c>AircraftState.GroundLayout.Edges</c>, filtered to exclude runway centerlines and ramps.
    /// </summary>
    public IReadOnlySet<string> TaxiwayNames { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
/// Mappers return null when the transcript doesn't map cleanly. The speech pipeline tries
/// the rule-based mapper first and only falls through to the LLM when it returns null.
/// </summary>
public interface ISpeechCommandMapper
{
    /// <summary>
    /// Maps a transcript to a canonical command. Returns null when no match was produced.
    /// </summary>
    Task<MapResult?> MapAsync(string transcript, MapContext context, CancellationToken ct);
}
