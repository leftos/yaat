namespace Yaat.Sim;

/// <summary>
/// Reasons an attempted visual acquisition of an airport or another aircraft
/// can fail. These map directly to the ordered checks inside
/// <see cref="VisualDetection"/> and exist so RPO-facing messages can explain
/// exactly why the pilot cannot see the target.
/// </summary>
public enum VisualAcquisitionFailure
{
    /// <summary>Acquisition succeeded.</summary>
    None,

    /// <summary>Ownship is at or above the Class A floor (FL180). Airport only.</summary>
    InClassA,

    /// <summary>Ownship is at or above the reported ceiling — field is in IMC. Airport only.</summary>
    AboveCeiling,

    /// <summary>Ownship and target are on opposite sides of the cloud layer. Traffic only.</summary>
    MixedCeiling,

    /// <summary>Target bearing lies outside the ±90° forward hemisphere of the ownship.</summary>
    BehindOwnship,

    /// <summary>During a turn, the high wing blocks the view of the target.</summary>
    OccludedByBank,

    /// <summary>Distance exceeds the reported visibility or the maximum detection range.</summary>
    OutOfRange,

    /// <summary>Airport-with-runway variant: ownship would have to overfly the field to reach the approach end.</summary>
    OppositeSideOfRunway,
}

/// <summary>
/// Result of a visual acquisition attempt. Always carries the computed distance
/// and the maximum range used for the distance check so failure messages can
/// say "5 nm too far" instead of just "too far".
/// </summary>
public readonly record struct VisualAcquisitionResult(bool Acquired, VisualAcquisitionFailure Reason, double DistanceNm, double MaxRangeNm)
{
    public static VisualAcquisitionResult Success(double distanceNm, double maxRangeNm) =>
        new(true, VisualAcquisitionFailure.None, distanceNm, maxRangeNm);

    public static VisualAcquisitionResult Fail(VisualAcquisitionFailure reason, double distanceNm, double maxRangeNm) =>
        new(false, reason, distanceNm, maxRangeNm);
}

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
    /// Attempts to visually acquire the airport. Pass <paramref name="bankAngleDeg"/> for
    /// initial acquisition checks; pass 0 for maintained-contact checks.
    /// </summary>
    public static VisualAcquisitionResult TryAcquireAirport(
        AircraftState aircraft,
        double airportLat,
        double airportLon,
        double airportElevation,
        IReadOnlyList<MetarParser.CloudLayer>? layers,
        double? visibilitySm,
        double bankAngleDeg
    )
    {
        return TryAcquireAirportCore(aircraft, airportLat, airportLon, airportElevation, layers, visibilitySm, runwayHeading: null, bankAngleDeg);
    }

    /// <summary>
    /// Attempts to visually acquire the airport for a specific runway. In addition to
    /// the basic visual checks, ensures the aircraft is on the approach side of the
    /// runway (not on the opposite side, where the pilot would need to overfly the
    /// field). Pass <paramref name="bankAngleDeg"/> for initial acquisition checks;
    /// pass 0 for maintained-contact checks.
    /// </summary>
    public static VisualAcquisitionResult TryAcquireAirportForRunway(
        AircraftState aircraft,
        double airportLat,
        double airportLon,
        double airportElevation,
        IReadOnlyList<MetarParser.CloudLayer>? layers,
        double? visibilitySm,
        TrueHeading runwayHeading,
        double bankAngleDeg
    )
    {
        return TryAcquireAirportCore(aircraft, airportLat, airportLon, airportElevation, layers, visibilitySm, runwayHeading, bankAngleDeg);
    }

    /// <summary>
    /// Attempts to visually acquire another aircraft. No FL180 gate — pilots can see
    /// traffic in Class A; only visual separation is prohibited (7110.65 §7-1-1).
    /// Pass <paramref name="bankAngleDeg"/> for initial acquisition checks; pass 0 for
    /// maintained-contact checks.
    /// </summary>
    public static VisualAcquisitionResult TryAcquireTraffic(
        AircraftState ownship,
        AircraftState target,
        IReadOnlyList<MetarParser.CloudLayer>? layers,
        double airportElevation,
        double? visibilitySm,
        double bankAngleDeg
    )
    {
        double distance = GeoMath.DistanceNm(ownship.Latitude, ownship.Longitude, target.Latitude, target.Longitude);
        double maxRange = WakeTurbulenceData.TrafficDetectionRangeNm(target.AircraftType, AircraftCategorization.Categorize(target.AircraftType));
        if (visibilitySm is not null)
        {
            maxRange = Math.Min(visibilitySm.Value * SmToNm, maxRange);
        }

        // Both must be on same side of the lowest BKN/OVC layer (FEW/SCT ignored).
        // C2 will replace this with a per-layer obstruction check.
        int? lowestObstructingAgl = LowestObstructingLayerAgl(layers);
        if (lowestObstructingAgl is not null)
        {
            double ceilingMsl = lowestObstructingAgl.Value + airportElevation;
            bool ownBelow = ownship.Altitude < ceilingMsl;
            bool tgtBelow = target.Altitude < ceilingMsl;
            if (ownBelow != tgtBelow)
            {
                return VisualAcquisitionResult.Fail(VisualAcquisitionFailure.MixedCeiling, distance, maxRange);
            }
        }

        // Forward hemisphere check
        double bearing = GeoMath.BearingTo(ownship.Latitude, ownship.Longitude, target.Latitude, target.Longitude);
        double angleDiff = ownship.TrueHeading.AbsAngleTo(new TrueHeading(bearing));
        if (angleDiff > 90.0)
        {
            return VisualAcquisitionResult.Fail(VisualAcquisitionFailure.BehindOwnship, distance, maxRange);
        }

        // Bank angle occlusion: high wing blocks view of targets on that side at/below altitude
        if (IsOccludedByBank(bankAngleDeg, ownship.TrueHeading, new TrueHeading(bearing), ownship.Altitude, target.Altitude))
        {
            return VisualAcquisitionResult.Fail(VisualAcquisitionFailure.OccludedByBank, distance, maxRange);
        }

        if (distance > maxRange)
        {
            return VisualAcquisitionResult.Fail(VisualAcquisitionFailure.OutOfRange, distance, maxRange);
        }

        return VisualAcquisitionResult.Success(distance, maxRange);
    }

    /// <summary>
    /// Check if the target is occluded by the aircraft's bank angle.
    /// During a turn, the high wing blocks the view of targets on that side
    /// at or below the aircraft's altitude.
    /// Per 7110.65 §7-4-4.c.2 NOTE 1 ("belly-up configuration") and AIM §4-4-15.
    /// </summary>
    public static bool IsOccludedByBank(
        double bankAngleDeg,
        TrueHeading ownshipHeading,
        TrueHeading bearingToTarget,
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
        double signedAngle = ownshipHeading.SignedAngleTo(bearingToTarget);

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

    private static VisualAcquisitionResult TryAcquireAirportCore(
        AircraftState aircraft,
        double airportLat,
        double airportLon,
        double airportElevation,
        IReadOnlyList<MetarParser.CloudLayer>? layers,
        double? visibilitySm,
        TrueHeading? runwayHeading,
        double bankAngleDeg
    )
    {
        double distance = GeoMath.DistanceNm(aircraft.Latitude, aircraft.Longitude, airportLat, airportLon);
        double maxRange = visibilitySm is not null ? Math.Min(visibilitySm.Value * SmToNm, MaxAirportRangeNm) : MaxAirportRangeNm;

        // Class A: no visual approaches at or above FL180 (7110.65 §7-2-1.a)
        if (aircraft.Altitude >= ClassAFloorFt)
        {
            return VisualAcquisitionResult.Fail(VisualAcquisitionFailure.InClassA, distance, maxRange);
        }

        // Must be below the lowest BKN/OVC layer (FEW/SCT ignored).
        // C2 will replace this with an "above any BKN/OVC" check that surfaces the binding layer.
        int? lowestObstructingAgl = LowestObstructingLayerAgl(layers);
        if (lowestObstructingAgl is not null)
        {
            double ceilingMsl = lowestObstructingAgl.Value + airportElevation;
            if (aircraft.Altitude >= ceilingMsl)
            {
                return VisualAcquisitionResult.Fail(VisualAcquisitionFailure.AboveCeiling, distance, maxRange);
            }
        }

        // Forward hemisphere check: bearing to airport within ±90° of heading
        double bearing = GeoMath.BearingTo(aircraft.Latitude, aircraft.Longitude, airportLat, airportLon);
        double angleDiff = aircraft.TrueHeading.AbsAngleTo(new TrueHeading(bearing));
        if (angleDiff > 90.0)
        {
            return VisualAcquisitionResult.Fail(VisualAcquisitionFailure.BehindOwnship, distance, maxRange);
        }

        // Bank angle occlusion: airport is always below the aircraft
        if (IsOccludedByBank(bankAngleDeg, aircraft.TrueHeading, new TrueHeading(bearing), aircraft.Altitude, airportElevation))
        {
            return VisualAcquisitionResult.Fail(VisualAcquisitionFailure.OccludedByBank, distance, maxRange);
        }

        if (distance > maxRange)
        {
            return VisualAcquisitionResult.Fail(VisualAcquisitionFailure.OutOfRange, distance, maxRange);
        }

        // Runway direction check: aircraft should not need to overfly the airport.
        // The bearing FROM the airport TO the aircraft should be within ±120° of the
        // runway's reciprocal (approach side). This means the aircraft is on the
        // approach side or on a downwind/crosswind that can reasonably join.
        if (runwayHeading is { } rwyHdg)
        {
            TrueHeading approachSide = rwyHdg.ToReciprocal();
            double bearingFromAirport = GeoMath.BearingTo(airportLat, airportLon, aircraft.Latitude, aircraft.Longitude);
            double sideAngle = approachSide.AbsAngleTo(new TrueHeading(bearingFromAirport));
            if (sideAngle > 120.0)
            {
                return VisualAcquisitionResult.Fail(VisualAcquisitionFailure.OppositeSideOfRunway, distance, maxRange);
            }
        }

        return VisualAcquisitionResult.Success(distance, maxRange);
    }

    private static int? LowestObstructingLayerAgl(IReadOnlyList<MetarParser.CloudLayer>? layers)
    {
        if (layers is null)
        {
            return null;
        }
        int? lowest = null;
        foreach (var layer in layers)
        {
            if (layer.Cover is MetarParser.CloudCover.Broken or MetarParser.CloudCover.Overcast)
            {
                if (lowest is null || layer.BaseFeetAgl < lowest)
                {
                    lowest = layer.BaseFeetAgl;
                }
            }
        }
        return lowest;
    }
}
