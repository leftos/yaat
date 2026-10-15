using System.Text.RegularExpressions;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Logging;

/// <summary>
/// One distinct participant in a training session, collapsed across the identity sources that name
/// them in the server log: their VATSIM <paramref name="Cid"/>, their CRC <paramref name="RealName"/>,
/// and their controller <paramref name="Initials"/>. Any of these may be null when a source didn't
/// supply it (e.g. an instructor on the YAAT client has no CRC real name). A person seen under more than one
/// name or set of initials (a rejoin with other initials) keeps the first in <paramref name="RealName"/> and
/// <paramref name="Initials"/> and the rest in <see cref="OtherRealNames"/> and <see cref="OtherInitials"/>; every one
/// of them stands for the participant's one pseudonym.
/// </summary>
public sealed record LogParticipant(string? Cid, string? RealName, string? Initials)
{
    public IReadOnlyList<string> OtherRealNames { get; init; } = [];

    public IReadOnlyList<string> OtherInitials { get; init; } = [];

    /// <summary><see cref="RealName"/> then <see cref="OtherRealNames"/>; the first may be null.</summary>
    public IEnumerable<string?> EveryRealName() => OtherRealNames.Prepend(RealName);

    /// <summary><see cref="Initials"/> then <see cref="OtherInitials"/>; the first may be null.</summary>
    public IEnumerable<string?> EveryInitials() => OtherInitials.Prepend(Initials);

    // The other names and initials compare by their contents, so two participants built from the same sightings are equal.
    public bool Equals(LogParticipant? other) =>
        (other is not null)
        && (Cid == other.Cid)
        && (RealName == other.RealName)
        && (Initials == other.Initials)
        && OtherRealNames.SequenceEqual(other.OtherRealNames)
        && OtherInitials.SequenceEqual(other.OtherInitials);

    public override int GetHashCode() => HashCode.Combine(Cid, RealName, Initials);
}

/// <summary>
/// Redacts a room-scoped server-log slice before it leaves the server in a bug-report bundle. Each
/// distinct participant is assigned a stable pseudonym (<c>A0</c>, <c>A1</c>, … <c>A9</c>, <c>B0</c>,
/// …) and every occurrence of that participant's CID, real name, and initials is replaced with it.
/// Initials are redacted too because within an ARTCC they identify the controller on their own — the
/// consistent pseudonym keeps the slice readable ("A0 issued X, A1 issued Y") without naming anyone.
///
/// Replacement is whole-word (no word character on either side) so a CID embedded in a longer token (a
/// TCP code like <c>C1826127</c>, a beacon, a coordinate run) is not corrupted. Aircraft callsigns and
/// position TCPs are not participant identifiers and are left intact. The known non-person markers
/// <c>YAAT</c> (scenario auto-positions) and <c>CRC</c> (the RPO/CRC system author) are never treated as
/// people, and a token the caller asks to keep (a command verb, so <c>CM 050</c> survives a controller
/// whose initials are CM) is never replaced.
/// </summary>
public static class SessionLogAnonymizer
{
    /// <summary>What a person outside the participant list becomes.</summary>
    public const string Unknown = "??";

    private static readonly HashSet<string> NonPersonTokens = new(StringComparer.Ordinal) { "YAAT", "CRC" };

    /// <summary>
    /// Every command verb and alias, and every canonical command name, ignoring case: the keep set for a log
    /// whose command lines must stay readable.
    /// </summary>
    public static IReadOnlySet<string> CommandVerbs { get; } = BuildCommandVerbs();

    /// <summary>
    /// Collapses the per-source identity rows for a room into one <see cref="LogParticipant"/> per
    /// distinct CID, merged from training-hub membership (CID + initials) and CRC sessions (CID + real
    /// name). Kept in the order the CIDs are first seen (the members first, then the CRC identities), so a person's
    /// pseudonym, which is their position in the list, never changes when someone is seen after them. Every distinct
    /// initials and real name a CID is seen with is kept: the first in the participant's own field, the rest among its
    /// others.
    /// </summary>
    public static IReadOnlyList<LogParticipant> BuildParticipants(
        IEnumerable<(string Cid, string Initials)> members,
        IEnumerable<(string Cid, string RealName)> crcIdentities
    )
    {
        var firstSeen = new List<string>();
        var byCid = new Dictionary<string, Sightings>(StringComparer.Ordinal);

        foreach ((string? cid, string? initials) in members)
        {
            if (!string.IsNullOrWhiteSpace(cid))
            {
                AddDistinct(Seen(byCid, firstSeen, cid).Initials, initials);
            }
        }

        foreach ((string? cid, string? realName) in crcIdentities)
        {
            if (!string.IsNullOrWhiteSpace(cid))
            {
                AddDistinct(Seen(byCid, firstSeen, cid).RealNames, realName);
            }
        }

        return [.. firstSeen.Select(cid => byCid[cid].ToParticipant(cid))];
    }

    private static Sightings Seen(Dictionary<string, Sightings> byCid, List<string> firstSeen, string cid)
    {
        if (!byCid.TryGetValue(cid, out Sightings? sightings))
        {
            sightings = new Sightings();
            byCid[cid] = sightings;
            firstSeen.Add(cid);
        }

        return sightings;
    }

    private static void AddDistinct(List<string> values, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) && !values.Contains(value, StringComparer.Ordinal))
        {
            values.Add(value);
        }
    }

    /// <summary>The initials and real names one CID was seen with, in the order they were seen.</summary>
    private sealed class Sightings
    {
        public List<string> Initials { get; } = [];
        public List<string> RealNames { get; } = [];

        public LogParticipant ToParticipant(string cid) =>
            new(cid, RealNames.FirstOrDefault(), Initials.FirstOrDefault())
            {
                OtherRealNames = [.. RealNames.Skip(1)],
                OtherInitials = [.. Initials.Skip(1)],
            };
    }

    /// <summary>
    /// Returns <paramref name="log"/> with every participant identifier replaced by that participant's
    /// stable pseudonym, except a token in <paramref name="keepTokens"/>. A <c>(from XX)</c> sender the list does not
    /// hold becomes <c>??</c> unless it is a system sender (<c>AI</c>, <c>CRC</c>, <c>YAAT</c>), an issued pseudonym or a
    /// position callsign.
    /// </summary>
    public static string Anonymize(string log, IReadOnlyList<LogParticipant> participants, IReadOnlySet<string> keepTokens)
    {
        if (string.IsNullOrEmpty(log))
        {
            return log;
        }

        // token -> pseudonym, first writer wins so a shared initial maps consistently.
        var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < participants.Count; i++)
        {
            string pseudonym = Pseudonym(i);
            AddToken(replacements, participants[i].Cid, pseudonym);
            foreach (string? realName in participants[i].EveryRealName())
            {
                AddToken(replacements, realName, pseudonym);
            }

            foreach (string? initials in participants[i].EveryInitials())
            {
                AddToken(replacements, initials, pseudonym);
            }
        }

        // Longest token first so a full name is redacted before its constituent words / initials, and a
        // longer CID before a shorter coincidental one.
        IOrderedEnumerable<KeyValuePair<string, string>> ordered = replacements.OrderByDescending(kvp => kvp.Key.Length);

        string result = log;
        foreach ((string? token, string? pseudonym) in ordered)
        {
            if (!keepTokens.Contains(token))
            {
                result = Regex.Replace(result, $@"(?<!\w){Regex.Escape(token)}(?!\w)", pseudonym);
            }
        }

        // The sender slot names a person whatever the keep set says, and one the list does not hold is hidden.
        Dictionary<string, string> byInitials = PseudonymsByInitials(participants);
        HashSet<string> issued = IssuedPseudonyms(participants);
        return Regex.Replace(
            result,
            @"(?<=\(from )[^\s()]+(?=\))",
            m => byInitials.GetValueOrDefault(m.Value) ?? (IsKeptSender(m.Value, issued) ? m.Value : Unknown)
        );
    }

    private static bool IsKeptSender(string sender, HashSet<string> issued) =>
        NonPersonTokens.Contains(sender) || (sender is "AI" or Unknown) || issued.Contains(sender) || CrcDisplayName.IsPositionCallsign(sender);

    /// <summary>The pseudonyms <paramref name="participants"/> issue: one per participant, by their position in the list.</summary>
    public static HashSet<string> IssuedPseudonyms(IReadOnlyList<LogParticipant> participants) =>
        [.. Enumerable.Range(0, participants.Count).Select(Pseudonym)];

    /// <summary>
    /// Each listed participant's initials (every set they used) mapped to that participant's pseudonym, first participant
    /// winning on a shared initial; the non-person markers are left out.
    /// </summary>
    public static Dictionary<string, string> PseudonymsByInitials(IReadOnlyList<LogParticipant> participants)
    {
        var byInitials = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < participants.Count; i++)
        {
            foreach (string? every in participants[i].EveryInitials())
            {
                string? initials = every?.Trim();
                if (!string.IsNullOrEmpty(initials) && !NonPersonTokens.Contains(initials))
                {
                    byInitials.TryAdd(initials, Pseudonym(i));
                }
            }
        }

        return byInitials;
    }

    private static void AddToken(Dictionary<string, string> replacements, string? token, string pseudonym)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return;
        }

        token = token.Trim();
        if (NonPersonTokens.Contains(token) || (token == pseudonym))
        {
            return;
        }

        replacements.TryAdd(token, pseudonym);
    }

    private static HashSet<string> BuildCommandVerbs()
    {
        var verbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach ((CanonicalCommandType type, CommandDefinition definition) in CommandRegistry.All)
        {
            verbs.Add(type.ToString());
            verbs.UnionWith(definition.DefaultAliases);
        }

        return verbs;
    }

    /// <summary>The pseudonym of the participant at <paramref name="index"/>: <c>A0</c> … <c>Z9</c>, then <c>P260</c>, ….</summary>
    public static string Pseudonym(int index)
    {
        int letter = index / 10;
        int digit = index % 10;
        return letter < 26 ? $"{(char)('A' + letter)}{digit}" : $"P{index}";
    }
}
