using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;

namespace Yaat.Sim.Simulation;

/// <summary>
/// Whether the silently simulated tower may clear a departure for takeoff right now: nobody else is departing, landing,
/// on or fouling the same physical runway (either end), and no arrival to that runway is less than
/// <see cref="ArrivalGateSeconds"/> from its threshold. Intersecting runways are not checked. Modelled on
/// <see cref="ControllerAi.Rules.RunwayCrossingGate"/>, but a fouler closes the runway too.
/// </summary>
internal static class SimulatedTowerRunwayGuard
{
    /// <summary>An arrival to the departure's runway closer than this to its threshold keeps the departure waiting, seconds.</summary>
    public const double ArrivalGateSeconds = 60;

    /// <summary>
    /// True when <paramref name="runway"/> is clear for <paramref name="departure"/> to be cleared for takeoff; otherwise
    /// false, with the first aircraft that keeps it waiting named in <paramref name="reason"/>.
    /// </summary>
    /// <param name="departure">The departure the simulated tower would clear; skipped among the traffic.</param>
    /// <param name="runway">The departure's runway.</param>
    /// <param name="traffic">Every aircraft in the world.</param>
    /// <param name="layout">The airport's ground layout, for the landing threshold and the hold-short bars; may be null.</param>
    /// <param name="reason">Why the runway is not clear; null when it is.</param>
    /// <returns>True when the runway is clear.</returns>
    public static bool IsClear(
        AircraftState departure,
        RunwayInfo runway,
        IEnumerable<AircraftState> traffic,
        AirportGroundLayout? layout,
        out string? reason
    )
    {
        foreach (AircraftState other in traffic)
        {
            if (string.Equals(other.Callsign, departure.Callsign, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            RunwayInfo end = RunwayOccupancy.AlignedEnd(other.TrueTrack.Degrees, runway);
            RunwayUseKind? kind = RunwayOccupancy.Classify(other, end, layout)?.Kind;
            if (RunwayOccupancy.OccupiesSurface(kind) || RunwayOccupancy.FoulsRunway(kind))
            {
                reason = $"{other.Callsign} {kind} runway {end.Designator}";
                return false;
            }

            if (IsArrivalInsideTheGate(other, runway, layout))
            {
                reason = $"{other.Callsign} under {ArrivalGateSeconds:0} s from the threshold of runway {other.Phases!.AssignedRunway!.Designator}";
                return false;
            }
        }

        reason = null;
        return true;
    }

    /// <summary>An airborne arrival assigned to the same physical runway, short of its threshold and less than <see cref="ArrivalGateSeconds"/> from it.</summary>
    private static bool IsArrivalInsideTheGate(AircraftState other, RunwayInfo runway, AirportGroundLayout? layout) =>
        !other.IsOnGround
        && (other.Phases?.AssignedRunway is { } assigned)
        && assigned.Id.Overlaps(runway.Id)
        && (RunwayOccupancy.SecondsToAssignedThreshold(other, assigned, layout) is > 0 and < ArrivalGateSeconds);
}
