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
/// say "5 nm too far" instead of just "too far". For <see cref="VisualAcquisitionFailure.AboveCeiling"/>
/// and <see cref="VisualAcquisitionFailure.MixedCeiling"/> failures, <see cref="BindingLayer"/>
/// identifies the specific BKN/OVC layer that blocked the view so messages can
/// name it ("below BKN070" rather than "below the layer").
/// </summary>
public readonly record struct VisualAcquisitionResult(bool Acquired, VisualAcquisitionFailure Reason, double DistanceNm, double MaxRangeNm)
{
    public MetarParser.CloudLayer? BindingLayer { get; init; }

    public static VisualAcquisitionResult Success(double distanceNm, double maxRangeNm) =>
        new(true, VisualAcquisitionFailure.None, distanceNm, maxRangeNm);

    public static VisualAcquisitionResult Fail(VisualAcquisitionFailure reason, double distanceNm, double maxRangeNm) =>
        new(false, reason, distanceNm, maxRangeNm);

    public static VisualAcquisitionResult FailLayer(
        VisualAcquisitionFailure reason,
        double distanceNm,
        double maxRangeNm,
        MetarParser.CloudLayer bindingLayer
    ) => new(false, reason, distanceNm, maxRangeNm) { BindingLayer = bindingLayer };
}

/// <summary>
/// Determines whether an aircraft can visually acquire the airport or another aircraft,
/// per FAA 7110.65 §7-4-3 and AIM §5-4-23.
/// Bank angle occlusion per 7110.65 §7-4-4.c.2 NOTE 1 and AIM §4-4-15.
/// </summary>
public static class VisualDetection
{
    private const double SmToNm = 0.869;
    private const double ClassAFloorFt = 18000.0;

    // Geometric horizon = 1.23 * sqrt(eye-height in ft), in nm.
    // Half is a defensible upper bound for visual airport acquisition: full
    // horizon ignores haze, scan limits, and the field-of-view problem
    // (finding a small target in a wide sky). At 4,000 ft AGL this gives
    // ~39 nm; at 1,000 ft, ~19 nm; at 100 ft, ~6 nm — all reasonable.
    private const double HorizonNmPerSqrtFt = 1.23;
    private const double HorizonScaleFactor = 0.5;

    // Bank occlusion thresholds
    private const double MinBankForOcclusion = 15.0;
    private const double SteepBankThreshold = 25.0;
    private const double SteepBankAltBuffer = 1000.0;
    private const double ModerateBankAltBuffer = 500.0;
    private const double NoseConeDeg = 10.0;

    /// <summary>
    /// Attempts to visually acquire the airport. Pass <paramref name="bankAngleDeg"/> for
    /// initial acquisition checks; pass 0 for maintained-contact checks.
    /// <paramref name="airportSizeCapNm"/> is the maximum acquisition range driven by
    /// airport conspicuity (lighting, runway length, terrain backdrop) — see
    /// <see cref="VisualAcquisition.AirportSizeCapNm"/> for the canonical lookup.
    /// </summary>
    public static VisualAcquisitionResult TryAcquireAirport(
        AircraftState aircraft,
        double airportLat,
        double airportLon,
        double airportElevation,
        IReadOnlyList<MetarParser.CloudLayer>? layers,
        double? visibilitySm,
        double bankAngleDeg,
        double airportSizeCapNm
    )
    {
        return TryAcquireAirportCore(
            aircraft,
            airportLat,
            airportLon,
            airportElevation,
            layers,
            visibilitySm,
            runwayHeading: null,
            bankAngleDeg,
            airportSizeCapNm
        );
    }

    /// <summary>
    /// Attempts to visually acquire the airport for a specific runway. In addition to
    /// the basic visual checks, ensures the aircraft is on the approach side of the
    /// runway (not on the opposite side, where the pilot would need to overfly the
    /// field). Pass <paramref name="bankAngleDeg"/> for initial acquisition checks;
    /// pass 0 for maintained-contact checks. See <see cref="TryAcquireAirport"/> for
    /// <paramref name="airportSizeCapNm"/>.
    /// </summary>
    public static VisualAcquisitionResult TryAcquireAirportForRunway(
        AircraftState aircraft,
        double airportLat,
        double airportLon,
        double airportElevation,
        IReadOnlyList<MetarParser.CloudLayer>? layers,
        double? visibilitySm,
        TrueHeading runwayHeading,
        double bankAngleDeg,
        double airportSizeCapNm
    )
    {
        return TryAcquireAirportCore(
            aircraft,
            airportLat,
            airportLon,
            airportElevation,
            layers,
            visibilitySm,
            runwayHeading,
            bankAngleDeg,
            airportSizeCapNm
        );
    }

    /// <summary>
    /// Maintained-contact check for the airport, used after the pilot has already
    /// reported the field in sight. Runs ONLY weather-driven obstructions (Class A
    /// floor, BKN/OVC layer above the aircraft); skips geometric checks
    /// (BehindOwnship, OutOfRange, OppositeSideOfRunway, OccludedByBank).
    ///
    /// Rationale: the airport is a multi-acre polygon the pilot is approaching or
    /// overflying. Once acquired, only weather can realistically obscure it. The
    /// initial-acquisition geometric checks (which use the airport reference point
    /// — a single lat/lon — as a proxy) produce false "lost sight of the field"
    /// reports as the aircraft crosses the threshold and the ARP falls behind the
    /// nose, even though the runway is directly under the cockpit.
    ///
    /// DistanceNm/MaxRangeNm in the result are zero (not meaningful for this
    /// regime). Callers should read <see cref="VisualAcquisitionResult.Acquired"/>,
    /// <see cref="VisualAcquisitionResult.Reason"/>, and <see cref="VisualAcquisitionResult.BindingLayer"/>.
    /// </summary>
    public static VisualAcquisitionResult TryMaintainAirportContact(
        AircraftState aircraft,
        double airportElevation,
        IReadOnlyList<MetarParser.CloudLayer>? layers
    )
    {
        // Class A: no visual approaches at or above FL180 (7110.65 §7-2-1.a).
        // An aircraft that climbs back into Class A on a visual is a procedural
        // bust and should drop the visual clearance.
        if (aircraft.Altitude >= ClassAFloorFt)
        {
            return VisualAcquisitionResult.Fail(VisualAcquisitionFailure.InClassA, 0.0, 0.0);
        }

        // BKN/OVC layer between aircraft and ground obscures the field.
        var binding = FindBindingCeilingAbove(aircraft.Altitude, airportElevation, layers);
        if (binding is { } above)
        {
            return VisualAcquisitionResult.FailLayer(VisualAcquisitionFailure.AboveCeiling, 0.0, 0.0, above);
        }

        return VisualAcquisitionResult.Success(0.0, 0.0);
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
        double distance = GeoMath.DistanceNm(ownship.Position, target.Position);
        double maxRange = WakeTurbulenceData.TrafficDetectionRangeNm(target.AircraftType, AircraftCategorization.Categorize(target.AircraftType));
        if (visibilitySm is not null)
        {
            maxRange = Math.Min(visibilitySm.Value * SmToNm, maxRange);
        }

        // Any BKN/OVC layer whose base lies strictly between the two altitudes
        // obstructs the line of sight. FEW/SCT have too many gaps to reliably block.
        var obstructing = FindObstructingLayerBetween(ownship.Altitude, target.Altitude, airportElevation, layers);
        if (obstructing is { } mixed)
        {
            return VisualAcquisitionResult.FailLayer(VisualAcquisitionFailure.MixedCeiling, distance, maxRange, mixed);
        }

        // Forward hemisphere check
        double bearing = GeoMath.BearingTo(ownship.Position, target.Position);
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
    /// Maintained-contact check for traffic the pilot has already reported in sight.
    /// Runs ONLY the weather obstruction (a BKN/OVC layer lying between the two
    /// aircraft) and skips every geometric check — <see cref="VisualAcquisitionFailure.OutOfRange"/>,
    /// <see cref="VisualAcquisitionFailure.BehindOwnship"/>, <see cref="VisualAcquisitionFailure.OccludedByBank"/>.
    ///
    /// <para>
    /// Rationale mirrors <see cref="TryMaintainAirportContact"/>: the geometric checks model the
    /// problem of <em>finding</em> an unknown target in a wide sky. Once the pilot has called the
    /// traffic in sight (the RTIS gate FOLLOW requires) they are actively tracking a known point,
    /// and re-applying the acquisition-range / forward-hemisphere / bank-occlusion gates every tick
    /// produces false "lost sight" reports as the follower flies its own pattern turns or lag-pursues
    /// a lead that is still opening. A lead that merely pulls ahead is <em>increasing</em> separation,
    /// which is never a reason for the follower to break off — that is the controller's to re-sequence
    /// (AIM §5-5-12.a.2 / §4-4-14 NOTE: the pilot reports only when it genuinely cannot maintain
    /// visual contact).
    /// </para>
    ///
    /// <para>
    /// DistanceNm/MaxRangeNm in the result are zero (not meaningful for this regime), matching
    /// <see cref="TryMaintainAirportContact"/>. Callers should read <see cref="VisualAcquisitionResult.Acquired"/>,
    /// <see cref="VisualAcquisitionResult.Reason"/>, and <see cref="VisualAcquisitionResult.BindingLayer"/>.
    /// </para>
    /// </summary>
    public static VisualAcquisitionResult TryMaintainTrafficContact(
        AircraftState ownship,
        AircraftState target,
        IReadOnlyList<MetarParser.CloudLayer>? layers,
        double airportElevation
    )
    {
        var obstructing = FindObstructingLayerBetween(ownship.Altitude, target.Altitude, airportElevation, layers);
        if (obstructing is { } mixed)
        {
            return VisualAcquisitionResult.FailLayer(VisualAcquisitionFailure.MixedCeiling, 0.0, 0.0, mixed);
        }

        return VisualAcquisitionResult.Success(0.0, 0.0);
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
        double bankAngleDeg,
        double airportSizeCapNm
    )
    {
        double distance = GeoMath.DistanceNm(aircraft.Position, new LatLon(airportLat, airportLon));

        // Multi-factor acquisition range. Neither 7110.65 §7-4-3 nor AIM §5-4-23
        // prescribe a distance limit ("airport in sight" is the only criterion);
        // AIM §5-4-24.6 (CVFP design) treats 20 nm acquisition as routine. We
        // model the realistic limiters: METAR visibility (hard ceiling), the
        // observer's geometric horizon scaled by HorizonScaleFactor (haze, scan,
        // field-of-view), and an airport-conspicuity cap (large hub vs GA field).
        double altAgl = Math.Max(0, aircraft.Altitude - airportElevation);
        double horizonNm = HorizonScaleFactor * HorizonNmPerSqrtFt * Math.Sqrt(altAgl);
        double maxRange = Math.Min(horizonNm, airportSizeCapNm);
        if (visibilitySm is not null)
        {
            maxRange = Math.Min(maxRange, visibilitySm.Value * SmToNm);
        }

        // Class A: no visual approaches at or above FL180 (7110.65 §7-2-1.a)
        if (aircraft.Altitude >= ClassAFloorFt)
        {
            return VisualAcquisitionResult.Fail(VisualAcquisitionFailure.InClassA, distance, maxRange);
        }

        // Aircraft must be below every BKN/OVC layer — if it's at or above any of
        // them, the deck obstructs the view of the field. Surface the lowest such
        // layer as the binding one so the failure message can name it.
        var binding = FindBindingCeilingAbove(aircraft.Altitude, airportElevation, layers);
        if (binding is { } above)
        {
            return VisualAcquisitionResult.FailLayer(VisualAcquisitionFailure.AboveCeiling, distance, maxRange, above);
        }

        // Forward hemisphere check: bearing to airport within ±90° of heading
        double bearing = GeoMath.BearingTo(aircraft.Position, new LatLon(airportLat, airportLon));
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
            double bearingFromAirport = GeoMath.BearingTo(new LatLon(airportLat, airportLon), aircraft.Position);
            double sideAngle = approachSide.AbsAngleTo(new TrueHeading(bearingFromAirport));
            if (sideAngle > 120.0)
            {
                return VisualAcquisitionResult.Fail(VisualAcquisitionFailure.OppositeSideOfRunway, distance, maxRange);
            }
        }

        return VisualAcquisitionResult.Success(distance, maxRange);
    }

    /// <summary>
    /// Returns the lowest BKN/OVC layer whose base MSL lies strictly between the
    /// two aircraft altitudes, i.e. one aircraft is below the layer and the other
    /// is at or above it. Returns null if no obstructing layer separates them.
    /// FEW and SCT layers are ignored — they have gaps and don't reliably block
    /// the line of sight.
    /// </summary>
    public static MetarParser.CloudLayer? FindObstructingLayerBetween(
        double altitudeMslA,
        double altitudeMslB,
        double airportElevation,
        IReadOnlyList<MetarParser.CloudLayer>? layers
    )
    {
        if (layers is null)
        {
            return null;
        }
        double low = Math.Min(altitudeMslA, altitudeMslB);
        double high = Math.Max(altitudeMslA, altitudeMslB);
        MetarParser.CloudLayer? binding = null;
        foreach (var layer in layers)
        {
            if (layer.Cover is not (MetarParser.CloudCover.Broken or MetarParser.CloudCover.Overcast))
            {
                continue;
            }
            double baseMsl = layer.BaseFeetAgl + airportElevation;
            // Strictly between: low aircraft is genuinely below the base, high aircraft
            // is at or above it. Preserves the original "(A < base) != (B < base)" semantic.
            if (low < baseMsl && baseMsl <= high)
            {
                if (binding is null || layer.BaseFeetAgl < binding.BaseFeetAgl)
                {
                    binding = layer;
                }
            }
        }
        return binding;
    }

    /// <summary>
    /// Returns the lowest BKN/OVC layer that the aircraft is at or above. Any such
    /// layer blocks the view of the ground (and therefore the field) for an
    /// observer above it. Returns null if the aircraft is below every BKN/OVC
    /// layer — i.e. the view downward is clear.
    /// </summary>
    public static MetarParser.CloudLayer? FindBindingCeilingAbove(
        double altitudeMsl,
        double airportElevation,
        IReadOnlyList<MetarParser.CloudLayer>? layers
    )
    {
        if (layers is null)
        {
            return null;
        }
        MetarParser.CloudLayer? binding = null;
        foreach (var layer in layers)
        {
            if (layer.Cover is not (MetarParser.CloudCover.Broken or MetarParser.CloudCover.Overcast))
            {
                continue;
            }
            double baseMsl = layer.BaseFeetAgl + airportElevation;
            if (altitudeMsl >= baseMsl)
            {
                if (binding is null || layer.BaseFeetAgl < binding.BaseFeetAgl)
                {
                    binding = layer;
                }
            }
        }
        return binding;
    }
}
