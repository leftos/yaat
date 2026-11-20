using System.Globalization;
using System.Text.RegularExpressions;
using Yaat.Client.Views.Map;
using Yaat.Sim.Commands;

namespace Yaat.Client.Views.Scope.Entries;

/// <summary>What a STARS entry needs from the Scope's state before its translation can be sent.</summary>
internal enum StarsEntryNeed
{
    /// <summary>Nothing: the translation is sent as it is.</summary>
    None,

    /// <summary>Bare <c>HND</c>: <c>ACCEPT</c> for a handoff inbound to the acting position, <c>CANCEL</c> for one outbound.</summary>
    HandoffAcceptOrCancel,

    /// <summary><c>*J</c>/<c>*P</c> with a size: the selected aircraft's callsign goes after the translation.</summary>
    RingPlacement,

    /// <summary>Bare <c>*J</c>/<c>*P</c>: the selected aircraft's ring of that kind, at its current size, is removed.</summary>
    RingRemoval,

    /// <summary>Bare <c>MF R</c>: the selected aircraft's callsign goes after the translation.</summary>
    SelectedTrack,

    /// <summary>
    /// <c>MIN</c>: the Scope's own min-sep readout, run on the Scope and never sent: bare it clears the pair, with two tracks it
    /// shows theirs, and with one it answers that two are needed.
    /// </summary>
    MinimumSeparation,

    /// <summary>
    /// An entry led by <c>*T</c>: the range bearing line entry, run on the Scope by CRC's grammar
    /// (<see cref="StarsEntryParser.ReadRangeBearing"/>) and never sent.
    /// </summary>
    RangeBearingLine,
}

/// <summary>What a <c>*T</c> entry, or the entry completing a pending line, does by CRC's grammar.</summary>
public enum StarsRblAction
{
    /// <summary><c>*T</c> alone: with a slew it anchors a line there; with none it clears the lines.</summary>
    Bare,

    /// <summary><c>*T</c> and a line number from 1 to 15: removes that line.</summary>
    Remove,

    /// <summary><c>*T</c> and a one- or two-digit line number outside 1 to 15: <see cref="StarsErrorText.RblId"/>.</summary>
    RblId,

    /// <summary>Four digits 0-7: the track with that beacon code.</summary>
    Beacon,

    /// <summary>Two to ten of <c>[A-Z0-9-]</c>: under three characters an ACID, else a fix or airport first, then an ACID.</summary>
    Name,

    /// <summary>Any other entry: <see cref="StarsErrorText.Format"/>.</summary>
    Format,
}

/// <summary>A <c>*T</c> entry as CRC's grammar reads it.</summary>
/// <param name="Action">What the entry does.</param>
/// <param name="Slot">The line number of a <see cref="StarsRblAction.Remove"/> or <see cref="StarsRblAction.RblId"/>; 0 otherwise.</param>
/// <param name="Token">
/// The beacon code or the fix/ACID of a <see cref="StarsRblAction.Beacon"/> or <see cref="StarsRblAction.Name"/>; the line
/// number's digits as typed for a <see cref="StarsRblAction.Remove"/> or <see cref="StarsRblAction.RblId"/>; empty otherwise.
/// </param>
internal readonly record struct StarsRblEntry(StarsRblAction Action, int Slot, string Token);

/// <summary>CRC's STARS error categories, as its preview shows them: upper-cased, with no trailing period.</summary>
internal static class StarsErrorText
{
    public const string Format = "FORMAT";
    public const string IllegalFunction = "ILL FNCT";
    public const string IllegalTrack = "ILL TRK";
    public const string IllegalPosition = "ILL POS";
    public const string IllegalParameter = "ILL PARAM";
    public const string IllegalValue = "ILL VALUE";
    public const string IllegalMap = "ILL MAP";
    public const string IllegalPrefSet = "ILL PREFSET";
    public const string NoFlight = "NO FLIGHT";
    public const string NoTrack = "NO TRK";
    public const string MultipleFlight = "MULTIPLE FLIGHT";
    public const string DuplicateTrack = "DUP TRK";
    public const string Capacity = "CAPACITY";
    public const string RangeLimit = "RANGE LIMIT";
    public const string RblId = "RBL ID";
    public const string NotImplemented = "NOT IMPLEMENTED";
}

/// <summary>A STARS entry's reading: its first word, the YAAT command it translates to (null when not supported) and its gloss.</summary>
/// <param name="FirstWord">The entry's first word, upper-cased, as the kind label names it.</param>
/// <param name="Translation">The YAAT command text Enter sends, or null for a STARS entry the Scope does not support.</param>
/// <param name="Gloss">The translation's registry label, lower-cased; empty when not supported.</param>
internal sealed record StarsEntryRead(string FirstWord, string? Translation, string Gloss)
{
    /// <summary>What the translation needs from the Scope's state before it is sent.</summary>
    public StarsEntryNeed Need { get; init; }

    /// <summary>The callsign typed after the entry, or null when the entry acts on the selected aircraft.</summary>
    public string? Callsign { get; init; }

    /// <summary>The second callsign typed after a <c>MIN</c> entry, or null.</summary>
    public string? SecondCallsign { get; init; }

    /// <summary>The ring kind of a <c>*J</c>/<c>*P</c> entry; <see cref="ScopeRingTool.None"/> otherwise.</summary>
    public ScopeRingTool Ring { get; init; }

    /// <summary>Whether the entry maps to a YAAT command.</summary>
    public bool Supported => Translation is not null;
}

/// <summary>
/// Reads an entry typed on a STARS-profile Scope as a STARS entry: the glued shapes it knows (<c>+AB1</c>, <c>++100</c>,
/// <c>4</c>, <c>1D*</c>, <c>**2B</c>, <c>*J5</c>, ...), YAAT's own key words standing for the function keys (<c>INIT</c>,
/// <c>TERM</c>, <c>HND</c>, <c>MF</c>, <c>CA</c>, <c>RANGE</c>), which CRC has as keys, not text, and the per-track PTL of a
/// <c>MF R</c> entry. The <c>*T</c> range bearing line entry reads by CRC's grammar (<see cref="ReadRangeBearing"/>): <c>*T</c>
/// alone, <c>*T</c> glued to a line number (<c>*T1</c>), to a four-digit beacon code (<c>*T1234</c>) or to a fix or ACID
/// (<c>*TOAK</c>), and the entry completing a pending line (<see cref="ReadRangeBearingCompletion"/>). An entry with a YAAT
/// command translates to it; a STARS entry with none reads as not supported; anything else is no STARS entry (null). Pure:
/// the rows that need state say so in <see cref="StarsEntryRead.Need"/>.
/// </summary>
internal static partial class StarsEntryParser
{
    private const string Override = "** ";

    /// <summary>The words that stand for STARS's function keys.</summary>
    internal static readonly IReadOnlyList<string> KeyWords = ["INIT", "TERM", "HND", "MF", "CA", "RANGE", "UN"];

    [GeneratedRegex(@"^\+\+(\d{3})$")]
    private static partial Regex RequestedAltitude();

    [GeneratedRegex(@"^\+(\d{3})$")]
    private static partial Regex TemporaryAltitude();

    [GeneratedRegex(@"^\+([A-Z0-9*./]{1,4})?$")]
    private static partial Regex Scratchpad2();

    [GeneratedRegex(@"^\*([JP])([\d.]+)?$")]
    private static partial Regex Tpa();

    [GeneratedRegex("^[0-9]{1,2}$")]
    private static partial Regex RblLineNumber();

    [GeneratedRegex(@"^[0-7]{4}$")]
    private static partial Regex RblBeacon();

    [GeneratedRegex(@"^[A-Z0-9\-]{2,10}$")]
    private static partial Regex RblName();

    [GeneratedRegex(" {2,}")]
    private static partial Regex SpaceRun();

    [GeneratedRegex(@"^\*\*([A-Z0-9]{1,3}( [A-Z0-9]{1,3}){0,9})$")]
    private static partial Regex ForceQuicklook();

    [GeneratedRegex(@"^([A-Z0-9]{1,3})\*$")]
    private static partial Regex PointOut();

    [GeneratedRegex(@"^[1-9]$")]
    private static partial Regex Direction();

    [GeneratedRegex(@"^\d{3}$")]
    private static partial Regex Altitude();

    [GeneratedRegex(@"^[A-Z][A-Z0-9]{1,9}$")]
    private static partial Regex CallsignShape();

    [GeneratedRegex(@"^[A-Z0-9]{1,3}$")]
    private static partial Regex TcpShape();

    [GeneratedRegex(@"^L([1-9])$")]
    private static partial Regex GluedLeader();

    /// <summary>Reads <paramref name="trimmed"/> as a STARS entry.</summary>
    /// <returns>The reading, or null when the text is no STARS entry.</returns>
    public static StarsEntryRead? TryRead(string trimmed)
    {
        if ((trimmed.Length == 0) || trimmed.StartsWith(Override, StringComparison.Ordinal))
        {
            return null;
        }

        string upper = trimmed.ToUpperInvariant();
        string[] words = upper.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words[0][0] switch
        {
            '+' => ReadPlus(upper, words),
            '*' => ReadStar(upper, words),
            '/' => Unsupported(words[0]),
            _ => ReadKeyWord(words) ?? ReadBare(upper, words),
        };
    }

    /// <summary>
    /// Reads <paramref name="text"/> as a <c>*T</c> entry by CRC's grammar, in its order (InputManager's implied-entry branch):
    /// <c>*T</c> alone; <c>*T</c> and one or two digits, a line number (1-15, else RBL ID); <c>*T</c> and four digits 0-7, a
    /// beacon code; <c>*T</c> and two to ten of <c>[A-Z0-9-]</c>, a fix or ACID; any other <c>*T</c> entry is FORMAT. Runs of
    /// spaces collapse and the text is upper-cased first, as CRC's preview does.
    /// </summary>
    /// <returns>The reading, or null when the text is not led by <c>*T</c>.</returns>
    public static StarsRblEntry? ReadRangeBearing(string text)
    {
        string entry = NormalizeRbl(text);
        if (!entry.StartsWith("*T", StringComparison.Ordinal))
        {
            return null;
        }

        string rest = entry[2..];
        if (rest.Length == 0)
        {
            return new StarsRblEntry(StarsRblAction.Bare, 0, "");
        }

        if (RblLineNumber().IsMatch(rest))
        {
            int slot = int.Parse(rest, CultureInfo.InvariantCulture);
            return new StarsRblEntry((slot is >= 1 and <= RangeBearingLineStore.MaxLines) ? StarsRblAction.Remove : StarsRblAction.RblId, slot, rest);
        }

        return ReadRblTarget(rest);
    }

    /// <summary>
    /// Reads the entry that completes a pending line by CRC's grammar (CompletePendingRbl): four digits 0-7, a beacon code;
    /// two to ten of <c>[A-Z0-9-]</c>, a fix or ACID; anything else is FORMAT.
    /// </summary>
    public static StarsRblEntry ReadRangeBearingCompletion(string text) => ReadRblTarget(NormalizeRbl(text));

    private static StarsRblEntry ReadRblTarget(string token) =>
        RblBeacon().IsMatch(token) ? new StarsRblEntry(StarsRblAction.Beacon, 0, token)
        : RblName().IsMatch(token) ? new StarsRblEntry(StarsRblAction.Name, 0, token)
        : new StarsRblEntry(StarsRblAction.Format, 0, "");

    private static string NormalizeRbl(string text) => SpaceRun().Replace(text.Trim(), " ").ToUpperInvariant();

    // S1-S3: requested altitude, temporary altitude, scratchpad 2.
    private static StarsEntryRead ReadPlus(string upper, string[] words)
    {
        string first = words[0];
        if (RequestedAltitude().Match(upper) is { Success: true } cruise)
        {
            // CRUISE 0 fails the parse, so ++000 has no YAAT command, as 000 has none.
            return (cruise.Groups[1].Value == "000")
                ? Unsupported(first)
                : Supported(first, $"CRUISE {Hundreds(cruise)}", CanonicalCommandType.Cruise);
        }

        if (TemporaryAltitude().Match(upper) is { Success: true } temp)
        {
            return Supported(first, $"TEMPALT {Hundreds(temp)}", CanonicalCommandType.TemporaryAltitude);
        }

        return ReadScratchpad2(first, upper, null) ?? Unsupported(first);
    }

    // +text / + : scratchpad 2, with a callsign before it when one is given; null when the token is no scratchpad 2 entry.
    private static StarsEntryRead? ReadScratchpad2(string first, string token, string? callsign) =>
        (Scratchpad2().Match(token) is { Success: true } sp2)
            ? Supported(first, WithCallsign(callsign, $"SP2 {sp2.Groups[1].Value}".TrimEnd()), CanonicalCommandType.Scratchpad2)
            : null;

    // S7 (**), S8 force quicklook, S9 TPA, S10 RBL; every other *-led entry is not supported.
    private static StarsEntryRead ReadStar(string upper, string[] words)
    {
        string first = words[0];
        if (upper == "**")
        {
            return Supported(first, "POHO", CanonicalCommandType.ConvertPointout);
        }

        // Every *T-led entry is the range bearing line entry; CRC's grammar answers FORMAT for the shapes it does not read.
        if (upper.StartsWith("*T", StringComparison.Ordinal))
        {
            return new StarsEntryRead(first, string.Join(' ', words), "range bearing line") { Need = StarsEntryNeed.RangeBearingLine };
        }

        if (Tpa().Match(upper) is { Success: true } tpa)
        {
            return ReadTpa(first, tpa);
        }

        string joined = string.Join(' ', words);
        return IsForceQuicklook(joined) ? Supported(first, $"FQL {joined[2..]}", CanonicalCommandType.ForceQuicklook) : Unsupported(first);
    }

    // **<tcp>[ <tcp>...]: up to ten TCPs, never **J/**P (TPA for all tracks) or an ALL form.
    private static bool IsForceQuicklook(string joined) =>
        ForceQuicklook().IsMatch(joined) && (joined is not ("**J" or "**P")) && !joined[2..].Split(' ').Contains("ALL");

    private static StarsEntryRead ReadTpa(string first, Match tpa)
    {
        bool jRing = tpa.Groups[1].Value == "J";
        string kind = jRing ? "jring" : "cone";
        string size = tpa.Groups[2].Value;
        string gloss = Gloss(jRing ? CanonicalCommandType.JRing : CanonicalCommandType.Cone);
        return new StarsEntryRead(first, $".scope rings {kind} {size}".TrimEnd(), gloss)
        {
            Need = (size.Length == 0) ? StarsEntryNeed.RingRemoval : StarsEntryNeed.RingPlacement,
            Ring = jRing ? ScopeRingTool.JRing : ScopeRingTool.Cone,
        };
    }

    // S11: the function-key words; null when the first word is none (or bare CA, the pilot Circle Airport).
    private static StarsEntryRead? ReadKeyWord(string[] words) =>
        words[0] switch
        {
            "INIT" => ReadTrackVerb(words, "TRACK", CanonicalCommandType.TrackAircraft),
            "TERM" => ReadTerm(words),
            "HND" => ReadHandoff(words),
            "MF" => ReadMultiFunc(words),
            "CA" => ReadCa(words),
            "RANGE" => ReadRange(words),
            "MAPS" => Unsupported(words[0]),
            "MIN" => ReadMinSep(words),
            "UN" => ReadUn(words),
            _ => null,
        };

    // MIN [callsign [callsign]]: the Scope's min-sep readout. The translation is the entry as typed, upper-cased: it is run on
    // the Scope, never sent. The gloss is hand-written, as the range row's is: min sep is no YAAT command.
    private static StarsEntryRead ReadMinSep(string[] words) =>
        ((words.Length <= 3) && words.Skip(1).All(IsCallsign))
            ? new StarsEntryRead(words[0], string.Join(' ', words), "minimum separation")
            {
                Need = StarsEntryNeed.MinimumSeparation,
                Callsign = words.ElementAtOrDefault(1),
                SecondCallsign = words.ElementAtOrDefault(2),
            }
            : Unsupported(words[0]);

    // TERM ALL has no YAAT command (no drop-all verb).
    private static StarsEntryRead ReadTerm(string[] words) =>
        (words is [_, "ALL"]) ? Unsupported(words[0]) : ReadTrackVerb(words, "DROP", CanonicalCommandType.DropTrack);

    // Bare CA is the pilot Circle Airport; CA followed by a letter is a STARS entry.
    private static StarsEntryRead? ReadCa(string[] words) => (words.Length == 1) ? null : ReadConflictAlert(words);

    // Bare UN is STARS reject point-out; UN followed by anything is left to the pilot read.
    private static StarsEntryRead? ReadUn(string[] words) =>
        (words.Length == 1) ? Supported(words[0], "PORJ", CanonicalCommandType.RejectPointout) : null;

    // INIT and TERM: the verb, with a callsign before it when one is typed after the word.
    private static StarsEntryRead ReadTrackVerb(string[] words, string verb, CanonicalCommandType type) =>
        words.Length switch
        {
            1 => Supported(words[0], verb, type),
            2 when IsCallsign(words[1]) => Supported(words[0], $"{words[1]} {verb}", type),
            _ => Unsupported(words[0]),
        };

    // HND <id> [callsign] hands off; bare HND [callsign] accepts or cancels by the track's state.
    private static StarsEntryRead ReadHandoff(string[] words)
    {
        string first = words[0];
        if (IsHandoffWithId(words))
        {
            return Supported(first, WithCallsign(words.ElementAtOrDefault(2), $"HO {words[1]}"), CanonicalCommandType.InitiateHandoff);
        }

        string? callsign = words.ElementAtOrDefault(1);
        if ((words.Length == 1) || ((words.Length == 2) && IsCallsign(words[1])))
        {
            return new StarsEntryRead(first, WithCallsign(callsign, "ACCEPT"), "accept or cancel handoff")
            {
                Need = StarsEntryNeed.HandoffAcceptOrCancel,
                Callsign = callsign,
            };
        }

        return Unsupported(first);
    }

    // HND <id> or HND <id> <callsign>.
    private static bool IsHandoffWithId(string[] words) =>
        (words.Length is 2 or 3) && TcpShape().IsMatch(words[1]) && ((words.Length == 2) || IsCallsign(words[2]));

    // MF Y: scratchpad 1 or pilot-reported altitude; MF L: leader direction; MF R: the track's PTL; every other letter is not
    // supported.
    private static StarsEntryRead ReadMultiFunc(string[] words)
    {
        if (words.Length < 2)
        {
            return Unsupported(words[0]);
        }

        if (words[1] == "Y")
        {
            return ReadScratchpad1(words[0], words[2..]);
        }

        if (words[1] == "L")
        {
            return ReadLeader(words[0], words.ElementAtOrDefault(2) ?? "", [.. words.Skip(3)]);
        }

        if (words[1] == "R")
        {
            return ReadPtl(words);
        }

        return (GluedLeader().Match(words[1]) is { Success: true } glued)
            ? ReadLeader(words[0], glued.Groups[1].Value, words[2..])
            : Unsupported(words[0]);
    }

    // MF R [callsign]: toggles the Scope-local PTL on the typed callsign's track, or on the selected track when none is typed.
    // The gloss is hand-written, as the range row's is: the PTL is no YAAT command, so the registry has no label for it.
    private static StarsEntryRead ReadPtl(string[] words) =>
        words switch
        {
            [string first, _] => new StarsEntryRead(first, ".scope ptl", "scope ptl") { Need = StarsEntryNeed.SelectedTrack },
            [string first, _, string callsign] when IsCallsign(callsign) => new StarsEntryRead(first, $".scope ptl {callsign}", "scope ptl")
            {
                Callsign = callsign,
            },
            _ => Unsupported(words[0]),
        };

    // MF Y in CRC's order: [callsign] then the text, the altitude or a +text for scratchpad 2. One callsign-shaped token of five
    // or more characters is a callsign whose scratchpad 1 is cleared; a shorter token is the text.
    private static StarsEntryRead ReadScratchpad1(string first, string[] rest) =>
        rest switch
        {
            [] => Supported(first, "SP1", CanonicalCommandType.Scratchpad1),
            [string callsign] when (callsign.Length >= 5) && IsCallsign(callsign) => Supported(
                first,
                $"{callsign} SP1",
                CanonicalCommandType.Scratchpad1
            ),
            [string token] => ReadScratchpad1Field(first, token, null),
            [string callsign, string token] when IsCallsign(callsign) => ReadScratchpad1Field(first, token, callsign),
            _ => Unsupported(first),
        };

    // The field after MF Y [callsign]: +text for scratchpad 2, ### for the pilot-reported altitude, else 1-4 characters of text.
    private static StarsEntryRead ReadScratchpad1Field(string first, string token, string? callsign)
    {
        if (token.StartsWith('+'))
        {
            return ReadScratchpad2(first, token, callsign) ?? Unsupported(first);
        }

        if (Altitude().IsMatch(token))
        {
            return (token == "000")
                ? Unsupported(first)
                : Supported(first, WithCallsign(callsign, PilotReported(token)), CanonicalCommandType.PilotReportedAltitude);
        }

        return (token.Length <= 4) ? Supported(first, WithCallsign(callsign, $"SP1 {token}"), CanonicalCommandType.Scratchpad1) : Unsupported(first);
    }

    private static StarsEntryRead ReadLeader(string first, string direction, string[] rest)
    {
        bool callsignOk = (rest.Length == 0) || ((rest.Length == 1) && IsCallsign(rest[0]));
        return (Direction().IsMatch(direction) && callsignOk)
            ? Supported(first, WithCallsign(rest.ElementAtOrDefault(0), $"LDR {direction}"), CanonicalCommandType.LeaderDirection)
            : Unsupported(first);
    }

    // CA K [callsign] toggles the conflict-alert inhibit; any other CA letter is not supported.
    private static StarsEntryRead ReadConflictAlert(string[] words)
    {
        bool callsignOk = (words.Length == 2) || ((words.Length == 3) && IsCallsign(words[2]));
        return ((words[1] == "K") && callsignOk)
            ? Supported(words[0], WithCallsign(words.ElementAtOrDefault(2), "CAINH"), CanonicalCommandType.InhibitConflictAlert)
            : Unsupported(words[0]);
    }

    private static StarsEntryRead ReadRange(string[] words) =>
        ((words.Length == 2) && words[1].All(char.IsAsciiDigit))
            ? new StarsEntryRead(words[0], $".scope range {words[1]}", "scope range")
            : Unsupported(words[0]);

    // S4 leader direction, S5 pilot-reported altitude, S6 point out: shapes with no key word.
    private static StarsEntryRead? ReadBare(string upper, string[] words)
    {
        string first = words[0];
        if (PointOut().Match(upper) is { Success: true } pointOut)
        {
            return Supported(first, $"PO {pointOut.Groups[1].Value}", CanonicalCommandType.PointOut);
        }

        if (Direction().IsMatch(first) && ((words.Length == 1) || ((words.Length == 2) && IsCallsign(words[1]))))
        {
            return Supported(first, WithCallsign(words.ElementAtOrDefault(1), $"LDR {first}"), CanonicalCommandType.LeaderDirection);
        }

        if ((words.Length == 1) && Altitude().IsMatch(first))
        {
            return (first == "000") ? Unsupported(first) : Supported(first, PilotReported(first), CanonicalCommandType.PilotReportedAltitude);
        }

        return null;
    }

    private static bool IsCallsign(string word) => CallsignShape().IsMatch(word);

    private static string WithCallsign(string? callsign, string command) => (callsign is null) ? command : $"{callsign} {command}";

    private static int Hundreds(Match match) => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);

    private static string PilotReported(string digits) => $"PRA {int.Parse(digits, CultureInfo.InvariantCulture)}";

    private static string Gloss(CanonicalCommandType type) => CommandRegistry.Get(type)?.Label.ToLowerInvariant() ?? "";

    private static StarsEntryRead Supported(string first, string translation, CanonicalCommandType type) => new(first, translation, Gloss(type));

    private static StarsEntryRead Unsupported(string first) => new(first, null, "");
}
