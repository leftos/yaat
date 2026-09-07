namespace Yaat.Sim.Data;

/// <summary>
/// Resolves the field elevation (ft MSL) under an aircraft and the shared acquisition floor
/// used to decide when a track's displayed altitude rounds to 000. The autotrack passes, the
/// CRC STARS visibility gate (<c>CrcVisibilityTracker</c>) and the training-hub
/// <c>BelowDisplayFloor</c> flag use this so YAAT's own radar agrees with CRC to the foot.
/// </summary>
public static class FieldElevationResolver
{
    /// <summary>
    /// AGL height (ft) at which a track's displayed altitude turns from 000 to 001. The
    /// datablock truncates altitude to hundreds (<c>(int)(alt / 100)</c>), so an aircraft
    /// reads 000 until it is 100 ft above field elevation. Matches Mode C quantisation.
    /// </summary>
    public const double AcquisitionFloorAglFt = 100;

    /// <summary>
    /// Field elevation (ft MSL) under the aircraft. Prefers the assigned runway end, then the
    /// nearest indexed airport to the aircraft's position (the terrain STARS actually displays
    /// over), then the scenario airport, then the filed departure, then 0.
    /// </summary>
    public static double Resolve(AircraftState ac, NavigationDatabase navDb)
    {
        // Most precise: the runway the aircraft is taking off from / landing at.
        if (ac.Phases?.AssignedRunway is { } rwy)
        {
            return rwy.ElevationFt;
        }

        // Position-based: nearest indexed airport's elevation as a terrain proxy.
        // Without this, an inbound aircraft from a high-elevation airport (e.g. KBLU,
        // 5284 ft) flying at 2500 ft over a low-elevation destination would fall below
        // the AGL gate purely because the *filed departure* airport sat above the
        // aircraft. The aircraft's actual position is what STARS displays, so the
        // terrain under that position drives the gate.
        var nearestElev = navDb.FindNearestAirportElevation(ac.Position);
        if (nearestElev is not null)
        {
            return nearestElev.Value;
        }

        // Fallbacks for callers without a populated spatial index (mostly tests):
        // the aircraft's scenario airport, then its filed departure.
        if (!string.IsNullOrEmpty(ac.AirportId))
        {
            var elev = navDb.GetAirportElevation(ac.AirportId);
            if (elev is not null)
            {
                return elev.Value;
            }
        }

        if (!string.IsNullOrEmpty(ac.FlightPlan.Departure))
        {
            return navDb.GetAirportElevation(ac.FlightPlan.Departure) ?? 0;
        }

        return 0;
    }

    /// <summary>
    /// True when the aircraft's displayed altitude rounds to 000 — i.e. it sits below the
    /// acquisition floor (AGL &lt; 100 ft) and should be withheld from radar / coast on STARS.
    /// </summary>
    public static bool IsBelowDisplayFloor(AircraftState ac, NavigationDatabase navDb)
    {
        return ac.Altitude < Resolve(ac, navDb) + AcquisitionFloorAglFt;
    }
}
