using Yaat.Sim.Data.Airspace;
using Yaat.Sim.Phases;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Pilot;

/// <summary>
/// Proactive pilot transmissions that fire on simulator state alone (no controller command
/// to react to). Houses airborne-spawn check-ins, arrival approach requests, controlled-airspace
/// boundary holds, and pending-request follow-up reminders.
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
    /// On success, queues the check-in line into <see cref="AircraftState.PendingPilotTransmissions"/>
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

        PilotResponder.QueueSoloPilotTransmission(aircraft, line, PilotTransmissionKind.Proactive, PilotResponder.SourceResponse);
        aircraft.HasMadeInitialContact = true;
    }

    public static void TickArrivalApproachRequest(AircraftState aircraft, SimScenarioState scenario, Func<string, LatLon?> airportLookup)
    {
        if (!scenario.SoloTrainingMode)
        {
            return;
        }

        if (aircraft.IsOnGround)
        {
            return;
        }

        if (aircraft.FlightPlan.IsVfr)
        {
            return;
        }

        if (!aircraft.HasMadeInitialContact)
        {
            return;
        }

        if (aircraft.PendingPilotRequest is { IsOpen: true })
        {
            return;
        }

        if (aircraft.PendingPilotTransmissions.Count > 0)
        {
            return;
        }

        if (aircraft.Phases?.ActiveApproach is not null)
        {
            return;
        }

        if (aircraft.Phases?.LandingClearance is not null)
        {
            return;
        }

        var destination = !string.IsNullOrWhiteSpace(aircraft.FlightPlan.Destination) ? aircraft.FlightPlan.Destination : scenario.PrimaryAirportId;
        if (string.IsNullOrWhiteSpace(destination))
        {
            return;
        }

        var destinationPosition = airportLookup(destination);
        if (destinationPosition is null)
        {
            return;
        }

        var distanceNm = GeoMath.DistanceNm(destinationPosition.Value, aircraft.Position);
        if (distanceNm > 10.0)
        {
            return;
        }

        var positionType = scenario.StudentPositionType;
        if (string.IsNullOrWhiteSpace(positionType))
        {
            return;
        }

        if (positionType is "GND" or "TWR")
        {
            return;
        }

        var facilityCallName = PilotResponder.ResolveStudentFacilityCallName(scenario, positionType, positionType == "CTR" ? "center" : "approach");
        var runwayId = aircraft.Procedure.DestinationRunway ?? aircraft.Phases?.AssignedRunway?.Designator;
        var line = PilotResponder.BuildArrivalApproachRequest(aircraft, runwayId, (int)Math.Round(distanceNm), facilityCallName);
        PilotResponder.QueueSoloPilotTransmission(aircraft, line, PilotTransmissionKind.Proactive, PilotResponder.SourceResponse);
        PilotRequestTracker.RecordRequest(
            aircraft,
            PilotPendingRequestKind.Approach,
            scenario.ElapsedSeconds,
            line,
            PilotRequestContext.Runway(runwayId, facilityCallName)
        );
    }

    public static void TickPendingRequests(AircraftState aircraft, SimScenarioState scenario)
    {
        if (!scenario.SoloTrainingMode)
        {
            return;
        }

        PilotRequestTracker.TryQueueFollowUp(aircraft, scenario.ElapsedSeconds);
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
            VolumeLowerFtMsl = crossing.Volume.LowerFtMsl,
            VolumeUpperFtMsl = crossing.Volume.UpperFtMsl,
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
