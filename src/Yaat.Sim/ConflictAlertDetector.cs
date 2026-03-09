using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim;

/// <summary>
/// Stateless STARS-style Conflict Alert (CA) detection.
/// Called once per tick after physics. Reports pairs of airborne aircraft
/// that currently violate separation thresholds.
/// </summary>
public static class ConflictAlertDetector
{
    // Standard STARS CA thresholds
    private const double HorizontalNm = 3.0;
    private const double VerticalFt = 1000;

    // Hysteresis thresholds — must exceed these to clear an existing alert
    private const double HysteresisHorizontalNm = 3.3;
    private const double HysteresisVerticalFt = 1100;

    // Final approach suppression zone
    private const double ApproachZoneHalfWidthNm = 2.0;
    private const double ApproachZoneLengthNm = 30.0;
    private const double ApproachZoneCeilingAboveGsFt = 1500;

    public record ConflictPair(string CallsignA, string CallsignB, string Id);

    /// <summary>
    /// Detect pairs of aircraft currently in a separation conflict.
    /// Pass <paramref name="existingConflictIds"/> to enable hysteresis
    /// (existing alerts use wider thresholds to clear).
    /// </summary>
    public static List<ConflictPair> Detect(List<AircraftState> aircraft, HashSet<string>? existingConflictIds = null)
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
                bool alreadyInConflict = existingConflictIds?.Contains(id) ?? false;

                if (IsInConflict(a, b, alreadyInConflict))
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

            eligible.Add(ac);
        }

        return eligible;
    }

    private static bool IsInConflict(AircraftState a, AircraftState b, bool alreadyInConflict)
    {
        double currentHorizontal = GeoMath.DistanceNm(a.Latitude, a.Longitude, b.Latitude, b.Longitude);
        double currentVertical = Math.Abs(a.Altitude - b.Altitude);

        // Check if suppressed by final approach corridor
        if (IsSuppressedByApproachZone(a, b))
        {
            return false;
        }

        if (alreadyInConflict)
        {
            // Hysteresis: clears when either dimension exceeds its hysteresis threshold
            return currentHorizontal < HysteresisHorizontalNm && currentVertical < HysteresisVerticalFt;
        }

        // Current separation violation: both dimensions must be within thresholds
        return currentHorizontal < HorizontalNm && currentVertical < VerticalFt;
    }

    private static bool IsSuppressedByApproachZone(AircraftState a, AircraftState b)
    {
        // If either aircraft is on final approach, check if the other is in the approach corridor
        if (IsOnFinalApproach(a) && IsInApproachCorridor(a, b))
        {
            return true;
        }

        if (IsOnFinalApproach(b) && IsInApproachCorridor(b, a))
        {
            return true;
        }

        return false;
    }

    private static bool IsOnFinalApproach(AircraftState ac)
    {
        return ac.Phases?.CurrentPhase is FinalApproachPhase;
    }

    private static bool IsInApproachCorridor(AircraftState approachAircraft, AircraftState other)
    {
        // Use approach course if available, otherwise aircraft heading
        double course = approachAircraft.Phases?.ActiveApproach?.FinalApproachCourse ?? approachAircraft.Heading;

        double crossTrack = Math.Abs(
            GeoMath.SignedCrossTrackDistanceNm(other.Latitude, other.Longitude, approachAircraft.Latitude, approachAircraft.Longitude, course)
        );

        if (crossTrack > ApproachZoneHalfWidthNm)
        {
            return false;
        }

        double alongTrack = GeoMath.AlongTrackDistanceNm(
            other.Latitude,
            other.Longitude,
            approachAircraft.Latitude,
            approachAircraft.Longitude,
            course
        );

        // Other aircraft must be ahead (in front along approach course) and within zone length
        if (alongTrack < 0 || alongTrack > ApproachZoneLengthNm)
        {
            return false;
        }

        // Altitude check: other aircraft must be below glideslope + ceiling buffer
        // Approximate glideslope altitude: approach aircraft alt decreasing linearly
        double glideSlopeAlt = approachAircraft.Altitude;
        if (other.Altitude > glideSlopeAlt + ApproachZoneCeilingAboveGsFt)
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
