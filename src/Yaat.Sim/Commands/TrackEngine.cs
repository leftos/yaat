using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Commands;

/// <summary>
/// Pure domain logic for STARS track operations. All methods mutate <see cref="AircraftState"/>
/// directly and return a <see cref="CommandResult"/>. No server-specific dependencies.
/// </summary>
public static class TrackEngine
{
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
                or AcknowledgeConflictAlertCommand
                or InhibitConflictAlertCommand
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
                or HalfStripEditCommand
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
                or CoordinationAutoAckCommand;

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
    public static CommandResult HandleTrack(
        AircraftState ac,
        string? tcpCode,
        TrackOwner? fallbackIdentity,
        SimScenarioState scenario,
        ArtccConfigRoot? artccConfig
    )
    {
        if (tcpCode is not null)
        {
            var owner = TrackResolver.ResolveTcpToOwner(scenario, tcpCode, artccConfig);
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

    public static CommandResult HandlePointOut(AircraftState ac, Tcp targetTcp, Tcp senderTcp)
    {
        if (ac.Track.Owner is null)
        {
            return new CommandResult(false, $"{ac.Callsign} is not tracked");
        }

        if (ac.Track.Pointout is { IsPending: true })
        {
            return new CommandResult(false, $"Pointout already pending for {ac.Callsign}");
        }

        ac.Track.Pointout = new StarsPointout(targetTcp, senderTcp);
        return new CommandResult(true, $"Point out {ac.Callsign} to {targetTcp}");
    }

    public static CommandResult HandlePointOutNoArgs(AircraftState ac, TrackOwner identity)
    {
        if (ac.Track.Pointout is not { IsPending: true })
        {
            return new CommandResult(false, $"No pending pointout for {ac.Callsign}");
        }

        var tcpStr = $"{identity.Subset}{identity.SectorId}";

        if (ac.Track.Pointout.Recipient.ToString() == tcpStr)
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
        ac.Stars.TemporaryAltitude = altHundreds;
        return new CommandResult(true, $"Temp alt: {altHundreds * 100}");
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
        if (ac.Track.Pointout is not { IsPending: true })
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

    public static CommandResult HandlePilotReportedAltitude(AircraftState ac, int altHundreds)
    {
        ac.Stars.PilotReportedAltitude = altHundreds == 0 ? null : altHundreds;
        return new CommandResult(true, $"Pilot reported altitude: {(altHundreds == 0 ? "cleared" : $"{altHundreds * 100}")}");
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
    /// Mirrors yaat-server's <c>TrackCommandHandler.HandleHandoff</c> mutation step
    /// (without the consolidation-redirect logic, which depends on the live
    /// PositionRegistry attendance map). Resolves the target TCP via the scenario,
    /// then writes <c>Track.HandoffPeer</c>.
    /// </summary>
    public static CommandResult ApplyHandoff(AircraftState ac, SimScenarioState scenario, string? tcpCode, ArtccConfigRoot? artccConfig = null)
    {
        if (ac.Track.Owner is null)
        {
            return new CommandResult(false, $"{ac.Callsign} is not tracked");
        }

        TrackOwner? target;
        if (tcpCode is null)
        {
            target = scenario.StudentPosition;
            if (target is null)
            {
                return new CommandResult(false, "No student position configured");
            }
        }
        else
        {
            target = TrackResolver.ResolveTcpToOwner(scenario, tcpCode, artccConfig);
            if (target is null)
            {
                return new CommandResult(false, $"Unknown position: {tcpCode}");
            }
        }

        ac.Track.HandoffPeer = target;
        ac.Track.HandoffInitiatedAt = scenario.ElapsedSeconds;
        return new CommandResult(true, $"Handoff {ac.Callsign} to {tcpCode ?? FormatOwner(target)}");
    }

    /// <summary>
    /// Mirrors yaat-server's <c>TrackCommandHandler.HandleForceHandoff</c>: transfer
    /// ownership to the target TCP without the standard ownership check.
    /// </summary>
    public static CommandResult ApplyForceHandoff(AircraftState ac, SimScenarioState scenario, string tcpCode, ArtccConfigRoot? artccConfig = null)
    {
        var target = TrackResolver.ResolveTcpToOwner(scenario, tcpCode, artccConfig);
        if (target is null)
        {
            return new CommandResult(false, $"Unknown position: {tcpCode}");
        }

        ac.Track.Owner = target;
        ac.Track.HandoffPeer = null;
        ac.Track.HandoffInitiatedAt = null;
        ac.Track.HandoffRedirectedBy = null;
        return new CommandResult(true, $"Force handoff {ac.Callsign} to {tcpCode}");
    }

    /// <summary>
    /// Mirrors yaat-server's <c>TrackCommandHandler.HandlePointOut(... tcpCode)</c>:
    /// resolves the target and sender TCPs (sender = current owner), then delegates to
    /// <see cref="HandlePointOut(AircraftState, Tcp, Tcp)"/>.
    /// </summary>
    public static CommandResult ApplyPointOut(AircraftState ac, SimScenarioState scenario, string tcpCode, ArtccConfigRoot? artccConfig = null)
    {
        if (ac.Track.Owner is null)
        {
            return new CommandResult(false, $"{ac.Callsign} is not tracked");
        }

        var targetTcp = TrackResolver.FindTcpByCode(scenario, tcpCode, artccConfig);
        if (targetTcp is null)
        {
            return new CommandResult(false, $"Unknown position: {tcpCode}");
        }

        // The pointout sender is the track owner — no separate "acting position" needed.
        var senderTcp = TrackResolver.FindTcpForOwner(ac.Track.Owner, scenario);
        if (senderTcp is null)
        {
            return new CommandResult(false, "Cannot determine sender TCP");
        }

        return HandlePointOut(ac, targetTcp, senderTcp);
    }

    /// <summary>
    /// Top-level dispatch for any <see cref="ParsedCommand"/> classified as a track
    /// command (see <see cref="IsTrackCommand"/>). Routes to the appropriate
    /// <c>HandleX</c> / <c>ApplyX</c> with the resolved identity.
    ///
    /// Excludes server-only branches (consolidation-redirect handoff, conflict-alert
    /// state on the engine, ghost-track aircraft creation) — those stay in
    /// yaat-server's <c>TrackCommandHandler</c> and dispatch around this method.
    /// Returns <see langword="null"/> when the parsed command is not a recognised
    /// pure-Sim track command, so callers can fall through to their own logic.
    /// </summary>
    public static CommandResult? Dispatch(
        ParsedCommand parsed,
        AircraftState ac,
        TrackOwner? identity,
        SimScenarioState scenario,
        ArtccConfigRoot? artccConfig = null
    )
    {
        if (identity is null && RequiresIdentity(parsed))
        {
            return new CommandResult(false, "No active position — use AS to set one");
        }

        var starsConfig = artccConfig?.GetStarsConfigForFacility(scenario.StudentPosition?.FacilityId ?? "");
        int maxScratchpad = ScratchpadRuleEngine.MaxScratchpadLength(starsConfig);

        return parsed switch
        {
            TrackAircraftCommand t => HandleTrack(ac, t.TcpCode, identity, scenario, artccConfig),
            DropTrackCommand => HandleDrop(ac),
            InitiateHandoffCommand ho => ApplyHandoff(ac, scenario, ho.TcpCode, artccConfig),
            ForceHandoffCommand hof => ApplyForceHandoff(ac, scenario, hof.TcpCode, artccConfig),
            AcceptHandoffCommand => HandleAccept(ac, scenario),
            CancelHandoffCommand => HandleCancel(ac),
            PointOutCommand po when po.TcpCode is not null => ApplyPointOut(ac, scenario, po.TcpCode, artccConfig),
            PointOutCommand => HandlePointOutNoArgs(ac, identity!),
            AcknowledgeCommand => HandleAcknowledge(ac),
            RejectPointoutCommand => HandleRejectPointout(ac),
            RetractPointoutCommand => HandleRetractPointout(ac),
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
            // Server-only branches: caller dispatches before reaching Dispatch
            // (AcknowledgeConflictAlertCommand mutates engine-level ConflictAlerts).
            _ => null,
        };
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
            // Pointout responses act as the pointout's recipient (ack/reject) or sender (retract).
            AcknowledgeCommand or RejectPointoutCommand or RetractPointoutCommand => false,
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
            or AcknowledgeConflictAlertCommand
            or AsdexEditCommand
            or AsdexVerbCommand => false,
            // TRACK (claims an unowned track), pointout acknowledge/reject/retract, force handoff.
            _ => true,
        };
}
