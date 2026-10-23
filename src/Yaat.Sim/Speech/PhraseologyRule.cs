using Yaat.Sim.Commands;

namespace Yaat.Sim.Speech;

/// <summary>
/// A single phraseology-to-canonical mapping rule used by <see cref="PhraseologyMapper"/>.
/// </summary>
/// <param name="Pattern">
/// Ordered sequence of pattern tokens matched against a normalized transcript. Token syntax:
/// <list type="bullet">
///   <item><description><c>literal</c> — case-insensitive literal match against one transcript token.</description></item>
///   <item>
///     <description>
///       <c>literal?</c> — same as literal but optional. Matches if present; skipped if absent.
///       Used for filler words like "and" in "climb and? maintain".
///     </description>
///   </item>
///   <item>
///     <description>
///       <c>{name}</c> — capture one transcript token into the named group. The name is
///       referenced by <see cref="OutputTemplate"/> via <c>{name}</c> placeholders.
///     </description>
///   </item>
/// </list>
/// </param>
/// <param name="OutputTemplate">
/// Canonical command string with <c>{name}</c> placeholders filled from pattern captures.
/// E.g. <c>"CM {alt}"</c> becomes <c>"CM 5000"</c> when <c>alt=5000</c>.
/// </param>
/// <param name="Type">
/// The <see cref="CanonicalCommandType"/> this rule maps to. Used for diagnostics, rule
/// sorting, and future context-sensitive rule disambiguation (e.g. <c>maintain {alt}</c>
/// dispatching to CM vs DM based on current altitude).
/// </param>
/// <param name="PilotVariants">
/// Optional output-only spoken wordings, per frequency load band, that the pilot AI's <c>PhraseologyVerbalizer</c> picks
/// among when <c>PilotPersonality.Varied</c> is enabled. Each row is a template over the same <c>{name}</c> captures as
/// <see cref="Pattern"/>, rendered by the same spoken-form formatters, and must keep the aviation floor against the pattern
/// (<c>AviationFloor.HoldsTemplate</c>). The verbalizer renders the band's rows, drops any that leave a placeholder
/// unresolved or whose tags rule them out, and picks one by a seeded, uniform pick (<c>VariantPick</c>):
/// <code>
/// new(
///     ["climb", "and?", "maintain", "{alt}"],
///     "CM {alt}",
///     ClimbMaintain,
///     PilotVariants: PilotVariantSet.BusyOnly(new PilotVariant("up to {alt}", PilotVariantTags.FlightLevelOnly)))
/// </code>
/// When null, the verbalizer always emits the pattern-derived verbatim form.
/// </param>
/// <param name="SttOnly">
/// When true, this rule exists only to recover canonical commands from imperfect STT output
/// (e.g. <c>"descent and maintain {alt}"</c> as an acoustic alias for Whisper mishearing
/// "descend" as "descent"). The pattern is consulted during transcript → canonical mapping
/// but is excluded from <c>PhraseologyVerbalizer</c>'s candidate set, so the pilot AI never
/// speaks the alias form back. Default false — most rules are bidirectional.
/// </param>
public sealed record PhraseologyRule(
    string[] Pattern,
    string OutputTemplate,
    CanonicalCommandType Type,
    PilotVariantSet? PilotVariants = null,
    bool SttOnly = false
)
{
    /// <summary>Number of non-optional tokens in the pattern — used for match-length comparison.</summary>
    public int RequiredLength { get; } = Pattern.Count(t => !t.EndsWith('?'));

    /// <summary>Total pattern length including optionals — used as a tiebreaker for longest-match selection.</summary>
    public int TotalLength { get; } = Pattern.Length;
}
