using Microsoft.Extensions.Logging;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Phases.Approach;

/// <summary>
/// An altitude the controller assigns (CM, DM or a forced altitude) to an aircraft flying an instrument approach. Outside the
/// final approach fix the approach is kept and the altitude is "maintain until established" (AIM 5-4-7.f): it is held in place
/// of the published altitudes until glideslope capture on the final, or until approach navigation sequences its next fix, and
/// then released so the published altitudes resume (<see cref="ApproachClearance.AltitudeHeldUntilEstablished"/>). Inside the
/// final approach fix the instruction cancels the approach.
/// </summary>
internal static class ApproachAltitudeHold
{
    private static readonly ILogger Log = SimLog.CreateLogger("ApproachAltitudeHold");

    /// <summary>
    /// The legs of an instrument approach before the landing: the intercept, the approach's fixes, a procedure turn, a
    /// hold-in-lieu of one and the final. The published missed approach (its fixes and its hold, which is not a hold-in-lieu)
    /// is flown away from the runway and is not one.
    /// </summary>
    internal static bool IsApproachPhase(Phase phase) =>
        phase
            is InterceptCoursePhase
                or ApproachNavigationPhase { IsMissedApproach: false }
                or ProcedureTurnPhase
                or HoldingPatternPhase { IsHoldInLieu: true }
                or FinalApproachPhase;

    /// <summary>
    /// True when the aircraft is outside the published final approach fix of its assigned runway
    /// (<see cref="FinalApproachFix.IsInsidePublishedFaf"/> false). With no assigned runway there is nothing to measure from:
    /// false, treated as inside.
    /// </summary>
    internal static bool IsOutsideFaf(AircraftState aircraft, AirportGroundLayout? layout)
    {
        if (aircraft.Phases?.AssignedRunway is not { } runway)
        {
            Log.LogDebug("{Callsign}: no assigned runway to measure the final approach fix from; treated as inside", aircraft.Callsign);
            return false;
        }

        return !FinalApproachFix.IsInsidePublishedFaf(aircraft, runway, layout);
    }

    /// <summary>
    /// After a controller's altitude has been assigned: holds it until established when the aircraft is on an approach leg
    /// outside the final approach fix with its approach clearance still in force.
    /// </summary>
    internal static void HoldIfOutsideFaf(AircraftState aircraft, AirportGroundLayout? layout)
    {
        if ((aircraft.Phases is not { ActiveApproach: { } clearance, CurrentPhase: { } phase }) || !IsApproachPhase(phase))
        {
            return;
        }

        if (!IsOutsideFaf(aircraft, layout))
        {
            return;
        }

        clearance.AltitudeHeldUntilEstablished = true;
        Log.LogDebug("{Callsign}: {Altitude} held until established on {Phase}", aircraft.Callsign, aircraft.Targets.AssignedAltitude, phase.Name);
    }

    /// <summary>True while a controller's altitude is held until established.</summary>
    internal static bool IsHeld(AircraftState aircraft) => aircraft.Phases?.ActiveApproach is { AltitudeHeldUntilEstablished: true };

    /// <summary>Established: the held altitude is released and the published altitudes resume.</summary>
    internal static void Release(AircraftState aircraft, string reason)
    {
        if (aircraft.Phases?.ActiveApproach is not { AltitudeHeldUntilEstablished: true } clearance)
        {
            return;
        }

        clearance.AltitudeHeldUntilEstablished = false;
        aircraft.Targets.AssignedAltitude = null;
        Log.LogDebug("{Callsign}: held altitude released ({Reason}); published altitudes resume", aircraft.Callsign, reason);
    }
}
