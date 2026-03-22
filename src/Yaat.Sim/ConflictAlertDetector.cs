using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim;

/// <summary>
/// Bundles per-tick detection parameters for <see cref="ConflictAlertDetector"/>.
/// </summary>
public record ConflictAlertContext(HashSet<string> ExistingConflictIds, List<string> InternalAirports);

/// <summary>
/// STARS-style Conflict Alert (CA) detection.
/// Called once per tick after physics. Predicts each aircraft's position 5 seconds
/// into the future and reports pairs whose current or predicted separation violates
/// thresholds, provided separation is not increasing.
/// </summary>
public static class ConflictAlertDetector
{
    private const double PredictionSeconds = 5.0;

    // Standard STARS CA thresholds (IFR)
    private const double HorizontalNm = 3.0;
    private const double VerticalFt = 1000;

    // IFR hysteresis thresholds — must exceed these to clear an existing alert
    private const double HysteresisHorizontalNm = 3.3;
    private const double HysteresisVerticalFt = 1100;

    // VFR thresholds — "target resolution" per STARS behavior
    private const double VfrHorizontalNm = 0.25;
    private const double VfrVerticalFt = 500;
    private const double VfrHysteresisHorizontalNm = 0.30;
    private const double VfrHysteresisVerticalFt = 550;

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

    private static List<AircraftState> FilterEligible(List<AircraftState> aircraft)
    {
        var eligible = new List<AircraftState>(aircraft.Count);
        foreach (var ac in aircraft)
        {
            if (ac.IsOnGround)
            {
                continue;
            }

            if (!ac.TransponderMode.Equals("C", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (ac.IsCaInhibited)
            {
                continue;
            }

            if (ac.IsUnsupported)
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
        var (lat, lon) = GeoMath.ProjectPoint(ac.Latitude, ac.Longitude, ac.TrueTrack, distNm);
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
        double currentHorizontal = GeoMath.DistanceNm(a.Latitude, a.Longitude, b.Latitude, b.Longitude);
        double currentVertical = Math.Abs(a.Altitude - b.Altitude);

        var (predLatA, predLonA, predAltA) = Predict(a);
        var (predLatB, predLonB, predAltB) = Predict(b);
        double predictedHorizontal = GeoMath.DistanceNm(predLatA, predLonA, predLatB, predLonB);
        double predictedVertical = Math.Abs(predAltA - predAltB);

        // Divergence check: if separation is increasing in both dimensions, no alert
        if (IsDiverging(currentHorizontal, currentVertical, predictedHorizontal, predictedVertical))
        {
            return false;
        }

        // Check if suppressed by final approach corridor
        if (IsSuppressedByApproachZone(a, b, context.InternalAirports))
        {
            return false;
        }

        // When either target is VFR, use target-resolution thresholds
        bool vfr = (a.IsVfr) || (b.IsVfr);

        if (alreadyInConflict)
        {
            double hystH = vfr ? VfrHysteresisHorizontalNm : HysteresisHorizontalNm;
            double hystV = vfr ? VfrHysteresisVerticalFt : HysteresisVerticalFt;
            return (currentHorizontal < hystH) && (currentVertical < hystV);
        }

        double threshH = vfr ? VfrHorizontalNm : HorizontalNm;
        double threshV = vfr ? VfrVerticalFt : VerticalFt;

        // Alert if current OR predicted separation violates thresholds
        bool currentViolation = (currentHorizontal < threshH) && (currentVertical < threshV);
        bool predictedViolation = (predictedHorizontal < threshH) && (predictedVertical < threshV);
        return currentViolation || predictedViolation;
    }

    private static bool IsSuppressedByApproachZone(AircraftState a, AircraftState b, List<string> internalAirports)
    {
        if (IsOnFinalApproach(a) && IsInRunwayCorridor(a, b, internalAirports))
        {
            return true;
        }

        if (IsOnFinalApproach(b) && IsInRunwayCorridor(b, a, internalAirports))
        {
            return true;
        }

        return false;
    }

    private static bool IsOnFinalApproach(AircraftState ac)
    {
        return ac.Phases?.CurrentPhase is FinalApproachPhase;
    }

    /// <summary>
    /// Check if <paramref name="other"/> is inside the runway approach corridor
    /// for <paramref name="approachAircraft"/>'s target runway. The corridor is
    /// anchored at the runway threshold, extends 30 NM along the extended centerline,
    /// is 4 NM wide, and ranges from field elevation to 1500 ft above the glideslope.
    /// Only applies to airports with ICAO IDs (4-char codes) in the internal airports list.
    /// </summary>
    private static bool IsInRunwayCorridor(AircraftState approachAircraft, AircraftState other, List<string> internalAirports)
    {
        var approach = approachAircraft.Phases?.ActiveApproach;
        if (approach is null)
        {
            return false;
        }

        string airportCode = approach.AirportCode;

        // Only ICAO airports (4-char codes) in the internal airports list
        if (airportCode.Length != 4)
        {
            return false;
        }

        bool isInternal = false;
        foreach (var apt in internalAirports)
        {
            if (apt.Equals(airportCode, StringComparison.OrdinalIgnoreCase))
            {
                isInternal = true;
                break;
            }
        }

        if (!isInternal)
        {
            return false;
        }

        // Look up runway threshold from NavigationDatabase
        var runway = NavigationDatabase.Instance.GetRunway(airportCode, approach.RunwayId);
        if (runway is null)
        {
            return false;
        }

        double threshLat = runway.ThresholdLatitude;
        double threshLon = runway.ThresholdLongitude;
        double fieldElevation = runway.ElevationFt;

        // The approach course extends outward from the threshold (away from the runway).
        // That's the reciprocal of the runway heading.
        var outboundCourse = new TrueHeading((runway.TrueHeading.Degrees + 180.0) % 360.0);

        // Cross-track: perpendicular distance from centerline
        double crossTrack = Math.Abs(GeoMath.SignedCrossTrackDistanceNm(other.Latitude, other.Longitude, threshLat, threshLon, outboundCourse));

        if (crossTrack > ApproachZoneHalfWidthNm)
        {
            return false;
        }

        // Along-track: distance from threshold along the approach extended centerline
        double alongTrack = GeoMath.AlongTrackDistanceNm(other.Latitude, other.Longitude, threshLat, threshLon, outboundCourse);

        if (alongTrack < 0 || alongTrack > ApproachZoneLengthNm)
        {
            return false;
        }

        // Vertical: from field elevation up to glideslope + 1500 ft at this distance
        double glideSlopeAltitude = fieldElevation + (alongTrack * GlideSlopeFtPerNm);
        double ceiling = glideSlopeAltitude + ApproachZoneCeilingAboveGsFt;

        if ((other.Altitude < fieldElevation) || (other.Altitude > ceiling))
        {
            return false;
        }

        return true;
    }

    internal static string MakeConflictId(string callsignA, string callsignB)
    {
        int cmp = string.Compare(callsignA, callsignB, StringComparison.Ordinal);
        string first = cmp <= 0 ? callsignA : callsignB;
        string second = cmp <= 0 ? callsignB : callsignA;
        return $"CA_{first}_{second}";
    }
}
