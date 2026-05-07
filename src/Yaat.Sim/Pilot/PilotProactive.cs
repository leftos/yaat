using Yaat.Sim.Data.Airspace;
using Yaat.Sim.Phases;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Pilot;

/// <summary>
/// Proactive pilot transmissions that fire on simulator state alone (no controller command
/// to react to). Currently houses the airborne-spawn check-in; later milestones add
/// pending-clearance reminders and DA/MDA missed-approach contingency handling.
///
/// Each entry point is idempotent: it consults the aircraft's <see cref="AircraftState.HasMadeInitialContact"/>
/// (or feature-specific flag) so it fires once per logical event.
/// </summary>
public static class PilotProactive
{
    /// <summary>
    /// Per-aircraft, per-tick airborne-spawn check-in. Fires once when an aircraft is first
    /// observed airborne in solo-training mode and has not yet spoken to ATC. No-op when any
    /// gate fails: solo mode off, on the ground, already made initial contact, student
    /// position is GND or unknown, primary airport unknown, or the airport lookup returns null.
    /// On success, pushes the check-in line into <see cref="AircraftState.PendingNotifications"/>
    /// and sets <see cref="AircraftState.HasMadeInitialContact"/> so subsequent ticks no-op.
    /// </summary>
    public static void TickAirborneCheckIn(AircraftState aircraft, SimScenarioState scenario, Func<string, LatLon?> airportLookup)
    {
        if (!scenario.SoloTrainingMode)
        {
            return;
        }

        if (aircraft.HasMadeInitialContact)
        {
            return;
        }

        if (aircraft.IsOnGround)
        {
            return;
        }

        var positionType = scenario.StudentPositionType;
        if (string.IsNullOrEmpty(positionType) || positionType == "GND")
        {
            return;
        }

        var primaryAirport = scenario.PrimaryAirportId;
        if (string.IsNullOrEmpty(primaryAirport))
        {
            return;
        }

        var airportPos = airportLookup(primaryAirport);
        if (airportPos is null)
        {
            return;
        }

        var line = PilotResponder.BuildAirborneCheckIn(aircraft, scenario, airportPos.Value);
        if (string.IsNullOrEmpty(line))
        {
            return;
        }

        PilotResponder.QueueSoloPilotTransmission(aircraft, line);
        aircraft.HasMadeInitialContact = true;
    }

    /// <summary>
    /// Watches airborne VFR aircraft in solo training and inserts a self-clearing boundary
    /// hold when the projected track would enter Class B/C before the required gate is met.
    /// AIM §3-2-1.4 places responsibility on the pilot to meet Class B/C/D entry
    /// requirements before entry; this models that responsibility for the student.
    /// </summary>
    public static void TickAirspaceBoundaryRespect(
        AircraftState aircraft,
        SimScenarioState scenario,
        AirspaceDatabase airspace,
        Func<string, LatLon?> airportLookup
    )
    {
        if (!scenario.SoloTrainingMode)
        {
            return;
        }

        if (aircraft.IsOnGround || !aircraft.FlightPlan.IsVfr)
        {
            return;
        }

        if (aircraft.Phases is { IsComplete: false })
        {
            return;
        }

        var crossing = airspace.FindFirstProjectedEntry(aircraft, lookaheadSeconds: 60);
        if (crossing is null || EntryGateSatisfied(aircraft, crossing.Volume.Class))
        {
            return;
        }

        var reference = airportLookup(crossing.Volume.Ident) ?? airportLookup(crossing.Volume.IcaoId) ?? crossing.Intersection;
        var phase = new AirspaceBoundaryHoldPhase
        {
            AirspaceClass = crossing.Volume.Class,
            Ident = string.IsNullOrWhiteSpace(crossing.Volume.Ident) ? crossing.Volume.IcaoId : crossing.Volume.Ident,
            NameText = crossing.Volume.Name,
            ReferencePosition = reference,
            OrbitDirection = TurnDirection.Right,
        };

        var phases = new PhaseList();
        phases.Add(phase);
        aircraft.Phases = phases;
    }

    private static bool EntryGateSatisfied(AircraftState aircraft, AirspaceClass airspaceClass) =>
        airspaceClass switch
        {
            AirspaceClass.Bravo => aircraft.IsClearedIntoBravo,
            AirspaceClass.Charlie => aircraft.HasMadeInitialContact && aircraft.HasControllerAcknowledgedInitialContact,
            _ => true,
        };
}
