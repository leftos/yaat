using Microsoft.Extensions.Logging;
using Yaat.Sim.Phases;

namespace Yaat.Sim.Pilot;

/// <summary>
/// The arrival's call to ground once it is clear of the landing runway (AIM 4-3-21.c: change to ground control and
/// obtain a taxi clearance): "clear of runway 28R at W, taxi to gate 29". Made from whichever idle phase the exit ends
/// in — holding after the exit, or holding in position past a parallel it had to cross first — to whichever position
/// answers ground calls at the airport (an AI Ground, the solo student, or an AI tower working the cab alone). While a
/// separately staffed Local answers at the airport, the pilot stays on the tower's frequency until sent to ground (AIM
/// 4-3-14.c, 4-3-21.c: a <c>CT</c> to ground or a frequency change approved); a combined cab needs no release. Nobody
/// answering means the call waits, exactly like the parked aircraft's ready-to-taxi call.
/// </summary>
public static class TaxiInRequest
{
    /// <summary>A short pause after stopping before the pilot keys up.</summary>
    public const double DelaySeconds = 3.0;

    private static readonly ILogger Log = SimLog.CreateLogger("TaxiInRequest");

    /// <summary>Makes the call when it is due and someone answers; true when the request was recorded this tick.</summary>
    public static bool TryAnnounce(PhaseContext ctx, double phaseElapsedSeconds, string? runwayId, string? taxiway)
    {
        var aircraft = ctx.Aircraft;
        if (!aircraft.Ground.AwaitingTaxiInCall || (phaseElapsedSeconds < DelaySeconds))
        {
            return false;
        }

        // The initial-contact transfer SOP (7110.65 §2-1-17) governs the airborne first call; an aircraft that has
        // landed is the cab's already, so the ground call is not gated on it.
        var atAirportId = PilotContactRoster.SurfaceAirportOf(aircraft);
        if (ctx.PilotContacts.ResolveFor(aircraft, "GND", atAirportId, ctx.ToEligibilityContext(), false) is not { } answering)
        {
            return false;
        }

        var tower = ctx.PilotContacts.ResolveFor(aircraft, "TWR", atAirportId, ctx.ToEligibilityContext(), false);
        bool separateLocal = (tower is not null) && !SamePosition(tower, answering);
        if (separateLocal && !aircraft.Ground.ReleasedToGround)
        {
            return false;
        }

        var parking = ArrivalParkingPicker.Pick(aircraft, ctx.GroundLayout, ctx.ListAircraft?.Invoke() ?? [], 0);
        if (parking is null)
        {
            Log.LogDebug("[TaxiIn] {Callsign}: no parking in the layout to ask for; no taxi-in call", aircraft.Callsign);
            aircraft.Ground.AwaitingTaxiInCall = false;
            return false;
        }

        var facilityCallName = PilotResponder.ResolveAnsweringCallName(answering, "GND", "ground");
        var line = PilotResponder.BuildTaxiInRequest(aircraft, facilityCallName, runwayId, taxiway, parking);
        PilotResponder.QueueSoloPilotTransmission(aircraft, line, PilotTransmissionKind.Proactive, PilotResponder.SourceResponse);
        PilotRequestTracker.RecordRequest(
            aircraft,
            PilotPendingRequestKind.Taxi,
            ctx.ScenarioElapsedSeconds,
            line,
            PilotRequestContext.TaxiIn(facilityCallName, parking)
        );
        answering.MarkInitialContact(aircraft);
        aircraft.Ground.AwaitingTaxiInCall = false;
        aircraft.Ground.ReleasedToGround = false;
        Log.LogDebug("[TaxiIn] {Callsign}: asked {Facility} for taxi to {Parking}", aircraft.Callsign, facilityCallName, parking);
        return true;
    }

    /// <summary>The same answering position under two hats (a student, or one AI position working the cab alone).</summary>
    private static bool SamePosition(PilotAnsweringPosition a, PilotAnsweringPosition b) =>
        (a.Agent == b.Agent) && string.Equals(a.PositionId, b.PositionId, StringComparison.Ordinal);
}
