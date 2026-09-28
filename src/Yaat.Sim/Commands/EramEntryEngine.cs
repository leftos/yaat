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
/// (QH F — the altitude is snapshotted from the aircraft at apply time); <c>COAST T{seconds} [@{lat},{lon}]
/// [S{knots}] [A{alt}] [H{degrees}] [R{lat},{lon}]…</c> (QT CT — see <see cref="ApplyCoast"/>); <c>QQ</c>, <c>QQ L</c>, <c>QQ [R|L|P]{alt}</c>
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
/// initiator removes it); <c>POCONVERT {fromFacility} {fromSector} {toFacility} {toSector}</c> or <c>POCONVERT /OK</c>
/// (QT C, Convert Point Out Track: see <see cref="ApplyPointoutConvert"/>);
/// <c>DRI [J|T]</c> (QP DRI: sets the standard or reduced-separation halo; a bare <c>DRI</c> removes it).
/// The live handler decides who may acknowledge or clear and whether a DRI entry sets or removes the halo, and records
/// the outcome, so a replay applies it without the acting position. A <c>PO</c> also removes its initiating sector and
/// every receiving sector from <see cref="AircraftEramState.PointoutMinimizedSectors"/>, so the fresh point
/// out's FDB reappears for both parties.
/// </para>
///
/// <para>
/// Data-block format per sector: <c>MIN {facility} {sector}</c> (QP minimize: the sector's point-out FDB goes back to an
/// LDB; adding a sector already listed changes nothing); <c>FDB {facility} {sector}</c> (the bare-FLID LDB↔FDB cycle:
/// toggles the sector in <see cref="AircraftEramState.FdbOpenSectors"/>).
/// </para>
///
/// <para>
/// Conflict alerts: <c>CO {otherCallsign}</c> (CO Conflict Suppress: toggles the suppression of the active ERAM conflict
/// alert between this aircraft and the other, in <see cref="EramEntryContext.EramConflicts"/>).
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
            "COAST" => ApplyCoast(ac, args, identity),
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
            "POCONVERT" => ApplyPointoutConvert(ac, args, ctx),
            "DRI" => ApplyDri(ac, args),
            "MIN" => ApplyMinimize(ac, args),
            "FDB" => ApplyFdbToggle(ac, args),
            "CO" => ApplyConflictSuppress(ac, args, ctx.EramConflicts),
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
    /// yaat-server's ERAM edit ownership gate applies the same rule to every <c>/OK</c>.
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

        // A fresh point out re-forces the FDB for both parties: drop any earlier minimize so the indicator reappears.
        List<EramSectorKey> minimized = ac.Eram.PointoutMinimizedSectors;
        lock (minimized)
        {
            minimized.RemoveAll(m => m.Is(fromFacility, fromSector) || receivers.Any(r => m.Is(r.Facility, r.Sector)));
        }

        return new CommandResult(true, $"PO {string.Join(' ', args)} {ac.Callsign}");
    }

    /// <summary>
    /// QP minimize (QP.yaml, <c>QP &lt;FLID&gt;</c>): adds the named sector to
    /// <see cref="AircraftEramState.PointoutMinimizedSectors"/>, so its point-out FDB shows as an LDB. A sector already
    /// listed stays listed once.
    /// </summary>
    private static CommandResult ApplyMinimize(AircraftState ac, List<string> args)
    {
        if (RefuseMalformedSector(args) is { } malformed)
        {
            return malformed;
        }

        var key = new EramSectorKey(args[0], args[1]);
        List<EramSectorKey> minimized = ac.Eram.PointoutMinimizedSectors;
        lock (minimized)
        {
            if (!minimized.Contains(key))
            {
                minimized.Add(key);
            }
        }

        return new CommandResult(true, $"MIN {args[0]} {args[1]} {ac.Callsign}");
    }

    /// <summary>
    /// The bare-FLID implied command's LDB↔FDB cycle: toggles the named sector in
    /// <see cref="AircraftEramState.FdbOpenSectors"/>.
    /// </summary>
    private static CommandResult ApplyFdbToggle(AircraftState ac, List<string> args)
    {
        if (RefuseMalformedSector(args) is { } malformed)
        {
            return malformed;
        }

        var key = new EramSectorKey(args[0], args[1]);
        List<EramSectorKey> open = ac.Eram.FdbOpenSectors;
        bool nowOpen;
        lock (open)
        {
            nowOpen = !open.Remove(key);
            if (nowOpen)
            {
                open.Add(key);
            }
        }

        return new CommandResult(true, $"FDB {args[0]} {args[1]} {(nowOpen ? "on" : "off")} {ac.Callsign}");
    }

    /// <summary>The <c>{facility} {sector}</c> shape <c>MIN</c> and <c>FDB</c> take.</summary>
    private static CommandResult? RefuseMalformedSector(List<string> args) =>
        args.Count switch
        {
            < 2 => Refused(EramEntryErrors.MessageTooShort),
            > 2 => Refused(EramEntryErrors.MessageTooLong),
            _ => null,
        };

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

    /// <summary>
    /// QT Convert Point Out Track (QT.yaml, action <c>C</c>): the acting position takes the track and the point out the
    /// four fields name is removed, as STARS <c>**</c> converts a point out (<see cref="TrackEngine.HandleConvertPointout"/>):
    /// the in-progress handoff ends, the handoff counts as accepted and the previous owner keeps its accepted indicator.
    /// The live handler picks the point out whose receiver is the acting sector and records its key. <c>/OK</c> in place
    /// of the key is the field 60 override with no point out to the acting sector: the track is taken, nothing is removed,
    /// and it reaches only an untracked track or one an ERAM sector of the acting centre owns. Like every QT track start,
    /// converting unfreezes the track and ends a coast.
    /// </summary>
    private static CommandResult ApplyPointoutConvert(AircraftState ac, List<string> args, EramEntryContext ctx)
    {
        if ((ctx.Identity is not { } identity) || (ctx.Scenario is not { } scenario))
        {
            return Refused(EramEntryErrors.SessionNotActive);
        }

        if (RefuseMalformedPointoutConvert(args) is { } malformed)
        {
            return malformed;
        }

        bool forced = args.Count == 1;
        if (forced ? RefuseForcedConvert(ac, identity) : !TryRemovePointout(ac, args))
        {
            return Refused(forced ? EramEntryErrors.NotYourControl : EramEntryErrors.PoNotFound);
        }

        TrackOwner? previousOwner = ac.Track.Owner;
        StartTrack(ac, identity);
        ac.Track.HandoffAccepted = true;
        Unfreeze(ac);
        ac.Eram.EndCoast();

        // Converting a track the acting sector already owns hands nothing over: no previous owner to mark.
        if ((previousOwner is not null) && !previousOwner.MatchesPosition(identity))
        {
            TrackEngine.MarkPreviousOwnerRetained(ac, previousOwner, scenario);
            TrackEngine.MarkRecentHandoffAccepted(ac, previousOwner, wasForced: forced, scenario);
        }
        return new CommandResult(true, $"POCONVERT {string.Join(' ', args)} {ac.Callsign}");
    }

    /// <summary>The field 60 override reaches an untracked track or one an ERAM sector of the acting centre owns.</summary>
    private static bool RefuseForcedConvert(AircraftState ac, TrackOwner identity) =>
        (ac.Track.Owner is not null) && !IsOwnedByThisCentre(ac, identity);

    /// <summary>Removes the point out the four <c>POCONVERT</c> fields name, if it is pending (not cleared on the R side).</summary>
    private static bool TryRemovePointout(AircraftState ac, List<string> args)
    {
        List<EramPointoutState> pointouts = ac.Eram.Pointouts;
        lock (pointouts)
        {
            int index = pointouts.FindIndex(p =>
                IsPointoutKey(p, args[0], args[1], args[3])
                && string.Equals(p.ReceivingFacility, args[2], StringComparison.Ordinal)
                && !p.IsRSideCleared
            );
            if (index < 0)
            {
                return false;
            }
            pointouts.RemoveAt(index);
            return true;
        }
    }

    /// <summary>The <c>POCONVERT</c> shape: the four point-out key fields, or the field 60 override alone.</summary>
    private static CommandResult? RefuseMalformedPointoutConvert(List<string> args)
    {
        if (args.Count == 1)
        {
            return string.Equals(args[0], "/OK", StringComparison.OrdinalIgnoreCase) ? null : Refused(EramEntryErrors.CofieFormat, args[0]);
        }

        return args.Count switch
        {
            < 4 => Refused(EramEntryErrors.MessageTooShort),
            > 4 => Refused(EramEntryErrors.MessageTooLong),
            _ => null,
        };
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

    /// <summary>
    /// ERAM Conflict Suppress (CO.yaml, 7110.65 §5-13-1c.1): toggles <see cref="EramActiveConflict.Suppressed"/> on the
    /// active alert between this aircraft and the one named, so a second entry restores it. The same aircraft twice is
    /// <c>INVALID COMBINATION</c>; a pair with no active alert is refused <c>NO CONFLICT ALERT</c>.
    /// </summary>
    private static CommandResult ApplyConflictSuppress(AircraftState ac, List<string> args, EramConflictState conflicts)
    {
        if (args.Count != 1)
        {
            return Refused(args.Count < 1 ? EramEntryErrors.MessageTooShort : EramEntryErrors.MessageTooLong);
        }

        string other = args[0];
        if (string.Equals(other, ac.Callsign, StringComparison.Ordinal))
        {
            return Refused(EramEntryErrors.InvalidCombination);
        }

        if (conflicts.FindPair(ac.Callsign, other) is not { } conflict)
        {
            return Refused(EramEntryErrors.NoConflictAlert);
        }

        conflict.Suppressed = !conflict.Suppressed;
        return new CommandResult(true, $"CA {(conflict.Suppressed ? "suppressed" : "restored")} {ac.Callsign}/{other}");
    }

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

        StartTrack(ac, identity);
        Unfreeze(ac);
        ac.Eram.EndCoast();
        return new CommandResult(true, $"QT {ac.Callsign}");
    }

    private static void StartTrack(AircraftState ac, TrackOwner identity)
    {
        ac.Track.Owner = identity;
        ac.Track.HandoffPeer = null;
        ac.Track.HandoffInitiatedAt = null;
        ac.Track.HandoffRedirectedBy = null;
    }

    /// <summary>The fields of a <c>COAST</c> entry, keyed by their one-letter prefix; <c>R</c> fixes in order.</summary>
    private sealed class CoastArgs
    {
        public Dictionary<char, string> Values { get; } = [];

        public List<LatLon> Route { get; } = [];
    }

    /// <summary>A coast as it starts: the anchor and its sim time, altitude (hundreds of feet), speed, course and route.</summary>
    private readonly record struct CoastStart(LatLon Anchor, double Seconds, int Altitude, int Speed, double TrueCourse, List<LatLon> Route);

    /// <summary>
    /// QT Coast Track (docs/eram/commands/QT.yaml, action <c>CT</c>; 7110.65 §5-13-8a flat track): takes the track the way
    /// <c>TRACK</c> does, but never another sector's track (the Coast Track format has no field 60), then unpairs it from
    /// the target and moves it on its own until a track start or a drop. <c>T</c> is the sim time of the entry, the time
    /// of the anchor. What the entry leaves out is taken from the track at apply time: the anchor is its displayed
    /// position (the frozen spot, the coasted position, or the target's), the altitude the data block's, the speed the
    /// filed true airspeed (the ground speed when none is filed). With <c>H</c>, a magnetic heading, the track holds that
    /// heading and flies no route; without it, it flies the <c>R</c> fixes as <see cref="JoinRoute"/> joins them. Coasting
    /// unfreezes a frozen track.
    /// </summary>
    private static CommandResult ApplyCoast(AircraftState ac, List<string> args, TrackOwner? identity)
    {
        if (identity is null)
        {
            return Refused(EramEntryErrors.SessionNotActive);
        }

        var coast = new CoastArgs();
        if (ReadCoastArgs(args, coast) is { } badToken)
        {
            return Refused(EramEntryErrors.CofieFormat, badToken);
        }
        if (!coast.Values.TryGetValue('T', out string? secondsText))
        {
            return Refused(EramEntryErrors.MessageTooShort);
        }
        if ((ac.Track.Owner is not null) && !ac.Track.Owner.MatchesPosition(identity))
        {
            return Refused(EramEntryErrors.AlreadyTracked);
        }

        CoastStart start = ResolveCoastStart(ac, coast, double.Parse(secondsText, NumberStyles.Float, CultureInfo.InvariantCulture));
        StartTrack(ac, identity);
        Unfreeze(ac);
        WriteCoast(ac.Eram, start);
        return new CommandResult(true, $"CST {ac.Callsign}");
    }

    // What the entry leaves out comes from the track as it shows at the entry's time.
    private static CoastStart ResolveCoastStart(AircraftState ac, CoastArgs coast, double seconds)
    {
        LatLon anchor = coast.Values.TryGetValue('@', out string? at) ? ParseCoastLatLon(at)!.Value : DisplayedPosition(ac, seconds);
        int altitude = CoastInt(coast, 'A') ?? DisplayedAltitude(ac);
        int speed = CoastInt(coast, 'S') ?? FiledTrueAirspeed(ac);
        if (CoastInt(coast, 'H') is { } heading)
        {
            double trueCourse = MagneticDeclination.MagneticToTrue(heading, anchor.Lat, anchor.Lon);
            return new CoastStart(anchor, seconds, altitude, speed, trueCourse, []);
        }

        // A re-coast keeps the coast's own course; otherwise the target's track.
        double currentCourse = ac.Eram.CoastCourseAt(seconds) ?? ac.TrueTrack.Degrees;
        (List<LatLon> route, double course) = JoinRoute(anchor, currentCourse, coast.Route);
        return new CoastStart(anchor, seconds, altitude, speed, course, route);
    }

    private static void WriteCoast(AircraftEramState eram, CoastStart start)
    {
        eram.IsCoastTrack = true;
        eram.CoastLat = start.Anchor.Lat;
        eram.CoastLon = start.Anchor.Lon;
        eram.CoastStartSeconds = start.Seconds;
        eram.CoastAltitude = start.Altitude;
        eram.CoastSpeed = start.Speed;
        eram.CoastTrueCourse = start.TrueCourse;
        eram.CoastRoute = start.Route;
    }

    // Returns the first token that is not a COAST field, or null when every token reads.
    private static string? ReadCoastArgs(List<string> args, CoastArgs coast)
    {
        foreach (string arg in args)
        {
            if ((arg.Length < 2) || !IsCoastValue(arg[0], arg[1..]))
            {
                return arg;
            }
            if (arg[0] == 'R')
            {
                coast.Route.Add(ParseCoastLatLon(arg[1..])!.Value);
                continue;
            }
            coast.Values[arg[0]] = arg[1..];
        }
        return null;
    }

    private static bool IsCoastValue(char prefix, string value) =>
        prefix switch
        {
            'T' => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double t) && double.IsFinite(t) && (t >= 0),
            '@' or 'R' => ParseCoastLatLon(value) is not null,
            'H' => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int heading) && (heading <= 360),
            'S' or 'A' => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out _),
            _ => false,
        };

    private static LatLon? ParseCoastLatLon(string value)
    {
        string[] parts = value.Split(',');
        if (
            (parts.Length != 2)
            || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double lat)
            || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double lon)
        )
        {
            return null;
        }
        return (Math.Abs(lat) <= 90) && (Math.Abs(lon) <= 180) ? new LatLon(lat, lon) : null;
    }

    private static int? CoastInt(CoastArgs coast, char prefix) =>
        coast.Values.TryGetValue(prefix, out string? text) ? int.Parse(text, CultureInfo.InvariantCulture) : null;

    // Where the track shows now: the frozen spot, the coasted position, or the target.
    private static LatLon DisplayedPosition(AircraftState ac, double nowSeconds)
    {
        if (ac.Eram.IsFrozen && (ac.Eram.FrozenLat is { } lat) && (ac.Eram.FrozenLon is { } lon))
        {
            return new LatLon(lat, lon);
        }
        return ac.Eram.CoastPositionAt(nowSeconds) ?? ac.Position;
    }

    // The altitude the data block shows now, in hundreds of feet.
    private static int DisplayedAltitude(AircraftState ac)
    {
        if (ac.Eram.IsFrozen && (ac.Eram.FrozenAltitude is { } frozen))
        {
            return frozen;
        }
        return (ac.Eram.IsCoastTrack && (ac.Eram.CoastAltitude is { } coasted)) ? coasted : (int)(ac.Altitude / 100);
    }

    private static int FiledTrueAirspeed(AircraftState ac) =>
        ac.FlightPlan.CruiseSpeed > 0 ? ac.FlightPlan.CruiseSpeed : (int)Math.Round(ac.GroundSpeed);

    /// <summary>
    /// The route the coasted track flies from <paramref name="anchor"/>, and the course it holds after it. The track joins
    /// the route leg nearest the anchor (ties go to the later leg) and flies to that leg's end fix; an anchor before the
    /// leg's start goes to its start fix, and an anchor at or past its end goes to the fix after it, or, past the last
    /// fix, flies no route and holds the last leg's course. A lone fix is flown to only when it lies ahead on
    /// <paramref name="course"/>. <paramref name="course"/> is held when there is no route to fly.
    /// </summary>
    private static (List<LatLon> Route, double Course) JoinRoute(LatLon anchor, double course, List<LatLon> fixes)
    {
        if (fixes.Count < 2)
        {
            bool ahead = (fixes.Count == 1) && (Math.Abs(NormalizeSigned(GeoMath.BearingTo(anchor, fixes[0]) - course)) <= 90);
            return (ahead ? [fixes[0]] : [], course);
        }

        int leg = NearestLeg(anchor, fixes);
        LatLon from = fixes[leg - 1];
        LatLon to = fixes[leg];
        double legCourse = GeoMath.BearingTo(from, to);
        double along = GeoMath.AlongTrackDistanceNm(anchor.Lat, anchor.Lon, from.Lat, from.Lon, new TrueHeading(legCourse));
        if (along < 0)
        {
            return ([.. fixes.Skip(leg - 1)], course);
        }
        if (along < GeoMath.DistanceNm(from, to))
        {
            return ([.. fixes.Skip(leg)], course);
        }
        return (leg + 1) < fixes.Count ? ([.. fixes.Skip(leg + 1)], course) : ([], legCourse);
    }

    // The index of the end fix of the leg nearest the point; ties go to the later leg.
    private static int NearestLeg(LatLon point, List<LatLon> fixes)
    {
        int leg = 1;
        double best = double.MaxValue;
        for (int i = 1; i < fixes.Count; i++)
        {
            double d = DistanceToLegNm(point, fixes[i - 1], fixes[i]);
            if (d <= best)
            {
                best = d;
                leg = i;
            }
        }
        return leg;
    }

    private static double NormalizeSigned(double degrees) => ((((degrees + 180) % 360) + 360) % 360) - 180;

    private static double DistanceToLegNm(LatLon point, LatLon from, LatLon to)
    {
        double legNm = GeoMath.DistanceNm(from, to);
        if (legNm <= 0)
        {
            return GeoMath.DistanceNm(point, from);
        }

        var course = new TrueHeading(GeoMath.BearingTo(from, to));
        double along = GeoMath.AlongTrackDistanceNm(point.Lat, point.Lon, from.Lat, from.Lon, course);
        if (along <= 0)
        {
            return GeoMath.DistanceNm(point, from);
        }
        return along >= legNm ? GeoMath.DistanceNm(point, to) : Math.Abs(GeoMath.SignedCrossTrackDistanceNm(point, from, course));
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
        ac.Eram.EndCoast();
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
