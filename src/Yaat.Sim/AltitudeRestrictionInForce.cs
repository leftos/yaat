using Microsoft.Extensions.Logging;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases.Pattern;

namespace Yaat.Sim;

/// <summary>
/// Whether a route altitude restriction binds the aircraft now, by who wrote it
/// (<see cref="CifpAltitudeRestriction.Source"/>). A controller's crossing always does; a point a pattern phase inserts
/// does while that phase flies; a published approach altitude does once the aircraft is established on a segment of a
/// cleared approach: an active approach, or a deferred one once the aircraft is past the fix its altitudes wait for,
/// keeping its last assigned altitude until then (AIM 5-4-7.f; 7110.65 4-8-1.a.4). A published SID or STAR altitude does
/// only under a via clearance, or on a SID departure released with no controller altitude at all (AIM 5-2-9.h.8 for a
/// SID, 5-4-1.a.2 for a STAR; 7110.65 4-5-7.h). A maintain clearance off a via therefore leaves the published altitudes
/// on the route but out of force, while their speeds still apply.
/// </summary>
internal static class AltitudeRestrictionInForce
{
    private static readonly ILogger Log = SimLog.CreateLogger("AltitudeRestrictionInForce");

    /// <summary>
    /// Whether a descend via (<see cref="AircraftProcedure.StarViaMode"/>) or a climb via
    /// (<see cref="AircraftProcedure.ClimbViaTargetFt"/>) is in force. Unlike
    /// <see cref="AircraftProcedure.ProcedureSpeedsInForce"/>, a SID departure flying its SID with no climb via is not.
    /// </summary>
    /// <param name="aircraft">The aircraft whose clearance is read.</param>
    /// <returns>True under a descend via or a climb via.</returns>
    internal static bool ViaClearanceInForce(AircraftState aircraft) =>
        aircraft.Procedure.StarViaMode || (aircraft.Procedure.ClimbViaTargetFt is not null);

    /// <summary>
    /// Whether a SID's or STAR's published altitudes bind the aircraft: under a via clearance, or on a SID departure with
    /// no controller altitude anywhere (no climb via, no assigned altitude, no TDLS "maintain" value), which is the no-data
    /// fallback treated as climb via. A TDLS "maintain" altitude (<see cref="AircraftProcedure.SidInitialAltitudeFt"/>) is a
    /// controller altitude, so the SID's published altitudes are not in force under it (AIM 5-2-9.h.8 Example 2).
    /// </summary>
    /// <param name="aircraft">The aircraft whose clearance is read.</param>
    /// <returns>True when published procedure altitudes are in force.</returns>
    internal static bool ProcedureAltitudesInForce(AircraftState aircraft) =>
        ViaClearanceInForce(aircraft)
        || (
            aircraft.Procedure.SidViaMode
            && (aircraft.Procedure.ClimbViaTargetFt is null)
            && (aircraft.Targets.AssignedAltitude is null)
            && (aircraft.Procedure.SidInitialAltitudeFt is null)
        );

    /// <summary>Whether <paramref name="restriction"/> binds <paramref name="aircraft"/> now, by its source.</summary>
    /// <param name="aircraft">The aircraft flying the route.</param>
    /// <param name="restriction">A route fix's altitude restriction.</param>
    /// <returns>True when the restriction is in force.</returns>
    internal static bool IsInForce(AircraftState aircraft, CifpAltitudeRestriction restriction)
    {
        bool inForce = restriction.Source switch
        {
            CifpAltitudeRestrictionSource.Atc => true,
            CifpAltitudeRestrictionSource.System => aircraft.Phases?.CurrentPhase is PatternEntryPhase or TeardropReentryPhase,
            CifpAltitudeRestrictionSource.Procedure => ProcedureAltitudesInForce(aircraft),
            CifpAltitudeRestrictionSource.Approach => PendingApproachEstablished(aircraft) || (aircraft.Phases?.ActiveApproach is not null),
            _ => throw new ArgumentOutOfRangeException(
                nameof(restriction),
                restriction.Source,
                $"Undefined altitude restriction source {(int)restriction.Source}"
            ),
        };

        if (!inForce && Log.IsEnabled(LogLevel.Trace))
        {
            Log.LogTrace(
                "{Callsign}: {Source} {Type} {Altitude} not in force",
                aircraft.Callsign,
                restriction.Source,
                restriction.Type,
                restriction.Altitude1Ft
            );
        }

        return inForce;
    }

    /// <summary>
    /// Whether a deferred approach clearance is held and the aircraft is past the fix the approach joins
    /// (<see cref="Phases.PendingApproachInfo.ConnectingFix"/>): that fix is no longer on the route, by a case-insensitive
    /// name match, whether it was sequenced or dropped by a direct. A clearance with no connecting fix (restored from a
    /// pre-V45 snapshot) counts as past it.
    /// </summary>
    internal static bool PendingApproachEstablished(AircraftState aircraft)
    {
        if (aircraft.Approach.PendingClearance is not { } pending)
        {
            return false;
        }

        if (pending.ConnectingFix is not { } connectingFix)
        {
            return true;
        }

        return !aircraft.Targets.NavigationRoute.Exists(t => t.Name.Equals(connectingFix, StringComparison.OrdinalIgnoreCase));
    }
}
