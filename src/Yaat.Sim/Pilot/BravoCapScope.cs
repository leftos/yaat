using Microsoft.Extensions.Logging;
using Yaat.Sim.Data.Airspace;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Pilot;

/// <summary>
/// Who the Class B cap (<see cref="BravoCap"/>) applies to and where it sits. The cap applies to an airborne VFR aircraft
/// in any running phase except a military route, aerial refuelling and the free-flight boundary hold itself, which own the
/// ceiling themselves; a departure from an airport inside a Class B surface area is treated as cleared and never capped.
/// The caller gates on solo training: in an RPO session nothing here runs.
/// </summary>
public static class BravoCapScope
{
    /// <summary>How far ahead, in seconds, the cap looks for a shelf to sit under and for the footprint to clear.</summary>
    public const double LookaheadSeconds = 60.0;

    private const double LateralSampleSeconds = 10.0;

    private static readonly ILogger Log = SimLog.CreateLogger("BravoCapScope");

    /// <summary>Whether a new cap may start for <paramref name="aircraft"/>: airborne, VFR, in a running phase the cap covers.</summary>
    public static bool Applies(AircraftState aircraft, AirspaceDatabase airspace) =>
        !aircraft.IsOnGround
        && aircraft.FlightPlan.IsVfr
        && (aircraft.Phases is { IsComplete: false } phases)
        && IsScopePhase(phases.CurrentPhase)
        && !IsSurfaceAreaDeparture(aircraft, airspace);

    /// <summary>Whether <paramref name="phase"/> is one the cap runs in: any phase but the ones that own the ceiling themselves.</summary>
    public static bool IsScopePhase(Phase? phase) =>
        (phase is not null) && (phase is not (MilitaryRoutePhase or AerialRefuelingAnchorPhase or AirspaceBoundaryHoldPhase));

    /// <summary>
    /// The shelf the aircraft caps under: the lowest floor above it among the volumes laterally over it, else the volume a
    /// projected entry within <see cref="LookaheadSeconds"/> reaches while the aircraft is still below its floor (an
    /// aircraft above the Class B descending into it gets no cap: the cap would drive it down through the Class B). Only a
    /// Class B the aircraft is not cleared into and not already inside counts. Null when there is none.
    /// </summary>
    public static AirspaceVolume? FindCappingVolume(AircraftState aircraft, AirspaceDatabase airspace) =>
        LowestShelfOver(aircraft, airspace) ?? ProjectedCappingVolume(aircraft, airspace);

    /// <summary>The lowest-floor volume laterally over the aircraft, floor above it, of a Class B it may be capped under.</summary>
    private static AirspaceVolume? LowestShelfOver(AircraftState aircraft, AirspaceDatabase airspace)
    {
        AirspaceVolume? best = null;
        foreach ((string area, IReadOnlyList<AirspaceVolume> volumes) in airspace.ClassBAreas)
        {
            if (!IsCappable(aircraft, area, airspace))
            {
                continue;
            }

            foreach (AirspaceVolume volume in volumes)
            {
                bool above = (volume.LowerFtMsl > aircraft.Altitude) && volume.ContainsLateral(aircraft.Position);
                if (above && ((best is null) || (volume.LowerFtMsl < best.LowerFtMsl)))
                {
                    best = volume;
                }
            }
        }

        return best;
    }

    /// <summary>
    /// The volume a projected Class B entry within <see cref="LookaheadSeconds"/> reaches, while the aircraft is still below
    /// its floor; null for an entry at or above the floor (from above the Class B).
    /// </summary>
    private static AirspaceVolume? ProjectedCappingVolume(AircraftState aircraft, AirspaceDatabase airspace)
    {
        AirspaceBoundaryCrossing? crossing = airspace.FindFirstProjectedEntry(aircraft, LookaheadSeconds, AirspaceClass.Bravo);
        if ((crossing is null) || !IsCappable(aircraft, AirspaceDatabase.ClassBAreaName(crossing.Volume), airspace))
        {
            return null;
        }

        if (aircraft.Altitude >= crossing.Volume.LowerFtMsl)
        {
            if (Log.IsEnabled(LogLevel.Debug))
            {
                Log.LogDebug(
                    "{Callsign}: projected Class B entry into {Volume} from {Altitude:F0} ft, at or above its floor {Floor}: no cap",
                    aircraft.Callsign,
                    crossing.Volume.Id,
                    aircraft.Altitude,
                    crossing.Volume.LowerFtMsl
                );
            }

            return null;
        }

        return crossing.Volume;
    }

    /// <summary>
    /// The volumes of the Class B <paramref name="area"/> the aircraft is under, or reaches along its straight track within
    /// <see cref="LookaheadSeconds"/> (sampled every <see cref="LateralSampleSeconds"/>), below their top: the shelves a
    /// standing cap is judged against for lowering.
    /// </summary>
    public static IEnumerable<AirspaceVolume> ShelvesAlongTrack(AircraftState aircraft, string area, AirspaceDatabase airspace)
    {
        IReadOnlyList<AirspaceVolume> volumes = airspace.ClassBArea(area);
        var points = new List<LatLon>();
        for (double seconds = 0; seconds <= LookaheadSeconds; seconds += LateralSampleSeconds)
        {
            points.Add((seconds == 0) ? aircraft.Position : AirspaceDatabase.ProjectPosition(aircraft, seconds));
        }

        return volumes.Where(volume => (aircraft.Altitude < volume.UpperFtMsl) && points.Any(volume.ContainsLateral));
    }

    /// <summary>
    /// The controller-assigned altitude a new cap under <paramref name="volume"/> cannot let the pilot fly: one at or above
    /// the shelf floor, from a maintain or an at-or-above. An at-or-below bound draws nothing: flying under it complies.
    /// </summary>
    public static int? ConflictingAssignment(AircraftState aircraft, AirspaceVolume volume)
    {
        if ((aircraft.Targets.AssignedAltitude is not { } assigned) || (assigned < volume.LowerFtMsl))
        {
            return null;
        }

        return ((aircraft.Targets.AltitudeCeiling is { } bound) && (bound == assigned)) ? null : (int)assigned;
    }

    /// <summary>Why the cap ends now, or null while it stands: never for a phase change, a list replacement or a hold's end.</summary>
    public static string? ReleaseReason(AircraftState aircraft, BravoCap cap, AirspaceDatabase airspace)
    {
        if (aircraft.IsOnGround)
        {
            return "on the ground";
        }

        if (!aircraft.FlightPlan.IsVfr)
        {
            return "IFR";
        }

        if (aircraft.IsClearedIntoBravo(cap.AreaName))
        {
            return "cleared into the Class B";
        }

        return IsLaterallyClear(aircraft, cap.AreaName, airspace) ? "laterally clear of the Class B" : null;
    }

    /// <summary>
    /// Whether the aircraft is laterally clear of every volume of the Class B <paramref name="area"/> now and along its
    /// straight track for <see cref="LookaheadSeconds"/>, sampled every <see cref="LateralSampleSeconds"/>.
    /// </summary>
    public static bool IsLaterallyClear(AircraftState aircraft, string area, AirspaceDatabase airspace)
    {
        IReadOnlyList<AirspaceVolume> volumes = airspace.ClassBArea(area);
        for (double seconds = 0; seconds <= LookaheadSeconds; seconds += LateralSampleSeconds)
        {
            LatLon point = (seconds == 0) ? aircraft.Position : AirspaceDatabase.ProjectPosition(aircraft, seconds);
            if (volumes.Any(volume => volume.ContainsLateral(point)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// A departure (its list holds a takeoff or initial climb) from a runway laterally inside a Class B volume whose floor is
    /// at or below the field: inside the surface area, treated as cleared (7110.65 §7-9-3.c).
    /// </summary>
    public static bool IsSurfaceAreaDeparture(AircraftState aircraft, AirspaceDatabase airspace)
    {
        if ((aircraft.Phases is not { AssignedRunway: { } runway } phases) || !phases.Phases.Any(IsDeparturePhase))
        {
            return false;
        }

        var threshold = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude);
        return airspace.ClassBAreas.Values.Any(volumes =>
            volumes.Any(volume => (volume.LowerFtMsl <= runway.AirportElevationFt) && volume.ContainsLateral(threshold))
        );
    }

    private static bool IsDeparturePhase(Phase phase) => phase is TakeoffPhase or HelicopterTakeoffPhase or InitialClimbPhase;

    private static bool IsCappable(AircraftState aircraft, string area, AirspaceDatabase airspace) =>
        !aircraft.IsClearedIntoBravo(area)
        && !airspace.FindClassBContaining(aircraft.Position, aircraft.Altitude).Any(volume => IsInArea(volume, area));

    private static bool IsInArea(AirspaceVolume volume, string area) =>
        string.Equals(AirspaceDatabase.ClassBAreaName(volume), area, StringComparison.OrdinalIgnoreCase);
}
