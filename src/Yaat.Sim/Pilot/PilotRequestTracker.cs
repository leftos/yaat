using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Phases.Ground;

namespace Yaat.Sim.Pilot;

public static class PilotRequestTracker
{
    private static readonly ILogger Log = SimLog.CreateLogger("PilotRequestTracker");

    public const double NormalFollowUpDelaySeconds = 120.0;
    public const double StandbyFollowUpDelaySeconds = 90.0;
    public const double FurtherClearanceFollowUpDelaySeconds = 300.0;

    private static double FollowUpDelaySeconds(PilotPendingRequestKind kind) =>
        kind == PilotPendingRequestKind.FurtherClearance ? FurtherClearanceFollowUpDelaySeconds : NormalFollowUpDelaySeconds;

    private static double StandbyDelaySeconds(PilotPendingRequestKind kind) =>
        kind == PilotPendingRequestKind.FurtherClearance ? FurtherClearanceFollowUpDelaySeconds : StandbyFollowUpDelaySeconds;

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
            NextFollowUpDueSeconds = nowSeconds + FollowUpDelaySeconds(kind),
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
                    MarkClearanceObtained(aircraft, pending.Kind, response, nowSeconds);
                    return;
                case PilotPendingRequestResponseState.Standby:
                    sawStandby = true;
                    break;
            }
        }

        if (sawStandby)
        {
            pending.ResponseState = PilotPendingRequestResponseState.Standby;
            pending.NextFollowUpDueSeconds = nowSeconds + StandbyDelaySeconds(pending.Kind);
        }
    }

    /// <summary>
    /// Answers the aircraft's open request of <paramref name="kind"/> by an action that is not an aircraft command, so it
    /// never passes <see cref="ApplyControllerResponse"/>: a release (<c>REL</c>, <c>HFROFF</c>) answers a
    /// <see cref="PilotPendingRequestKind.Release"/> request, a PDC sent by data link (<c>TDLSS</c>) a
    /// <see cref="PilotPendingRequestKind.Clearance"/> request. An open request of any other kind is left alone.
    /// </summary>
    /// <returns>True when an open request of <paramref name="kind"/> was marked satisfied.</returns>
    public static bool SatisfyOpenRequest(AircraftState aircraft, PilotPendingRequestKind kind, double nowSeconds)
    {
        if ((aircraft.PendingPilotRequest is not { IsOpen: true } request) || (request.Kind != kind))
        {
            return false;
        }

        request.ResponseState = PilotPendingRequestResponseState.Satisfied;
        MarkClearanceObtained(aircraft, kind, PilotPendingRequestResponseState.Satisfied, nowSeconds);
        return true;
    }

    /// <summary>
    /// A satisfied <see cref="PilotPendingRequestKind.Clearance"/> request gives the departure its clearance, stamped at
    /// <paramref name="nowSeconds"/> (<see cref="AircraftGroundOps.ClearanceObtainedAtSeconds"/>); a later answer keeps the first stamp.
    /// </summary>
    private static void MarkClearanceObtained(
        AircraftState aircraft,
        PilotPendingRequestKind kind,
        PilotPendingRequestResponseState response,
        double nowSeconds
    )
    {
        if ((kind == PilotPendingRequestKind.Clearance) && (response == PilotPendingRequestResponseState.Satisfied))
        {
            aircraft.Ground.ClearanceObtainedAtSeconds ??= nowSeconds;
        }
    }

    /// <summary>
    /// Re-voices the aircraft's open request once its follow-up is due, or closes it as moot. <paramref name="studentPosition"/>
    /// is the student's position, against which the pilot's tuned position is read.
    /// </summary>
    public static bool TryQueueFollowUp(AircraftState aircraft, double nowSeconds, TrackOwner? studentPosition)
    {
        PilotPendingRequest? pending = aircraft.PendingPilotRequest;
        if (pending is not { IsOpen: true })
        {
            return false;
        }

        if (IsMoot(pending, aircraft, studentPosition))
        {
            pending.ResponseState = PilotPendingRequestResponseState.Superseded;
            return false;
        }

        if ((nowSeconds < pending.NextFollowUpDueSeconds) || WaitsForTheHoldReport(pending, aircraft))
        {
            return false;
        }

        if (aircraft.PendingPilotTransmissions.Count > 0)
        {
            return false;
        }

        RefreshFurtherClearanceLine(pending, aircraft);

        // Re-queue both forms independently — the terminal (SAY) form is callsign-free (the SAY
        // column carries the callsign) and the spoken form spells it. RpoTerminal is null for every
        // proactive builder that records a request (only traffic/follow calls produce it).
        PilotResponder.QueueSoloPilotTransmission(
            aircraft,
            new PilotSpeechText(pending.LastPilotLine, pending.LastPilotLineTts),
            PilotTransmissionKind.Proactive,
            initialContact: false
        );
        pending.ReaskedAfterStandby = pending.ResponseState == PilotPendingRequestResponseState.Standby;
        pending.ResponseState = PilotPendingRequestResponseState.None;
        pending.LastRequestedAtSeconds = nowSeconds;
        pending.NextFollowUpDueSeconds = nowSeconds + FollowUpDelaySeconds(pending.Kind);
        return true;
    }

    /// <summary>
    /// Whether a request for further clearance waits, still open, for the aircraft to reach its clearance limit: the
    /// five-minute call is not repeated on the direct, and the holding report there records the request re-asked after.
    /// </summary>
    private static bool WaitsForTheHoldReport(PilotPendingRequest request, AircraftState aircraft) =>
        (request.Kind == PilotPendingRequestKind.FurtherClearance)
        && (
            PilotProactive.IsFlyingClearanceLimitDirect(aircraft)
            || (aircraft.Phases?.CurrentPhase is HoldingPatternPhase { IsClearanceLimitHold: true, ClearanceLimitReported: false })
        );

    /// <summary>
    /// Rebuilds the re-ask of a request for further clearance from the clearance-limit hold, so that it states the
    /// altitude in force now rather than the one the hold began at.
    /// </summary>
    private static void RefreshFurtherClearanceLine(PilotPendingRequest pending, AircraftState aircraft)
    {
        if (
            (pending.Kind != PilotPendingRequestKind.FurtherClearance)
            || (aircraft.Phases?.CurrentPhase is not HoldingPatternPhase { IsClearanceLimitHold: true } hold)
        )
        {
            return;
        }

        PilotSpeechText line = PilotResponder.BuildHoldingFurtherClearanceRequest(aircraft, pending.FacilityCallName ?? "", hold.FixName);
        Log.LogDebug("{Callsign}: re-asks for further clearance holding over {Airport}: {Line}", aircraft.Callsign, hold.FixName, line.Terminal);
        pending.LastPilotLine = line.Terminal;
        pending.LastPilotLineTts = line.Tts;
    }

    private static bool IsFurtherClearanceMoot(PilotPendingRequest request, AircraftState aircraft) =>
        (request.Kind == PilotPendingRequestKind.FurtherClearance)
        && (aircraft.FlightPlan.IsVfr || !PilotProactive.IsAwaitingFurtherClearance(aircraft));

    /// <summary>
    /// Whether an open request no longer needs its follow-up because the aircraft has moved past it, so it is closed
    /// rather than re-announced.
    /// </summary>
    private static bool IsMoot(PilotPendingRequest request, AircraftState aircraft, TrackOwner? studentPosition)
    {
        // A ready-to-taxi / ready-for-departure call is only meaningful on the surface. Once the
        // aircraft is airborne the request is moot however it got resolved, so close it rather than
        // re-announce "holding short runway 28R, ready for departure" from 3000 ft.
        bool surfaceRequest =
            request.Kind
            is PilotPendingRequestKind.Taxi
                or PilotPendingRequestKind.Pushback
                or PilotPendingRequestKind.Takeoff
                or PilotPendingRequestKind.Clearance
                or PilotPendingRequestKind.Release;
        if (surfaceRequest && !aircraft.IsOnGround)
        {
            return true;
        }

        // A request for lower is moot once the pilot would not ask it: a descent authorised or queued, a climb, a
        // handoff, a hold, or an approach or landing clearance.
        if ((request.Kind == PilotPendingRequestKind.Descent) && PilotProactive.ShouldNotAskForLower(aircraft, studentPosition))
        {
            return true;
        }

        // A request for further clearance is moot once a lateral clearance has taken the aircraft off both the direct to
        // its clearance limit and the hold there, or once the flight is VFR (IFR cancelled).
        if (IsFurtherClearanceMoot(request, aircraft))
        {
            return true;
        }

        // A ready-to-taxi or taxi-in follow-up is only voiced while the aircraft is stopped and waiting on a
        // clearance. An aircraft that is taxiing, following, holding short, or crossing a runway is executing
        // one, so the request is moot and re-announcing "at parking GA16 … ready to taxi" from the taxiway is
        // wrong. A minimal harness with no active phase is left alone.
        return (request.Kind is PilotPendingRequestKind.Taxi or PilotPendingRequestKind.Pushback or PilotPendingRequestKind.Clearance)
            && (aircraft.Phases?.CurrentPhase is { } phase)
            && !IsWaitingForTaxi(phase);
    }

    /// <summary>
    /// The ground phases in which a pilot still legitimately awaits a taxi clearance: parked at a stand
    /// (<see cref="AtParkingPhase"/>), holding after pushback (<see cref="HoldingAfterPushbackPhase"/>), stopped
    /// after a runway exit for the taxi-in call (<see cref="HoldingAfterExitPhase"/>), or holding in position
    /// (<see cref="HoldingInPositionPhase"/>), or holding short of a taxiway or spot bar (<see cref="HoldingShortPhase"/>
    /// at a bar that protects no runway), where a spawn's preset taxi can end and its delayed call opens the request. Any
    /// other ground phase means the aircraft is already moving on one. Asked only of an open Taxi, Pushback or Clearance
    /// request.
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
            // A pushback request is answered by the push, and as a ready-to-taxi request is by anything else that clears the
            // aircraft off its stand: a TAXI straight off a push-back stand answers it too.
            PilotPendingRequestKind.Taxi or PilotPendingRequestKind.Pushback => command switch
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
            // Any descent answers a request for lower: an altitude, a descend via, a crossing, or an approach clearance. A
            // frequency change hands the request to the next controller and a hold replaces it. A STBY reaches Standby
            // through AcknowledgePilotContactCommand; anything else leaves the request open.
            PilotPendingRequestKind.Descent => command switch
            {
                ContactCommand or FrequencyChangeApprovedCommand or HoldingPatternCommand => PilotPendingRequestResponseState.Superseded,
                ExpediteCommand { Altitude: not null } => PilotPendingRequestResponseState.Satisfied,
                DescendMaintainCommand
                or ClimbMaintainCommand
                or ForceAltitudeCommand
                or DescendViaCommand
                or CrossFixCommand
                or ConstrainedForceDirectToCommand
                or ClearedApproachCommand
                or ClearedApproachStraightInCommand
                or ClearedVisualApproachCommand
                or PositionTurnAltitudeClearanceCommand
                or JoinApproachCommand
                or JoinApproachStraightInCommand
                or JoinFinalApproachCourseCommand => PilotPendingRequestResponseState.Satisfied,
                _ => PilotPendingRequestResponseState.None,
            },
            PilotPendingRequestKind.FurtherClearance => command switch
            {
                ContactCommand or FrequencyChangeApprovedCommand => PilotPendingRequestResponseState.Superseded,
                _ => PilotPendingRequestResponseState.None,
            },
            _ => PilotPendingRequestResponseState.None,
        };
}
