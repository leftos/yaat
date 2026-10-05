using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Pilot;

/// <summary>
/// The solo-training initial "ready to taxi" call a ground spawn makes (<see cref="InitialCallupPlan"/>), shared by the
/// stand call (<c>AtParkingPhase</c>), the after-push call (<c>HoldingAfterPushbackPhase</c>, or <c>AtParkingPhase</c>
/// on the stand a tow ended on) and the after-taxi-arrival call (<c>HoldingInPositionPhase</c>, <c>HoldingShortPhase</c>).
/// Each caller waits its own delay first; the call then waits for someone answering ground at the airport and for a
/// pacing slot, exactly as the stand call does. A student working clearance delivery at the airport gets a clearance
/// request instead, and no taxi call follows it.
/// </summary>
public static class InitialCallupCall
{
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
    /// processed. Returns false, changing nothing, when nobody answers or no slot is free; the caller retries next tick.
    /// </summary>
    public static bool TryMake(PhaseContext ctx, ReadyToTaxiLocation location)
    {
        // The same two checks as CanCall, in the same order, so the call is never owed while CanCall says it cannot come.
        if (!PacingAllowsCalls(ctx))
        {
            return false;
        }

        string? atAirportId = PilotContactRoster.SurfaceAirportOf(ctx.Aircraft);
        if (DeliveryStudentAt(ctx, atAirportId) is { } delivery)
        {
            return TryMakeClearanceRequest(ctx, delivery, atAirportId, location);
        }

        // Nobody answering (instructor room, no AI) or the SOP says this aircraft does not call the student yet.
        if (GroundAnswering(ctx, atAirportId) is not { } answering)
        {
            return false;
        }

        if (!TryReserveSlot(ctx))
        {
            return false;
        }

        string facilityCallName = PilotResponder.ResolveAnsweringCallName(answering, "GND", "ground");
        PilotSpeechText line = PilotResponder.BuildReadyToTaxi(ctx.Aircraft, facilityCallName, ctx.AtisLetter, location);
        PilotResponder.QueueSoloPilotTransmission(ctx.Aircraft, line, PilotTransmissionKind.Proactive, PilotResponder.SourceResponse);
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
    /// Whether the initial call can come at all: the pacing rate is above zero and someone answers it (a delivery student at
    /// the airport, or someone answering ground there under the SOP). Pacing slots are not consulted: a call waiting for a
    /// slot still comes.
    /// </summary>
    public static bool CanCall(PhaseContext ctx)
    {
        if (!PacingAllowsCalls(ctx))
        {
            return false;
        }

        string? atAirportId = PilotContactRoster.SurfaceAirportOf(ctx.Aircraft);
        return (DeliveryStudentAt(ctx, atAirportId) is not null) || (GroundAnswering(ctx, atAirportId) is not null);
    }

    private static PilotAnsweringPosition? GroundAnswering(PhaseContext ctx, string? atAirportId) =>
        ctx.PilotContacts.ResolveFor(ctx.Aircraft, "GND", atAirportId, ctx.ToEligibilityContext(), true);

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
            && (ArrivalParkingPicker.Pick(ctx.Aircraft, ctx.GroundLayout, ctx.ListAircraft?.Invoke() ?? [], 0) is { } parking)
        )
        {
            return PilotRequestContext.TaxiIn(facilityCallName, parking);
        }

        return PilotRequestContext.Facility(facilityCallName);
    }

    /// <summary>
    /// The solo student when they work a clearance delivery position (callsign <c>…_DEL</c>) at the airport the aircraft
    /// is on and the SOP lets the aircraft call them; null otherwise. A delivery student takes the first call even when
    /// an AI ground answers at the airport.
    /// </summary>
    private static PilotAnsweringPosition? DeliveryStudentAt(PhaseContext ctx, string? atAirportId)
    {
        if (ctx.PilotContacts.Student is not { Owner.Callsign: { } callsign } student || string.IsNullOrWhiteSpace(atAirportId))
        {
            return null;
        }

        int underscore = callsign.IndexOf('_');
        bool deliveryHere =
            callsign.EndsWith("_DEL", StringComparison.OrdinalIgnoreCase)
            && (underscore > 0)
            && NavigationDatabase.AirportIdsMatch(callsign[..underscore], atAirportId);
        return (deliveryHere && PilotInitialContactEligibility.CanInitiateWithStudent(ctx.Aircraft, ctx.ToEligibilityContext())) ? student : null;
    }

    /// <summary>
    /// The first call to a delivery student: an IFR clearance request, or a VFR departure request naming a direction of
    /// flight (chosen once and kept on <see cref="AircraftGroundOps.VfrDepartureDirection"/>) and the filed altitude. It
    /// records the open <see cref="PilotPendingRequestKind.Clearance"/> request and closes the decision, so no taxi call follows.
    /// </summary>
    private static bool TryMakeClearanceRequest(PhaseContext ctx, PilotAnsweringPosition delivery, string? atAirportId, ReadyToTaxiLocation location)
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

        string facilityCallName = PilotResponder.ResolveAnsweringCallName(delivery, "GND", "clearance");
        PilotSpeechText line = PilotResponder.BuildClearanceRequest(
            aircraft,
            facilityCallName,
            ctx.AtisLetter,
            location,
            aircraft.Ground.VfrDepartureDirection
        );
        PilotResponder.QueueSoloPilotTransmission(aircraft, line, PilotTransmissionKind.Proactive, PilotResponder.SourceResponse);
        PilotRequestTracker.RecordRequest(
            aircraft,
            PilotPendingRequestKind.Clearance,
            ctx.ScenarioElapsedSeconds,
            line,
            PilotRequestContext.Facility(facilityCallName)
        );
        delivery.MarkInitialContact(aircraft);
        aircraft.Ground.InitialCallupDecisionProcessed = true;
        return true;
    }

    private static bool TryReserveSlot(PhaseContext ctx) => ctx.TryReserveSoloParkingInitialCallupSlot?.Invoke(ctx.ScenarioElapsedSeconds) ?? true;
}
