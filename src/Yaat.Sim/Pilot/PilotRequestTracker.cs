using Yaat.Sim.Commands;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;

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
        double firstRequestedAt =
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
            ParkingName = context.ParkingName,
        };
    }

    public static void ApplyControllerResponse(AircraftState aircraft, CompoundCommand compound, double nowSeconds)
    {
        PilotPendingRequest? pending = aircraft.PendingPilotRequest;
        if (pending is not { IsOpen: true })
        {
            return;
        }

        bool sawStandby = false;
        foreach (ParsedCommand? command in compound.Blocks.SelectMany(block => block.Commands))
        {
            if (command is AcknowledgePilotContactCommand)
            {
                sawStandby = true;
                continue;
            }

            PilotPendingRequestResponseState response = ResolveResponse(pending.Kind, command);
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

    /// <summary>
    /// Answers the aircraft's open request of <paramref name="kind"/> by an action that is not an aircraft command, so it
    /// never passes <see cref="ApplyControllerResponse"/>: a release (<c>REL</c>, <c>HFROFF</c>) answers a
    /// <see cref="PilotPendingRequestKind.Release"/> request, a PDC sent by data link (<c>TDLSS</c>) a
    /// <see cref="PilotPendingRequestKind.Clearance"/> request. An open request of any other kind is left alone.
    /// </summary>
    /// <returns>True when an open request of <paramref name="kind"/> was marked satisfied.</returns>
    public static bool SatisfyOpenRequest(AircraftState aircraft, PilotPendingRequestKind kind)
    {
        if ((aircraft.PendingPilotRequest is not { IsOpen: true } request) || (request.Kind != kind))
        {
            return false;
        }

        request.ResponseState = PilotPendingRequestResponseState.Satisfied;
        return true;
    }

    public static bool TryQueueFollowUp(AircraftState aircraft, double nowSeconds)
    {
        PilotPendingRequest? pending = aircraft.PendingPilotRequest;
        if (pending is not { IsOpen: true })
        {
            return false;
        }

        if (IsMoot(pending, aircraft))
        {
            pending.ResponseState = PilotPendingRequestResponseState.Superseded;
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

    /// <summary>
    /// Whether an open request no longer needs its follow-up because the aircraft has moved past it, so it is closed
    /// rather than re-announced.
    /// </summary>
    private static bool IsMoot(PilotPendingRequest request, AircraftState aircraft)
    {
        // A ready-to-taxi / ready-for-departure call is only meaningful on the surface. Once the
        // aircraft is airborne the request is moot however it got resolved, so close it rather than
        // re-announce "holding short runway 28R, ready for departure" from 3000 ft.
        bool surfaceRequest =
            request.Kind
            is PilotPendingRequestKind.Taxi
                or PilotPendingRequestKind.Takeoff
                or PilotPendingRequestKind.Clearance
                or PilotPendingRequestKind.Release;
        if (surfaceRequest && !aircraft.IsOnGround)
        {
            return true;
        }

        // A ready-to-taxi or taxi-in follow-up is only voiced while the aircraft is stopped and waiting on a
        // clearance. An aircraft that is taxiing, following, holding short, or crossing a runway is executing
        // one, so the request is moot and re-announcing "at parking GA16 … ready to taxi" from the taxiway is
        // wrong. A minimal harness with no active phase is left alone.
        return (request.Kind is PilotPendingRequestKind.Taxi or PilotPendingRequestKind.Clearance)
            && (aircraft.Phases?.CurrentPhase is { } phase)
            && !IsWaitingForTaxi(phase);
    }

    /// <summary>
    /// The ground phases in which a pilot still legitimately awaits a taxi clearance: parked at a stand
    /// (<see cref="AtParkingPhase"/>), holding after pushback (<see cref="HoldingAfterPushbackPhase"/>), stopped
    /// after a runway exit for the taxi-in call (<see cref="HoldingAfterExitPhase"/>), or holding in position
    /// (<see cref="HoldingInPositionPhase"/>), or holding short of a taxiway or spot bar (<see cref="HoldingShortPhase"/>
    /// at a bar that protects no runway), where a spawn's preset taxi can end and its delayed call opens the request. Any
    /// other ground phase means the aircraft is already moving on one. Asked only of an open Taxi or Clearance request.
    /// </summary>
    private static bool IsWaitingForTaxi(Phase phase) =>
        phase
            is AtParkingPhase
                or HoldingAfterPushbackPhase
                or HoldingAfterExitPhase
                or HoldingInPositionPhase
                or HoldingShortPhase { ProtectsARunway: false };

    private static PilotPendingRequestResponseState ResolveResponse(PilotPendingRequestKind kind, ParsedCommand command) =>
        kind switch
        {
            PilotPendingRequestKind.Taxi => command switch
            {
                PushbackCommand
                or PushbackMultiCommand
                or TaxiCommand
                or TaxiAutoCommand
                or AirTaxiCommand
                or LandCommand
                or ClearedTakeoffPresentCommand
                // FOLLOWG clears the aircraft off its stand (AtParkingPhase.CanAcceptCommand): the pilot is
                // moving on a ground clearance now, so the ready-to-taxi request is answered.
                or FollowGroundCommand => PilotPendingRequestResponseState.Satisfied,
                _ => PilotPendingRequestResponseState.None,
            },
            // A delivery controller's clearance ends with the beacon code, IFR and VFR alike. A PDC sent by data link (TDLSS)
            // also clears an IFR departure, but only once it is actually sent: TdlsCommandHandler.HandleSend answers the request
            // then, so no TDLSS arm belongs here. A release (REL) is a group command, answered in HeldReleaseService.
            PilotPendingRequestKind.Clearance => command switch
            {
                SquawkCommand or RandomSquawkCommand or SquawkVfrCommand => PilotPendingRequestResponseState.Satisfied,
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
                or GoAroundCommand
                // "LEFT/RIGHT CLOSED TRAFFIC APPROVED" (7110.65 3-10-11) is the grant for the
                // "request closed traffic" call PatternEntryPhase records as a Landing request; in
                // YAAT that is MLT/MRT. Without it the approved pilot re-announces the request every
                // 120 s. Mirrors the AirspaceEntry arm below.
                or MakeLeftTrafficCommand
                or MakeRightTrafficCommand => PilotPendingRequestResponseState.Satisfied,
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
            // A frequency change answers the request too (the next controller takes it up), but it is not a clearance:
            // the implied-clearance set is the one ImplicitBravoClearance grants on.
            PilotPendingRequestKind.AirspaceEntry => command switch
            {
                ClearedBravoAirspaceCommand or ContactCommand or FrequencyChangeApprovedCommand => PilotPendingRequestResponseState.Satisfied,
                _ when ImplicitBravoClearance.IsImpliedClearanceCommand(command) => PilotPendingRequestResponseState.Satisfied,
                _ => PilotPendingRequestResponseState.None,
            },
            _ => PilotPendingRequestResponseState.None,
        };
}
