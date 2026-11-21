using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airspace;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Phases.Ground;

namespace Yaat.Sim.Pilot;

public static class PilotRequestTracker
{
    private static readonly ILogger Log = SimLog.CreateLogger("PilotRequestTracker");

    public const double NormalFollowUpDelaySeconds = 120.0;

    /// <summary>
    /// The re-ask delay after a "standby", longer than after silence: the wait was acknowledged, and the pilot re-calls only
    /// when the delay is lengthy (P/CG STAND BY).
    /// </summary>
    public const double StandbyFollowUpDelaySeconds = 150.0;
    public const double FurtherClearanceFollowUpDelaySeconds = 300.0;

    /// <summary>
    /// The re-asks of one unanswered request for lower or approach request the pilot voices before it waits for the
    /// controller (<see cref="PilotPendingRequest.IsCapped"/>). Other kinds re-ask without a cap.
    /// </summary>
    public const int MaxFollowUps = 2;

    private static double FollowUpDelaySeconds(PilotPendingRequestKind kind) =>
        kind == PilotPendingRequestKind.FurtherClearance ? FurtherClearanceFollowUpDelaySeconds : NormalFollowUpDelaySeconds;

    private static double StandbyDelaySeconds(PilotPendingRequestKind kind) =>
        kind == PilotPendingRequestKind.FurtherClearance ? FurtherClearanceFollowUpDelaySeconds : StandbyFollowUpDelaySeconds;

    /// <summary>Records <paramref name="kind"/> as the aircraft's open request, replacing any other, and returns it.</summary>
    public static PilotPendingRequest RecordRequest(
        AircraftState aircraft,
        PilotPendingRequestKind kind,
        double nowSeconds,
        PilotSpeechText line,
        PilotRequestContext context
    )
    {
        double firstRequestedAt =
            ((aircraft.PendingPilotRequest is { IsOpen: true, Kind: var existingKind } existing) && (existingKind == kind))
                ? existing.FirstRequestedAtSeconds
                : nowSeconds;

        PilotPendingRequest request = new()
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
            TaxiIn = context.IsTaxiIn,
        };
        aircraft.PendingPilotRequest = request;
        return request;
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

            PilotPendingRequestResponseState response = ResolveResponse(pending, aircraft, command);
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
    /// Stamps <see cref="AircraftState.DescentClearedAtSeconds"/> when the compound carries a descent clearance, whether or
    /// not the pilot asked for lower: <see cref="PilotProactive.TickDescentRequest"/> waits after it before asking. It also
    /// clears the aircraft's memory of a capped request for lower (<see cref="AircraftState.DescentRequestCap"/>). Called
    /// for the student's or a controller AI's accepted command once the aircraft has made its initial contact
    /// (<c>SimulationEngine.AnswerAcceptedCommand</c>); scripted presets never stamp.
    /// </summary>
    public static void StampDescentClearance(AircraftState aircraft, CompoundCommand compound, double nowSeconds)
    {
        if (compound.Blocks.SelectMany(block => block.Commands).Any(command => IsDescentClearance(command, aircraft.Altitude)))
        {
            aircraft.DescentClearedAtSeconds = nowSeconds;
            aircraft.DescentRequestCap = DescentRequestCap.None;
            aircraft.DescentRequestCappedAtSeconds = null;
        }
    }

    /// <summary>
    /// Stamps <see cref="AircraftState.ExpectedApproachAdvisedAtSeconds"/> when the compound tells the pilot what approach to
    /// expect (EAPP), so the pilot does not request an approach assignment; a later EAPP restamps it. An EAPP followed by a
    /// destination change (APT) later in the same compound is not advice for the new destination and does not stamp. Called
    /// for the student's or a controller AI's accepted command (<c>SimulationEngine.AnswerAcceptedCommand</c>); the
    /// scenario's seeded expected approach and scripted presets never stamp.
    /// </summary>
    public static void StampExpectedApproach(AircraftState aircraft, CompoundCommand compound, double nowSeconds)
    {
        bool advised = false;
        foreach (ParsedCommand? command in compound.Blocks.SelectMany(block => block.Commands))
        {
            if (command is ExpectApproachCommand)
            {
                advised = true;
            }
            else if (command is ChangeDestinationCommand)
            {
                advised = false;
            }
        }

        if (advised)
        {
            aircraft.ExpectedApproachAdvisedAtSeconds = nowSeconds;
        }
    }

    /// <summary>
    /// Whether <paramref name="command"/> clears the aircraft lower: an altitude command to below
    /// <paramref name="altitudeFt"/>, or one of the descents that answer a request for lower and name no altitude (a descend
    /// via, a constrained direct, an approach clearance).
    /// </summary>
    private static bool IsDescentClearance(ParsedCommand command, double altitudeFt) =>
        CommandAltitudeFt(command) is { } targetFt
            ? (targetFt < (altitudeFt - FlightPhysics.AltitudeSnapFt))
            : ResolveResponse(PilotPendingRequestKind.Descent, command) == PilotPendingRequestResponseState.Satisfied;

    private static int? CommandAltitudeFt(ParsedCommand command) =>
        command switch
        {
            DescendMaintainCommand descend => descend.Altitude,
            ClimbMaintainCommand climb => climb.Altitude,
            ForceAltitudeCommand force => force.Altitude,
            ExpediteCommand { Altitude: { } expedite } => expedite,
            CrossFixCommand cross => cross.Altitude,
            _ => null,
        };

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
    /// Re-voices the aircraft's open request once its follow-up is due, or closes it as moot. A request for further
    /// clearance is also held, still open, while the pilot is tuned away from the student's frequency, and resumes at its
    /// due time once retuned. <paramref name="studentPosition"/> is the student's position, against which the pilot's
    /// tuned position is read; <paramref name="airportLookup"/> (the engine's) places the destination for the
    /// request-for-lower floor. A request for lower that reaches its cap is remembered on the aircraft
    /// (<see cref="AircraftState.DescentRequestCap"/>, <see cref="AircraftState.DescentRequestCappedAtSeconds"/>).
    /// </summary>
    public static bool TryQueueFollowUp(AircraftState aircraft, double nowSeconds, TrackOwner? studentPosition, Func<string, LatLon?> airportLookup)
    {
        PilotPendingRequest? pending = aircraft.PendingPilotRequest;
        if (pending is not { IsOpen: true })
        {
            return false;
        }

        if (ClosesUnvoiced(pending, aircraft, nowSeconds, studentPosition, airportLookup))
        {
            pending.ResponseState = PilotPendingRequestResponseState.Superseded;
            return false;
        }

        if (
            (nowSeconds < pending.NextFollowUpDueSeconds)
            || pending.IsCapped
            || WaitsForTheHoldReport(pending, aircraft)
            || WaitsForTheStudentsFrequency(pending, aircraft, studentPosition)
        )
        {
            return false;
        }

        if (aircraft.PendingPilotTransmissions.Count > 0)
        {
            return false;
        }

        RefreshFurtherClearanceLine(pending, aircraft);
        RefreshBravoBoundaryLine(pending, aircraft);

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
        pending.FollowUpsVoiced++;
        RememberDescentCap(aircraft, pending);
        return true;
    }

    /// <summary>
    /// Whether the open <paramref name="request"/> closes without its follow-up: it is moot (<see cref="IsMoot"/>), or it is a
    /// spent "any estimate on the bravo?" ask (<see cref="IsSpentRemainOutsideAsk"/>).
    /// </summary>
    private static bool ClosesUnvoiced(
        PilotPendingRequest request,
        AircraftState aircraft,
        double nowSeconds,
        TrackOwner? studentPosition,
        Func<string, LatLon?> airportLookup
    ) => IsMoot(request, aircraft, studentPosition, airportLookup) || IsSpentRemainOutsideAsk(request, aircraft, nowSeconds);

    /// <summary>
    /// Whether <paramref name="request"/> is the "any estimate on the bravo?" ask of a pilot told to remain outside that
    /// Class B (<see cref="PilotProactive.TickRemainOutsideBravoAsk"/>).
    /// </summary>
    private static bool IsRemainOutsideAsk(PilotPendingRequest request, AircraftState aircraft) =>
        (request.Kind == PilotPendingRequestKind.AirspaceEntry)
        && (aircraft.RemainOutsideBravo is { AskVoiced: true } remain)
        && string.Equals(request.AirspaceIdent, remain.Ident, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the "any estimate on the bravo?" ask has used its one repeat, or was answered with a standby (7110.65
    /// §2-1-18.d: the controller owes the answer), and its next follow-up is due: it closes silently instead, and the pilot
    /// keeps holding.
    /// </summary>
    private static bool IsSpentRemainOutsideAsk(PilotPendingRequest request, AircraftState aircraft, double nowSeconds) =>
        (nowSeconds >= request.NextFollowUpDueSeconds)
        && ((request.FollowUpsVoiced >= 1) || (request.ResponseState == PilotPendingRequestResponseState.Standby))
        && IsRemainOutsideAsk(request, aircraft);

    /// <summary>
    /// Once a request for lower reaches its cap, the aircraft remembers it: re-openable once when it was asked from the
    /// 1,000 ft band, spent when it was asked from the 2,000 ft band or was itself the re-open.
    /// </summary>
    private static void RememberDescentCap(AircraftState aircraft, PilotPendingRequest request)
    {
        if ((request.Kind == PilotPendingRequestKind.Descent) && request.IsCapped)
        {
            aircraft.DescentRequestCap =
                (request.AskedInStepDownBand || request.ReopenedAfterCap) ? DescentRequestCap.Spent : DescentRequestCap.Reopenable;
            aircraft.DescentRequestCappedAtSeconds = request.LastRequestedAtSeconds;
        }
    }

    /// <summary>
    /// Whether a request for further clearance waits, still open, for the aircraft to reach its clearance limit: the
    /// five-minute call is not repeated on the way to the clearance limit (the direct, or a route end at the destination),
    /// and the holding report there records the request re-asked after.
    /// </summary>
    private static bool WaitsForTheHoldReport(PilotPendingRequest request, AircraftState aircraft) =>
        (request.Kind == PilotPendingRequestKind.FurtherClearance)
        && (
            FlightPhysics.IsLastTargetTheClearanceLimit(aircraft)
            || (aircraft.Phases?.CurrentPhase is HoldingPatternPhase { IsClearanceLimitHold: true, ClearanceLimitReported: false })
        );

    /// <summary>
    /// Whether a request for further clearance waits for the pilot to return to the student's frequency: while the pilot
    /// is tuned anywhere else (an AI controller's position included) the request stays open and unasked, and the re-ask
    /// resumes at its due time once retuned.
    /// </summary>
    private static bool WaitsForTheStudentsFrequency(PilotPendingRequest request, AircraftState aircraft, TrackOwner? studentPosition) =>
        (request.Kind == PilotPendingRequestKind.FurtherClearance) && aircraft.IsTunedAwayFrom(studentPosition);

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
        if (Log.IsEnabled(LogLevel.Debug))
        {
            Log.LogDebug("{Callsign}: re-asks for further clearance holding over {Airport}: {Line}", aircraft.Callsign, hold.FixName, line.Terminal);
        }
        pending.LastPilotLine = line.Terminal;
        pending.LastPilotLineTts = line.Tts;
    }

    /// <summary>The boundary hold the pilot flies outside the Class B its request named, or null (no hold, or another one).</summary>
    private static AirspaceBoundaryHoldPhase? HoldOutsideRequestedBravo(PilotPendingRequest pending, AircraftState aircraft) =>
        (
            (aircraft.Phases?.CurrentPhase is AirspaceBoundaryHoldPhase { AirspaceClass: AirspaceClass.Bravo } hold)
            && string.Equals(hold.Ident, pending.AirspaceIdent, StringComparison.OrdinalIgnoreCase)
        )
            ? hold
            : null;

    /// <summary>
    /// Rebuilds the re-ask of a Class B request from the boundary hold, so that it says where the pilot waits: orbiting
    /// outside the Class B, or levelled below its shelf. Away from a hold the re-ask is the request as first asked.
    /// </summary>
    private static void RefreshBravoBoundaryLine(PilotPendingRequest pending, AircraftState aircraft)
    {
        // The "any estimate on the bravo?" ask is repeated as it was asked.
        if (
            (pending.Kind != PilotPendingRequestKind.AirspaceEntry)
            || (pending.AirspaceClass != nameof(AirspaceClass.Bravo))
            || IsRemainOutsideAsk(pending, aircraft)
        )
        {
            return;
        }

        BravoClearanceWording wording = pending.BravoWording ?? BravoClearanceWording.Into;
        PilotSpeechText line;
        if (HoldOutsideRequestedBravo(pending, aircraft) is { } hold)
        {
            line = PilotResponder.BuildBravoBoundaryReask(aircraft, wording, hold);
        }
        else if (pending.BravoWording is not null)
        {
            // A re-ask voiced from a hold rewrote the stored line; once the hold is over the request is asked as first worded.
            line = PilotResponder.BuildBravoClearanceRequest(aircraft, wording);
        }
        else
        {
            return;
        }

        if (Log.IsEnabled(LogLevel.Debug))
        {
            Log.LogDebug("{Callsign}: re-asks for the Class B clearance: {Line}", aircraft.Callsign, line.Terminal);
        }
        pending.LastPilotLine = line.Terminal;
        pending.LastPilotLineTts = line.Tts;
    }

    private static bool IsFurtherClearanceMoot(PilotPendingRequest request, AircraftState aircraft) =>
        (request.Kind == PilotPendingRequestKind.FurtherClearance)
        && (aircraft.FlightPlan.IsVfr || !PilotProactive.IsAwaitingFurtherClearance(aircraft));

    /// <summary>
    /// An approach request is moot once the aircraft holds an approach clearance, or once the controller told it what approach
    /// to expect after it asked: advice given before the request (an EAPP before a visual the pilot then could not fly) does
    /// not answer it.
    /// </summary>
    private static bool IsApproachMoot(PilotPendingRequest request, AircraftState aircraft) =>
        (request.Kind == PilotPendingRequestKind.Approach)
        && (
            PilotProactive.HasApproachClearance(aircraft)
            || ((aircraft.ExpectedApproachAdvisedAtSeconds is { } advisedAt) && (advisedAt >= request.FirstRequestedAtSeconds))
        );

    /// <summary>
    /// Whether an open request no longer needs its follow-up because the aircraft has moved past it, so it is closed
    /// rather than re-announced.
    /// </summary>
    private static bool IsMoot(PilotPendingRequest request, AircraftState aircraft, TrackOwner? studentPosition, Func<string, LatLon?> airportLookup)
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
        // handoff, a hold, an approach or landing clearance, or inside the descent-request floor near the field.
        if ((request.Kind == PilotPendingRequestKind.Descent) && PilotProactive.ShouldNotAskForLower(aircraft, studentPosition, airportLookup))
        {
            return true;
        }

        // A request for further clearance is moot once a lateral clearance has taken the aircraft off both the direct to
        // its clearance limit and the hold there, or once the flight is VFR (IFR cancelled).
        if (IsFurtherClearanceMoot(request, aircraft))
        {
            return true;
        }

        // An approach request is moot once the aircraft holds an approach clearance, active or deferred (a queued AT fix
        // CAPP that fired after the request), or the controller has told it what approach to expect.
        if (IsApproachMoot(request, aircraft))
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

    /// <summary>
    /// How <paramref name="command"/> answers the open <paramref name="request"/> of <paramref name="aircraft"/>: as
    /// <see cref="ResolveResponse(PilotPendingRequestKind, ParsedCommand)"/> says, except that under <c>RMOB</c> for the
    /// request's Class B an implied-clearance command (a vector, direct, pattern entry or approach) grants nothing, so it
    /// is no answer (7110.65 §2-1-18: approve, unable or stand by) and the request stays open with its one repeat.
    /// </summary>
    private static PilotPendingRequestResponseState ResolveResponse(PilotPendingRequest request, AircraftState aircraft, ParsedCommand command) =>
        IsNoAnswerUnderRemainOutside(request, aircraft, command) ? PilotPendingRequestResponseState.None : ResolveResponse(request.Kind, command);

    /// <summary>
    /// Whether <paramref name="command"/> is an implied-clearance command given while the pilot is told to remain outside
    /// the Class B <paramref name="request"/> asks to enter.
    /// </summary>
    private static bool IsNoAnswerUnderRemainOutside(PilotPendingRequest request, AircraftState aircraft, ParsedCommand command) =>
        (request.Kind == PilotPendingRequestKind.AirspaceEntry)
        && (request.AirspaceIdent is { } ident)
        && aircraft.IsRemainOutsideBravo(ident)
        && ImplicitBravoClearance.IsImpliedClearanceCommand(command);

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
            // Telling the pilot what approach to expect is the approach assignment the pilot asked for.
            PilotPendingRequestKind.Approach => command switch
            {
                ExpectApproachCommand
                or ClearedApproachCommand
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
                // "Remain outside Bravo airspace" refuses the entry (7110.65 §7-9-2.a), ending the request's repeats.
                RemainOutsideBravoCommand => PilotPendingRequestResponseState.Denied,
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
