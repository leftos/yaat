namespace Yaat.Sim;

/// <summary>
/// Determines whether an aircraft can visually acquire the airport or another aircraft,
/// per FAA 7110.65 §7-4-3 and AIM §5-4-23.
/// Bank angle occlusion per 7110.65 §7-4-4.c.2 NOTE 1 and AIM §4-4-15.
/// </summary>
public static class VisualDetection
{
    private const double SmToNm = 0.869;
    private const double MaxAirportRangeNm = 12.0;
    private const double ClassAFloorFt = 18000.0;

    // Bank occlusion thresholds
    private const double MinBankForOcclusion = 15.0;
    private const double SteepBankThreshold = 25.0;
    private const double SteepBankAltBuffer = 1000.0;
    private const double ModerateBankAltBuffer = 500.0;
    private const double NoseConeDeg = 10.0;

    /// <summary>
    /// Can the aircraft visually acquire the airport?
    /// Checks forward hemisphere, bank occlusion, distance vs visibility, altitude vs ceiling, and Class A.
    /// Pass bankAngleDeg for initial acquisition checks; pass 0 for maintained-contact checks.
    /// </summary>
    public static bool CanSeeAirport(
        AircraftState aircraft,
        double airportLat,
        double airportLon,
        double airportElevation,
        int? ceilingAgl,
        double? visibilitySm,
        double bankAngleDeg = 0.0
    )
    {
        return CanSeeAirportCore(aircraft, airportLat, airportLon, airportElevation, ceilingAgl, visibilitySm, runwayHeading: null, bankAngleDeg);
    }

    /// <summary>
    /// Can the aircraft visually acquire the airport for a specific runway?
    /// In addition to basic visual checks, ensures the aircraft is on the approach
    /// side of the runway (not opposite direction where the pilot would need to
    /// overfly the field to reach the approach end).
    /// Pass bankAngleDeg for initial acquisition checks; pass 0 for maintained-contact checks.
    /// </summary>
    public static bool CanSeeAirportForRunway(
        AircraftState aircraft,
        double airportLat,
        double airportLon,
        double airportElevation,
        int? ceilingAgl,
        double? visibilitySm,
        double runwayHeading,
        double bankAngleDeg = 0.0
    )
    {
        return CanSeeAirportCore(aircraft, airportLat, airportLon, airportElevation, ceilingAgl, visibilitySm, runwayHeading, bankAngleDeg);
    }

    /// <summary>
    /// Can the ownship visually acquire the target aircraft?
    /// Checks forward hemisphere, bank occlusion, distance vs visibility, ceiling separation.
    /// No FL180 gate — pilots can see traffic in Class A; only visual separation is prohibited (7110.65 §7-1-1).
    /// Pass bankAngleDeg for initial acquisition checks; pass 0 for maintained-contact checks.
    /// </summary>
    public static bool CanSeeTraffic(
        AircraftState ownship,
        AircraftState target,
        int? ceilingAgl,
        double airportElevation,
        double? visibilitySm,
        double bankAngleDeg = 0.0
    )
    {
        // Both must be on same side of ceiling (if ceiling exists)
        if (ceilingAgl is not null)
        {
            double ceilingMsl = ceilingAgl.Value + airportElevation;
            bool ownBelow = ownship.Altitude < ceilingMsl;
            bool tgtBelow = target.Altitude < ceilingMsl;
            if (ownBelow != tgtBelow)
            {
                return false;
            }
        }

        // Forward hemisphere check
        double bearing = GeoMath.BearingTo(ownship.Latitude, ownship.Longitude, target.Latitude, target.Longitude);
        double angleDiff = Math.Abs(FlightPhysics.NormalizeAngle(bearing - ownship.Heading));
        if (angleDiff > 90.0)
        {
            return false;
        }

        // Bank angle occlusion: high wing blocks view of targets on that side at/below altitude
        if (IsOccludedByBank(bankAngleDeg, ownship.Heading, bearing, ownship.Altitude, target.Altitude))
        {
            return false;
        }

        // Distance check: max range scales with target aircraft WTG size
        double maxRange = WakeTurbulenceData.TrafficDetectionRangeNm(target.AircraftType, AircraftCategorization.Categorize(target.AircraftType));
        if (visibilitySm is not null)
        {
            maxRange = Math.Min(visibilitySm.Value * SmToNm, maxRange);
        }

        double distance = GeoMath.DistanceNm(ownship.Latitude, ownship.Longitude, target.Latitude, target.Longitude);
        return distance <= maxRange;
    }

    /// <summary>
    /// Check if the target is occluded by the aircraft's bank angle.
    /// During a turn, the high wing blocks the view of targets on that side
    /// at or below the aircraft's altitude.
    /// Per 7110.65 §7-4-4.c.2 NOTE 1 ("belly-up configuration") and AIM §4-4-15.
    /// </summary>
    public static bool IsOccludedByBank(
        double bankAngleDeg,
        double ownshipHeading,
        double bearingToTarget,
        double ownshipAltitude,
        double targetAltitude
    )
    {
        double absBankDeg = Math.Abs(bankAngleDeg);
        if (absBankDeg < MinBankForOcclusion)
        {
            return false;
        }

        // Signed angle from nose to target: positive = right of nose, negative = left
        double signedAngle = FlightPhysics.NormalizeAngle(bearingToTarget - ownshipHeading);

        // Target near the nose (within windscreen cone) → always visible
        if (Math.Abs(signedAngle) < NoseConeDeg)
        {
            return false;
        }

        // High-wing side: right bank (bank > 0) → high wing is LEFT (target signedAngle < 0)
        // Left bank (bank < 0) → high wing is RIGHT (target signedAngle > 0)
        bool targetOnHighWingSide = (bankAngleDeg > 0 && signedAngle < 0) || (bankAngleDeg < 0 && signedAngle > 0);

        if (!targetOnHighWingSide)
        {
            return false;
        }

        // Altitude buffer: steeper banks occlude higher targets
        double altBuffer = absBankDeg >= SteepBankThreshold ? SteepBankAltBuffer : ModerateBankAltBuffer;
        return targetAltitude <= ownshipAltitude + altBuffer;
    }

    private static bool CanSeeAirportCore(
        AircraftState aircraft,
        double airportLat,
        double airportLon,
        double airportElevation,
        int? ceilingAgl,
        double? visibilitySm,
        double? runwayHeading,
        double bankAngleDeg
    )
    {
        // Class A: no visual approaches at or above FL180 (7110.65 §7-2-1.a)
        if (aircraft.Altitude >= ClassAFloorFt)
        {
            return false;
        }

        // Must be below ceiling MSL (if ceiling exists)
        if (ceilingAgl is not null)
        {
            double ceilingMsl = ceilingAgl.Value + airportElevation;
            if (aircraft.Altitude >= ceilingMsl)
            {
                return false;
            }
        }

        // Forward hemisphere check: bearing to airport within ±90° of heading
        double bearing = GeoMath.BearingTo(aircraft.Latitude, aircraft.Longitude, airportLat, airportLon);
        double angleDiff = Math.Abs(FlightPhysics.NormalizeAngle(bearing - aircraft.Heading));
        if (angleDiff > 90.0)
        {
            return false;
        }

        // Bank angle occlusion: airport is always below the aircraft
        if (IsOccludedByBank(bankAngleDeg, aircraft.Heading, bearing, aircraft.Altitude, airportElevation))
        {
            return false;
        }

        // Distance check
        double distance = GeoMath.DistanceNm(aircraft.Latitude, aircraft.Longitude, airportLat, airportLon);
        double maxRange = visibilitySm is not null ? Math.Min(visibilitySm.Value * SmToNm, MaxAirportRangeNm) : MaxAirportRangeNm;
        if (distance > maxRange)
        {
            return false;
        }

        // Runway direction check: aircraft should not need to overfly the airport.
        // The bearing FROM the airport TO the aircraft should be within ±120° of the
        // runway's reciprocal (approach side). This means the aircraft is on the
        // approach side or on a downwind/crosswind that can reasonably join.
        if (runwayHeading is { } rwyHdg)
        {
            double approachSide = (rwyHdg + 180.0) % 360.0;
            double bearingFromAirport = GeoMath.BearingTo(airportLat, airportLon, aircraft.Latitude, aircraft.Longitude);
            double sideAngle = Math.Abs(FlightPhysics.NormalizeAngle(bearingFromAirport - approachSide));
            if (sideAngle > 120.0)
            {
                return false;
            }
        }

        return true;
    }
}
