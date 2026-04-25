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
                or SetActivePositionCommand;

    public static bool IsStripCommand(ParsedCommand? cmd) =>
        cmd
            is StripMoveCommand
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
                or BlankCreateCommand
                or BlankDeleteCommand;

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

    public static CommandResult HandleDrop(AircraftState ac, TrackOwner identity)
    {
        if (ac.Track.Owner is null || !ac.Track.Owner.MatchesPosition(identity))
        {
            return NotOwnedError(ac, identity);
        }

        ac.Track.Owner = null;
        ac.Track.HandoffPeer = null;
        ac.Track.HandoffInitiatedAt = null;
        ac.Track.HandoffRedirectedBy = null;
        return new CommandResult(true, $"Dropped {ac.Callsign}");
    }

    public static CommandResult HandleAccept(AircraftState ac, TrackOwner identity)
    {
        if (ac.Track.HandoffPeer is null || !ac.Track.HandoffPeer.MatchesPosition(identity))
        {
            return new CommandResult(false, $"No pending handoff to you for {ac.Callsign}");
        }

        ac.Track.Owner = ac.Track.HandoffPeer;
        ac.Track.HandoffPeer = null;
        ac.Track.HandoffInitiatedAt = null;
        ac.Track.HandoffRedirectedBy = null;
        ac.Track.HandoffAccepted = true;
        return new CommandResult(true, $"Accepted {ac.Callsign}");
    }

    public static CommandResult HandleCancel(AircraftState ac, TrackOwner identity)
    {
        if (ac.Track.Owner is null || !ac.Track.Owner.MatchesPosition(identity) || ac.Track.HandoffPeer is null)
        {
            return new CommandResult(false, $"No pending outbound handoff for {ac.Callsign}");
        }

        ac.Track.HandoffPeer = null;
        ac.Track.HandoffInitiatedAt = null;
        ac.Track.HandoffRedirectedBy = null;
        return new CommandResult(true, $"Cancelled handoff for {ac.Callsign}");
    }

    public static CommandResult HandleAcknowledge(AircraftState ac, TrackOwner identity)
    {
        if (ac.Track.Pointout is null || !ac.Track.Pointout.IsPending)
        {
            return new CommandResult(false, $"No pending pointout for {ac.Callsign}");
        }

        if (ac.Track.Pointout.Recipient.ToString() != $"{identity.Subset}{identity.SectorId}")
        {
            return new CommandResult(false, "Pointout not directed at you");
        }

        ac.Track.Pointout.Status = StarsPointoutStatus.Accepted;
        return new CommandResult(true, $"Acknowledged {ac.Callsign}");
    }

    public static CommandResult HandlePointOut(AircraftState ac, TrackOwner identity, Tcp targetTcp, Tcp senderTcp)
    {
        if (ac.Track.Owner is null || !ac.Track.Owner.MatchesPosition(identity))
        {
            return NotOwnedError(ac, identity);
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
            ac.Track.Pointout.Status = StarsPointoutStatus.Accepted;
            return new CommandResult(true, $"Acknowledged {ac.Callsign}");
        }

        if (ac.Track.Pointout.Sender.ToString() == tcpStr)
        {
            ac.Track.Pointout = null;
            return new CommandResult(true, $"Retracted pointout for {ac.Callsign}");
        }

        return new CommandResult(false, $"No pending pointout for {ac.Callsign}");
    }

    public static CommandResult HandleScratchpad1(AircraftState ac, string text)
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

        ac.Stars.PreviousScratchpad1 = ac.Stars.Scratchpad1;
        ac.Stars.Scratchpad1 = text;
        ac.Stars.WasScratchpad1Cleared = isClearing;
        return new CommandResult(true, $"SP1: {text}");
    }

    public static CommandResult HandleScratchpad2(AircraftState ac, string text)
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
        ac.FlightPlan.CruiseAltitude = altHundreds * 100;
        return new CommandResult(true, $"Cruise: {altHundreds * 100}");
    }

    public static CommandResult HandleOnHandoff(AircraftState ac)
    {
        ac.Track.OnHandoff = !ac.Track.OnHandoff;
        var state = ac.Track.OnHandoff ? "on" : "off";
        return new CommandResult(true, $"On-handoff {state} for {ac.Callsign}");
    }

    public static CommandResult HandleRejectPointout(AircraftState ac, TrackOwner identity)
    {
        if (ac.Track.Pointout is null || !ac.Track.Pointout.IsPending)
        {
            return new CommandResult(false, $"No pending pointout for {ac.Callsign}");
        }

        if (ac.Track.Pointout.Recipient.ToString() != $"{identity.Subset}{identity.SectorId}")
        {
            return new CommandResult(false, "Pointout not directed at you");
        }

        ac.Track.Pointout.Status = StarsPointoutStatus.Rejected;
        return new CommandResult(true, $"Rejected pointout for {ac.Callsign}");
    }

    public static CommandResult HandleRetractPointout(AircraftState ac, TrackOwner identity)
    {
        if (ac.Track.Pointout is not { IsPending: true })
        {
            return new CommandResult(false, $"No pending pointout for {ac.Callsign}");
        }

        var tcpStr = $"{identity.Subset}{identity.SectorId}";
        if (ac.Track.Pointout.Sender.ToString() != tcpStr)
        {
            return new CommandResult(false, "You are not the pointout sender");
        }

        ac.Track.Pointout = null;
        return new CommandResult(true, $"Retracted pointout for {ac.Callsign}");
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

    public static CommandResult HandleJRing(AircraftState ac, bool enable)
    {
        ac.Stars.TpaType = enable ? TpaJRing : null;
        return new CommandResult(true, $"J-Ring {(enable ? "on" : "off")} for {ac.Callsign}");
    }

    public static CommandResult HandleCone(AircraftState ac, bool enable)
    {
        ac.Stars.TpaType = enable ? TpaCone : null;
        return new CommandResult(true, $"Cone {(enable ? "on" : "off")} for {ac.Callsign}");
    }
}
