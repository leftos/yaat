using Microsoft.Extensions.Logging;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Commands;

/// <summary>
/// Pure domain logic for STARS track operations. All methods mutate <see cref="AircraftState"/>
/// directly and return a <see cref="CommandResult"/>. No server-specific dependencies.
/// </summary>
public static partial class TrackEngine
{
    private static readonly ILogger Log = SimLog.CreateLogger("TrackEngine");

    public static string FormatOwner(TrackOwner owner)
    {
        string tcp;
        if ((owner.OwnerType == TrackOwnerType.Eram) && owner.SectorId is not null)
        {
            tcp = $"C{owner.SectorId}";
        }
        else if (owner.Subset is not null && owner.SectorId is not null)
        {
            tcp = $"{owner.Subset}{owner.SectorId}";
        }
        else
        {
            tcp = "";
        }

        return string.IsNullOrEmpty(tcp) ? owner.Callsign : $"{owner.Callsign} ({tcp})";
    }

    public static bool IsTrackCommand(ParsedCommand? cmd) =>
        cmd
            is TrackAircraftCommand
                or DropTrackCommand
                or InitiateHandoffCommand
                or ForceHandoffCommand
                or AcceptHandoffCommand
                or CancelHandoffCommand
                or PointOutCommand
                or AcknowledgeCommand
                or RejectPointoutCommand
                or RetractPointoutCommand
                or ConvertPointoutCommand
                or ForceQuicklookCommand
                or ForceQuicklookClearCommand
                or AcknowledgeConflictAlertCommand
                or InhibitConflictAlertCommand
                or SuppressConflictAlertCommand
                or InhibitDuplicateBeaconCommand
                or PilotReportedAltitudeCommand
                or LeaderDirectionCommand
                or JRingCommand
                or ConeCommand
                or Scratchpad1Command
                or Scratchpad2Command
                or TemporaryAltitudeCommand
                or CruiseCommand
                or OnHandoffCommand
                or SetActivePositionCommand
                or AsdexEditCommand
                or AsdexVerbCommand;

    public static bool IsStripCommand(ParsedCommand? cmd) =>
        cmd
            is StripMoveCommand
                or StripScanCommand
                or StripAnnotateCommand
                or StripDeleteCommand
                or StripOffsetCommand
                or HalfStripCreateCommand
                or HalfStripAmendCommand
                or HalfStripDeleteCommand
                or HalfStripMoveCommand
                or HalfStripOffsetCommand
                or HalfStripSlideCommand
                or SeparatorCreateCommand
                or SeparatorDeleteCommand
                or SeparatorEditCommand
                or SeparatorMoveCommand
                or BlankCreateCommand
                or BlankDeleteCommand;

    public static bool IsTdlsCommand(ParsedCommand? cmd) => cmd is TdlsQueueCommand or TdlsSendCommand or TdlsWilcoCommand or TdlsDumpCommand;

    public static bool IsCoordinationCommand(ParsedCommand? cmd) =>
        cmd
            is CoordinationReleaseCommand
                or CoordinationHoldCommand
                or CoordinationRecallCommand
                or CoordinationAcknowledgeCommand
                or CoordinationAutoAckCommand
                or CoordinationDeleteCommand
                or CoordinationReorderCommand
                or CoordinationModifyCommand;

    public static CommandResult NotOwnedError(AircraftState ac, TrackOwner identity)
    {
        if (ac.Track.Owner is null)
        {
            return new CommandResult(false, $"{ac.Callsign} is not tracked");
        }

        var ownerDisplay = FormatOwner(ac.Track.Owner);
        return new CommandResult(false, $"{ac.Callsign} owned by {ownerDisplay}, not you — use AS to switch position, or HOF to force");
    }

    public static CommandResult HandleTrack(AircraftState ac, TrackOwner identity)
    {
        // Re-starting track on a QH-frozen track unfreezes it (7110.65 §5-2-15 "track start from frozen
        // status") and re-pairs it to the live target — even when the frozen track is still owned by the
        // acting position, which the normal already-tracked guard below would otherwise reject. A track
        // frozen but owned by a different position still cannot be stolen.
        if (ac.Eram.IsFrozen)
        {
            if (ac.Track.Owner is not null && !ac.Track.Owner.MatchesPosition(identity))
            {
                return new CommandResult(false, $"{ac.Callsign} already tracked by {ac.Track.Owner.Callsign}");
            }

            ac.Track.Owner = identity;
            ac.Eram.IsFrozen = false;
            ac.Eram.FrozenLat = null;
            ac.Eram.FrozenLon = null;
            ac.Eram.FrozenAltitude = null;
            return new CommandResult(true, $"Tracking {ac.Callsign}");
        }

        if (ac.Track.Owner is not null)
        {
            return new CommandResult(false, $"{ac.Callsign} already tracked by {ac.Track.Owner.Callsign}");
        }

        ac.Track.Owner = identity;
        return new CommandResult(true, $"Tracking {ac.Callsign}");
    }

    /// <summary>
    /// <c>TRACK [position]</c>: claims the track for the position named by <paramref name="tcpCode"/>
    /// rather than the acting identity (mirrors <c>HO [position]</c>). When <paramref name="tcpCode"/>
    /// is null this is a plain <c>TRACK</c> that claims the track for <paramref name="fallbackIdentity"/>.
    /// </summary>
    public static CommandResult HandleTrack(AircraftState ac, string? tcpCode, TrackOwner? fallbackIdentity, SimScenarioState scenario)
    {
        if (tcpCode is not null)
        {
            var owner = TrackResolver.ResolveTcpToOwner(scenario, tcpCode);
            return owner is null ? new CommandResult(false, $"Unknown position: {tcpCode}") : HandleTrack(ac, owner);
        }

        return fallbackIdentity is null ? new CommandResult(false, "No active position — use AS to set one") : HandleTrack(ac, fallbackIdentity);
    }

    public static CommandResult HandleDrop(AircraftState ac)
    {
        if (ac.Track.Owner is null)
        {
            return new CommandResult(false, $"{ac.Callsign} is not tracked");
        }

        ac.Track.Owner = null;
        ac.Track.HandoffPeer = null;
        ac.Track.HandoffInitiatedAt = null;
        ac.Track.HandoffRedirectedBy = null;
        // A dropped Track has no owner, so any pending accepted indicator (Oxxx/Kxxx) is meaningless and
        // would render against a null owner — clear it.
        ClearRecentHandoffAccepted(ac);
        // Consume the FP-creator auto-track entitlement so the next tick's
        // ProcessFlightPlanCreatorAutoTrack doesn't immediately re-acquire when
        // the pilot is still squawking the assigned code. Without this, manual
        // TERM CTLs are silently undone every tick (bug N427MX six-drop loop).
        ac.FlightPlan.CreatedByOwner = null;
        return new CommandResult(true, $"Dropped {ac.Callsign}");
    }

    public static CommandResult HandleAccept(AircraftState ac, SimScenarioState scenario)
    {
        if (ac.Track.HandoffPeer is null)
        {
            return new CommandResult(false, $"No pending handoff for {ac.Callsign}");
        }

        var previousOwner = ac.Track.Owner;
        ac.Track.Owner = ac.Track.HandoffPeer;
        ac.Track.HandoffPeer = null;
        ac.Track.HandoffInitiatedAt = null;
        ac.Track.HandoffRedirectedBy = null;
        ac.Track.HandoffAccepted = true;
        MarkPreviousOwnerRetained(ac, previousOwner, scenario);
        MarkRecentHandoffAccepted(ac, previousOwner, wasForced: false, scenario);
        return new CommandResult(true, $"Accepted {ac.Callsign}");
    }

    /// <summary>
    /// Flags the previous owner's STARS <c>SharedState</c> entry as previously-owned so that, after a
    /// handoff is accepted, that controller's datablock stays a white FDB (CRC's <c>WasPreviouslyOwned</c>
    /// semantics) until they slew to acknowledge — instead of dropping straight to an unowned green PDB.
    /// No-op when the previous owner has no resolvable TCP. Shared by the manual accept path
    /// (<see cref="HandleAccept"/>), the accept-all path, and the auto-accept timer so they cannot drift.
    /// </summary>
    public static void MarkPreviousOwnerRetained(AircraftState ac, TrackOwner? previousOwner, SimScenarioState scenario)
    {
        var previousTcp = previousOwner is not null ? TrackResolver.FindTcpForOwner(previousOwner, scenario) : null;
        if (previousTcp is null)
        {
            return;
        }

        if (!ac.Stars.SharedState.TryGetValue(previousTcp.Id, out var shared))
        {
            shared = new StarsTrackSharedState();
        }

        shared.WasPreviouslyOwned = true;
        ac.Stars.SharedState[previousTcp.Id] = shared;
    }

    /// <summary>
    /// Records the ERAM Field-E accepted indicator on the previous owner: after a handoff is accepted
    /// (<paramref name="wasForced"/> = false → <c>Oxxx</c>) or the Track is force-taken
    /// (<paramref name="wasForced"/> = true → <c>Kxxx</c>), the previous owner's FDB shows the acceptor's
    /// sector for a transient window (docs/crc/eram.md §Data Blocks). No-op when there was no previous
    /// owner (nothing to confirm). The 30 s window is enforced by the CRC broadcast against
    /// <see cref="AircraftEramState.RecentHandoffAcceptedAtSeconds"/>; the ERAM-only rendering means STARS
    /// previous owners are simply never matched by an ERAM subscriber. Shared by the manual accept,
    /// accept-all, auto-accept, and force paths so they cannot drift.
    /// </summary>
    public static void MarkRecentHandoffAccepted(AircraftState ac, TrackOwner? previousOwner, bool wasForced, SimScenarioState scenario)
    {
        if (previousOwner is null)
        {
            return;
        }

        ac.Eram.RecentHandoffPreviousOwner = previousOwner;
        ac.Eram.RecentHandoffWasForced = wasForced;
        ac.Eram.RecentHandoffAcceptedAtSeconds = scenario.ElapsedSeconds;
    }

    /// <summary>Clears the ERAM accepted indicator (see <see cref="MarkRecentHandoffAccepted"/>).</summary>
    public static void ClearRecentHandoffAccepted(AircraftState ac)
    {
        ac.Eram.RecentHandoffPreviousOwner = null;
        ac.Eram.RecentHandoffWasForced = false;
        ac.Eram.RecentHandoffAcceptedAtSeconds = null;
    }

    public static CommandResult HandleCancel(AircraftState ac)
    {
        if (ac.Track.Owner is null || ac.Track.HandoffPeer is null)
        {
            return new CommandResult(false, $"No pending outbound handoff for {ac.Callsign}");
        }

        ac.Track.HandoffPeer = null;
        ac.Track.HandoffInitiatedAt = null;
        ac.Track.HandoffRedirectedBy = null;
        return new CommandResult(true, $"Cancelled handoff for {ac.Callsign}");
    }

    public static CommandResult HandleAcknowledge(AircraftState ac)
    {
        if (ac.Track.Pointout is null || !ac.Track.Pointout.IsPending)
        {
            return new CommandResult(false, $"No pending pointout for {ac.Callsign}");
        }

        AcceptIncomingPointout(ac);
        return new CommandResult(true, $"Acknowledged {ac.Callsign}");
    }

    /// <summary>
    /// Marks the pending incoming pointout as accepted and sets the recipient's
    /// <see cref="StarsTrackSharedState.IsRecentlyAcceptedIncomingPointout"/> flag. CRC keeps the
    /// recipient's data block yellow (forced full) from the moment they slew to accept until they slew
    /// a second time to clear; that transient window is carried entirely by this per-TCP flag, which
    /// CRC reads back from the track DTO and never originates locally. Setting it here keeps the
    /// accepted pointout yellow on both CRC and YAAT's Radar View until the recipient dismisses it
    /// (see <see cref="ClearDismissedIncomingPointout"/>).
    /// </summary>
    private static void AcceptIncomingPointout(AircraftState ac)
    {
        var pointout = ac.Track.Pointout!;
        pointout.Status = StarsPointoutStatus.Accepted;

        var recipientId = pointout.Recipient.Id;
        if (!ac.Stars.SharedState.TryGetValue(recipientId, out var shared))
        {
            shared = new StarsTrackSharedState();
            ac.Stars.SharedState[recipientId] = shared;
        }

        shared.IsRecentlyAcceptedIncomingPointout = true;
    }

    public static CommandResult HandlePointOut(AircraftState ac, Tcp targetTcp, Tcp senderTcp, double elapsedSeconds)
    {
        if (ac.Track.Owner is null)
        {
            return new CommandResult(false, $"{ac.Callsign} is not tracked");
        }

        if (ac.Track.Pointout is { IsPending: true })
        {
            return new CommandResult(false, $"Pointout already pending for {ac.Callsign}");
        }

        ac.Track.Pointout = new StarsPointout(targetTcp, senderTcp) { InitiatedAt = elapsedSeconds };
        return new CommandResult(true, $"Point out {ac.Callsign} to {targetTcp}");
    }

    public static CommandResult HandlePointOutNoArgs(AircraftState ac, TrackOwner identity)
    {
        if (ac.Track.Pointout is null || ac.Track.Pointout.IsAccepted)
        {
            return new CommandResult(false, $"No pending pointout for {ac.Callsign}");
        }

        var tcpStr = $"{identity.Subset}{identity.SectorId}";

        if (ac.Track.Pointout.IsPending && ac.Track.Pointout.Recipient.ToString() == tcpStr)
        {
            AcceptIncomingPointout(ac);
            return new CommandResult(true, $"Acknowledged {ac.Callsign}");
        }

        if (ac.Track.Pointout.Sender.ToString() == tcpStr)
        {
            ac.Track.Pointout = null;
            return new CommandResult(true, $"Retracted pointout for {ac.Callsign}");
        }

        return new CommandResult(false, $"No pending pointout for {ac.Callsign}");
    }

    public static CommandResult HandleScratchpad1(AircraftState ac, string text, int maxLength)
    {
        bool isClearing = string.IsNullOrEmpty(text);

        if (isClearing && ac.Stars.WasScratchpad1Cleared)
        {
            // Undo: clear again restores previous
            ac.Stars.Scratchpad1 = ac.Stars.PreviousScratchpad1;
            ac.Stars.WasScratchpad1Cleared = string.IsNullOrEmpty(ac.Stars.PreviousScratchpad1);
            return new CommandResult(true, $"SP1: {ac.Stars.Scratchpad1}");
        }

        if (!isClearing && text == ac.Stars.Scratchpad1)
        {
            // Toggle: same value restores previous
            ac.Stars.Scratchpad1 = ac.Stars.PreviousScratchpad1;
            ac.Stars.WasScratchpad1Cleared = string.IsNullOrEmpty(ac.Stars.PreviousScratchpad1);
            return new CommandResult(true, $"SP1: {ac.Stars.Scratchpad1}");
        }

        if (!isClearing && text.Length > maxLength)
        {
            // STARS rejects an over-length scratchpad entry; leave the current value unchanged.
            return new CommandResult(false, "FORMAT");
        }

        ac.Stars.PreviousScratchpad1 = ac.Stars.Scratchpad1;
        ac.Stars.Scratchpad1 = text;
        ac.Stars.WasScratchpad1Cleared = isClearing;
        return new CommandResult(true, $"SP1: {text}");
    }

    /// <summary>
    /// Apply an ASDE-X display-field override to <c>AircraftStarsState</c>. An empty
    /// <paramref name="text"/> clears the override (DTO falls back to scenario/derived value).
    /// </summary>
    public static CommandResult HandleAsdexEdit(AircraftState ac, AsdexEditField field, string text)
    {
        var value = string.IsNullOrEmpty(text) ? null : text;
        switch (field)
        {
            case AsdexEditField.Scratchpad1:
                ac.Stars.AsdexScratchpad1 = value;
                return new CommandResult(true, $"ASDX SP1: {value ?? "(cleared)"}");
            case AsdexEditField.Scratchpad2:
                ac.Stars.AsdexScratchpad2 = value;
                return new CommandResult(true, $"ASDX SP2: {value ?? "(cleared)"}");
            case AsdexEditField.Callsign:
                ac.Stars.AsdexCallsignOverride = value;
                return new CommandResult(true, $"ASDX CS: {value ?? "(cleared)"}");
            case AsdexEditField.BeaconCode:
                ac.Stars.AsdexBeaconCodeOverride = value;
                return new CommandResult(true, $"ASDX BCN: {value ?? "(cleared)"}");
            case AsdexEditField.Category:
                ac.Stars.AsdexCategoryOverride = value;
                return new CommandResult(true, $"ASDX CAT: {value ?? "(cleared)"}");
            case AsdexEditField.AircraftType:
                ac.Stars.AsdexAircraftTypeOverride = value;
                return new CommandResult(true, $"ASDX TYPE: {value ?? "(cleared)"}");
            case AsdexEditField.Fix:
                ac.Stars.AsdexFixOverride = value;
                return new CommandResult(true, $"ASDX FIX: {value ?? "(cleared)"}");
            default:
                return new CommandResult(false, $"Unknown ASDE-X field '{field}'");
        }
    }

    /// <summary>
    /// Apply an ASDE-X per-aircraft verb (Tag/Terminate/Suspend/Unsuspend/InhibitAlerts) to
    /// <c>AircraftStarsState</c>. Tag clears the terminated bit (CRC's untermination path).
    /// Server-side <c>CrcBroadcastService</c> reads these bits to filter visibility / status.
    /// </summary>
    public static CommandResult HandleAsdexVerb(AircraftState ac, AsdexVerb verb)
    {
        switch (verb)
        {
            case AsdexVerb.Tag:
                ac.Stars.AsdexTerminated = false;
                return new CommandResult(true, $"ASDX TAG: {ac.Callsign}");
            case AsdexVerb.Terminate:
                ac.Stars.AsdexTerminated = true;
                return new CommandResult(true, $"ASDX TERM: {ac.Callsign}");
            case AsdexVerb.Suspend:
                ac.Stars.AsdexSuspended = true;
                return new CommandResult(true, $"ASDX SUSP: {ac.Callsign}");
            case AsdexVerb.Unsuspend:
                ac.Stars.AsdexSuspended = false;
                return new CommandResult(true, $"ASDX UNSUSP: {ac.Callsign}");
            case AsdexVerb.InhibitAlerts:
                ac.Stars.AsdexAlertsInhibited = true;
                return new CommandResult(true, $"ASDX INHIB: {ac.Callsign}");
            default:
                return new CommandResult(false, $"Unknown ASDE-X verb '{verb}'");
        }
    }

    public static CommandResult HandleScratchpad2(AircraftState ac, string text, int maxLength)
    {
        bool isClearing = string.IsNullOrEmpty(text);

        if (isClearing && string.IsNullOrEmpty(ac.Stars.Scratchpad2))
        {
            // Undo: clear again restores previous
            ac.Stars.Scratchpad2 = ac.Stars.PreviousScratchpad2;
            return new CommandResult(true, $"SP2: {ac.Stars.Scratchpad2}");
        }

        if (!isClearing && text == ac.Stars.Scratchpad2)
        {
            // Toggle: same value restores previous
            ac.Stars.Scratchpad2 = ac.Stars.PreviousScratchpad2;
            return new CommandResult(true, $"SP2: {ac.Stars.Scratchpad2}");
        }

        if (!isClearing && text.Length > maxLength)
        {
            // STARS rejects an over-length scratchpad entry; leave the current value unchanged.
            return new CommandResult(false, "FORMAT");
        }

        ac.Stars.PreviousScratchpad2 = ac.Stars.Scratchpad2;
        ac.Stars.Scratchpad2 = text;
        return new CommandResult(true, $"SP2: {text}");
    }

    public static CommandResult HandleTemporaryAltitude(AircraftState ac, int altHundreds)
    {
        // 0 is the clear sentinel (CRC "M Δ000", RPO bare/0 TA): CRC blanks the FDB
        // altitude line only when the wire value is null, so a stored 0 renders "A000".
        ac.Stars.TemporaryAltitude = altHundreds == 0 ? null : altHundreds;
        return new CommandResult(true, altHundreds == 0 ? "Temp alt cleared" : $"Temp alt: {altHundreds * 100}");
    }

    public static CommandResult HandleCruise(AircraftState ac, int altHundreds)
    {
        var feet = altHundreds * 100;
        // Preserve the existing altitude notation (VFR-on-top vs plain VFR vs IFR) while updating the value.
        ac.FlightPlan.Altitude =
            ac.FlightPlan.Altitude.IsVfrOnTop ? PlannedAltitude.Otp(feet)
            : ac.FlightPlan.IsVfr ? PlannedAltitude.Vfr(feet)
            : PlannedAltitude.Ifr(feet);
        return new CommandResult(true, $"Cruise: {feet}");
    }

    public static CommandResult HandleOnHandoff(AircraftState ac)
    {
        ac.Track.OnHandoff = !ac.Track.OnHandoff;
        var state = ac.Track.OnHandoff ? "on" : "off";
        return new CommandResult(true, $"On-handoff {state} for {ac.Callsign}");
    }

    public static CommandResult HandleRejectPointout(AircraftState ac)
    {
        if (ac.Track.Pointout is null || !ac.Track.Pointout.IsPending)
        {
            return new CommandResult(false, $"No pending pointout for {ac.Callsign}");
        }

        ac.Track.Pointout.Status = StarsPointoutStatus.Rejected;
        return new CommandResult(true, $"Rejected pointout for {ac.Callsign}");
    }

    public static CommandResult HandleRetractPointout(AircraftState ac)
    {
        // Pending: the sender retracts. Rejected: the sender dismisses the flashing UN indication —
        // CRC forwards the sender's slew for both states expecting the pointout to clear.
        if (ac.Track.Pointout is null || ac.Track.Pointout.IsAccepted)
        {
            return new CommandResult(false, $"No pending pointout for {ac.Callsign}");
        }

        ac.Track.Pointout = null;
        return new CommandResult(true, $"Retracted pointout for {ac.Callsign}");
    }

    /// <summary>
    /// Drops a completed incoming point-out from sim state when the recipient dismisses the
    /// just-accepted track. CRC's <c>IsRecentlyAcceptedIncomingPointout</c> flag flips true-&gt;false
    /// on the recipient's slew-to-clear gesture; at that point the accepted point-out has served its
    /// purpose and should not linger in sim state. <paramref name="recipientTcpId"/> is the
    /// <see cref="Tcp.Id"/> (ULID) of the position whose shared state changed — matched against
    /// <see cref="StarsPointout.Recipient"/> so an unrelated position's update is ignored. The
    /// transition guard (was-true, now-false) avoids clearing during the window between the accept
    /// and CRC pushing the flag.
    /// </summary>
    public static void ClearDismissedIncomingPointout(AircraftState ac, string recipientTcpId, bool wasRecentlyAccepted, bool isRecentlyAccepted)
    {
        if (wasRecentlyAccepted && !isRecentlyAccepted && ac.Track.Pointout is { IsAccepted: true } po && po.Recipient.Id == recipientTcpId)
        {
            ac.Track.Pointout = null;
        }
    }

    /// <summary>
    /// A position's per-TCP shared display state on the track (a CRC <c>UpdateStarsSharedTrackState</c>, recorded as a
    /// <see cref="Simulation.RecordedStarsSharedStateChange"/>): the entry is replaced whole, and the recipient's
    /// slew-to-clear — the recently-accepted flag flipping true to false — drops the completed point-out
    /// (<see cref="ClearDismissedIncomingPointout"/>).
    /// </summary>
    public static void ApplySharedState(AircraftState ac, string tcpId, SharedStateDto state)
    {
        var wasRecentlyAccepted = ac.Stars.SharedState.TryGetValue(tcpId, out var prior) && prior.IsRecentlyAcceptedIncomingPointout;
        ac.Stars.SharedState[tcpId] = StarsTrackSharedState.FromSnapshot(state);
        ClearDismissedIncomingPointout(ac, tcpId, wasRecentlyAccepted, state.IsRecentlyAcceptedIncomingPointout);
    }

    public static CommandResult HandlePilotReportedAltitude(AircraftState ac, int altHundreds)
    {
        ac.Stars.PilotReportedAltitude = altHundreds == 0 ? null : altHundreds;
        return new CommandResult(true, $"Pilot reported altitude: {(altHundreds == 0 ? "cleared" : $"{altHundreds * 100}")}");
    }

    /// <summary>
    /// Inhibits the flashing duplicate-beacon indication on the owner's data block (the owner's
    /// bare slew on a DB-flagged track). Set, not toggled: the flag only suppresses the current
    /// indication and is superseded once the duplicate resolves.
    /// </summary>
    public static CommandResult HandleInhibitDuplicateBeacon(AircraftState ac)
    {
        ac.Stars.IsDuplicateBeaconInhibited = true;
        return new CommandResult(true, $"Duplicate-beacon indication inhibited for {ac.Callsign}");
    }

    /// <summary>
    /// Per-pair conflict-alert suppression (the instructor's answer to a real aircraft the sim cannot see the
    /// separation for — visual, dependent approaches, MARSA). Stored on the aircraft that received the command;
    /// the detectors check both sides of a pair.
    /// </summary>
    public static CommandResult HandleSuppressConflictAlert(AircraftState ac, string otherCallsign)
    {
        var other = otherCallsign.ToUpperInvariant();
        if (string.Equals(other, ac.Callsign, StringComparison.OrdinalIgnoreCase))
        {
            return new CommandResult(false, "CASUP needs another aircraft's callsign");
        }

        bool removed = ac.Stars.CaSuppressedWith.RemoveAll(c => string.Equals(c, other, StringComparison.OrdinalIgnoreCase)) > 0;
        if (!removed)
        {
            ac.Stars.CaSuppressedWith.Add(other);
        }

        return new CommandResult(
            true,
            removed ? $"Conflict alert restored between {ac.Callsign} and {other}" : $"Conflict alert suppressed between {ac.Callsign} and {other}"
        );
    }

    public static CommandResult HandleInhibitConflictAlert(AircraftState ac)
    {
        ac.Stars.IsCaInhibited = !ac.Stars.IsCaInhibited;
        var state = ac.Stars.IsCaInhibited ? "inhibited" : "enabled";
        return new CommandResult(true, $"Conflict alert {state} for {ac.Callsign}");
    }

    public static CommandResult HandleLeaderDirection(AircraftState ac, int direction)
    {
        ac.Stars.GlobalLeaderDirection = direction == 5 ? null : direction;
        return new CommandResult(true, $"Leader direction: {(direction == 5 ? "default" : $"{direction}")}");
    }

    private const int TpaJRing = 1;
    private const int TpaCone = 2;

    public static CommandResult HandleJRing(AircraftState ac, bool enable, double? size)
    {
        ac.Stars.TpaType = enable ? TpaJRing : null;
        ac.Stars.TpaSize = enable ? (size ?? 0.0) : 0.0;
        var detail = enable ? $"on ({ac.Stars.TpaSize:0.#} NM)" : "off";
        return new CommandResult(true, $"J-Ring {detail} for {ac.Callsign}");
    }

    public static CommandResult HandleCone(AircraftState ac, bool enable, double? size)
    {
        ac.Stars.TpaType = enable ? TpaCone : null;
        ac.Stars.TpaSize = enable ? (size ?? 0.0) : 0.0;
        var detail = enable ? $"on ({ac.Stars.TpaSize:0.#} NM)" : "off";
        return new CommandResult(true, $"Cone {detail} for {ac.Callsign}");
    }

    /// <summary>
    /// <c>HO</c>: offers the track to a position. Three outcomes. The pending handoff's <em>recipient</em> — not the owner —
    /// entering a new handoff ID re-points the inbound handoff at that position (stars.md "Redirecting a Handoff"), with
    /// <c>HandoffRedirectedBy</c> carrying the recipient it was redirected away from, the convention CRC renders from.
    /// A target that is unattended but consolidated under an attended position receives the handoff there
    /// (<paramref name="redirect"/>, null when no host answers attendance), <c>HandoffRedirectedBy</c> carrying the
    /// addressed position. Otherwise the target itself becomes the peer.
    /// </summary>
    public static CommandResult ApplyHandoff(
        AircraftState ac,
        SimScenarioState scenario,
        TrackOwner? identity,
        string? tcpCode,
        ConsolidationRedirect? redirect
    )
    {
        if (ac.Track.Owner is null)
        {
            return new CommandResult(false, $"{ac.Callsign} is not tracked");
        }

        if (tcpCode is null)
        {
            var studentPos = scenario.StudentPosition;
            if (studentPos is null)
            {
                return new CommandResult(false, "No student position configured");
            }

            ac.Track.HandoffPeer = studentPos;
            ac.Track.HandoffInitiatedAt = scenario.ElapsedSeconds;
            return new CommandResult(true, $"Handoff {ac.Callsign} to {FormatOwner(studentPos)}");
        }

        var target = TrackResolver.ResolveTcpToOwner(scenario, tcpCode);
        if (target is null)
        {
            return new CommandResult(false, $"Unknown position: {tcpCode}");
        }

        if (
            (identity is not null)
            && (ac.Track.HandoffPeer is not null)
            && ac.Track.HandoffPeer.MatchesPosition(identity)
            && !ac.Track.Owner.MatchesPosition(identity)
        )
        {
            var manualRedirectFrom = ac.Track.HandoffPeer;
            ac.Track.HandoffPeer = redirect?.TryRedirect(target) ?? target;
            ac.Track.HandoffRedirectedBy = manualRedirectFrom;
            ac.Track.HandoffInitiatedAt = scenario.ElapsedSeconds;
            return new CommandResult(true, $"Redirected handoff {ac.Callsign} to {tcpCode}");
        }

        if (redirect?.TryRedirect(target) is { } redirectOwner)
        {
            ac.Track.HandoffPeer = redirectOwner;
            ac.Track.HandoffRedirectedBy = target;
            ac.Track.HandoffInitiatedAt = scenario.ElapsedSeconds;
            return new CommandResult(true, $"Handoff {ac.Callsign} to {tcpCode} (redirected to {redirectOwner.Subset}{redirectOwner.SectorId})");
        }

        ac.Track.HandoffPeer = target;
        ac.Track.HandoffInitiatedAt = scenario.ElapsedSeconds;
        Log.LogInformation(
            "[Handoff] {Callsign}: Owner={OwnerCallsign} (type={OwnerType}, fac={OwnerFac}, {OwnerSubset}{OwnerSector}) → "
                + "Peer={PeerCallsign} (type={PeerType}, fac={PeerFac}, {PeerSubset}{PeerSector})",
            ac.Callsign,
            ac.Track.Owner.Callsign,
            ac.Track.Owner.OwnerType,
            ac.Track.Owner.FacilityId,
            ac.Track.Owner.Subset,
            ac.Track.Owner.SectorId,
            target.Callsign,
            target.OwnerType,
            target.FacilityId,
            target.Subset,
            target.SectorId
        );
        return new CommandResult(true, $"Handoff {ac.Callsign} to {tcpCode}");
    }

    /// <summary>
    /// Mirrors yaat-server's <c>TrackCommandHandler.HandleForceHandoff</c>: transfer
    /// ownership to the target TCP without the standard ownership check.
    /// </summary>
    public static CommandResult ApplyForceHandoff(AircraftState ac, SimScenarioState scenario, string tcpCode)
    {
        var target = TrackResolver.ResolveTcpToOwner(scenario, tcpCode);
        if (target is null)
        {
            return new CommandResult(false, $"Unknown position: {tcpCode}");
        }

        var previousOwner = ac.Track.Owner;
        ac.Track.Owner = target;
        ac.Track.HandoffPeer = null;
        ac.Track.HandoffInitiatedAt = null;
        ac.Track.HandoffRedirectedBy = null;
        // A force-take (/OK steal) shows Kxxx on the sector it was taken from.
        MarkRecentHandoffAccepted(ac, previousOwner, wasForced: true, scenario);
        return new CommandResult(true, $"Force handoff {ac.Callsign} to {tcpCode}");
    }

    /// <summary>
    /// <c>PO {tcp}</c>: resolves the target and sender TCPs (the sender is the track owner — no acting position
    /// needed), then files the point-out. An unattended target consolidated under an attended position receives it
    /// there (<paramref name="redirect"/>, null when no host answers attendance): the controller working the combined
    /// position acts under the parent's identity, so a point-out left on the literal child TCP could never be
    /// acknowledged and would stick pending.
    /// </summary>
    public static CommandResult ApplyPointOut(AircraftState ac, SimScenarioState scenario, string tcpCode, ConsolidationRedirect? redirect)
    {
        if (ac.Track.Owner is null)
        {
            return new CommandResult(false, $"{ac.Callsign} is not tracked");
        }

        var targetTcp = TrackResolver.FindTcpByCode(scenario, tcpCode);
        if (targetTcp is null)
        {
            return new CommandResult(false, $"Unknown position: {tcpCode}");
        }

        var senderTcp = TrackResolver.FindTcpForOwner(ac.Track.Owner, scenario);
        if (senderTcp is null)
        {
            return new CommandResult(false, "Cannot determine sender TCP");
        }

        var targetOwner = TrackResolver.ResolveTcpToOwner(scenario, tcpCode);
        if ((targetOwner is not null) && (redirect?.TryRedirect(targetOwner) is { } redirectOwner))
        {
            var redirectedTcp = TrackResolver.FindTcpForOwner(redirectOwner, scenario);
            if (redirectedTcp is not null)
            {
                targetTcp = redirectedTcp;
            }
        }

        return HandlePointOut(ac, targetTcp, senderTcp, scenario.ElapsedSeconds);
    }

    /// <summary>
    /// STARS <c>**</c> on a track with an incoming pointout: convert it to a handoff and accept
    /// it — ownership transfers to the pointout recipient (stars.md Table 21). Mirrors
    /// <see cref="HandleAccept"/>'s transfer semantics (previous-owner white FDB + accepted
    /// indicator, ONHO trigger).
    /// </summary>
    public static CommandResult HandleConvertPointout(AircraftState ac, TrackOwner newOwner, SimScenarioState scenario)
    {
        if (ac.Track.Pointout is null || ac.Track.Pointout.IsRejected)
        {
            return new CommandResult(false, $"No pointout to convert for {ac.Callsign}");
        }

        var previousOwner = ac.Track.Owner;
        ac.Track.Pointout = null;
        ac.Track.Owner = newOwner;
        ac.Track.HandoffPeer = null;
        ac.Track.HandoffInitiatedAt = null;
        ac.Track.HandoffRedirectedBy = null;
        ac.Track.HandoffAccepted = true;
        MarkPreviousOwnerRetained(ac, previousOwner, scenario);
        MarkRecentHandoffAccepted(ac, previousOwner, wasForced: false, scenario);
        return new CommandResult(true, $"Converted pointout to handoff; {ac.Callsign} now tracked by {FormatOwner(newOwner)}");
    }

    /// <summary>Resolves the pointout recipient to a TrackOwner, then converts (see above).</summary>
    public static CommandResult ApplyConvertPointout(AircraftState ac, SimScenarioState scenario)
    {
        if (ac.Track.Pointout is null || ac.Track.Pointout.IsRejected)
        {
            return new CommandResult(false, $"No pointout to convert for {ac.Callsign}");
        }

        var recipient = ac.Track.Pointout.Recipient;
        var newOwner = TrackResolver.ResolveTcpToOwner(scenario, $"{recipient.Subset}{recipient.SectorId}");
        if (newOwner is null)
        {
            return new CommandResult(false, $"Cannot resolve pointout recipient {recipient.Subset}{recipient.SectorId}");
        }

        return HandleConvertPointout(ac, newOwner, scenario);
    }

    /// <summary>STARS force quicklook (<c>**</c> family): adds the TCPs to <see cref="AircraftStarsState.ForcedPointoutsTo"/>.</summary>
    public static CommandResult HandleForceQuicklook(AircraftState ac, List<Tcp> tcps)
    {
        foreach (var tcp in tcps)
        {
            if (!ac.Stars.ForcedPointoutsTo.Any(t => t.Id == tcp.Id))
            {
                ac.Stars.ForcedPointoutsTo.Add(tcp);
            }
        }

        return new CommandResult(true, $"Forced quicklook at {string.Join(", ", tcps.Select(t => $"{t.Subset}{t.SectorId}"))}");
    }

    /// <summary>Resolves the TCP codes, then forces quicklook (see above).</summary>
    public static CommandResult ApplyForceQuicklook(AircraftState ac, SimScenarioState scenario, List<string> tcpCodes)
    {
        var tcps = new List<Tcp>();
        foreach (var code in tcpCodes)
        {
            var tcp = TrackResolver.FindTcpByCode(scenario, code);
            if (tcp is null)
            {
                return new CommandResult(false, $"Unknown position: {code}");
            }

            tcps.Add(tcp);
        }

        return HandleForceQuicklook(ac, tcps);
    }

    /// <summary>Removes a TCP's forced-quicklook entry (the forced TCP's own slew acknowledge).</summary>
    public static CommandResult HandleForceQuicklookClear(AircraftState ac, Tcp tcp)
    {
        return ac.Stars.ForcedPointoutsTo.RemoveAll(t => t.Id == tcp.Id) > 0
            ? new CommandResult(true, $"Cleared forced quicklook at {tcp.Subset}{tcp.SectorId}")
            : new CommandResult(false, $"No forced quicklook at {tcp.Subset}{tcp.SectorId} for {ac.Callsign}");
    }

    /// <summary>Resolves the TCP code, then clears its forced quicklook (see above).</summary>
    public static CommandResult ApplyForceQuicklookClear(AircraftState ac, SimScenarioState scenario, string tcpCode)
    {
        var tcp = TrackResolver.FindTcpByCode(scenario, tcpCode);
        return tcp is null ? new CommandResult(false, $"Unknown position: {tcpCode}") : HandleForceQuicklookClear(ac, tcp);
    }

    /// <summary>
    /// Top-level dispatch for any <see cref="ParsedCommand"/> classified as a track
    /// command (see <see cref="IsTrackCommand"/>). Routes to the appropriate
    /// <c>HandleX</c> / <c>ApplyX</c> with the resolved identity.
    ///
    /// Per-aircraft only: the position-scoped verbs (<see cref="DispatchGlobal"/>), the ghost and reposition
    /// display objects (<c>TrackEngine.Ghost.cs</c>) and <c>CAACK</c> (<see cref="AcknowledgeConflictAlert"/>,
    /// which needs the engine's conflict-alert set) have their own entry points. <paramref name="redirect"/> is
    /// where a handoff or point-out to an unattended TCP lands (<see cref="ConsolidationRedirect"/>); null when no
    /// host answers attendance, and then nothing redirects. Returns <see langword="null"/> when the parsed command
    /// is not one this method dispatches, so callers can fall through to their own logic.
    /// </summary>
    public static CommandResult? Dispatch(
        ParsedCommand parsed,
        AircraftState ac,
        TrackOwner? identity,
        SimScenarioState scenario,
        ConsolidationRedirect? redirect
    )
    {
        if (identity is null && RequiresIdentity(parsed))
        {
            return new CommandResult(false, "No active position — use AS to set one");
        }

        var starsConfig = scenario.ArtccConfig?.GetStarsConfigForFacility(scenario.StudentPosition?.FacilityId ?? "");
        int maxScratchpad = ScratchpadRuleEngine.MaxScratchpadLength(starsConfig);

        return parsed switch
        {
            TrackAircraftCommand t => HandleTrack(ac, t.TcpCode, identity, scenario),
            DropTrackCommand => HandleDrop(ac),
            InitiateHandoffCommand ho => ApplyHandoff(ac, scenario, identity, ho.TcpCode, redirect),
            ForceHandoffCommand hof => ApplyForceHandoff(ac, scenario, hof.TcpCode),
            AcceptHandoffCommand => HandleAccept(ac, scenario),
            CancelHandoffCommand => HandleCancel(ac),
            PointOutCommand po when po.TcpCode is not null => ApplyPointOut(ac, scenario, po.TcpCode, redirect),
            PointOutCommand => HandlePointOutNoArgs(ac, identity!),
            AcknowledgeCommand => HandleAcknowledge(ac),
            RejectPointoutCommand => HandleRejectPointout(ac),
            RetractPointoutCommand => HandleRetractPointout(ac),
            ConvertPointoutCommand => ApplyConvertPointout(ac, scenario),
            ForceQuicklookCommand fql => ApplyForceQuicklook(ac, scenario, fql.TcpCodes),
            ForceQuicklookClearCommand fqlc => ApplyForceQuicklookClear(ac, scenario, fqlc.TcpCode),
            PilotReportedAltitudeCommand pra => HandlePilotReportedAltitude(ac, pra.AltitudeHundreds),
            LeaderDirectionCommand ldr => HandleLeaderDirection(ac, ldr.Direction),
            JRingCommand jr => HandleJRing(ac, jr.Enable, jr.Size),
            ConeCommand cone => HandleCone(ac, cone.Enable, cone.Size),
            Scratchpad1Command sp1 => HandleScratchpad1(ac, sp1.Text, maxScratchpad),
            Scratchpad2Command sp2 => HandleScratchpad2(ac, sp2.Text, maxScratchpad),
            AsdexEditCommand asdexEdit => HandleAsdexEdit(ac, asdexEdit.Field, asdexEdit.Text),
            AsdexVerbCommand asdexVerb => HandleAsdexVerb(ac, asdexVerb.Verb),
            TemporaryAltitudeCommand ta => HandleTemporaryAltitude(ac, ta.AltitudeHundreds),
            CruiseCommand cr => HandleCruise(ac, cr.AltitudeHundreds),
            OnHandoffCommand => HandleOnHandoff(ac),
            InhibitConflictAlertCommand => HandleInhibitConflictAlert(ac),
            SuppressConflictAlertCommand sup => HandleSuppressConflictAlert(ac, sup.OtherCallsign),
            InhibitDuplicateBeaconCommand => HandleInhibitDuplicateBeacon(ac),
            // AcknowledgeConflictAlertCommand mutates engine-level ConflictAlerts, which this per-aircraft method never
            // sees: callers dispatch it to AcknowledgeConflictAlert before reaching here.
            _ => null,
        };
    }

    /// <summary>
    /// The position-scoped track verbs — <c>ACCEPTALL</c> (every handoff offered to the issuing position) and
    /// <c>HOALL</c> (every track the issuing position owns and is not already handing off, to one TCP). Both need an
    /// identity; neither names an aircraft.
    /// </summary>
    public static CommandResult DispatchGlobal(ParsedCommand cmd, SimulationWorld world, SimScenarioState scenario, TrackOwner? identity)
    {
        if (identity is null)
        {
            return new CommandResult(false, "No active position — use AS to set one");
        }

        var snapshot = world.GetSnapshot();
        if (cmd is AcceptAllHandoffsCommand)
        {
            int count = 0;
            foreach (var ac in snapshot)
            {
                if ((ac.Track.HandoffPeer is not null) && (ac.Track.HandoffPeer.Callsign == identity.Callsign))
                {
                    var previousOwner = ac.Track.Owner;
                    ac.Track.Owner = ac.Track.HandoffPeer;
                    ac.Track.HandoffPeer = null;
                    ac.Track.HandoffInitiatedAt = null;
                    ac.Track.HandoffRedirectedBy = null;
                    ac.Track.HandoffAccepted = true;
                    MarkPreviousOwnerRetained(ac, previousOwner, scenario);
                    MarkRecentHandoffAccepted(ac, previousOwner, wasForced: false, scenario);
                    count++;
                }
            }

            return new CommandResult(true, $"Accepted {count} handoff(s)");
        }

        if (cmd is InitiateHandoffAllCommand hoAll)
        {
            var target = TrackResolver.ResolveTcpToOwner(scenario, hoAll.TcpCode);
            if (target is null)
            {
                return new CommandResult(false, $"Unknown position: {hoAll.TcpCode}");
            }

            int count = 0;
            foreach (var ac in snapshot)
            {
                if ((ac.Track.Owner is not null) && (ac.Track.Owner.Callsign == identity.Callsign) && (ac.Track.HandoffPeer is null))
                {
                    ac.Track.HandoffPeer = target;
                    ac.Track.HandoffInitiatedAt = scenario.ElapsedSeconds;
                    count++;
                }
            }

            return new CommandResult(true, $"Initiated handoff for {count} aircraft to {hoAll.TcpCode}");
        }

        return new CommandResult(false, "Unknown global track command");
    }

    /// <summary><c>CAACK</c>: acknowledges every unacknowledged conflict alert the aircraft is party to.</summary>
    public static CommandResult AcknowledgeConflictAlert(AircraftState ac, ConflictAlertState conflicts)
    {
        int count = 0;
        foreach (var conflict in conflicts.Conflicts.Values)
        {
            if (((conflict.CallsignA == ac.Callsign) || (conflict.CallsignB == ac.Callsign)) && !conflict.IsAcknowledged)
            {
                conflict.IsAcknowledged = true;
                count++;
            }
        }

        return count > 0
            ? new CommandResult(true, $"Acknowledged {count} conflict alert(s) for {ac.Callsign}")
            : new CommandResult(false, $"No active conflict alerts for {ac.Callsign}");
    }

    /// <summary>
    /// Track commands that need the issuer's identity. Ownership and pointout commands infer the
    /// acting position from track state (owner / handoff peer / pointout recipient or sender), so
    /// they are exempt; the no-arg pointout still needs identity to tell acknowledge from retract.
    /// Used by <see cref="Dispatch"/> to skip the no-active-position guard.
    /// </summary>
    private static bool RequiresIdentity(ParsedCommand parsed) =>
        parsed switch
        {
            // Pointout initiation (target TCP present) infers the sender from the track owner; the
            // no-arg pointout (TcpCode null) still needs identity to disambiguate ack vs retract.
            PointOutCommand po => po.TcpCode is null,
            // Ownership commands infer the acting position from the track's owner / handoff peer.
            DropTrackCommand or InitiateHandoffCommand or AcceptHandoffCommand or CancelHandoffCommand => false,
            // TRACK with a position argument names the owner explicitly, so it needs no active position.
            TrackAircraftCommand { TcpCode: not null } => false,
            // Pointout responses act as the pointout's recipient (ack/reject/convert) or sender (retract);
            // forced-quicklook commands name their TCPs explicitly.
            AcknowledgeCommand
            or RejectPointoutCommand
            or RetractPointoutCommand
            or ConvertPointoutCommand
            or ForceQuicklookCommand
            or ForceQuicklookClearCommand => false,
            // Pure state mutations that never needed identity.
            Scratchpad1Command
            or Scratchpad2Command
            or TemporaryAltitudeCommand
            or CruiseCommand
            or PilotReportedAltitudeCommand
            or LeaderDirectionCommand
            or JRingCommand
            or ConeCommand
            or OnHandoffCommand
            or InhibitConflictAlertCommand
            or SuppressConflictAlertCommand
            or InhibitDuplicateBeaconCommand
            or AcknowledgeConflictAlertCommand
            or AsdexEditCommand
            or AsdexVerbCommand => false,
            // TRACK (claims an unowned track), pointout acknowledge/reject/retract, force handoff.
            _ => true,
        };
}
