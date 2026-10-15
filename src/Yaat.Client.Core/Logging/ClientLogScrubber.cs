using System.Net;
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
/// (initials or a CID), a CRC position's <c>CALLSIGN (Name)</c>, the CRC lobby list and a URL's <c>initials=</c> and
/// <c>cid=</c> query values. The user's own servers become <c>{server}</c>,
/// non-loopback IP addresses <c>{ip}</c>, the profile folder <c>%USERPROFILE%</c>, other users' home folders and
/// the user and machine names <c>{user}</c> / <c>{machine}</c>, and anything secret-shaped a placeholder. Public
/// endpoints, the official server, loopback and speech transcripts are kept.
/// </summary>
public static partial class ClientLogScrubber
{
    private const string Unknown = "??";
    private const int MinNameLength = 3;
    private const RegexOptions NameOptions = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    // Not people: system authors, a null initials field as the logger prints it, the server's own fallback, and the
    // server's system senders in the initials slot: a CRC flight plan's "[CRC] {TCP}" (RoomEngine) and the ERAM
    // "SECTOR {id}" and "SIGWX" messages (CrcClientState.Eram.Messages).
    private static readonly HashSet<string> KeptWho = new(StringComparer.Ordinal)
    {
        "YAAT",
        "CRC",
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
        result = ScrubLeftoverPeople(result, SessionLogAnonymizer.PseudonymsByInitials(ctx.Participants));
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

    private static string ScrubLeftoverPeople(string text, Dictionary<string, string> byInitials)
    {
        string result = TerminalInitialsRegex().Replace(text, m => Resolve(m.Value, byInitials));
        // The anonymizer already mapped a listed sender; what is left in the slot is someone outside the list.
        result = FromInitialsRegex().Replace(result, m => KeepOrHideWho(m.Value));
        result = RoomActionWhoRegex().Replace(result, m => Resolve(m.Value, byInitials));
        // The anonymizer already mapped a listed CID; one left in a membership line is someone outside the list.
        result = MembershipCidRegex().Replace(result, Unknown);
        result = InitialsQueryValueRegex().Replace(result, m => Resolve(m.Value, byInitials));
        result = CidQueryValueRegex().Replace(result, m => KeepOrUnknown(m.Value));
        result = AssignTargetRegex().Replace(result, m => Resolve(m.Value, byInitials));
        result = RpoPullNameRegex().Replace(result, m => KeepOrUnknown(m.Value));
        result = PositionNameRegex().Replace(result, ScrubPositionName);
        return CrcLobbyLineRegex().Replace(result, line => CrcLobbyEntryRegex().Replace(line.Value, ScrubLobbyEntry));
    }

    // A listed participant's initials kept by the anonymizer (a command verb such as CM) still name them in a who slot.
    private static string Resolve(string who, Dictionary<string, string> byInitials) =>
        (byInitials.TryGetValue(who, out string? pseudonym) && !IsPseudonym(who)) ? pseudonym : KeepOrHideWho(who);

    // A CRC position's callsign in a who slot (a CRC private message to an aircraft) names a position, not a person.
    private static string KeepOrHideWho(string who) =>
        (KeptWho.Contains(who) || IsPseudonym(who) || PositionCallsignRegex().IsMatch(who)) ? who : Unknown;

    private static string KeepOrUnknown(string name) => ((name.Length == 0) || (name == Unknown) || IsPseudonym(name)) ? name : Unknown;

    private static string ScrubPositionName(Match position)
    {
        string sync = position.Groups["sync"].Value;
        string name = position.Groups["name"].Value;
        bool isSectorCode = (sync.Length > 0) && SectorCodeRegex().IsMatch(name);
        return $"{sync}{position.Groups["callsign"].Value} ({(isSectorCode ? name : KeepOrUnknown(name))})";
    }

    private static string ScrubLobbyEntry(Match entry)
    {
        string content = entry.Groups["content"].Value;
        Match pair = CallsignAndNameRegex().Match(content);
        string scrubbed;
        if (pair.Success)
        {
            scrubbed = $"{pair.Groups["callsign"].Value} ({KeepOrUnknown(pair.Groups["name"].Value)})";
        }
        else
        {
            scrubbed = IsLobbyMarker(content) ? content : Unknown;
        }

        return $"{entry.Groups["id"].Value}({scrubbed})";
    }

    // A lobby entry without a "CALLSIGN (Name)" pair is a bare callsign, a placeholder, or a bare real name.
    private static bool IsLobbyMarker(string content) =>
        (content is "no-name" or CrcDisplayName.Unknown or Unknown) || IsPseudonym(content) || BareCallsignRegex().IsMatch(content);

    private static bool IsPseudonym(string token) => PseudonymRegex().IsMatch(token);

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

    [GeneratedRegex(@"^(?:[A-Z]\d|P\d+)$")]
    private static partial Regex PseudonymRegex();

    // MainViewModel.AddTerminalEntry: "[Terminal] [{Kind}] {Initials} {Callsign}: {Message}"; empty initials match nothing.
    [GeneratedRegex(@"(?<=\[Terminal\] \[[^\]\r\n]*\] )\S+(?= )")]
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

    // "{who} pulled {name or CID} into the room as an RPO".
    [GeneratedRegex(@"(?<=\[Terminal\] \[System\] \S* : \S+ pulled ).+?(?= into the room as an RPO)")]
    private static partial Regex RpoPullNameRegex();

    // A CRC position's display name, "{callsign} ({realName})", wherever it appears; the sync group marks RoomEngine's
    // "CRC position synced: now acting as {callsign} ({code})", whose parentheses hold a sector code instead.
    [GeneratedRegex(@"(?<sync>CRC position synced: now acting as )?(?<callsign>\b[A-Z0-9]+_[A-Z0-9_]+) \((?<name>[^()\r\n]*)\)")]
    private static partial Regex PositionNameRegex();

    [GeneratedRegex(@"^[A-Z0-9]+_[A-Z0-9_]+$")]
    private static partial Regex PositionCallsignRegex();

    [GeneratedRegex(@"^.*\[CrcLobby\].*$", RegexOptions.Multiline)]
    private static partial Regex CrcLobbyLineRegex();

    // "{ClientId}({DisplayName ?? "no-name"})", comma-separated; the display name may hold one "(…)" of its own.
    [GeneratedRegex(@"(?<id>[^\s,()]+)\((?<content>(?:[^()\r\n]|\([^()\r\n]*\))*)\)")]
    private static partial Regex CrcLobbyEntryRegex();

    [GeneratedRegex(@"^(?<callsign>\S+) \((?<name>[^()]*)\)$")]
    private static partial Regex CallsignAndNameRegex();

    [GeneratedRegex(@"^[A-Z0-9_]+$")]
    private static partial Regex BareCallsignRegex();

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
