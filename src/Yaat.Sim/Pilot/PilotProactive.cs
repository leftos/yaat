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

        aircraft.PendingNotifications.Add(line);
        aircraft.HasMadeInitialContact = true;
    }
}
