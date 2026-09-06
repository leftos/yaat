using System.Globalization;

namespace Yaat.Sim.Commands;

/// <summary>
/// The one body for the ERAM keyboard entries that write per-track ERAM state. The live CRC handler parses the wire
/// message, validates it (the FLID, the sector scope, the FDB requirement) and records a <see cref="Simulation.RecordedEramEntry"/>
/// whose <c>Entry</c> is one of the forms below; the router applies the record through <see cref="Apply"/> on every run
/// kind, so a rewind or a bundle reconstruction reproduces the entry the way the live room did.
///
/// <para>
/// Grammar: <c>TRACK [/OK]</c> (QT — the unforced form refuses another sector's track); <c>FREEZE {lat} {lon}</c>
/// (QH F — the altitude is snapshotted from the aircraft at apply time); <c>QQ</c>, <c>QQ L</c>, <c>QQ [R|L|P]{alt}</c>
/// (interim / local / procedure altitude tiers, in hundreds of feet); <c>QR {alt}</c> (controller-entered altitude);
/// <c>QS *</c>, <c>QS */</c>, <c>QS /*</c>, <c>QS /{speed}</c>, <c>QS {heading}</c>, <c>QS `{text}</c> (the FDB line-4
/// HSF fields, stored in the canonical forms CRC's menus re-parse); <c>LF [{label}]</c> (CRR group membership; a bare
/// <c>LF</c> clears it).
/// </para>
/// </summary>
public static class EramEntryEngine
{
    public const int FreeTextMaxLength = 40;

    private const string Format = "FORMAT";

    public static CommandResult Apply(AircraftState ac, string entry, TrackOwner? identity)
    {
        var tokens = entry.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return new CommandResult(false, Format);
        }

        var args = tokens[1..].ToList();
        return tokens[0].ToUpperInvariant() switch
        {
            "TRACK" => ApplyTrack(ac, args, identity),
            "FREEZE" => ApplyFreeze(ac, args),
            "QQ" => ApplyQq(ac, args),
            "QR" => ApplyQr(ac, args),
            "QS" => ApplyQs(ac, args),
            "LF" => ApplyLf(ac, args),
            var verb => new CommandResult(false, $"Unknown ERAM entry '{verb}'"),
        };
    }

    /// <summary>
    /// Initiating control must not steal a track owned by another sector unless forced with <c>/OK</c> (the logic-check
    /// override, docs/crc/eram.md §MCA, §Handoffs). Taking control terminates any in-progress handoff on the track,
    /// and re-starting track on a frozen track unfreezes it (7110.65 §5-2-15 "track start from frozen status").
    /// </summary>
    private static CommandResult ApplyTrack(AircraftState ac, List<string> args, TrackOwner? identity)
    {
        if (identity is null)
        {
            return new CommandResult(false, "NOT ACTIVE");
        }

        var force = args.Any(a => string.Equals(a, "/OK", StringComparison.OrdinalIgnoreCase));
        if (!force && (ac.Track.Owner is not null) && !ac.Track.Owner.MatchesPosition(identity))
        {
            return new CommandResult(false, "ALREADY TRACKED");
        }

        ac.Track.Owner = identity;
        ac.Track.HandoffPeer = null;
        ac.Track.HandoffInitiatedAt = null;
        ac.Track.HandoffRedirectedBy = null;
        Unfreeze(ac);
        return new CommandResult(true, $"QT {ac.Callsign}");
    }

    private static void Unfreeze(AircraftState ac)
    {
        ac.Eram.IsFrozen = false;
        ac.Eram.FrozenLat = null;
        ac.Eram.FrozenLon = null;
        ac.Eram.FrozenAltitude = null;
    }

    /// <summary>
    /// Parks the track at the location, unpaired from the target (docs/crc/eram.md §QH Command): it shows FRZN, holds
    /// the altitude it had when frozen, and is exempt from coast and every auto-removal path until re-started.
    /// </summary>
    private static CommandResult ApplyFreeze(AircraftState ac, List<string> args)
    {
        if (
            (args.Count != 2)
            || !double.TryParse(args[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)
            || !double.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var lon)
        )
        {
            return new CommandResult(false, Format);
        }

        ac.Eram.IsFrozen = true;
        ac.Eram.FrozenLat = lat;
        ac.Eram.FrozenLon = lon;
        ac.Eram.FrozenAltitude = (int)(ac.Altitude / 100);
        return new CommandResult(true, $"FRZN {ac.Callsign}");
    }

    /// <summary>
    /// The interim / local-interim / procedure altitude tiers, in hundreds of feet — the unit CRC renders directly. A
    /// bare <c>QQ</c> clears the interim and procedure altitudes (mutually exclusive, so at most one is set); <c>QQ L</c>
    /// clears the local interim only; <c>R</c> sets the controller-entered altitude alongside the interim.
    /// </summary>
    private static CommandResult ApplyQq(AircraftState ac, List<string> args)
    {
        if (args.Count == 0)
        {
            ac.Eram.InterimAltitude = null;
            ac.Eram.ProcedureAltitude = null;
            return new CommandResult(true, $"QQ cleared {ac.Callsign}");
        }

        if ((args.Count == 1) && string.Equals(args[0], "L", StringComparison.OrdinalIgnoreCase))
        {
            ac.Eram.LocalInterimAltitude = null;
            return new CommandResult(true, $"QQ L cleared {ac.Callsign}");
        }

        foreach (var token in args)
        {
            var prefix = char.ToUpperInvariant(token[0]);
            var rest = prefix is 'R' or 'L' or 'P' ? token[1..] : token;
            if (!int.TryParse(rest, out var altHundreds))
            {
                continue;
            }

            switch (prefix)
            {
                case 'R':
                    ac.Eram.InterimAltitude = altHundreds;
                    ac.Eram.ControllerEnteredAltitude = altHundreds;
                    ac.Eram.ProcedureAltitude = null;
                    return new CommandResult(true, $"QQ R{altHundreds} {ac.Callsign}");
                case 'L':
                    ac.Eram.LocalInterimAltitude = altHundreds;
                    return new CommandResult(true, $"QQ L{altHundreds} {ac.Callsign}");
                case 'P':
                    ac.Eram.ProcedureAltitude = altHundreds;
                    ac.Eram.InterimAltitude = null;
                    return new CommandResult(true, $"QQ P{altHundreds} {ac.Callsign}");
                default:
                    ac.Eram.InterimAltitude = altHundreds;
                    ac.Eram.ProcedureAltitude = null;
                    return new CommandResult(true, $"QQ {altHundreds} {ac.Callsign}");
            }
        }

        return new CommandResult(false, Format);
    }

    /// <summary>The controller-entered reported altitude alone (docs/crc/eram.md §QR), in hundreds of feet.</summary>
    private static CommandResult ApplyQr(AircraftState ac, List<string> args)
    {
        foreach (var token in args)
        {
            if (int.TryParse(token, out var altHundreds) && (altHundreds > 0))
            {
                ac.Eram.ControllerEnteredAltitude = altHundreds;
                return new CommandResult(true, $"QR {altHundreds} {ac.Callsign}");
            }
        }

        return new CommandResult(false, Format);
    }

    /// <summary>
    /// The FDB line-4 HSF fields (docs/crc/eram.md §QS Command, Table 5): a manual controller annotation, not the
    /// aircraft's assigned vector. Free text is the backtick form; Table 5 has no free-text-only delete (<c>QS *</c>
    /// clears it), so an empty payload is a format error.
    /// </summary>
    private static CommandResult ApplyQs(AircraftState ac, List<string> args)
    {
        if (args.Count == 0)
        {
            return new CommandResult(false, Format);
        }

        var op = args[0];
        switch (op)
        {
            case "*":
                ac.Eram.AssignedHeading = null;
                ac.Eram.AssignedSpeed = null;
                ac.Eram.FreeText = null;
                return new CommandResult(true, $"QS * {ac.Callsign}");
            case "*/":
                ac.Eram.AssignedHeading = null;
                return new CommandResult(true, $"QS */ {ac.Callsign}");
            case "/*":
                ac.Eram.AssignedSpeed = null;
                return new CommandResult(true, $"QS /* {ac.Callsign}");
        }

        if (op.StartsWith('`'))
        {
            var text = string.Join(' ', args)[1..].Trim().ToUpperInvariant();
            if (text.Length == 0)
            {
                return new CommandResult(false, Format);
            }

            ac.Eram.FreeText = text.Length > FreeTextMaxLength ? text[..FreeTextMaxLength] : text;
            return new CommandResult(true, $"QS {ac.Eram.FreeText} {ac.Callsign}");
        }

        if (op.StartsWith('/'))
        {
            var speed = ParseHsfSpeed(op[1..]);
            if (speed is null)
            {
                return new CommandResult(false, Format);
            }

            ac.Eram.AssignedSpeed = speed;
            return new CommandResult(true, $"QS /{speed} {ac.Callsign}");
        }

        var heading = ParseHsfHeading(op);
        if (heading is null)
        {
            return new CommandResult(false, Format);
        }

        ac.Eram.AssignedHeading = heading;
        return new CommandResult(true, $"QS {heading} {ac.Callsign}");
    }

    /// <summary>
    /// An HSF assigned heading in CRC's canonical stored form — the format the Heading Menu composes AND re-parses on
    /// reopen (<c>ViewHeadingMenu.OnOpen</c> regexes <c>^H\d{3}$</c> / <c>^\d{1,2}L$</c> / <c>^\d{1,2}R$</c>): a compass
    /// heading 001–360 (north = 360, never 000; 7110.65 §2-4-17.h) stored H-prefixed and zero-padded, or a degrees-of-turn
    /// annotation (<c>20L</c> / <c>20R</c>; 7110.65 §5-6-2.a.2). Null when the token is neither.
    /// </summary>
    public static string? ParseHsfHeading(string token)
    {
        var t = token.ToUpperInvariant();
        if (t.Length == 0)
        {
            return null;
        }

        if (t[^1] is 'L' or 'R')
        {
            var turn = t[..^1];
            return (turn.Length is 1 or 2) && turn.All(char.IsDigit) && (int.Parse(turn) >= 1) ? t : null;
        }

        var digits = t.StartsWith('H') ? t[1..] : t;
        if ((digits.Length is < 1 or > 3) || !digits.All(char.IsDigit) || !int.TryParse(digits, out var deg))
        {
            return null;
        }

        return deg is >= 1 and <= 360 ? $"H{deg:D3}" : null;
    }

    /// <summary>
    /// An HSF assigned speed in CRC's canonical stored form: knots IAS (7110.65 §5-7-1.g) as exactly three bare digits
    /// — two-digit values are rejected because CRC's Speed Menu reads a stored two-digit value as Mach — or Mach as
    /// <c>M</c> plus two or three digits in 0.01 increments (Center speed control at/above FL240), each with an optional
    /// trailing <c>+</c> / <c>-</c> ("or greater" / "or less", 7110.65 §5-7-2.a.2). The Speed Menu's <c>S</c> prefix is
    /// stripped on store. Null when the token is neither.
    /// </summary>
    public static string? ParseHsfSpeed(string token)
    {
        var t = token.ToUpperInvariant();
        var modifier = "";
        if ((t.Length > 0) && (t[^1] is '+' or '-'))
        {
            modifier = t[^1..];
            t = t[..^1];
        }

        if (t.Length == 0)
        {
            return null;
        }

        if (t[0] == 'M')
        {
            var machDigits = t[1..];
            return (machDigits.Length is 2 or 3) && machDigits.All(char.IsDigit) ? "M" + machDigits + modifier : null;
        }

        if (t[0] == 'S')
        {
            t = t[1..];
        }

        return (t.Length == 3) && t.All(char.IsDigit) && (int.Parse(t) >= 100) ? t + modifier : null;
    }

    /// <summary>
    /// CRR group membership rides the aircraft's <c>CrrGroupLabel</c>; the group itself is the host's (a
    /// <see cref="Simulation.RecordedEramCrrGroup"/>).
    /// </summary>
    private static CommandResult ApplyLf(AircraftState ac, List<string> args)
    {
        if (args.Count == 0)
        {
            ac.Eram.CrrGroupLabel = null;
            return new CommandResult(true, $"LF cleared {ac.Callsign}");
        }

        var label = args[0].ToUpperInvariant();
        ac.Eram.CrrGroupLabel = label;
        return new CommandResult(true, $"LF {label}");
    }
}
