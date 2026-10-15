using System.Text.RegularExpressions;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Logging;

/// <summary>A recording's action log and terminal log with the people in them replaced by pseudonyms.</summary>
public sealed record PseudonymizedRecording(List<RecordedAction> Actions, List<RecordedTerminalEntry> TerminalLog);

/// <summary>
/// Pseudonymises the people in a recording before it goes into a bug report bundle, with the same participant list and the
/// same pseudonyms as <see cref="SessionLogAnonymizer"/>, so <c>A0</c> is one person in the recording and in the logs.
/// The initials slot of every command, chat and terminal line and every bookmark's creator becomes the person's pseudonym,
/// or <c>??</c> for someone the list does not hold; the terminal and chat text goes through
/// <see cref="SessionLogAnonymizer.Anonymize"/>, and the who slots the server writes into terminal text (the assignment
/// lines and the <c>Assigned to XY.</c> error) are resolved the same way. System senders (no initials, <c>AI</c>,
/// <c>CRC</c>, <c>YAAT</c>, a CRC flight plan's <c>[CRC] {TCP}</c>, the ERAM <c>SECTOR {id}</c> and <c>SIGWX</c>
/// senders) and CRC position callsigns are kept. A command's text and every other field stay as recorded: nothing the
/// simulation replays reads the initials, so the pseudonymised recording replays to the same states.
/// </summary>
public static partial class RecordingPseudonymizer
{
    public const string Unknown = SessionLogAnonymizer.Unknown;
    private const string OverrideSuffix = " (override)";

    // The initials slot's non-person senders, and the CRC private-message sender's fallbacks when the client has no name.
    private static readonly HashSet<string> SystemSenders = new(StringComparer.Ordinal)
    {
        "",
        "AI",
        "CRC",
        "YAAT",
        "SIGWX",
        "UNKNOWN",
        CrcDisplayName.Unknown,
    };

    /// <summary>
    /// Returns <paramref name="actions"/> and <paramref name="terminalLog"/> with every person replaced by their pseudonym in
    /// <paramref name="participants"/> (their position in the list), or <c>??</c> when the list does not hold them. An action
    /// that names no person is returned as the same instance.
    /// </summary>
    public static PseudonymizedRecording Pseudonymize(
        IReadOnlyList<RecordedAction> actions,
        IReadOnlyList<RecordedTerminalEntry> terminalLog,
        IReadOnlyList<LogParticipant> participants
    )
    {
        var people = new People(participants);
        List<RecordedAction> scrubbedActions =
        [
            .. actions.Select<RecordedAction, RecordedAction>(action =>
                action switch
                {
                    RecordedCommand command => command with { Initials = people.Who(command.Initials) },
                    RecordedChat chat => chat with { Initials = people.Who(chat.Initials), Message = people.Text(chat.Message) },
                    _ => action,
                }
            ),
        ];
        List<RecordedTerminalEntry> scrubbedLog =
        [
            .. terminalLog.Select(entry => entry with { Initials = people.Who(entry.Initials), Message = people.TerminalText(entry) }),
        ];
        return new PseudonymizedRecording(scrubbedActions, scrubbedLog);
    }

    /// <summary>
    /// Returns <paramref name="bookmarks"/> with each creator replaced by their pseudonym in <paramref name="participants"/>,
    /// or <c>??</c>; a bookmark with no creator keeps none, and every name stays as typed.
    /// </summary>
    public static IReadOnlyList<TimelineBookmark> PseudonymizeBookmarks(
        IReadOnlyList<TimelineBookmark> bookmarks,
        IReadOnlyList<LogParticipant> participants
    )
    {
        var people = new People(participants);
        return [.. bookmarks.Select(b => b.CreatorInitials is null ? b : b with { CreatorInitials = people.Who(b.CreatorInitials) })];
    }

    private sealed class People
    {
        private readonly IReadOnlyList<LogParticipant> _participants;
        private readonly Dictionary<string, string> _byIdentity = new(StringComparer.Ordinal);
        private readonly HashSet<string> _issued = new(StringComparer.Ordinal);

        public People(IReadOnlyList<LogParticipant> participants)
        {
            _participants = participants;
            for (int i = 0; i < participants.Count; i++)
            {
                string pseudonym = SessionLogAnonymizer.Pseudonym(i);
                _issued.Add(pseudonym);
                // Initials first, as SessionLogAnonymizer.PseudonymsByInitials does: first participant wins a shared token.
                foreach (string? initials in participants[i].EveryInitials())
                {
                    Add(initials, pseudonym);
                }

                foreach (string? realName in participants[i].EveryRealName())
                {
                    Add(realName, pseudonym);
                }

                Add(participants[i].Cid, pseudonym);
            }
        }

        /// <summary>A who slot as recorded: a system sender or a position stays, a person becomes a pseudonym or <c>??</c>.</summary>
        public string Who(string who)
        {
            if (IsSystemSender(who))
            {
                return who;
            }

            if (who.EndsWith(OverrideSuffix, StringComparison.Ordinal))
            {
                return Who(who[..^OverrideSuffix.Length]) + OverrideSuffix;
            }

            Match position = PositionAndNameRegex().Match(who);
            if (position.Success)
            {
                return $"{position.Groups["callsign"].Value} ({_byIdentity.GetValueOrDefault(position.Groups["name"].Value, Unknown)})";
            }

            return _byIdentity.GetValueOrDefault(who.Trim(), Unknown);
        }

        public string Text(string text) => SessionLogAnonymizer.Anonymize(text, _participants, SessionLogAnonymizer.CommandVerbs);

        /// <summary>
        /// Terminal text: anonymized, then the who slots of the server's assignment lines resolved. The assign and unassign
        /// lines are RoomEngine's own (a System entry with no initials); a chat or command shaped like one is left as is.
        /// </summary>
        public string TerminalText(RecordedTerminalEntry entry)
        {
            string result = Text(entry.Message);
            result = AssignedToRegex().Replace(result, m => $"Assigned to {Resolve(m.Groups["who"].Value)}{m.Groups["rest"].Value}");
            if ((entry.Kind != "System") || (entry.Initials.Length != 0))
            {
                return result;
            }

            result = AssignLineRegex()
                .Replace(result, m => $"{Resolve(m.Groups["actor"].Value)} assigned {m.Groups["list"].Value} to {Resolve(m.Groups["target"].Value)}");
            return UnassignLineRegex().Replace(result, m => $"{Resolve(m.Groups["actor"].Value)}{m.Groups["rest"].Value}");
        }

        // A who in anonymized text: an issued pseudonym is the anonymizer's work; initials it kept (a command verb such as
        // CM) or never knew are resolved as a who slot.
        private string Resolve(string who) => _issued.Contains(who) ? who : Who(who);

        private void Add(string? token, string pseudonym)
        {
            string? trimmed = token?.Trim();
            if (!string.IsNullOrEmpty(trimmed) && !SystemSenders.Contains(trimmed))
            {
                _byIdentity.TryAdd(trimmed, pseudonym);
            }
        }

        private static bool IsSystemSender(string who) =>
            SystemSenders.Contains(who)
            || who.StartsWith("[CRC] ", StringComparison.Ordinal)
            || who.StartsWith("SECTOR ", StringComparison.Ordinal)
            || CrcDisplayName.IsPositionCallsign(who);
    }

    // A CRC private message's sender when the position has no callsign entry: the display name "{callsign} ({realName})".
    [GeneratedRegex(@"^(?<callsign>[A-Z0-9]+_[A-Z0-9_]+) \((?<name>.*)\)$")]
    private static partial Regex PositionAndNameRegex();

    // yaat-server RoomEngine: $"Assigned to {ownerInitials}. Prefix with ** to override."
    [GeneratedRegex(@"^Assigned to (?<who>\S+?)(?<rest>\. Prefix with \*\* to override\.)$")]
    private static partial Regex AssignedToRegex();

    // yaat-server RoomEngine: $"{actorInitials} assigned {list} to {targetInitials}".
    [GeneratedRegex(@"^(?<actor>\S+) assigned (?<list>.+) to (?<target>\S+)$")]
    private static partial Regex AssignLineRegex();

    // yaat-server RoomEngine: $"{actorInitials} unassigned {list} (available to all)".
    [GeneratedRegex(@"^(?<actor>\S+)(?<rest> unassigned .+ \(available to all\))$")]
    private static partial Regex UnassignLineRegex();
}
