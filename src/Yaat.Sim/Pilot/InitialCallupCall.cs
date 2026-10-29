using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Pilot;

/// <summary>
/// The solo-training initial "ready to taxi" call a ground spawn makes (<see cref="InitialCallupPlan"/>), shared by the
/// stand call (<c>AtParkingPhase</c>), the after-push call (<c>HoldingAfterPushbackPhase</c>, or <c>AtParkingPhase</c>
/// on the stand a tow ended on) and the after-taxi-arrival call (<c>HoldingInPositionPhase</c>, <c>HoldingShortPhase</c>).
/// Each caller waits its own delay first; the call then waits for someone answering ground at the airport and for a
/// pacing slot, exactly as the stand call does. Only the stand call asks for a clearance: an IFR departure from the airport
/// it is on, with no clearance yet, asks the student for it first — a delivery student, else the student answering ground
/// when nobody else covers delivery there — and no ground call follows that request unless the student answers ground there
/// too, in which case it comes once the clearance is obtained. An aircraft a preset (or a controller) already pushed or
/// taxied counts as cleared and makes its ground call.
/// </summary>
public static class InitialCallupCall
{
    /// <summary>
    /// The least time between a departure's clearance request being answered and its ready-to-taxi or pushback request to the
    /// same student, seconds: the crew copies and sets up the clearance first (a judgement; no published figure).
    /// </summary>
    public const double TaxiCallAfterClearanceSeconds = 30.0;

    /// <summary>The shortest after-taxi-arrival delay, seconds.</summary>
    public const double MinAfterTaxiArrivalDelaySeconds = 10.0;

    /// <summary>How many whole seconds past <see cref="MinAfterTaxiArrivalDelaySeconds"/> the delay can run (10 to 20 s inclusive).</summary>
    private const uint AfterTaxiArrivalDelayRangeSeconds = 11;

    /// <summary>
    /// How long a crew takes after a push to start engines and run its checks before reporting ready to taxi: jet 90 s,
    /// turboprop 60 s, piston and helicopter 30 s (a judgement; no published figure).
    /// </summary>
    public static double PostPushSetupDelaySeconds(AircraftCategory category) =>
        category switch
        {
            AircraftCategory.Jet => 90.0,
            AircraftCategory.Turboprop => 60.0,
            _ => 30.0,
        };

    /// <summary>Salt for the after-taxi-arrival draw, so it is not correlated with the other per-callsign draws.</summary>
    private const string AfterTaxiArrivalSalt = "after-taxi-arrival-callup:";

    /// <summary>
    /// The pause between coming to rest at the preset taxi's stop and calling: a fixed 10 to 20 s per aircraft from its
    /// callsign (<see cref="DeterministicHash"/>; replay-safe, no RNG state).
    /// </summary>
    public static double AfterTaxiArrivalDelaySeconds(string callsign) =>
        MinAfterTaxiArrivalDelaySeconds + (DeterministicHash.Fnv1a(AfterTaxiArrivalSalt, callsign) % AfterTaxiArrivalDelayRangeSeconds);

    /// <summary>
    /// The call-up bookkeeping before a tug move (<c>PUSH</c>, <c>PUSHM</c>, a tow) replaces the aircraft's phases. A stand
    /// call not yet made becomes the after-push call: the crew reports ready to taxi once the engines are started, and an
    /// after-push call not yet made carries over a further tow. What that call names is set here: the stand this move
    /// leaves (a further tow from the alley keeps the stand the first one left) and the spot it ends on
    /// (<paramref name="terminusSpot"/>, null when it ends elsewhere). Read before the phases are cleared, since leaving the
    /// stand or the post-push hold drops an uncalled plan (<c>AtParkingPhase.OnEnd</c>, <c>HoldingAfterPushbackPhase.OnEnd</c>).
    /// </summary>
    /// <returns>Whether the aircraft calls after this move; pass it to <see cref="AfterTow"/>.</returns>
    public static bool BeforeTow(AircraftState aircraft, bool atStand, string? terminusSpot)
    {
        AircraftGroundOps ground = aircraft.Ground;
        bool callsAfterThisPush =
            !ground.InitialCallupDecisionProcessed
            && ((atStand && (ground.InitialCallup == InitialCallupPlan.StandCall)) || (ground.InitialCallup == InitialCallupPlan.AfterPush));
        if (atStand && (ground.ParkingSpot is { Length: > 0 } originStand))
        {
            ground.PushedBackFrom = originStand;
        }

        ground.PushEndSpot = terminusSpot;
        return callsAfterThisPush;
    }

    /// <summary>
    /// Arms the after-push call once the tug move's phases are in place, when <see cref="BeforeTow"/> found it owed: clearing
    /// the phases the move replaced dropped the plan.
    /// </summary>
    public static void AfterTow(AircraftState aircraft, bool callsAfterThisPush)
    {
        if (callsAfterThisPush)
        {
            aircraft.Ground.InitialCallup = InitialCallupPlan.AfterPush;
        }
    }

    /// <summary>
    /// Makes the initial call from <paramref name="location"/> once someone answers ground at the airport and a pacing slot
    /// is free: queues the line, records the open Taxi request, marks the aircraft as having called and the decision
    /// processed. A jet or turboprop on a push-back stand asks for pushback instead: the open request is a Pushback one,
    /// and the decision stays open as the after-push call (<see cref="MakePushbackRequest"/>). A stand call from an IFR
    /// departure with no clearance asks for the clearance first (<see cref="TryMakeClearanceRequest"/>), to a delivery
    /// student or, delivery outside the worked range, to the student answering ground; the pushback or taxi request follows
    /// once it is answered. A call from an aircraft a preset or a controller already pushed or taxied
    /// (<see cref="CallsFromTheStand"/> false) counts as cleared and asks for the pushback or taxi instead. Returns false,
    /// changing nothing, when nobody answers or no slot is free; the caller retries next tick.
    /// </summary>
    public static bool TryMake(PhaseContext ctx, ReadyToTaxiLocation location)
    {
        // The same checks as CanCall, in the same order, so the call is never owed while CanCall says it cannot come.
        if (!PacingAllowsCalls(ctx) || AwaitsItsClearance(ctx.Aircraft))
        {
            return false;
        }

        string? atAirportId = PilotContactRoster.SurfaceAirportOf(ctx.Aircraft);
        if (DeliveryStudentAt(ctx, atAirportId) is { } delivery)
        {
            return TryMakeClearanceRequest(ctx, delivery, atAirportId, location, "clearance");
        }

        // A crew that has just copied its clearance sets up before it calls for taxi; the call is still owed meanwhile.
        if (
            ctx.Aircraft.Ground.ClearanceObtainedAtSeconds is { } clearedAt
            && (ctx.ScenarioElapsedSeconds < clearedAt + TaxiCallAfterClearanceSeconds)
        )
        {
            return false;
        }

        // Nobody answering (instructor room, no AI) or the SOP says this aircraft does not call the student yet.
        if (GroundAnswering(ctx, atAirportId) is not { } answering)
        {
            return false;
        }

        // Top-down coverage: an uncleared IFR departure from this airport asks the student for its clearance before anything
        // else, and the pushback or taxi request follows once it is answered.
        if (AsksForItsClearanceFirst(ctx, answering, atAirportId))
        {
            return TryMakeClearanceRequest(ctx, answering, atAirportId, location, "ground");
        }

        if (!TryReserveSlot(ctx))
        {
            return false;
        }

        string facilityCallName = PilotResponder.ResolveAnsweringCallName(answering, "GND", "ground");
        if (RequestsPushback(ctx, location))
        {
            MakePushbackRequest(ctx, answering, facilityCallName, location);
            return true;
        }

        PilotSpeechText line = PilotResponder.BuildReadyToTaxi(ctx.Aircraft, facilityCallName, ctx.AtisLetter, location);
        PilotResponder.QueueSoloPilotTransmission(ctx.Aircraft, line, PilotTransmissionKind.Proactive, initialContact: false);
        PilotRequestTracker.RecordRequest(
            ctx.Aircraft,
            PilotPendingRequestKind.Taxi,
            ctx.ScenarioElapsedSeconds,
            line,
            TaxiRequestContext(ctx, location, facilityCallName)
        );
        ctx.Aircraft.Ground.HasAnnouncedReady = true;
        answering.MarkInitialContact(ctx.Aircraft);
        ctx.Aircraft.Ground.InitialCallupDecisionProcessed = true;
        return true;
    }

    /// <summary>
    /// Whether the call is a pushback request: a jet or turboprop (the categories the RPO's push-back note names) calling
    /// from the push-back stand it is parked on. A piston or helicopter, and any aircraft on an either or taxi-out stand,
    /// calls ready to taxi, and so does the after-push call made on a stand a tow ended on.
    /// </summary>
    private static bool RequestsPushback(PhaseContext ctx, ReadyToTaxiLocation location) =>
        (ctx.Aircraft.Ground.InitialCallup == InitialCallupPlan.StandCall)
        && (location.Kind == ReadyToTaxiLocationKind.Stand)
        && (ctx.Category is AircraftCategory.Jet or AircraftCategory.Turboprop)
        && (ParkedStandDeparture.Of(ctx.Aircraft, ctx.GroundLayout) == StandDeparture.PushBack);

    /// <summary>
    /// The stand call as a pushback request: queues "request pushback", records the open
    /// <see cref="PilotPendingRequestKind.Pushback"/> request, and re-arms the plan as the after-push call with the decision
    /// still open, so the push (<see cref="BeforeTow"/>) carries it and the crew reports ready to taxi after the push.
    /// </summary>
    private static void MakePushbackRequest(PhaseContext ctx, PilotAnsweringPosition answering, string facilityCallName, ReadyToTaxiLocation location)
    {
        AircraftState aircraft = ctx.Aircraft;
        PilotSpeechText line = PilotResponder.BuildPushbackRequest(aircraft, facilityCallName, ctx.AtisLetter, location);
        PilotResponder.QueueSoloPilotTransmission(aircraft, line, PilotTransmissionKind.Proactive, initialContact: false);
        PilotRequestTracker.RecordRequest(
            aircraft,
            PilotPendingRequestKind.Pushback,
            ctx.ScenarioElapsedSeconds,
            line,
            PilotRequestContext.Facility(facilityCallName)
        );
        aircraft.Ground.HasAnnouncedReady = true;
        answering.MarkInitialContact(aircraft);
        aircraft.Ground.InitialCallup = InitialCallupPlan.AfterPush;
        aircraft.Ground.InitialCallupDecisionProcessed = false;
    }

    /// <summary>
    /// Whether the initial call can come at all: the pacing rate is above zero and someone answers it (a delivery student at
    /// the airport on a stand call, or someone answering ground there under the SOP), and no clearance request of the
    /// aircraft's is still open. Pacing slots are not consulted: a call waiting for a slot still comes.
    /// </summary>
    public static bool CanCall(PhaseContext ctx)
    {
        if (!PacingAllowsCalls(ctx) || AwaitsItsClearance(ctx.Aircraft))
        {
            return false;
        }

        string? atAirportId = PilotContactRoster.SurfaceAirportOf(ctx.Aircraft);
        return (DeliveryStudentAt(ctx, atAirportId) is not null) || (GroundAnswering(ctx, atAirportId) is not null);
    }

    private static PilotAnsweringPosition? GroundAnswering(PhaseContext ctx, string? atAirportId) =>
        ctx.PilotContacts.ResolveFor(ctx.Aircraft, "GND", atAirportId, ctx.ToEligibilityContext(), true);

    /// <summary>
    /// Whether the call is the aircraft's clearance request rather than its ground request: the stand call of an IFR
    /// departure from the airport it is on, with no clearance yet, calling the student when nobody else covers delivery
    /// there (the pilot knows delivery is offline). An aircraft a preset or a controller already pushed or taxied
    /// (<see cref="CallsFromTheStand"/> false) counts as cleared and asks for ground instead. An AI ground is never asked:
    /// nothing would answer a clearance given to it.
    /// </summary>
    private static bool AsksForItsClearanceFirst(PhaseContext ctx, PilotAnsweringPosition answering, string? atAirportId) =>
        CallsFromTheStand(ctx.Aircraft)
        && (answering.Agent == PilotAnsweringAgent.Student)
        && !ctx.Aircraft.FlightPlan.IsVfr
        && (ctx.Aircraft.Ground.ClearanceObtainedAtSeconds is null)
        && DepartsFrom(ctx.Aircraft, atAirportId)
        && !ctx.PilotContacts.DeliveryCoveredByAnother(atAirportId);

    /// <summary>
    /// Whether the aircraft still makes its stand call, the only call that asks for a clearance: a preset push or taxi (or
    /// a controller's <c>PUSH</c>) replaces <see cref="InitialCallupPlan.StandCall"/> with an after-push or after-taxi-arrival
    /// plan, so the aircraft is already past its clearance and makes its ordinary ground call.
    /// </summary>
    private static bool CallsFromTheStand(AircraftState aircraft) => aircraft.Ground.InitialCallup == InitialCallupPlan.StandCall;

    /// <summary>Whether the flight plan departs from <paramref name="atAirportId"/>, in either its ICAO or its FAA form.</summary>
    private static bool DepartsFrom(AircraftState aircraft, string? atAirportId) =>
        NavigationDatabase.AirportIdsMatch(aircraft.FlightPlan.Departure, atAirportId);

    private static bool PacingAllowsCalls(PhaseContext ctx) =>
        ScenarioPacing.ClampParkingInitialCallupPercent(ctx.SoloParkingInitialCallupRatePercent) > 0;

    /// <summary>
    /// What the Taxi request records: a "request taxi to parking" call is a taxi-in request for a stand the pilot has in
    /// mind (picked as an arrival's is), so the taxi-in path answers it; a layout with no parking, and every other call,
    /// records the plain outbound request.
    /// </summary>
    private static PilotRequestContext TaxiRequestContext(PhaseContext ctx, ReadyToTaxiLocation location, string facilityCallName)
    {
        if (
            PilotResponder.RequestsTaxiToParking(ctx.Aircraft, location)
            && (
                ArrivalParkingPicker.Pick(
                    ctx.Aircraft,
                    ctx.GroundLayout,
                    ctx.ListAircraft?.Invoke() ?? [],
                    0,
                    NavigationDatabase.InstanceOrNull?.AirportSidecars ?? AirportSidecarCatalog.Empty
                ) is
                { } parking
            )
        )
        {
            return PilotRequestContext.TaxiIn(facilityCallName, parking);
        }

        return PilotRequestContext.Facility(facilityCallName);
    }

    /// <summary>
    /// The solo student when they work clearance delivery at the airport the aircraft is on
    /// (<see cref="PilotContactRoster.StudentWorksDeliveryAt"/>), the aircraft departs from that airport, has no clearance yet
    /// (<see cref="AircraftGroundOps.ClearanceObtainedAtSeconds"/>), makes its stand call and the SOP lets the aircraft call
    /// them; null otherwise. A delivery student takes the first call even when an AI ground answers at the airport, and an
    /// aircraft a preset or a controller already pushed or taxied counts as cleared, so delivery gets no call from it.
    /// </summary>
    private static PilotAnsweringPosition? DeliveryStudentAt(PhaseContext ctx, string? atAirportId)
    {
        if (
            (ctx.Aircraft.Ground.ClearanceObtainedAtSeconds is not null)
            || !CallsFromTheStand(ctx.Aircraft)
            || (ctx.PilotContacts.Student is not { } student)
            || string.IsNullOrWhiteSpace(atAirportId)
            || !DepartsFrom(ctx.Aircraft, atAirportId)
            || !ctx.PilotContacts.StudentWorksDeliveryAt(atAirportId)
        )
        {
            return null;
        }

        return PilotInitialContactEligibility.CanInitiateWithStudent(ctx.Aircraft, ctx.ToEligibilityContext()) ? student : null;
    }

    /// <summary>Whether the aircraft's clearance request is still open: no other call-up goes out before it is answered.</summary>
    private static bool AwaitsItsClearance(AircraftState aircraft) =>
        aircraft.PendingPilotRequest is { IsOpen: true, Kind: PilotPendingRequestKind.Clearance };

    /// <summary>
    /// The first call to the solo student — a delivery student, or, with delivery outside the worked range, the student
    /// answering ground: an IFR clearance request, or a VFR departure request naming a direction of flight (chosen once and
    /// kept on <see cref="AircraftGroundOps.VfrDepartureDirection"/>) and the filed altitude. It records the open
    /// <see cref="PilotPendingRequestKind.Clearance"/> request, addressed by the addressee's radio name when it works ground
    /// and by <paramref name="fallbackFacilityWord"/> otherwise. The decision closes, so no further call follows, unless the
    /// student also answers ground at the airport: then it stays open, and once the clearance is obtained the next call-up
    /// is the ground call (pushback or ready to taxi) to the same student.
    /// </summary>
    private static bool TryMakeClearanceRequest(
        PhaseContext ctx,
        PilotAnsweringPosition answering,
        string? atAirportId,
        ReadyToTaxiLocation location,
        string fallbackFacilityWord
    )
    {
        if (!TryReserveSlot(ctx))
        {
            return false;
        }

        AircraftState aircraft = ctx.Aircraft;
        if (aircraft.FlightPlan.IsVfr && (aircraft.Ground.VfrDepartureDirection is null) && (atAirportId is not null))
        {
            aircraft.Ground.VfrDepartureDirection = VfrDepartureDirection.Choose(aircraft.Callsign, atAirportId);
        }

        string facilityCallName = PilotResponder.ResolveAnsweringCallName(answering, "GND", fallbackFacilityWord);
        PilotSpeechText line = PilotResponder.BuildClearanceRequest(
            aircraft,
            facilityCallName,
            ctx.AtisLetter,
            location,
            aircraft.Ground.VfrDepartureDirection
        );
        PilotResponder.QueueSoloPilotTransmission(aircraft, line, PilotTransmissionKind.Proactive, initialContact: false);
        PilotRequestTracker.RecordRequest(
            aircraft,
            PilotPendingRequestKind.Clearance,
            ctx.ScenarioElapsedSeconds,
            line,
            PilotRequestContext.Facility(facilityCallName)
        );
        answering.MarkInitialContact(aircraft);
        aircraft.Ground.InitialCallupDecisionProcessed = GroundAnswering(ctx, atAirportId)?.Agent != PilotAnsweringAgent.Student;
        return true;
    }

    private static bool TryReserveSlot(PhaseContext ctx) => ctx.TryReserveSoloParkingInitialCallupSlot?.Invoke(ctx.ScenarioElapsedSeconds) ?? true;
}
