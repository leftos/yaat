namespace Yaat.Sim.Speech;

/// <summary>
/// A natural-language phrase that maps to a custom fix's canonical alias. Used by
/// <see cref="PhraseologyMapper"/> to collapse multi-token spoken references to custom fixes
/// (e.g. "runway 30 numbers") into a single token matching the canonical identifier
/// (e.g. "OAK30NUM") before rule matching runs.
///
/// Patterns are built at <c>NavigationDatabase</c> load time from the <c>spokenPatterns</c> field
/// on each <c>CustomFixDefinition</c>. The raw phrase strings are tokenized, lowercased, and
/// stored alongside the first alias of the fix.
/// </summary>
/// <param name="Tokens">Lowercase tokens the phrase splits into, after digit normalization has
/// been applied. For example, <c>"runway 30 numbers"</c> → <c>["runway", "30", "numbers"]</c>.</param>
/// <param name="CanonicalAlias">The identifier that replaces the matched tokens in the transcript.
/// Downstream rule captures see this as a single <c>{fix}</c> token.</param>
public sealed record CustomFixSpeechPattern(IReadOnlyList<string> Tokens, string CanonicalAlias);
