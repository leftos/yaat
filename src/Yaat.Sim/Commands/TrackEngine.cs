namespace Yaat.Sim.Commands;

/// <summary>
/// Pure domain logic for STARS track operations. All methods mutate <see cref="AircraftState"/>
/// directly and return a <see cref="CommandResult"/>. No server-specific dependencies.
/// </summary>
public static class TrackEngine
{
    public static string FormatOwner(TrackOwner owner)
    {
        var tcp = owner.Subset is not null && owner.SectorId is not null ? $"{owner.Subset}{owner.SectorId}" : "";
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
                or Scratchpad1Command
                or Scratchpad2Command
                or TemporaryAltitudeCommand
                or CruiseCommand
                or OnHandoffCommand
                or SetActivePositionCommand;

    public static bool IsStripCommand(ParsedCommand? cmd) => cmd is StripPushCommand or StripAnnotateCommand;

    public static bool IsCoordinationCommand(ParsedCommand? cmd) =>
        cmd
            is CoordinationReleaseCommand
                or CoordinationHoldCommand
                or CoordinationRecallCommand
                or CoordinationAcknowledgeCommand
                or CoordinationAutoAckCommand;

    public static CommandResult NotOwnedError(AircraftState ac, TrackOwner identity)
    {
        if (ac.Owner is null)
        {
            return new CommandResult(false, $"{ac.Callsign} is not tracked");
        }

        var ownerDisplay = FormatOwner(ac.Owner);
        return new CommandResult(false, $"{ac.Callsign} owned by {ownerDisplay}, not you — use AS to switch position, or HOF to force");
    }

    public static CommandResult HandleTrack(AircraftState ac, TrackOwner identity)
    {
        if (ac.Owner is not null)
        {
            return new CommandResult(false, $"{ac.Callsign} already tracked by {ac.Owner.Callsign}");
        }

        ac.Owner = identity;
        return new CommandResult(true, $"Tracking {ac.Callsign}");
    }

    public static CommandResult HandleDrop(AircraftState ac, TrackOwner identity)
    {
        if (ac.Owner is null || ac.Owner.Callsign != identity.Callsign)
        {
            return NotOwnedError(ac, identity);
        }

        ac.Owner = null;
        ac.HandoffPeer = null;
        ac.HandoffInitiatedAt = null;
        ac.HandoffRedirectedBy = null;
        return new CommandResult(true, $"Dropped {ac.Callsign}");
    }

    public static CommandResult HandleAccept(AircraftState ac, TrackOwner identity)
    {
        if (ac.HandoffPeer is null || ac.HandoffPeer.Callsign != identity.Callsign)
        {
            return new CommandResult(false, $"No pending handoff to you for {ac.Callsign}");
        }

        ac.Owner = ac.HandoffPeer;
        ac.HandoffPeer = null;
        ac.HandoffInitiatedAt = null;
        ac.HandoffRedirectedBy = null;
        ac.HandoffAccepted = true;
        return new CommandResult(true, $"Accepted {ac.Callsign}");
    }

    public static CommandResult HandleCancel(AircraftState ac, TrackOwner identity)
    {
        if (ac.Owner is null || ac.Owner.Callsign != identity.Callsign || ac.HandoffPeer is null)
        {
            return new CommandResult(false, $"No pending outbound handoff for {ac.Callsign}");
        }

        ac.HandoffPeer = null;
        ac.HandoffInitiatedAt = null;
        ac.HandoffRedirectedBy = null;
        return new CommandResult(true, $"Cancelled handoff for {ac.Callsign}");
    }

    public static CommandResult HandleAcknowledge(AircraftState ac, TrackOwner identity)
    {
        if (ac.Pointout is null || !ac.Pointout.IsPending)
        {
            return new CommandResult(false, $"No pending pointout for {ac.Callsign}");
        }

        if (ac.Pointout.Recipient.ToString() != $"{identity.Subset}{identity.SectorId}")
        {
            return new CommandResult(false, "Pointout not directed at you");
        }

        ac.Pointout.Status = StarsPointoutStatus.Accepted;
        return new CommandResult(true, $"Acknowledged {ac.Callsign}");
    }

    public static CommandResult HandleScratchpad1(AircraftState ac, string text)
    {
        ac.Scratchpad1 = text;
        ac.WasScratchpad1Cleared = string.IsNullOrEmpty(text);
        return new CommandResult(true, $"SP1: {text}");
    }

    public static CommandResult HandleScratchpad2(AircraftState ac, string text)
    {
        ac.Scratchpad2 = text;
        return new CommandResult(true, $"SP2: {text}");
    }

    public static CommandResult HandleTemporaryAltitude(AircraftState ac, int altHundreds)
    {
        ac.TemporaryAltitude = altHundreds;
        return new CommandResult(true, $"Temp alt: {altHundreds * 100}");
    }

    public static CommandResult HandleCruise(AircraftState ac, int altHundreds)
    {
        ac.CruiseAltitude = altHundreds * 100;
        return new CommandResult(true, $"Cruise: {altHundreds * 100}");
    }

    public static CommandResult HandleOnHandoff(AircraftState ac)
    {
        ac.OnHandoff = !ac.OnHandoff;
        var state = ac.OnHandoff ? "on" : "off";
        return new CommandResult(true, $"On-handoff {state} for {ac.Callsign}");
    }
}
