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
public sealed record PhraseologyRule(string[] Pattern, string OutputTemplate, CanonicalCommandType Type)
{
    /// <summary>Number of non-optional tokens in the pattern — used for match-length comparison.</summary>
    public int RequiredLength { get; } = Pattern.Count(t => !t.EndsWith('?'));

    /// <summary>Total pattern length including optionals — used as a tiebreaker for longest-match selection.</summary>
    public int TotalLength { get; } = Pattern.Length;
}
