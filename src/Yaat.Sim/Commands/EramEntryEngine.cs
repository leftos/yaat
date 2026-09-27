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
/// <c>LF</c> clears it); <c>VCI {sector}</c> (toggles the sector's on-frequency indicator); <c>LEADER [D{1-9}] [L{n}]</c>
/// (data-block offset direction and leader length).
/// </para>
///
/// <para>
/// <c>HANDOFF {tcp} [/OK]</c> is QN Initiate Handoff: a code that names no configured position is refused
/// <c>SECTOR NOT ADAPTED</c>, the unforced form refuses a track the acting position does not own, <c>/OK</c> reaches only
/// another sector of the same centre, and a code that resolves to the track's owner is refused <c>SECTOR IS OWNER</c>.
/// </para>
///
/// <para>
/// Point outs and DRIs: <c>PO {fromFacility} {fromSector} {toFacility} {toSector} [{toFacility} {toSector}]…</c> (QP Point
/// Out, one point out per receiving sector); <c>POACK {fromFacility} {fromSector} {toFacility} {toSector}</c> (the
/// receiver acknowledges that point out); <c>POCLEAR {fromFacility} {fromSector} {toFacility} {toSector}</c> (the
/// initiator removes it); <c>DRI [J|T]</c> (QP DRI: sets the standard or reduced-separation halo; a bare <c>DRI</c>
/// removes it). The live handler decides who may acknowledge or clear and whether a DRI entry sets or removes the halo,
/// and records the outcome, so a replay applies it without the acting position.
/// </para>
///
/// <para>
/// A refused entry's message is an <see cref="EramEntryErrors"/> id, optionally followed by a space and the contents of
/// the field in error; success messages are free text.
/// </para>
/// </summary>
public static class EramEntryEngine
{
    public static CommandResult Apply(AircraftState ac, string entry, EramEntryContext ctx)
    {
        TrackOwner? identity = ctx.Identity;
        string[] tokens = entry.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return Refused(EramEntryErrors.MessageTooShort);
        }

        List<string> args = [.. tokens[1..]];
        return tokens[0].ToUpperInvariant() switch
        {
            "TRACK" => ApplyTrack(ac, args, identity),
            "FREEZE" => ApplyFreeze(ac, args),
            "QQ" => ApplyQq(ac, args),
            "QR" => ApplyQr(ac, args),
            "QS" => ApplyQs(ac, args),
            "LF" => ApplyLf(ac, args),
            "VCI" => ApplyVci(ac, args),
            "LEADER" => ApplyLeader(ac, args),
            "HANDOFF" => ApplyHandoff(ac, args, ctx),
            "PO" => ApplyPointout(ac, args),
            "POACK" => ApplyPointoutChange(ac, args, p => p.IsAcknowledged = true, "POACK"),
            "POCLEAR" => ApplyPointoutChange(ac, args, ClearPointout, "POCLEAR"),
            "DRI" => ApplyDri(ac, args),
            _ => Refused(EramEntryErrors.InvalidMessageType),
        };
    }

    /// <summary>
    /// ERAM Initiate Handoff (QN.yaml): offers the track to the position the TCP code names, through
    /// <see cref="TrackEngine.ApplyHandoff"/>. The code must name a configured position (QN.yaml field 16's adaptation
    /// check, made before ownership: <c>SECTOR NOT ADAPTED</c>). Only the track's owner may hand it off —
    /// the pending handoff's recipient accepts it first, or overrides. The field 60 override <c>/OK</c> lifts the check,
    /// offering the track on its owner's behalf, but only for a track another ERAM sector of the same centre owns. A code
    /// that resolves to the owner itself, forced or not, is refused <c>SECTOR IS OWNER</c> (a yaat ruling).
    /// </summary>
    private static CommandResult ApplyHandoff(AircraftState ac, List<string> args, EramEntryContext ctx)
    {
        if ((ctx.Identity is not { } identity) || (ctx.Scenario is not { } scenario))
        {
            return Refused(EramEntryErrors.SessionNotActive);
        }

        if (RefuseMalformedHandoff(args) is { } malformed)
        {
            return malformed;
        }

        if (TrackResolver.ResolveTcpToOwner(scenario, args[0]) is not { } target)
        {
            return Refused(EramEntryErrors.NonAdaptedSector);
        }

        if (RefuseHandoffBy(ac, identity, target, forced: args.Count == 2) is { } refused)
        {
            return refused;
        }

        return TrackEngine.ApplyHandoff(ac, scenario, identity, args[0], ctx.Redirect);
    }

    /// <summary>The <c>HANDOFF {tcp} [/OK]</c> shape: one code, optionally followed by the field 60 override.</summary>
    private static CommandResult? RefuseMalformedHandoff(List<string> args)
    {
        if (args.Count is < 1 or > 2)
        {
            return Refused(args.Count < 1 ? EramEntryErrors.MessageTooShort : EramEntryErrors.MessageTooLong);
        }

        bool forced = args.Count == 2;
        return (forced && !string.Equals(args[1], "/OK", StringComparison.OrdinalIgnoreCase)) ? Refused(EramEntryErrors.CofieFormat, args[1]) : null;
    }

    /// <summary>The ownership check (<c>/OK</c> reaching only this centre's ERAM sectors), then the handoff to the owner itself.</summary>
    private static CommandResult? RefuseHandoffBy(AircraftState ac, TrackOwner identity, TrackOwner target, bool forced)
    {
        bool allowed = forced ? IsOwnedByThisCentre(ac, identity) : IsOwner(ac, identity);
        if (!allowed)
        {
            return Refused(EramEntryErrors.NotYourControl);
        }

        return ((ac.Track.Owner is { } owner) && owner.MatchesPosition(target)) ? Refused(EramEntryErrors.HandoffToOwner) : null;
    }

    private static bool IsOwner(AircraftState ac, TrackOwner identity) => (ac.Track.Owner is not null) && ac.Track.Owner.MatchesPosition(identity);

    /// <summary>
    /// The override reaches only a track an ERAM sector of the acting centre owns: docs/crc/eram.md, "flights owned by
    /// external ARTCCs cannot be edited, even with a logic check override" — nor, then, one a STARS position owns.
    /// yaat-server's QR ownership gate applies the same rule to its <c>/OK</c>.
    /// </summary>
    public static bool IsOwnedByThisCentre(AircraftState ac, TrackOwner identity) =>
        (ac.Track.Owner is { } owner)
        && (owner.OwnerType == TrackOwnerType.Eram)
        && string.Equals(owner.FacilityId, identity.FacilityId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Toggles the on-frequency indicator (VCI) for one ERAM sector: CRC lights the glyph for a viewer whose sector is in
    /// <see cref="AircraftEramState.OnFrequencySectorIds"/>, so the entry names the acting sector. The list is also read
    /// by the broadcast path, so every touch locks it.
    /// </summary>
    private static CommandResult ApplyVci(AircraftState ac, List<string> args)
    {
        if (args.Count != 1)
        {
            return Refused(args.Count < 1 ? EramEntryErrors.MessageTooShort : EramEntryErrors.MessageTooLong);
        }

        string sectorId = args[0];
        List<string> sectors = ac.Eram.OnFrequencySectorIds;
        bool nowOn;
        lock (sectors)
        {
            nowOn = !sectors.Remove(sectorId);
            if (nowOn)
            {
                sectors.Add(sectorId);
            }
        }

        return new CommandResult(true, $"VCI {sectorId} {(nowOn ? "on" : "off")} {ac.Callsign}");
    }

    /// <summary>
    /// Data-block offset and leader length (QN.yaml field 59): <c>D{n}</c> is the <c>LeaderDirection</c> keypad value,
    /// 1–9, and <c>L{n}</c> the leader length, 0, 1, 2, 3 or 5; either or both, and a field left out keeps its value.
    /// Values are unsigned digits.
    /// </summary>
    private static CommandResult ApplyLeader(AircraftState ac, List<string> args)
    {
        if (args.Count == 0)
        {
            return Refused(EramEntryErrors.MessageTooShort);
        }

        int? direction = null;
        int? length = null;
        foreach (string token in args)
        {
            char field = char.ToUpperInvariant(token[0]);
            if (!int.TryParse(token.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int value) || (field is not ('D' or 'L')))
            {
                return Refused(EramEntryErrors.CofieFormat, token);
            }

            if (field == 'D')
            {
                if (value is < 1 or > 9)
                {
                    return Refused(EramEntryErrors.InvalidDirection);
                }

                direction = value;
            }
            else if (value is 0 or 1 or 2 or 3 or 5)
            {
                length = value;
            }
            else
            {
                return Refused(EramEntryErrors.InvalidLength);
            }
        }

        ac.Eram.LeaderDirection = direction ?? ac.Eram.LeaderDirection;
        ac.Eram.LeaderLength = length ?? ac.Eram.LeaderLength;
        return new CommandResult(true, $"LEADER {string.Join(' ', args)} {ac.Callsign}");
    }

    /// <summary>
    /// QP Point Out (QP.yaml): adds a point out from the named sector to each receiving sector. A point out is keyed on
    /// its originating facility and sector and its receiving sector, the key CRC's point-out id carries; when any
    /// receiver already has one from this sector, nothing is added. <see cref="AircraftEramState.Pointouts"/> is read by
    /// the broadcast path, so every touch locks it.
    /// </summary>
    private static CommandResult ApplyPointout(AircraftState ac, List<string> args)
    {
        if (args.Count < 4)
        {
            return Refused(EramEntryErrors.MessageTooShort);
        }
        if ((args.Count % 2) != 0)
        {
            return Refused(EramEntryErrors.CofieFormat, args[^1]);
        }

        string fromFacility = args[0];
        string fromSector = args[1];
        var receivers = new List<(string Facility, string Sector)>();
        for (int i = 2; i < args.Count; i += 2)
        {
            // The point-out key (and CRC's id) carries the receiving sector without its facility, so one entry naming a
            // sector twice would mint two point outs with one id.
            if (receivers.Any(r => string.Equals(r.Sector, args[i + 1], StringComparison.Ordinal)))
            {
                return Refused(EramEntryErrors.PoExists);
            }
            receivers.Add((args[i], args[i + 1]));
        }

        List<EramPointoutState> pointouts = ac.Eram.Pointouts;
        lock (pointouts)
        {
            if (receivers.Any(r => pointouts.Any(p => IsPointoutKey(p, fromFacility, fromSector, r.Sector))))
            {
                return Refused(EramEntryErrors.PoExists);
            }

            foreach ((string facility, string sector) in receivers)
            {
                pointouts.Add(
                    new EramPointoutState
                    {
                        OriginatingFacility = fromFacility,
                        OriginatingSector = fromSector,
                        ReceivingFacility = facility,
                        ReceivingSector = sector,
                    }
                );
            }
        }

        return new CommandResult(true, $"PO {string.Join(' ', args)} {ac.Callsign}");
    }

    /// <summary>
    /// Acknowledges (<c>POACK</c>) or removes (<c>POCLEAR</c>) the point out the four fields name: originating facility
    /// and sector, receiving facility and sector.
    /// </summary>
    private static CommandResult ApplyPointoutChange(AircraftState ac, List<string> args, Action<EramPointoutState> change, string form)
    {
        if (args.Count != 4)
        {
            return Refused(args.Count < 4 ? EramEntryErrors.MessageTooShort : EramEntryErrors.MessageTooLong);
        }

        List<EramPointoutState> pointouts = ac.Eram.Pointouts;
        lock (pointouts)
        {
            EramPointoutState? match = pointouts.FirstOrDefault(p =>
                IsPointoutKey(p, args[0], args[1], args[3]) && string.Equals(p.ReceivingFacility, args[2], StringComparison.Ordinal)
            );
            if (match is null)
            {
                return Refused(EramEntryErrors.PoNotFound);
            }

            change(match);
        }

        return new CommandResult(true, $"{form} {string.Join(' ', args)} {ac.Callsign}");
    }

    private static void ClearPointout(EramPointoutState pointout)
    {
        pointout.IsRSideCleared = true;
        pointout.IsDSideCleared = true;
    }

    private static bool IsPointoutKey(EramPointoutState p, string fromFacility, string fromSector, string toSector) =>
        string.Equals(p.OriginatingFacility, fromFacility, StringComparison.Ordinal)
        && string.Equals(p.OriginatingSector, fromSector, StringComparison.Ordinal)
        && string.Equals(p.ReceivingSector, toSector, StringComparison.Ordinal);

    /// <summary>
    /// QP DRI (QP.yaml): <c>J</c> sets the standard halo and <c>T</c> the reduced-separation halo, stored as CRC's
    /// <c>HaloType</c> ordinal (1 and 2) in <see cref="AircraftEramState.DriHaloType"/>; a bare <c>DRI</c> removes it.
    /// </summary>
    private static CommandResult ApplyDri(AircraftState ac, List<string> args)
    {
        if (args.Count > 1)
        {
            return Refused(EramEntryErrors.MessageTooLong);
        }

        int? halo = null;
        if (args.Count == 1)
        {
            halo = args[0].ToUpperInvariant() switch
            {
                "J" => StandardHalo,
                "T" => ReducedSeparationHalo,
                _ => null,
            };
            if (halo is null)
            {
                return Refused(EramEntryErrors.CofieFormat, args[0]);
            }
        }

        ac.Eram.DriHaloType = halo;
        return new CommandResult(true, halo is null ? $"DRI off {ac.Callsign}" : $"DRI {args[0].ToUpperInvariant()} {ac.Callsign}");
    }

    private const int StandardHalo = 1;
    private const int ReducedSeparationHalo = 2;

    private static CommandResult Refused(string errorId) => new(false, errorId);

    private static CommandResult Refused(string errorId, string cofie) => new(false, $"{errorId} {cofie}");

    /// <summary>
    /// Initiating control must not steal a track owned by another sector unless forced with <c>/OK</c> (the logic-check
    /// override, docs/crc/eram.md §MCA, §Handoffs). Taking control terminates any in-progress handoff on the track,
    /// and re-starting track on a frozen track unfreezes it (7110.65 §5-2-15 "track start from frozen status").
    /// </summary>
    private static CommandResult ApplyTrack(AircraftState ac, List<string> args, TrackOwner? identity)
    {
        if (identity is null)
        {
            return Refused(EramEntryErrors.SessionNotActive);
        }

        bool force = args.Any(a => string.Equals(a, "/OK", StringComparison.OrdinalIgnoreCase));
        if (!force && (ac.Track.Owner is not null) && !ac.Track.Owner.MatchesPosition(identity))
        {
            return Refused(EramEntryErrors.AlreadyTracked);
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
        if (args.Count != 2)
        {
            return Refused(args.Count < 2 ? EramEntryErrors.MessageTooShort : EramEntryErrors.MessageTooLong);
        }

        if (!double.TryParse(args[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double lat))
        {
            return Refused(EramEntryErrors.CofieFormat, args[0]);
        }

        if (!double.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double lon))
        {
            return Refused(EramEntryErrors.CofieFormat, args[1]);
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

        foreach (string token in args)
        {
            char prefix = char.ToUpperInvariant(token[0]);
            string rest = prefix is 'R' or 'L' or 'P' ? token[1..] : token;
            if (!int.TryParse(rest, out int altHundreds))
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

        return Refused(EramEntryErrors.AltFormat);
    }

    /// <summary>
    /// The controller-entered reported altitude alone (docs/crc/eram.md §QR), in hundreds of feet. QR.yaml field 54 is
    /// exactly one <c>ddd</c> field, and <c>000</c> clears the value; anything else is <c>ALT FORMAT</c>.
    /// </summary>
    private static CommandResult ApplyQr(AircraftState ac, List<string> args)
    {
        if (args.Count == 0)
        {
            return Refused(EramEntryErrors.MessageTooShort);
        }

        if ((args.Count > 1) || !IsQrAltitudeField(args[0]))
        {
            return Refused(EramEntryErrors.AltFormat);
        }

        int altHundreds = int.Parse(args[0], NumberStyles.None, CultureInfo.InvariantCulture);
        if (altHundreds == 0)
        {
            ac.Eram.ControllerEnteredAltitude = null;
            return new CommandResult(true, $"QR cleared {ac.Callsign}");
        }

        ac.Eram.ControllerEnteredAltitude = altHundreds;
        return new CommandResult(true, $"QR {altHundreds} {ac.Callsign}");
    }

    /// <summary>QR.yaml field 54, Reported Altitude: exactly three digits (<c>ddd</c>), where <c>000</c> clears it.</summary>
    public static bool IsQrAltitudeField(string token) => (token.Length == 3) && token.All(char.IsAsciiDigit);

    private static bool IsQsActionType(string token) => token is "*" or "*/" or "/*";

    /// <summary>
    /// The FDB line-4 HSF fields (docs/crc/eram.md §QS Command, Table 5; docs/eram/commands/QS.yaml): a manual controller
    /// annotation, not the aircraft's assigned vector. An entry carries either a field 64 action type (<c>*</c>,
    /// <c>*/</c>, <c>/*</c>) or field 155 data, never both. Free text is the backtick form; Table 5 has no
    /// free-text-only delete (<c>QS *</c> clears it), so an empty payload is a format error.
    /// </summary>
    private static CommandResult ApplyQs(AircraftState ac, List<string> args)
    {
        if (args.Count == 0)
        {
            return Refused(EramEntryErrors.MessageTooShort);
        }

        string op = args[0];
        bool opIsAction = IsQsActionType(op);
        string? combined = args.Skip(1).FirstOrDefault(a => opIsAction || IsQsActionType(a));
        if (combined is not null)
        {
            return Refused(EramEntryErrors.CofieFormat, combined);
        }

        if (opIsAction)
        {
            return ApplyQsAction(ac, op);
        }

        if (op.StartsWith('`'))
        {
            return ApplyQsFreeText(ac, args);
        }

        if (op.StartsWith('/'))
        {
            string? speed = ParseHsfSpeed(op[1..]);
            if (speed is null)
            {
                return Refused(EramEntryErrors.SpeedFormat);
            }

            ac.Eram.AssignedSpeed = speed;
            return new CommandResult(true, $"QS /{speed} {ac.Callsign}");
        }

        string? heading = ParseHsfHeading(op);
        if (heading is null)
        {
            return Refused(EramEntryErrors.HeadingFormat);
        }

        ac.Eram.AssignedHeading = heading;
        return new CommandResult(true, $"QS {heading} {ac.Callsign}");
    }

    /// <summary><c>*</c> deletes every HSF field, <c>*/</c> the heading, <c>/*</c> the speed.</summary>
    private static CommandResult ApplyQsAction(AircraftState ac, string action)
    {
        if (action == "*")
        {
            ac.Eram.FreeText = null;
        }

        if (action is "*" or "*/")
        {
            ac.Eram.AssignedHeading = null;
        }

        if (action is "*" or "/*")
        {
            ac.Eram.AssignedSpeed = null;
        }

        return new CommandResult(true, $"QS {action} {ac.Callsign}");
    }

    /// <summary>
    /// Free form text: 1–8 non-special characters — letters and digits — after the clear-weather symbol, with no leading
    /// or embedded spaces (QS.yaml field 155, <c>MsgInvalidTextFormat</c>). The entry's tokens are split on spaces, so
    /// text with a space arrives as more than one token, and a leading space as a bare backtick followed by the text.
    /// </summary>
    private static CommandResult ApplyQsFreeText(AircraftState ac, List<string> args)
    {
        string text = args[0][1..].ToUpperInvariant();
        if ((args.Count > 1) || (text.Length is < 1 or > 8) || !text.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c)))
        {
            return Refused(EramEntryErrors.TextFormat);
        }

        ac.Eram.FreeText = text;
        return new CommandResult(true, $"QS {text} {ac.Callsign}");
    }

    /// <summary>
    /// An HSF assigned heading in CRC's canonical stored form — the format the Heading Menu composes AND re-parses on
    /// reopen (<c>ViewHeadingMenu.OnOpen</c> regexes <c>^H\d{3}$</c> / <c>^\d{1,2}L$</c> / <c>^\d{1,2}R$</c>): a compass
    /// heading 001–360 (north = 360, never 000; 7110.65 §2-4-17.h) stored H-prefixed and zero-padded, or a degrees-of-turn
    /// annotation (<c>20L</c> / <c>20R</c>; 7110.65 §5-6-2.a.2). Null when the token is neither.
    /// </summary>
    public static string? ParseHsfHeading(string token)
    {
        string t = token.ToUpperInvariant();
        if (t.Length == 0)
        {
            return null;
        }

        if (t[^1] is 'L' or 'R')
        {
            string turn = t[..^1];
            return (turn.Length is 1 or 2) && turn.All(char.IsDigit) && (int.Parse(turn) >= 1) ? t : null;
        }

        string digits = t.StartsWith('H') ? t[1..] : t;
        if ((digits.Length is < 1 or > 3) || !digits.All(char.IsDigit) || !int.TryParse(digits, out int deg))
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
        string t = token.ToUpperInvariant();
        string modifier = "";
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
            string machDigits = t[1..];
            return (machDigits.Length is 2 or 3) && machDigits.All(char.IsDigit) ? "M" + machDigits + modifier : null;
        }

        if (t[0] == 'S')
        {
            t = t[1..];
        }

        return (t.Length == 3) && t.All(char.IsDigit) && (int.Parse(t) >= 100) ? t + modifier : null;
    }

    /// <summary>
    /// CRR group membership rides the aircraft's <c>CrrGroupLabel</c>; the group itself is the engine's
    /// (<see cref="Simulation.SimulationEngine.CrrGroups"/>, written by a <see cref="Simulation.RecordedEramCrrGroup"/>).
    /// </summary>
    private static CommandResult ApplyLf(AircraftState ac, List<string> args)
    {
        if (args.Count == 0)
        {
            ac.Eram.CrrGroupLabel = null;
            return new CommandResult(true, $"LF cleared {ac.Callsign}");
        }

        string label = args[0].ToUpperInvariant();
        ac.Eram.CrrGroupLabel = label;
        return new CommandResult(true, $"LF {label}");
    }
}
