using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Yaat.Client.Services;
using Yaat.Sim.Logging;

namespace Yaat.Client.Logging;

/// <summary>
/// What <see cref="ClientLogScrubber"/> hides: the session's participants (in the server log's pseudonym order),
/// the user's profile folder, the user and machine names, and the hosts of the user's own servers
/// (<see cref="ClientLogScrubber.ServerHostsOf"/>).
/// </summary>
public sealed record ClientLogScrubContext(
    IReadOnlyList<LogParticipant> Participants,
    string UserProfilePath,
    string UserName,
    string MachineName,
    IReadOnlyList<string> ServerHosts
);

/// <summary>
/// Scrubs a client log before it goes into a bug report bundle, which users post publicly. People become the
/// server log's pseudonyms (<c>A0</c> is the same person in both); command verbs stay, so <c>CM 050</c> survives a
/// controller whose initials are CM. People outside the participant list that a rotated log still names become
/// <c>??</c> where the line's structure marks them: the terminal initials slot, <c>(from XX)</c>, membership lines
/// (initials or a CID), the <c>Assigned to XX.</c> error, a CRC position's <c>CALLSIGN (Name)</c> (a name with
/// parentheses of its own is hidden whole, one whose parentheses never close hides the rest of its line), a CRC client's
/// bare real name in its connect, activate, end, flight plan, consolidation, pull, kick, kill and message lines (and in the initials
/// slot of a private message to an aircraft), the room's kick line, the CRC lobby list (where an entry that is
/// no position callsign is a person) and a URL's <c>initials=</c> and
/// <c>cid=</c> query values. A pseudonym-shaped token is kept only when the participant list issued it; a departed
/// person whose initials are <c>B2</c> still becomes <c>??</c>. The user's own servers become <c>{server}</c>,
/// non-loopback IP addresses <c>{ip}</c>, the profile folder <c>%USERPROFILE%</c>, other users' home folders and
/// the user and machine names <c>{user}</c> / <c>{machine}</c>, and anything secret-shaped a placeholder. Public
/// endpoints, the official server, loopback and speech transcripts are kept.
/// </summary>
public static partial class ClientLogScrubber
{
    private const string Unknown = "??";
    private const string OverrideSuffix = " (override)";
    private const int MinNameLength = 3;
    private const RegexOptions NameOptions = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    // Not people: system authors, the AI's own commands (SimulationEngine.Commands), a null initials field as the logger
    // prints it, the server's own fallback, and the server's system senders in the initials slot: a CRC flight plan's
    // "[CRC] {TCP}" (RoomEngine) and the ERAM "SECTOR {id}" and "SIGWX" messages (CrcClientState.Eram.Messages).
    private static readonly HashSet<string> KeptWho = new(StringComparer.Ordinal)
    {
        "YAAT",
        "CRC",
        "AI",
        "(null)",
        "Unknown",
        Unknown,
        "[CRC]",
        "SECTOR",
        "SIGWX",
    };

    // The server's room-action verbs after a who (TrainingHub, TowerCabRooms and RoomEngine terminal lines).
    private const string RoomActionVerbs =
        "set|enabled|disabled|joined|left|created|loaded|unloaded|cleared|updated|amended|went|was|is|pulled|assigned|unassigned";
    private static readonly string OfficialHost = new Uri(UserPreferences.OfficialServerUrl).Host;

    /// <summary>Returns <paramref name="text"/> with every identifying token replaced; the input is not modified.</summary>
    public static string Scrub(string text, ClientLogScrubContext ctx)
    {
        string result = SessionLogAnonymizer.Anonymize(text, ctx.Participants, SessionLogAnonymizer.CommandVerbs);
        result = ScrubLeftoverPeople(result, ctx.Participants);
        // Servers before the machine, so a user name inside a server's host name never splits the host.
        result = ScrubServers(result, ctx.ServerHosts);
        result = ScrubMachine(result, ctx);
        return ScrubSecrets(result);
    }

    /// <summary>
    /// The hosts to hide among <paramref name="serverUrls"/> (the connected server and the saved ones): every parsable
    /// URL's host except the official server's and loopback, once each.
    /// </summary>
    public static IReadOnlyList<string> ServerHostsOf(IEnumerable<string> serverUrls)
    {
        var hosts = new List<string>();
        foreach (string url in serverUrls)
        {
            if (Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri? uri) && !uri.IsLoopback && IsHiddenHost(uri.Host, hosts))
            {
                hosts.Add(uri.Host);
            }
        }

        return hosts;
    }

    private static bool IsHiddenHost(string host, List<string> found) =>
        (host.Length > 0)
        && !string.Equals(host, OfficialHost, StringComparison.OrdinalIgnoreCase)
        && !found.Contains(host, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The fail-closed passes for people outside <paramref name="participants"/>: every who slot and name slot the server's
    /// and client's line shapes mark, left by <see cref="SessionLogAnonymizer.Anonymize"/> as something other than a
    /// pseudonym the list issued, a position or a system sender, becomes <c>??</c>. Run over text already anonymized
    /// with the same list; the bundle runs it over the server log too.
    /// </summary>
    public static string ScrubLeftoverPeople(string text, IReadOnlyList<LogParticipant> participants)
    {
        var people = new LeftoverPeople(
            SessionLogAnonymizer.PseudonymsByInitials(participants),
            PseudonymsByRealName(participants),
            SessionLogAnonymizer.IssuedPseudonyms(participants)
        );
        string result = TerminalInitialsRegex().Replace(text, m => people.Resolve(m.Value));
        // The anonymizer already mapped a listed sender; what is left in the slot is someone outside the list.
        result = FromInitialsRegex().Replace(result, m => people.KeepOrHideWho(m.Value));
        result = RoomActionWhoRegex().Replace(result, m => people.Resolve(m.Value));
        // The anonymizer already mapped a listed CID; one left in a membership line is someone outside the list.
        result = MembershipCidRegex().Replace(result, Unknown);
        result = InitialsQueryValueRegex().Replace(result, m => people.Resolve(m.Value));
        result = CidQueryValueRegex().Replace(result, m => people.KeepOrUnknown(m.Value));
        result = AssignTargetRegex().Replace(result, m => people.Resolve(m.Value));
        result = AssignedToRegex().Replace(result, m => people.Resolve(m.Value));
        result = RpoPullNameRegex().Replace(result, m => people.KeepOrUnknown(m.Value));
        result = ReplaceBalanced(result, PositionNameOpenerRegex(), people.ScrubPositionName);
        result = CrcLobbyLineRegex().Replace(result, line => ReplaceBalanced(line.Value, CrcLobbyEntryOpenerRegex(), people.ScrubLobbyEntry));
        // A CRC client with no position is named by its bare real name; after the passes above a name left in these
        // lines is a position, a placeholder, a pseudonym, or someone outside the list.
        result = CrcEventNameRegex().Replace(result, m => people.KeepOrHideCrcName(m.Value));
        result = CrcPullOrKickNameRegex().Replace(result, m => people.KeepOrHideCrcName(m.Value));
        result = KickedWhoRegex().Replace(result, m => people.KeepOrHideCrcName(m.Value));
        // The killer before the message sender: "[CRC] {sender} killed {victim}: {reason}" also reads as a message.
        result = CrcKillerRegex().Replace(result, m => people.KeepOrHideCrcName(m.Value));
        return CrcMessageSenderRegex().Replace(result, m => people.KeepOrHideCrcSender(m.Value));
    }

    // A listed real name the anonymizer kept (one that is also a command verb) still names its person in a lobby entry.
    private static Dictionary<string, string> PseudonymsByRealName(IReadOnlyList<LogParticipant> participants)
    {
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < participants.Count; i++)
        {
            foreach (string? realName in participants[i].EveryRealName())
            {
                string? name = realName?.Trim();
                if (!string.IsNullOrEmpty(name))
                {
                    byName.TryAdd(name, SessionLogAnonymizer.Pseudonym(i));
                }
            }
        }

        return byName;
    }

    /// <summary>
    /// Replaces each <paramref name="opener"/> match (which ends in "(") and the rest of its parenthesis, nested to any
    /// depth, with <paramref name="render"/>'s text. The renderer gets the text inside the parenthesis, or null when it does
    /// not close on its line, in which case the replacement takes the rest of the line with it.
    /// </summary>
    private static string ReplaceBalanced(string text, Regex opener, Func<Match, string?, string> render)
    {
        var result = new StringBuilder(text.Length);
        int copied = 0;
        Match match = opener.Match(text);
        while (match.Success)
        {
            int innerStart = match.Index + match.Length;
            int close = ClosingParenthesis(text, innerStart);
            string? inner = close >= 0 ? text[innerStart..close] : null;
            result.Append(text, copied, match.Index - copied).Append(render(match, inner));
            copied = close >= 0 ? close + 1 : LineEnd(text, innerStart);
            match = opener.Match(text, copied);
        }

        return result.Append(text, copied, text.Length - copied).ToString();
    }

    // The index of the ")" that closes a parenthesis opened just before start, or -1 when the line ends first.
    private static int ClosingParenthesis(string text, int start)
    {
        int depth = 1;
        for (int i = start; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '\r' or '\n':
                    return -1;
                case '(':
                    depth++;
                    break;
                case ')':
                    if (--depth == 0)
                    {
                        return i;
                    }

                    break;
            }
        }

        return -1;
    }

    private static int LineEnd(string text, int start)
    {
        int end = text.IndexOfAny(['\r', '\n'], start);
        return end < 0 ? text.Length : end;
    }

    /// <summary>The fail-closed who and name rules, against one participant list's initials and issued pseudonyms.</summary>
    private sealed class LeftoverPeople(Dictionary<string, string> byInitials, Dictionary<string, string> byRealName, HashSet<string> issued)
    {
        // A listed participant's initials kept by the anonymizer (a command verb such as CM) still name them in a who slot.
        // An override command's slot is "{initials} (override)" (RoomEngine): the initials resolve, the suffix stays.
        public string Resolve(string who)
        {
            if (who.EndsWith(OverrideSuffix, StringComparison.Ordinal))
            {
                return Resolve(who[..^OverrideSuffix.Length]) + OverrideSuffix;
            }

            return (byInitials.TryGetValue(who, out string? pseudonym) && !issued.Contains(who)) ? pseudonym : KeepOrHideWho(who);
        }

        // A CRC position's callsign in a who slot (a CRC private message to an aircraft) names a position, not a person; with
        // its display name's real name after it, the name is the position-name pass's to hide. The server's system senders
        // "[CRC] {TCP}" and "SECTOR {id}" are two words.
        public string KeepOrHideWho(string who) =>
            (
                KeptWho.Contains(who)
                || issued.Contains(who)
                || CrcDisplayName.IsPositionCallsign(who)
                || PositionWithNameRegex().IsMatch(who)
                || who.StartsWith("[CRC] ", StringComparison.Ordinal)
                || who.StartsWith("SECTOR ", StringComparison.Ordinal)
            )
                ? who
                : Unknown;

        public string KeepOrUnknown(string name) => ((name.Length == 0) || (name == Unknown) || issued.Contains(name)) ? name : Unknown;

        public string ScrubPositionName(Match position, string? name)
        {
            string sync = position.Groups["sync"].Value;
            string callsign = position.Groups["callsign"].Value;
            if (name is null)
            {
                return $"{sync}{callsign} ({Unknown})";
            }

            bool isSectorCode = (sync.Length > 0) && SectorCodeRegex().IsMatch(name);
            return $"{sync}{callsign} ({(isSectorCode ? name : KeepOrUnknown(name))})";
        }

        public string ScrubLobbyEntry(Match entry, string? content)
        {
            string id = entry.Groups["id"].Value;
            if (content is null)
            {
                return $"{id}({Unknown})";
            }

            Match pair = CallsignAndNameRegex().Match(content);
            if (pair.Success)
            {
                return $"{id}({pair.Groups["callsign"].Value} ({KeepOrUnknown(pair.Groups["name"].Value)}))";
            }

            if (byRealName.TryGetValue(content, out string? pseudonym))
            {
                return $"{id}({pseudonym})";
            }

            return $"{id}({(IsLobbyMarker(content) ? content : Unknown)})";
        }

        // A CRC event line's name: a position (bare or with its scrubbed name), a placeholder or an issued pseudonym stays.
        public string KeepOrHideCrcName(string name) =>
            (
                (name is CrcDisplayName.Unknown or Unknown or "UNKNOWN")
                || issued.Contains(name)
                || CrcDisplayName.IsPositionCallsign(name)
                || PositionWithNameRegex().IsMatch(name)
            )
                ? name
                : Unknown;

        // A CRC message's sender, the text before its first ": ": a name as above, or a kept name followed by the server's
        // own words ("OAK_TWR handoff rejected", "?? FP" after the passes above), which stay.
        public string KeepOrHideCrcSender(string sender)
        {
            if (KeepOrHideCrcName(sender) == sender)
            {
                return sender;
            }

            int space = sender.IndexOf(' ');
            return (space > 0) && (KeepOrHideCrcName(sender[..space]) == sender[..space]) ? sender : Unknown;
        }

        // A lobby entry without a "CALLSIGN (Name)" pair is a bare callsign, a placeholder, or a bare real name. A position
        // callsign always holds an underscore, so a one-word upper-case entry without one (BOB) is a person, and so is a
        // pseudonym-shaped one the list did not issue.
        private bool IsLobbyMarker(string content) =>
            (content is "no-name" or CrcDisplayName.Unknown or Unknown) || issued.Contains(content) || CrcDisplayName.IsPositionCallsign(content);
    }

    private static string ScrubServers(string text, IReadOnlyList<string> serverHosts)
    {
        string result = text;
        foreach (string host in serverHosts.OrderByDescending(h => h.Length))
        {
            result = Regex.Replace(result, HostPattern(host), "{server}", NameOptions);
        }

        result = Ipv4Regex().Replace(result, m => HidesAddress(m.Value) ? "{ip}" : m.Value);
        return Ipv6Regex().Replace(result, m => (IsFullOrCompressedIpv6(m.Value) && HidesAddress(m.Value)) ? "{ip}" : m.Value);
    }

    // A one-word host ("box", "yaat") is also a command alias or a folder name, so it is matched only where a host
    // stands: right after "//", or after "(" with a port (a socket exception's "(box:5000)"). A dotted name or an
    // address is matched anywhere it is a whole name. Both ignore case: Uri lower-cases the host, a log prints it as typed.
    private static string HostPattern(string host)
    {
        string escaped = Regex.Escape(host);
        return IsOneWordName(host) ? $@"(?<=//){escaped}(?![\w-]|\.\w)|(?<=\(){escaped}(?=:\d)" : $@"(?<![\w.-]){escaped}(?![\w-]|\.\w)";
    }

    private static bool IsOneWordName(string host) => !host.Contains('.', StringComparison.Ordinal) && !IPAddress.TryParse(host, out _);

    // "12:00:00" and MAC addresses are colon runs too; an IPv6 address has all eight groups or a "::".
    private static bool IsFullOrCompressedIpv6(string value) => value.Contains("::", StringComparison.Ordinal) || (value.Count(c => c == ':') == 7);

    private static bool HidesAddress(string value) =>
        IPAddress.TryParse(value, out IPAddress? address)
        && !IPAddress.IsLoopback(address)
        && !address.Equals(IPAddress.IPv6Any)
        && !address.Equals(IPAddress.Any);

    private static string ScrubMachine(string text, ClientLogScrubContext ctx)
    {
        string result = ReplaceUserProfile(text, ctx.UserProfilePath);
        result = WindowsUserPathRegex().Replace(result, "${prefix}{user}");
        result = UnixUserPathRegex().Replace(result, "${prefix}{user}");
        // The machine name first: it often contains the user name (DANMI-PC), which would otherwise split it.
        result = ReplaceWholeWord(result, ctx.MachineName, "{machine}");
        return ReplaceWholeWord(result, ctx.UserName, "{user}");
    }

    private static string ReplaceUserProfile(string text, string userProfilePath)
    {
        string trimmed = userProfilePath.TrimEnd('\\', '/');
        if (trimmed.Length == 0)
        {
            return text;
        }

        string pattern = string.Join(@"[\\/]", trimmed.Split('\\', '/').Select(Regex.Escape)) + @"(?![\w.-])";
        return Regex.Replace(text, pattern, "%USERPROFILE%", NameOptions);
    }

    // Placeholder braces and %VAR% marks count as boundaries, so a user named "user" leaves {user} alone; a dot does
    // too, so a user name inside a kept host (yaat1.leftos.dev) is not split.
    private static string ReplaceWholeWord(string text, string word, string placeholder)
    {
        string trimmed = word.Trim();
        if (trimmed.Length < MinNameLength)
        {
            return text;
        }

        string pattern = $@"(?<![\w{{%.]){Regex.Escape(trimmed)}(?![\w}}%]|\.\w)";
        return Regex.Replace(text, pattern, placeholder, NameOptions);
    }

    private static string ScrubSecrets(string text)
    {
        string result = EmailRegex().Replace(text, "{email}");
        result = SecretQueryValueRegex().Replace(result, "{secret}");
        result = BearerRegex().Replace(result, "Bearer {secret}");
        return JwtRegex().Replace(result, "{secret}");
    }

    // MainViewModel.AddTerminalEntry: "[Terminal] [{Kind}] {Initials} {Callsign}: {Message}"; empty initials match nothing.
    // The initials run to the callsign before the first ":", so a CRC client's multi-word name there (a private message
    // to an aircraft, CrcClientState.Messaging) is one slot.
    [GeneratedRegex(@"(?<=\[Terminal\] \[[^\]\r\n]*\] )\S[^\r\n]*?(?= \S*: )")]
    private static partial Regex TerminalInitialsRegex();

    [GeneratedRegex(@"(?<=\(from )[^\s()]+(?=\))")]
    private static partial Regex FromInitialsRegex();

    // The server's room lines, "[Terminal] [System] {initials?} : {who} {action}" ("CM set command run delay to 2s",
    // "XY was kicked from the room"): an initials-shaped first word followed by a lowercase word is the who.
    // Initials are two characters, and the next word is one of the server's room-action verbs (TrainingHub,
    // TowerCabRooms and RoomEngine terminal lines), so echoed alias text ("TWR frequency is 118.3") is left alone.
    [GeneratedRegex(@"(?<=\[Terminal\] \[System\] \S* : )[A-Z][A-Z0-9](?= (?:" + RoomActionVerbs + @")\b)")]
    private static partial Regex RoomActionWhoRegex();

    // A membership line whose who is a CID: a Tower Cab viewer with no same-CID member ("1999999 joined the room
    // (Tower Cab 3D)", TowerCabRooms) or a kicked CID with no member or CRC client left (TrainingHub's kick line).
    [GeneratedRegex(@"(?<=\[Terminal\] \[System\] \S* : )\d{4,8}(?= (?:joined the room|left the room|was kicked from the room)\b)")]
    private static partial Regex MembershipCidRegex();

    // The web clients' URLs carry the user's own initials ("/vstrips/?initials=CM&artcc=ZOA", MainViewModel.Rooms).
    [GeneratedRegex(@"(?<=[?&]initials=)[^&\s#""']+")]
    private static partial Regex InitialsQueryValueRegex();

    [GeneratedRegex(@"(?<=[?&]cid=)[^&\s#""']+")]
    private static partial Regex CidQueryValueRegex();

    // RoomEngine: "{actor} assigned {callsigns} to {target}".
    [GeneratedRegex(@"(?<=\[Terminal\] \[System\] \S* : \S+ assigned [^\r\n]+ to )\S+(?=\r?$)", RegexOptions.Multiline)]
    private static partial Regex AssignTargetRegex();

    // The sector or TCP code of RoomEngine's sync line ("now acting as OAK_TWR (TW)"); anywhere else a short all-caps
    // token in a position's parentheses can be a real name.
    [GeneratedRegex(@"^[A-Z0-9]{1,3}$")]
    private static partial Regex SectorCodeRegex();

    // RoomEngine's command result: "Assigned to {ownerInitials}. Prefix with ** to override."
    [GeneratedRegex(@"(?<=Assigned to )\S+?(?=\. Prefix with \*\* to override\.)")]
    private static partial Regex AssignedToRegex();

    // "{who} pulled {name or CID} into the room as an RPO".
    [GeneratedRegex(@"(?<=\[Terminal\] \[System\] \S* : \S+ pulled ).+?(?= into the room as an RPO)")]
    private static partial Regex RpoPullNameRegex();

    // The start of a CRC position's display name, "{callsign} (", wherever it appears; the real name inside may hold
    // parentheses of its own, so the scan for its end is ReplaceBalanced's. The sync group marks RoomEngine's
    // "CRC position synced: now acting as {callsign} ({code})", whose parentheses hold a sector code instead.
    [GeneratedRegex(@"(?<sync>CRC position synced: now acting as )?(?<callsign>\b[A-Z0-9]+_[A-Z0-9_]+) \(")]
    private static partial Regex PositionNameOpenerRegex();

    [GeneratedRegex(@"^[A-Z0-9]+_[A-Z0-9_]+ \(.*\)$")]
    private static partial Regex PositionWithNameRegex();

    // TrainingHub's kick line, "{who} was kicked from the room", who = the kicked CID's initials, else its CRC client's
    // display name (a bare real name when it has no position), else the CID. The name passes stop at a ":", so a CRC
    // message's text ("[CRC] OAK_TWR: ILS back up, frequency connected") is never taken for a name.
    [GeneratedRegex(@"(?<=\[Terminal\] \[System\] \S* : )[^\r\n:]+?(?= was kicked from the room\r?$)", RegexOptions.Multiline)]
    private static partial Regex KickedWhoRegex();

    // CrcClientState.Info's "[CRC] {sender} killed {victim}: {reason}".
    [GeneratedRegex(@"(?<=\[Terminal\] \[System\] \S* : \[CRC\] )[^\r\n:]+?(?= killed )")]
    private static partial Regex CrcKillerRegex();

    // CrcClientState.Messaging's "[CRC] {sender}: {text}", "[CRC PM → {recipient}] {sender}: {text}" and
    // "[CRC ATC|Broadcast|vNAS|Wallop] {sender}: {text}"; the sender is the position callsign, else the display name.
    [GeneratedRegex(@"(?<=\[Terminal\] \[System\] \S* : \[CRC(?: PM → [^\]\r\n]*| ATC| Broadcast| vNAS| Wallop)?\] )[^\r\n]+?(?=: )")]
    private static partial Regex CrcMessageSenderRegex();

    // CrcClientState's "[CRC] {displayName} {event}" lines (Session.cs, CrcClientState.cs), its flight plan echo,
    // "[CRC] {displayName} FP: ({line1} {line2})" (FlightPlan.cs), and its STARS "[CRC] {displayName} consolidated …" and
    // "… deconsolidated …" lines (Stars.cs).
    [GeneratedRegex(
        @"(?<=\[CRC\] )[^\r\n:]+?(?= (?:(?:connected|disconnected|activated|deactivated|ended session)\r?$|FP: \(|(?:de)?consolidated ))",
        RegexOptions.Multiline
    )]
    private static partial Regex CrcEventNameRegex();

    // TrainingHub's "[CRC] Pulled {name} into room" and "[CRC] Kicked {name} from room".
    [GeneratedRegex(@"(?<=\[CRC\] (?:Pulled|Kicked) )[^\r\n]+?(?= (?:into|from) room\r?$)", RegexOptions.Multiline)]
    private static partial Regex CrcPullOrKickNameRegex();

    [GeneratedRegex(@"^.*\[CrcLobby\].*$", RegexOptions.Multiline)]
    private static partial Regex CrcLobbyLineRegex();

    // The start of a lobby entry, "{ClientId}({DisplayName ?? "no-name"})", comma-separated; the display name may hold
    // parentheses nested to any depth, so the scan for its end is ReplaceBalanced's.
    [GeneratedRegex(@"(?<id>[^\s,()]+)\(")]
    private static partial Regex CrcLobbyEntryOpenerRegex();

    // A balanced lobby entry's "{callsign} ({name})"; the name runs to the entry's last ")". Only a position callsign
    // opens the pair, so a positionless name with parentheses of its own ("Zed (Jr)") is no pair.
    [GeneratedRegex(@"^(?<callsign>[A-Z0-9]+_[A-Z0-9_]+) \((?<name>.*)\)$")]
    private static partial Regex CallsignAndNameRegex();

    // The folder name runs to the next separator and may hold spaces; it cannot hold a drive colon or a wildcard.
    [GeneratedRegex(@"(?<prefix>\b[A-Za-z]:[\\/]Users[\\/])(?<name>[^\\/\r\n:*?""<>|]+?)(?=[\\/])", RegexOptions.IgnoreCase)]
    private static partial Regex WindowsUserPathRegex();

    // Only a path that starts a token: "/Users/123/" after a URL's host is a URL path, not a home folder.
    [GeneratedRegex(@"(?<=^|[\s""'(=])(?<prefix>/(?:home|Users)/)(?<name>[^/\s""']+)(?=/)", RegexOptions.Multiline)]
    private static partial Regex UnixUserPathRegex();

    // Not inside a longer dotted run, and not an assembly version ("Version=10.0.0.0").
    [GeneratedRegex(@"(?<![\w.])(?<!Version=)(?:\d{1,3}\.){3}\d{1,3}(?!\w|\.\d)")]
    private static partial Regex Ipv4Regex();

    [GeneratedRegex(@"(?<![\w:.])(?:[0-9A-Fa-f]{0,4}:){2,7}[0-9A-Fa-f]{0,4}(?![\w:.])")]
    private static partial Regex Ipv6Regex();

    [GeneratedRegex(@"[\w.+-]+@[\w-]+(?:\.[\w-]+)+")]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"(?<=[?&](?:code|token|access_token|refresh_token)=)[^&\s#""']+", RegexOptions.IgnoreCase)]
    private static partial Regex SecretQueryValueRegex();

    [GeneratedRegex(@"\bBearer\s+[^\s""',]+")]
    private static partial Regex BearerRegex();

    [GeneratedRegex(@"(?<![\w-])eyJ[\w-]+\.[\w-]+\.[\w-]+")]
    private static partial Regex JwtRegex();
}
