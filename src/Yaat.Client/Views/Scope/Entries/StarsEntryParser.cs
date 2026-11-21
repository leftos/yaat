using System.Globalization;
using System.Text.RegularExpressions;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Map;
using Yaat.Client.Views.Scope.Toolbar;
using Yaat.Sim.Commands;

namespace Yaat.Client.Views.Scope.Entries;

/// <summary>What a STARS entry needs from the Scope's state before its translation can be sent.</summary>
internal enum StarsEntryNeed
{
    /// <summary>Nothing: the translation is sent as it is.</summary>
    None,

    /// <summary>
    /// <c>HND</c> with no handoff id: <c>ACCEPT</c> for the typed callsign's track or the slewed one (a clicked track, else the
    /// selected aircraft); with neither, for the handoff inbound to the acting position nearest the Scope's home centre, else
    /// <see cref="StarsErrorText.NoFlight"/>.
    /// </summary>
    HandoffAccept,

    /// <summary><c>*J</c>/<c>*P</c> with a size: the slewed track's callsign (a click, else the selection) goes after the translation.</summary>
    RingPlacement,

    /// <summary>Bare <c>*J</c>/<c>*P</c>: the slewed track's ring of that kind, at its current size, is removed.</summary>
    RingRemoval,

    /// <summary>Bare <c>MF R</c>: the slewed track's callsign (a click, else the selection) goes after the translation.</summary>
    SelectedTrack,

    /// <summary>
    /// Bare <c>**</c> on the slewed track: <c>POHO</c> for a point-out pending to the acting position, else <c>FQL</c> to the
    /// acting position's own TCP.
    /// </summary>
    ConvertPointoutOrQuicklook,

    /// <summary>
    /// <c>MF L&lt;d&gt; &lt;id&gt;</c>: the translation is the direction alone (<c>LDR d</c>); the Scope finds the track the id
    /// names (<see cref="StarsEntryRead.TrackId"/>), a callsign or a four-digit beacon code, and names it first.
    /// </summary>
    LeaderForTrack,

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

    /// <summary>YAAT's own refusal, not a CRC category: a STARS entry CRC reads that has no YAAT command.</summary>
    public const string NotSupported = "NOT SUPPORTED";
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

    /// <summary>
    /// The id typed after <c>MF L&lt;d&gt;</c> (<see cref="StarsEntryNeed.LeaderForTrack"/>): an ACID, a beacon code or a line
    /// number, as typed; null for any other entry.
    /// </summary>
    public string? TrackId { get; init; }

    /// <summary>The second callsign typed after a <c>MIN</c> entry, or null.</summary>
    public string? SecondCallsign { get; init; }

    /// <summary>The ring kind of a <c>*J</c>/<c>*P</c> entry; <see cref="ScopeRingTool.None"/> otherwise.</summary>
    public ScopeRingTool Ring { get; init; }

    /// <summary>The CRC category the entry is answered with whatever is selected, nothing sent; null when it has none.</summary>
    public string? Error { get; init; }

    /// <summary>
    /// The answer, nothing sent, when the entry acts on CRC's slew and no aircraft is selected (the Scope's slew): a CRC category,
    /// or <see cref="StarsErrorText.NotSupported"/> where CRC's meaning with nothing slewed has no YAAT command; null when the
    /// entry needs no slew.
    /// </summary>
    public string? WithoutSlew { get; init; }

    /// <summary>Whether the entry maps to a YAAT command.</summary>
    public bool Supported => Translation is not null;
}

/// <summary>
/// Reads an entry typed on a STARS-profile Scope as a STARS entry by CRC's grammar: the glued shapes it knows (<c>+AB1</c>,
/// <c>++100</c>, <c>4</c>, <c>1D*</c>, <c>**2B</c>, <c>*J5</c>, ...), and YAAT's own key words standing for the function keys
/// (<c>INIT</c>, <c>TERM</c>, <c>HND</c>, <c>MF</c>, <c>CA</c>, <c>RANGE</c>, <c>MIN</c>), which CRC has as keys, not text.
/// After <c>MF</c> the first character is the function letter, glued or spaced (<c>MF L3</c>, <c>MF L 3</c>, <c>MF YAB1</c>).
/// An entry CRC refuses carries its category (<see cref="StarsEntryRead.Error"/>), and one that acts on CRC's slew carries its
/// answer for no selected aircraft (<see cref="StarsEntryRead.WithoutSlew"/>). The <c>*T</c> range bearing line entry reads by
/// CRC's grammar (<see cref="ReadRangeBearing"/>): <c>*T</c>
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

    [GeneratedRegex(@"^\*([JP])(.*)$")]
    private static partial Regex Tpa();

    // CRC's TPA size: 1-9 with an optional tenth of 1-9, or 10-30 whole.
    [GeneratedRegex(@"^(?:[1-9](?:\.[1-9])?|[12][0-9]|30)$")]
    private static partial Regex TpaSize();

    // CRC's quicklook shape, which a bare entry with nothing slewed reads as.
    [GeneratedRegex(@"^[0-9]?[A-Z0-9]\+?$")]
    private static partial Regex Quicklook();

    // MF L<d>*: the associated (unowned) tracks' default direction.
    [GeneratedRegex(@"^[1-9]\*$")]
    private static partial Regex LeaderAssociated();

    // MF L<d><d>: CRC sends it to its server with a slew, and has no reading for it without one.
    [GeneratedRegex(@"^([1-9])\1$")]
    private static partial Regex LeaderGlobal();

    // MF L<d> <id>: that track's direction.
    [GeneratedRegex(@"^([1-9]) (\S+)$")]
    private static partial Regex LeaderForTrack();

    // MF L<tcp> <d> or MF L<d>U: a TCP's tracks' or the unassociated tracks' direction; group 1 or 2 is the direction.
    [GeneratedRegex(@"^(?:[0-9]?[A-Z0-9] ([1-9])|([1-9])U)$")]
    private static partial Regex LeaderGroup();

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
            return Supported(first, "POHO", CanonicalCommandType.ConvertPointout) with
            {
                Need = StarsEntryNeed.ConvertPointoutOrQuicklook,
                WithoutSlew = StarsErrorText.Format,
            };
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

    // *J/*P alone removes the slewed track's ring; with a size in CRC's grammar it places one there (FORMAT with no slew); any
    // other size is FORMAT.
    private static StarsEntryRead ReadTpa(string first, Match tpa)
    {
        bool jRing = tpa.Groups[1].Value == "J";
        string kind = jRing ? "jring" : "cone";
        string size = tpa.Groups[2].Value;
        bool removal = size.Length == 0;
        if (!removal && !TpaSize().IsMatch(size))
        {
            return Refused(first, StarsErrorText.Format);
        }

        string gloss = Gloss(jRing ? CanonicalCommandType.JRing : CanonicalCommandType.Cone);
        return new StarsEntryRead(first, $".scope rings {kind} {size}".TrimEnd(), gloss)
        {
            Need = removal ? StarsEntryNeed.RingRemoval : StarsEntryNeed.RingPlacement,
            Ring = jRing ? ScopeRingTool.JRing : ScopeRingTool.Cone,
            WithoutSlew = removal ? null : StarsErrorText.Format,
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

    // MIN [callsign [callsign]]: the Scope's min-sep readout, the callsigns the clicked tracks filled in (the Scope answers
    // FORMAT for a typed one, as CRC's MIN takes no text); anything else is FORMAT. The translation is the entry as typed,
    // upper-cased: it is run on the Scope, never sent. The gloss is hand-written, as the range row's is: min sep is no YAAT
    // command.
    private static StarsEntryRead ReadMinSep(string[] words) =>
        ((words.Length <= 3) && words.Skip(1).All(IsCallsign))
            ? new StarsEntryRead(words[0], string.Join(' ', words), "minimum separation")
            {
                Need = StarsEntryNeed.MinimumSeparation,
                Callsign = words.ElementAtOrDefault(1),
                SecondCallsign = words.ElementAtOrDefault(2),
            }
            : Refused(words[0], StarsErrorText.Format);

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

    // HND <id> [callsign] hands off; bare HND [callsign] accepts a handoff, never cancels one.
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
            return Supported(first, WithCallsign(callsign, "ACCEPT"), CanonicalCommandType.AcceptHandoff) with
            {
                Need = StarsEntryNeed.HandoffAccept,
                Callsign = callsign,
            };
        }

        return Unsupported(first);
    }

    // HND <id> or HND <id> <callsign>.
    private static bool IsHandoffWithId(string[] words) =>
        (words.Length is 2 or 3) && TcpShape().IsMatch(words[1]) && ((words.Length == 2) || IsCallsign(words[2]));

    // MULTI FUNC as CRC reads it: the first character after MF is the function letter, glued or spaced, and the rest, trimmed,
    // its parameters. Y: scratchpad 1 or pilot-reported altitude; L: leader direction; R: the slewed track's PTL; MF alone is
    // FORMAT; every other letter is not supported.
    private static StarsEntryRead ReadMultiFunc(string[] words)
    {
        string first = words[0];
        string parameters = string.Join(' ', words.Skip(1));
        if (parameters.Length == 0)
        {
            return Refused(first, StarsErrorText.Format);
        }

        string rest = parameters[1..].Trim();
        return parameters[0] switch
        {
            'Y' => ReadScratchpad1(first, rest.Split(' ', StringSplitOptions.RemoveEmptyEntries)),
            'L' => ReadMultiFuncLeader(first, rest),
            'R' => ReadPtl(first, rest),
            _ => Unsupported(first),
        };
    }

    // MF R toggles the Scope-local PTL on the slewed (selected) track; it takes no text, and with no slew it is FORMAT. The gloss
    // is hand-written, as the range row's is: the PTL is no YAAT command, so the registry has no label for it.
    private static StarsEntryRead ReadPtl(string first, string rest) =>
        (rest.Length == 0)
            ? new StarsEntryRead(first, ".scope ptl", "scope ptl") { Need = StarsEntryNeed.SelectedTrack, WithoutSlew = StarsErrorText.Format }
            : Refused(first, StarsErrorText.Format);

    // MF L in CRC's order (ProcessMultiFuncL): <d>* (associated tracks) has no YAAT state; <d> is the slewed track's direction,
    // else the owned tracks' default (no YAAT state; 5, the default itself, FORMAT); <d><d> goes to CRC's server with a slew
    // (FORMAT without); <d> <id> is that track's; <tcp> <d> and <d>U set a group YAAT has no state for (5 FORMAT); else FORMAT.
    private static StarsEntryRead ReadMultiFuncLeader(string first, string rest)
    {
        if (LeaderAssociated().IsMatch(rest))
        {
            return Unsupported(first);
        }

        if (Direction().IsMatch(rest))
        {
            return LeaderOnSlew(first, rest, (rest == "5") ? StarsErrorText.Format : StarsErrorText.NotSupported);
        }

        return LeaderGlobal().IsMatch(rest) ? Unsupported(first) with { WithoutSlew = StarsErrorText.Format } : ReadMultiFuncLeaderNamed(first, rest);
    }

    // MF L<d> <id> and the groups MF L names (<tcp> <d>, <d>U); anything else is FORMAT.
    private static StarsEntryRead ReadMultiFuncLeaderNamed(string first, string rest)
    {
        if (LeaderForTrack().Match(rest) is { Success: true } track)
        {
            return ReadLeaderForTrack(first, track);
        }

        if (LeaderGroup().Match(rest) is { Success: true } group)
        {
            return IsDefaultGroup(group) ? Refused(first, StarsErrorText.Format) : Unsupported(first);
        }

        return Refused(first, StarsErrorText.Format);
    }

    // The direction of an MF L group entry is 5, the default, which CRC refuses for a group.
    private static bool IsDefaultGroup(Match group) => (group.Groups[1].Value + group.Groups[2].Value) == "5";

    // MF L<d> <id>: the id's track, which the Scope finds (an ACID or a beacon code) as CRC's LookupAssociatedTrack does; the
    // translation is the direction alone, the id carried as the read's callsign.
    private static StarsEntryRead ReadLeaderForTrack(string first, Match track)
    {
        string id = track.Groups[2].Value;
        return Supported(first, $"LDR {track.Groups[1].Value}", CanonicalCommandType.LeaderDirection) with
        {
            Need = StarsEntryNeed.LeaderForTrack,
            TrackId = id,
        };
    }

    // A leader direction for the slewed (selected) track; withoutSlew answers when none is selected.
    private static StarsEntryRead LeaderOnSlew(string first, string direction, string withoutSlew) =>
        Supported(first, $"LDR {direction}", CanonicalCommandType.LeaderDirection) with
        {
            WithoutSlew = withoutSlew,
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

    // CA K [callsign] toggles the conflict-alert inhibit; any other CA letter is not supported.
    private static StarsEntryRead ReadConflictAlert(string[] words)
    {
        bool callsignOk = (words.Length == 2) || ((words.Length == 3) && IsCallsign(words[2]));
        return ((words[1] == "K") && callsignOk)
            ? Supported(words[0], WithCallsign(words.ElementAtOrDefault(2), "CAINH"), CanonicalCommandType.InhibitConflictAlert)
            : Unsupported(words[0]);
    }

    // RANGE <n>: a number, as CRC's double.TryParse reads one, from 0.125 to 256 NM; not a number is FORMAT, out of that RANGE
    // LIMIT. The gloss is hand-written: the range is no YAAT command.
    private static StarsEntryRead ReadRange(string[] words)
    {
        string first = words[0];
        string text = string.Join(' ', words.Skip(1));
        if (!double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out double rangeNm))
        {
            return Refused(first, StarsErrorText.Format);
        }

        return (rangeNm is >= ScopeViewModel.MinRangeNm and <= ScopeViewModel.MaxRangeNm)
            ? new StarsEntryRead(first, ScopeToolbarButtons.RangeCommand(rangeNm), "scope range")
            : Refused(first, StarsErrorText.RangeLimit);
    }

    // S4 leader direction, S5 pilot-reported altitude, S6 point out, and CRC's quicklook shape: shapes with no key word. A bare
    // digit is the slewed track's leader direction, and with nothing slewed CRC's quicklook, which YAAT does not support; a
    // digit and a callsign goes to CRC's server, so it is not supported either.
    private static StarsEntryRead? ReadBare(string upper, string[] words)
    {
        string first = words[0];
        if (PointOut().Match(upper) is { Success: true } pointOut)
        {
            return Supported(first, $"PO {pointOut.Groups[1].Value}", CanonicalCommandType.PointOut);
        }

        if (Direction().IsMatch(first))
        {
            return ReadBareDirection(first, words);
        }

        if (words.Length != 1)
        {
            return null;
        }

        if (Altitude().IsMatch(first))
        {
            return (first == "000") ? Unsupported(first) : Supported(first, PilotReported(first), CanonicalCommandType.PilotReportedAltitude);
        }

        return Quicklook().IsMatch(first) ? Unsupported(first) : null;
    }

    // A bare digit 1-9: alone, the slewed track's leader direction (with nothing slewed, CRC's quicklook, not supported); with
    // a callsign, an entry CRC sends to its server (not supported); with anything else, no STARS entry.
    private static StarsEntryRead? ReadBareDirection(string first, string[] words) =>
        words switch
        {
            [_] => LeaderOnSlew(first, first, StarsErrorText.NotSupported),
            [_, string callsign] when IsCallsign(callsign) => Unsupported(first),
            _ => null,
        };

    private static bool IsCallsign(string word) => CallsignShape().IsMatch(word);

    private static string WithCallsign(string? callsign, string command) => (callsign is null) ? command : $"{callsign} {command}";

    private static int Hundreds(Match match) => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);

    private static string PilotReported(string digits) => $"PRA {int.Parse(digits, CultureInfo.InvariantCulture)}";

    private static string Gloss(CanonicalCommandType type) => CommandRegistry.Get(type)?.Label.ToLowerInvariant() ?? "";

    private static StarsEntryRead Supported(string first, string translation, CanonicalCommandType type) => new(first, translation, Gloss(type));

    private static StarsEntryRead Unsupported(string first) => new(first, null, "");

    private static StarsEntryRead Refused(string first, string error) => new(first, null, "") { Error = error };
}
