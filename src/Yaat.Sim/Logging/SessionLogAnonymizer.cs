using System.Text.RegularExpressions;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Logging;

/// <summary>
/// One distinct participant in a training session, collapsed across the identity sources that name
/// them in the server log: their VATSIM <paramref name="Cid"/>, their CRC <paramref name="RealName"/>,
/// and their controller <paramref name="Initials"/>. Any of these may be null when a source didn't
/// supply it (e.g. an instructor on the YAAT client has no CRC real name).
/// </summary>
public sealed record LogParticipant(string? Cid, string? RealName, string? Initials);

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
    private static readonly HashSet<string> NonPersonTokens = new(StringComparer.Ordinal) { "YAAT", "CRC" };

    /// <summary>
    /// Every command verb and alias, and every canonical command name, ignoring case: the keep set for a log
    /// whose command lines must stay readable.
    /// </summary>
    public static IReadOnlySet<string> CommandVerbs { get; } = BuildCommandVerbs();

    /// <summary>
    /// Collapses the per-source identity rows for a room into one <see cref="LogParticipant"/> per
    /// distinct CID, merged from training-hub membership (CID + initials) and CRC sessions (CID + real
    /// name). Ordered by CID so pseudonym assignment is deterministic across exports.
    /// </summary>
    public static IReadOnlyList<LogParticipant> BuildParticipants(
        IEnumerable<(string Cid, string Initials)> members,
        IEnumerable<(string Cid, string RealName)> crcIdentities
    )
    {
        var byCid = new Dictionary<string, (string? RealName, string? Initials)>(StringComparer.Ordinal);

        foreach ((string? cid, string? initials) in members)
        {
            if (string.IsNullOrWhiteSpace(cid))
            {
                continue;
            }

            (string? RealName, string? Initials) entry = byCid.GetValueOrDefault(cid);
            if (string.IsNullOrWhiteSpace(entry.Initials) && !string.IsNullOrWhiteSpace(initials))
            {
                entry.Initials = initials;
            }

            byCid[cid] = entry;
        }

        foreach ((string? cid, string? realName) in crcIdentities)
        {
            if (string.IsNullOrWhiteSpace(cid))
            {
                continue;
            }

            (string? RealName, string? Initials) entry = byCid.GetValueOrDefault(cid);
            if (string.IsNullOrWhiteSpace(entry.RealName) && !string.IsNullOrWhiteSpace(realName))
            {
                entry.RealName = realName;
            }

            byCid[cid] = entry;
        }

        return
        [
            .. byCid
                .OrderBy(kvp => kvp.Key, StringComparer.Ordinal)
                .Select(kvp => new LogParticipant(kvp.Key, kvp.Value.RealName, kvp.Value.Initials)),
        ];
    }

    /// <summary>
    /// Returns <paramref name="log"/> with every participant identifier replaced by that participant's
    /// stable pseudonym, except a token in <paramref name="keepTokens"/>. Returns the input unchanged when
    /// there is nothing to redact.
    /// </summary>
    public static string Anonymize(string log, IReadOnlyList<LogParticipant> participants, IReadOnlySet<string> keepTokens)
    {
        if (string.IsNullOrEmpty(log) || participants.Count == 0)
        {
            return log;
        }

        // token -> pseudonym, first writer wins so a shared initial maps consistently.
        var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < participants.Count; i++)
        {
            string pseudonym = Pseudonym(i);
            AddToken(replacements, participants[i].Cid, pseudonym);
            AddToken(replacements, participants[i].RealName, pseudonym);
            AddToken(replacements, participants[i].Initials, pseudonym);
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

        // The sender slot names a person whatever the keep set says; an unlisted sender is left to the caller.
        Dictionary<string, string> byInitials = PseudonymsByInitials(participants);
        return Regex.Replace(result, @"(?<=\(from )[^\s()]+(?=\))", m => byInitials.GetValueOrDefault(m.Value, m.Value));
    }

    /// <summary>
    /// Each listed participant's initials mapped to that participant's pseudonym, first participant winning on a
    /// shared initial; the non-person markers are left out.
    /// </summary>
    public static Dictionary<string, string> PseudonymsByInitials(IReadOnlyList<LogParticipant> participants)
    {
        var byInitials = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < participants.Count; i++)
        {
            string? initials = participants[i].Initials?.Trim();
            if (!string.IsNullOrEmpty(initials) && !NonPersonTokens.Contains(initials))
            {
                byInitials.TryAdd(initials, Pseudonym(i));
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
