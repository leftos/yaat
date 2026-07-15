using Yaat.Sim.Commands;

namespace Yaat.Sim.Pilot;

public static class PilotRequestTracker
{
    public const double NormalFollowUpDelaySeconds = 120.0;
    public const double StandbyFollowUpDelaySeconds = 90.0;

    public static void RecordRequest(
        AircraftState aircraft,
        PilotPendingRequestKind kind,
        double nowSeconds,
        PilotSpeechText line,
        PilotRequestContext context
    )
    {
        var firstRequestedAt =
            aircraft.PendingPilotRequest is { IsOpen: true, Kind: var existingKind } existing && existingKind == kind
                ? existing.FirstRequestedAtSeconds
                : nowSeconds;

        aircraft.PendingPilotRequest = new PilotPendingRequest
        {
            Kind = kind,
            ResponseState = PilotPendingRequestResponseState.None,
            FirstRequestedAtSeconds = firstRequestedAt,
            LastRequestedAtSeconds = nowSeconds,
            NextFollowUpDueSeconds = nowSeconds + NormalFollowUpDelaySeconds,
            LastPilotLine = line.Terminal,
            LastPilotLineTts = line.Tts,
            RunwayId = context.RunwayId,
            FacilityCallName = context.FacilityCallName,
            AirspaceClass = context.AirspaceClass?.ToString(),
            AirspaceIdent = context.AirspaceIdent,
            AirspaceReferencePosition = context.AirspaceReferencePosition,
        };
    }

    public static void ApplyControllerResponse(AircraftState aircraft, CompoundCommand compound, double nowSeconds)
    {
        var pending = aircraft.PendingPilotRequest;
        if (pending is not { IsOpen: true })
        {
            return;
        }

        var sawStandby = false;
        foreach (var command in compound.Blocks.SelectMany(block => block.Commands))
        {
            if (command is AcknowledgePilotContactCommand)
            {
                sawStandby = true;
                continue;
            }

            var response = ResolveResponse(pending.Kind, command);
            switch (response)
            {
                case PilotPendingRequestResponseState.Satisfied:
                case PilotPendingRequestResponseState.Denied:
                case PilotPendingRequestResponseState.Superseded:
                    pending.ResponseState = response;
                    return;
                case PilotPendingRequestResponseState.Standby:
                    sawStandby = true;
                    break;
            }
        }

        if (sawStandby)
        {
            pending.ResponseState = PilotPendingRequestResponseState.Standby;
            pending.NextFollowUpDueSeconds = nowSeconds + StandbyFollowUpDelaySeconds;
        }
    }

    public static bool TryQueueFollowUp(AircraftState aircraft, double nowSeconds)
    {
        var pending = aircraft.PendingPilotRequest;
        if (pending is not { IsOpen: true })
        {
            return false;
        }

        if (nowSeconds < pending.NextFollowUpDueSeconds)
        {
            return false;
        }

        if (aircraft.PendingPilotTransmissions.Count > 0)
        {
            return false;
        }

        // Re-queue both forms independently — the terminal (SAY) form is callsign-free (the SAY
        // column carries the callsign) and the spoken form spells it. RpoTerminal is null for every
        // proactive builder that records a request (only traffic/follow calls produce it).
        PilotResponder.QueueSoloPilotTransmission(
            aircraft,
            new PilotSpeechText(pending.LastPilotLine, pending.LastPilotLineTts),
            PilotTransmissionKind.Proactive,
            PilotResponder.SourceResponse
        );
        pending.ResponseState = PilotPendingRequestResponseState.None;
        pending.LastRequestedAtSeconds = nowSeconds;
        pending.NextFollowUpDueSeconds = nowSeconds + NormalFollowUpDelaySeconds;
        return true;
    }

    private static PilotPendingRequestResponseState ResolveResponse(PilotPendingRequestKind kind, ParsedCommand command) =>
        kind switch
        {
            PilotPendingRequestKind.Taxi => command switch
            {
                PushbackCommand or TaxiCommand or AirTaxiCommand or LandCommand or ClearedTakeoffPresentCommand =>
                    PilotPendingRequestResponseState.Satisfied,
                _ => PilotPendingRequestResponseState.None,
            },
            PilotPendingRequestKind.Takeoff => command switch
            {
                ClearedForTakeoffCommand or ClearedTakeoffPresentCommand => PilotPendingRequestResponseState.Satisfied,
                LineUpAndWaitCommand => PilotPendingRequestResponseState.Superseded,
                _ => PilotPendingRequestResponseState.None,
            },
            PilotPendingRequestKind.Landing => command switch
            {
                ClearedToLandCommand
                or LandAndHoldShortCommand
                or TouchAndGoCommand
                or StopAndGoCommand
                or LowApproachCommand
                or ClearedForOptionCommand
                or GoAroundCommand => PilotPendingRequestResponseState.Satisfied,
                _ => PilotPendingRequestResponseState.None,
            },
            PilotPendingRequestKind.Approach => command switch
            {
                ExpectApproachCommand => PilotPendingRequestResponseState.Standby,
                ClearedApproachCommand
                or ClearedApproachStraightInCommand
                or ClearedVisualApproachCommand
                or PositionTurnAltitudeClearanceCommand
                or JoinApproachCommand
                or JoinApproachStraightInCommand
                or JoinFinalApproachCourseCommand => PilotPendingRequestResponseState.Satisfied,
                _ => PilotPendingRequestResponseState.None,
            },
            PilotPendingRequestKind.AirspaceEntry => command switch
            {
                ClearedBravoAirspaceCommand
                or ContactCommand
                or FrequencyChangeApprovedCommand
                or ClearedApproachCommand
                or ClearedApproachStraightInCommand
                or ClearedVisualApproachCommand
                or PositionTurnAltitudeClearanceCommand
                or DirectToCommand
                or ForceDirectToCommand
                or TurnLeftDirectToCommand
                or TurnRightDirectToCommand
                or EnterLeftDownwindCommand
                or EnterRightDownwindCommand
                or EnterLeftCrosswindCommand
                or EnterRightCrosswindCommand
                or EnterLeftBaseCommand
                or EnterRightBaseCommand
                or EnterFinalCommand
                or MakeLeftTrafficCommand
                or MakeRightTrafficCommand => PilotPendingRequestResponseState.Satisfied,
                _ => PilotPendingRequestResponseState.None,
            },
            _ => PilotPendingRequestResponseState.None,
        };
}
