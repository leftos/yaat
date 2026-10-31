namespace Yaat.Sim.Speech.Context;

/// <summary>Kinds of thing a context entity can name.</summary>
public enum EntityKind
{
    /// <summary>A room aircraft's callsign (ICAO form, e.g. <c>SWA2495</c>).</summary>
    Callsign,

    /// <summary>A navigation fix, named by its bare id (e.g. <c>MITOE</c>).</summary>
    Fix,

    /// <summary>A runway, airport-prefixed (e.g. <c>KOAK/30</c>).</summary>
    Runway,

    /// <summary>A taxiway, airport-prefixed (e.g. <c>KOAK/W4</c>).</summary>
    Taxiway,

    /// <summary>A gate or parking spot, airport-prefixed (e.g. <c>KSFO/SPOT 6B</c>).</summary>
    Gate,

    /// <summary>A named landmark, airport-prefixed (e.g. <c>KOAK/KAISER</c>).</summary>
    Landmark,

    /// <summary>A SID, STAR, or approach (e.g. <c>KOAK/I30</c>).</summary>
    Procedure,
}

/// <summary>Where the transcript being decoded came from.</summary>
public enum InputSource
{
    /// <summary>A speech recogniser's output.</summary>
    Spoken,

    /// <summary>Text the controller typed.</summary>
    Typed,
}

/// <summary>How strictly a token span is scored against a spoken form.</summary>
public enum MatchMode
{
    /// <summary>Fuzzy phonemic matching.</summary>
    Fuzzy,

    /// <summary>Exact matching only; used for the slot scoring of raw transcripts.</summary>
    ExactOnly,
}

/// <summary>How the decoder settled the callsign of a transmission.</summary>
public enum DecodeState
{
    /// <summary>The callsign is resolved.</summary>
    Resolved,

    /// <summary>The callsign is ambiguous; the best guess is carried for a say-again.</summary>
    SayAgain,

    /// <summary>No room callsign matched at all.</summary>
    NoCallsign,
}

/// <summary>One spoken form of an entity, with the prior that ranks it against the entity's other forms.</summary>
/// <param name="Text">The spoken text, in the tokeniser's normalised spelling.</param>
/// <param name="Prior">Confidence that hearing this form means this entity.</param>
public sealed record SpokenForm(string Text, double Prior);

/// <summary>Something a controller can name in a transmission, and every way it is spoken.</summary>
/// <param name="Kind">What kind of thing this is.</param>
/// <param name="Value">The canonical value (callsign, bare fix id, or airport-prefixed id).</param>
/// <param name="Forms">Every spoken form of this entity.</param>
public sealed record ContextEntity(EntityKind Kind, string Value, IReadOnlyList<SpokenForm> Forms);

/// <summary>One aircraft in the training room and the entities that belong to it.</summary>
/// <param name="Callsign">The aircraft's callsign entity.</param>
/// <param name="OwnEntities">Entities the aircraft itself is using (its route, its assigned runway, and so on).</param>
public sealed record RoomAircraft(ContextEntity Callsign, IReadOnlyList<ContextEntity> OwnEntities);

/// <summary>Everything a transmission is decoded against.</summary>
/// <param name="AirportId">The airport the controller is working.</param>
/// <param name="Aircraft">Every aircraft in the room.</param>
/// <param name="AirportEntities">Entities belonging to the airport, shared by every aircraft.</param>
public sealed record TransmissionContext(string AirportId, IReadOnlyList<RoomAircraft> Aircraft, IReadOnlyList<ContextEntity> AirportEntities);

/// <summary>One recogniser piece and its log-probability.</summary>
/// <param name="Piece">The piece text, word-marked with <c>▁</c> where the recogniser opens a word.</param>
/// <param name="LogProb">The piece's log-probability.</param>
public sealed record TokenLogProb(string Piece, double LogProb);

/// <summary>One recogniser hypothesis.</summary>
/// <param name="Text">The hypothesis transcript.</param>
/// <param name="Tokens">Its pieces and log-probabilities, or null when the recogniser reported none.</param>
/// <param name="AcousticScore">Its acoustic score, or null when unavailable.</param>
public sealed record DecoderHypothesis(string Text, IReadOnlyList<TokenLogProb>? Tokens, double? AcousticScore);

/// <summary>The hypotheses and settings a decode runs on.</summary>
/// <param name="Hypotheses">The recogniser's hypotheses, best first.</param>
/// <param name="Source">Where the hypotheses came from.</param>
/// <param name="Mode">How strictly spans are scored.</param>
public sealed record DecoderInput(IReadOnlyList<DecoderHypothesis> Hypotheses, InputSource Source, MatchMode Mode);

/// <summary>A span of the transcript's whitespace words, end exclusive.</summary>
/// <param name="Start">The first source-word index.</param>
/// <param name="End">The source-word index after the last, exclusive.</param>
public readonly record struct WordSpan(int Start, int End);

/// <summary>One entity matched in a transmission.</summary>
/// <param name="Entity">The entity that matched.</param>
/// <param name="Span">The transcript span the match covers.</param>
/// <param name="Score">The match score.</param>
/// <param name="OwnAircraft">Whether the entity belongs to the addressed aircraft rather than the airport.</param>
/// <param name="Heard">The transcript text the match was made from.</param>
public sealed record EntityMatch(ContextEntity Entity, WordSpan Span, double Score, bool OwnAircraft, string Heard);

/// <summary>The decoder's answer for one transmission.</summary>
/// <param name="State">How the callsign settled.</param>
/// <param name="Callsign">The resolved callsign, if any.</param>
/// <param name="BestGuess">The best callsign guess when the state is <see cref="DecodeState.SayAgain"/>.</param>
/// <param name="Entities">Every entity matched in the remainder.</param>
/// <param name="RewrittenTranscript">The transcript with accepted spans rewritten.</param>
/// <param name="HypothesisIndex">The hypothesis the answer came from.</param>
/// <param name="JointScore">The winning hypothesis's joint score.</param>
/// <param name="ConfidenceGated">Whether the log-probability gate applied.</param>
public sealed record DecodeResult(
    DecodeState State,
    EntityMatch? Callsign,
    EntityMatch? BestGuess,
    IReadOnlyList<EntityMatch> Entities,
    string RewrittenTranscript,
    int HypothesisIndex,
    double JointScore,
    bool ConfidenceGated
);
