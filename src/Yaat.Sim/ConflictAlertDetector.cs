using Yaat.Sim.Data;
using Yaat.Sim.Phases;

namespace Yaat.Sim;

/// <summary>
/// Geometric runway approach corridor at an internal-airport runway. STARS suppresses
/// conflict alerts for any track inside such a volume — independent of the track's
/// phase of flight, approach state, or destination. Anchored at <see cref="Threshold"/>
/// and extending out along <see cref="OutboundCourse"/> (the reciprocal of runway heading).
/// </summary>
public record RunwayCorridor(LatLon Threshold, TrueHeading OutboundCourse, double FieldElevationFt);

/// <summary>
/// Bundles per-tick detection parameters for <see cref="ConflictAlertDetector"/>.
/// Build <paramref name="ApproachCorridors"/> via <see cref="ConflictAlertDetector.BuildCorridors"/>.
/// </summary>
public record ConflictAlertContext(HashSet<string> ExistingConflictIds, IReadOnlyList<RunwayCorridor> ApproachCorridors);

/// <summary>
/// STARS-style Conflict Alert (CA) detection.
/// Called once per tick after physics. Predicts each aircraft's position 5 seconds
/// into the future and reports pairs whose current or predicted separation violates
/// thresholds, provided separation is not increasing.
/// </summary>
public static class ConflictAlertDetector
{
    private const double PredictionSeconds = 5.0;

    // Standard STARS CA thresholds — applied to all associated tracks regardless of flight rules
    private const double HorizontalNm = 3.0;
    private const double VerticalFt = 1000;

    // Hysteresis thresholds — must exceed these to clear an existing alert
    private const double HysteresisHorizontalNm = 3.3;
    private const double HysteresisVerticalFt = 1100;

    // Final approach suppression zone (anchored at runway threshold)
    private const double ApproachZoneHalfWidthNm = 2.0;
    private const double ApproachZoneLengthNm = 30.0;
    private const double ApproachZoneCeilingAboveGsFt = 1500;

    // Glideslope: tan(3°) * 6076.12 ft/NM ≈ 318 ft/NM
    private const double GlideSlopeFtPerNm = 318.0;

    public record ConflictPair(string CallsignA, string CallsignB, string Id);

    /// <summary>
    /// Detect pairs of aircraft in a separation conflict.
    /// Compares both current and 5-second-predicted separation against thresholds.
    /// Pairs where separation is increasing in both dimensions are suppressed.
    /// </summary>
    public static List<ConflictPair> Detect(List<AircraftState> aircraft, ConflictAlertContext context)
    {
        var results = new List<ConflictPair>();
        var eligible = FilterEligible(aircraft);

        for (int i = 0; i < eligible.Count; i++)
        {
            for (int j = i + 1; j < eligible.Count; j++)
            {
                var a = eligible[i];
                var b = eligible[j];

                string id = MakeConflictId(a.Callsign, b.Callsign);
                bool alreadyInConflict = context.ExistingConflictIds.Contains(id);

                if (IsInConflict(a, b, alreadyInConflict, context))
                {
                    results.Add(new ConflictPair(a.Callsign, b.Callsign, id));
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Build the per-runway approach corridor volumes for a list of internal airports.
    /// Each physical runway emits two corridors (one per runway end) anchored at that
    /// end's threshold and extending along its reciprocal heading. Tries the LID first,
    /// then a "K"-prefixed ICAO fallback (mirrors <c>ApproachGateDatabase</c>).
    /// </summary>
    public static IReadOnlyList<RunwayCorridor> BuildCorridors(IEnumerable<string> internalAirports, NavigationDatabase navDb)
    {
        var corridors = new List<RunwayCorridor>();
        foreach (var apt in internalAirports)
        {
            var runways = navDb.GetRunways(apt);
            if (runways.Count == 0)
            {
                runways = navDb.GetRunways("K" + apt);
            }

            foreach (var rw in runways)
            {
                corridors.Add(MakeCorridor(rw, rw.Id.End1));
                corridors.Add(MakeCorridor(rw, rw.Id.End2));
            }
        }

        return corridors;
    }

    private static RunwayCorridor MakeCorridor(RunwayInfo rw, string endDesignator)
    {
        var oriented = rw.ForApproach(endDesignator);
        var outbound = new TrueHeading((oriented.TrueHeading.Degrees + 180.0) % 360.0);
        return new RunwayCorridor(
            Threshold: new LatLon(oriented.ThresholdLatitude, oriented.ThresholdLongitude),
            OutboundCourse: outbound,
            FieldElevationFt: oriented.ElevationFt
        );
    }

    private static List<AircraftState> FilterEligible(List<AircraftState> aircraft)
    {
        var eligible = new List<AircraftState>(aircraft.Count);
        foreach (var ac in aircraft)
        {
            if (ac.IsOnGround)
            {
                continue;
            }

            if (!ac.Transponder.Mode.Equals("C", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (ac.Stars.IsCaInhibited)
            {
                continue;
            }

            if (ac.Ghost.IsUnsupported)
            {
                continue;
            }

            eligible.Add(ac);
        }

        return eligible;
    }

    private static (double Lat, double Lon, double Altitude) Predict(AircraftState ac)
    {
        double distNm = ac.GroundSpeed * PredictionSeconds / 3600.0;
        var (lat, lon) = GeoMath.ProjectPoint(ac.Position, ac.TrueTrack, distNm);
        double altitude = ac.Altitude + (ac.VerticalSpeed * PredictionSeconds / 60.0);
        return (lat, lon, altitude);
    }

    /// <summary>
    /// Separation is increasing if at least one dimension grows and neither shrinks.
    /// Parallel aircraft (separation constant in both dimensions) return false — they
    /// are NOT diverging, so the alert fires.
    /// </summary>
    private static bool IsDiverging(double currentHorizontal, double currentVertical, double predictedHorizontal, double predictedVertical)
    {
        bool neitherShrinks = (predictedHorizontal >= currentHorizontal) && (predictedVertical >= currentVertical);
        bool atLeastOneGrows = (predictedHorizontal > currentHorizontal) || (predictedVertical > currentVertical);
        return neitherShrinks && atLeastOneGrows;
    }

    private static bool IsInConflict(AircraftState a, AircraftState b, bool alreadyInConflict, ConflictAlertContext context)
    {
        double currentHorizontal = GeoMath.DistanceNm(a.Position, b.Position);
        double currentVertical = Math.Abs(a.Altitude - b.Altitude);

        var (predLatA, predLonA, predAltA) = Predict(a);
        var (predLatB, predLonB, predAltB) = Predict(b);
        double predictedHorizontal = GeoMath.DistanceNm(new LatLon(predLatA, predLonA), new LatLon(predLatB, predLonB));
        double predictedVertical = Math.Abs(predAltA - predAltB);

        // Divergence check: if separation is increasing in both dimensions, no alert
        if (IsDiverging(currentHorizontal, currentVertical, predictedHorizontal, predictedVertical))
        {
            return false;
        }

        // Approach corridor suppression: if EITHER track is inside ANY internal-airport
        // runway approach corridor, suppress the alert. STARS doesn't consult phase of
        // flight or active approach — the volumes are purely geometric.
        if (IsInAnyApproachCorridor(a, context.ApproachCorridors) || IsInAnyApproachCorridor(b, context.ApproachCorridors))
        {
            return false;
        }

        if (alreadyInConflict)
        {
            return (currentHorizontal < HysteresisHorizontalNm) && (currentVertical < HysteresisVerticalFt);
        }

        // Alert if current OR predicted separation violates thresholds
        bool currentViolation = (currentHorizontal < HorizontalNm) && (currentVertical < VerticalFt);
        bool predictedViolation = (predictedHorizontal < HorizontalNm) && (predictedVertical < VerticalFt);
        return currentViolation || predictedViolation;
    }

    private static bool IsInAnyApproachCorridor(AircraftState ac, IReadOnlyList<RunwayCorridor> corridors)
    {
        foreach (var corridor in corridors)
        {
            if (IsInsideCorridor(ac, corridor))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Geometric containment test: 4 NM wide, 30 NM long along the extended runway
    /// centerline, from field elevation up to glideslope + 1500 ft (3° GS, ~318 ft/NM).
    /// </summary>
    private static bool IsInsideCorridor(AircraftState ac, RunwayCorridor corridor)
    {
        double crossTrack = Math.Abs(GeoMath.SignedCrossTrackDistanceNm(ac.Position, corridor.Threshold, corridor.OutboundCourse));
        if (crossTrack > ApproachZoneHalfWidthNm)
        {
            return false;
        }

        double alongTrack = GeoMath.AlongTrackDistanceNm(ac.Position, corridor.Threshold, corridor.OutboundCourse);
        if (alongTrack < 0 || alongTrack > ApproachZoneLengthNm)
        {
            return false;
        }

        double glideSlopeAltitude = corridor.FieldElevationFt + (alongTrack * GlideSlopeFtPerNm);
        double ceiling = glideSlopeAltitude + ApproachZoneCeilingAboveGsFt;
        return (ac.Altitude >= corridor.FieldElevationFt) && (ac.Altitude <= ceiling);
    }

    internal static string MakeConflictId(string callsignA, string callsignB)
    {
        int cmp = string.Compare(callsignA, callsignB, StringComparison.Ordinal);
        string first = cmp <= 0 ? callsignA : callsignB;
        string second = cmp <= 0 ? callsignB : callsignA;
        return $"CA_{first}_{second}";
    }
}
