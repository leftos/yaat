namespace Yaat.Sim;

/// <summary>
/// Determines whether an aircraft can visually acquire the airport or another aircraft,
/// per FAA 7110.65 §7-4-3 and AIM §5-4-23.
/// </summary>
public static class VisualDetection
{
    private const double SmToNm = 0.869;
    private const double MaxAirportRangeNm = 12.0;
    private const double MaxTrafficRangeNm = 8.0;
    private const double ClassAFloorFt = 18000.0;

    /// <summary>
    /// Can the aircraft visually acquire the airport?
    /// Checks forward hemisphere, distance vs visibility, altitude vs ceiling, and Class A.
    /// </summary>
    public static bool CanSeeAirport(
        AircraftState aircraft,
        double airportLat,
        double airportLon,
        double airportElevation,
        int? ceilingAgl,
        double? visibilitySm
    )
    {
        return CanSeeAirportCore(aircraft, airportLat, airportLon, airportElevation, ceilingAgl, visibilitySm, runwayHeading: null);
    }

    /// <summary>
    /// Can the aircraft visually acquire the airport for a specific runway?
    /// In addition to basic visual checks, ensures the aircraft is on the approach
    /// side of the runway (not opposite direction where the pilot would need to
    /// overfly the field to reach the approach end).
    /// </summary>
    public static bool CanSeeAirportForRunway(
        AircraftState aircraft,
        double airportLat,
        double airportLon,
        double airportElevation,
        int? ceilingAgl,
        double? visibilitySm,
        double runwayHeading
    )
    {
        return CanSeeAirportCore(aircraft, airportLat, airportLon, airportElevation, ceilingAgl, visibilitySm, runwayHeading);
    }

    /// <summary>
    /// Can the ownship visually acquire the target aircraft?
    /// Checks forward hemisphere, distance vs visibility, ceiling separation, and Class A.
    /// </summary>
    public static bool CanSeeTraffic(AircraftState ownship, AircraftState target, int? ceilingAgl, double airportElevation, double? visibilitySm)
    {
        // Class A: no visual acquisition above FL180
        if (ownship.Altitude >= ClassAFloorFt)
        {
            return false;
        }

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

        // Distance check
        double distance = GeoMath.DistanceNm(ownship.Latitude, ownship.Longitude, target.Latitude, target.Longitude);
        double maxRange = visibilitySm is not null ? Math.Min(visibilitySm.Value * SmToNm, MaxTrafficRangeNm) : MaxTrafficRangeNm;
        return distance <= maxRange;
    }

    private static bool CanSeeAirportCore(
        AircraftState aircraft,
        double airportLat,
        double airportLon,
        double airportElevation,
        int? ceilingAgl,
        double? visibilitySm,
        double? runwayHeading
    )
    {
        // Class A: no visual approaches at or above FL180
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
