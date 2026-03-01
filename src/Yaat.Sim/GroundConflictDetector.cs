namespace Yaat.Sim;

/// <summary>
/// Detects and resolves ground conflicts between taxiing aircraft.
/// Called once per tick over all ground aircraft. Returns speed overrides
/// that should be applied before physics — does NOT modify ControlTargets.
/// </summary>
public static class GroundConflictDetector
{
    private const double SameDirectionMaxDeg = 60.0;
    private const double OppositeDirectionMinDeg = 120.0;
    private const double TrailDistanceFt = 200.0;
    private const double StopDistanceFt = 100.0;
    private const double OppositeStopDistanceFt = 300.0;
    private const double SearchRangeNm = 0.1;
    private const double FtPerNm = 6076.12;

    /// <summary>
    /// Compute ground speed overrides for all ground aircraft in the list.
    /// Returns a dictionary of callsign → max allowed ground speed (kts).
    /// Aircraft not in the dictionary are not affected.
    /// </summary>
    public static Dictionary<string, double> ComputeSpeedOverrides(
        List<AircraftState> aircraft)
    {
        var overrides = new Dictionary<string, double>();

        for (int i = 0; i < aircraft.Count; i++)
        {
            var a = aircraft[i];
            if (!a.IsOnGround || a.GroundSpeed < 0.1)
            {
                continue;
            }

            for (int j = i + 1; j < aircraft.Count; j++)
            {
                var b = aircraft[j];
                if (!b.IsOnGround)
                {
                    continue;
                }

                double distNm = GeoMath.DistanceNm(
                    a.Latitude, a.Longitude,
                    b.Latitude, b.Longitude);

                if (distNm > SearchRangeNm)
                {
                    continue;
                }

                double distFt = distNm * FtPerNm;
                double headingDiff = HeadingDifference(a.Heading, b.Heading);

                if (headingDiff < SameDirectionMaxDeg)
                {
                    ResolveSameDirection(a, b, distFt, overrides);
                }
                else if (headingDiff > OppositeDirectionMinDeg)
                {
                    ResolveOppositeDirection(a, b, distFt, overrides);
                }
            }
        }

        return overrides;
    }

    private static void ResolveSameDirection(
        AircraftState a, AircraftState b, double distFt,
        Dictionary<string, double> overrides)
    {
        // Determine which aircraft is ahead
        double bearingAtoB = GeoMath.BearingTo(
            a.Latitude, a.Longitude,
            b.Latitude, b.Longitude);
        double bearingBtoA = GeoMath.BearingTo(
            b.Latitude, b.Longitude,
            a.Latitude, a.Longitude);

        double diffAtoB = HeadingDifference(a.Heading, bearingAtoB);
        double diffBtoA = HeadingDifference(b.Heading, bearingBtoA);

        // A is trailing if B is ahead of A (bearing to B is close to A's heading)
        if (diffAtoB < 90)
        {
            // A is behind B — A should slow down
            ApplyTrailOverride(a, b, distFt, overrides);
        }
        else if (diffBtoA < 90)
        {
            // B is behind A — B should slow down
            ApplyTrailOverride(b, a, distFt, overrides);
        }
    }

    private static void ApplyTrailOverride(
        AircraftState trailer, AircraftState leader, double distFt,
        Dictionary<string, double> overrides)
    {
        double maxSpeed;
        if (distFt <= StopDistanceFt)
        {
            maxSpeed = 0;
        }
        else if (distFt <= TrailDistanceFt)
        {
            maxSpeed = leader.GroundSpeed;
        }
        else
        {
            return;
        }

        ApplyMinOverride(overrides, trailer.Callsign, maxSpeed);
    }

    private static void ResolveOppositeDirection(
        AircraftState a, AircraftState b, double distFt,
        Dictionary<string, double> overrides)
    {
        if (distFt > OppositeStopDistanceFt)
        {
            return;
        }

        // Both aircraft are head-on; check if they're closing
        double bearingAtoB = GeoMath.BearingTo(
            a.Latitude, a.Longitude,
            b.Latitude, b.Longitude);
        double diffA = HeadingDifference(a.Heading, bearingAtoB);

        // A is closing on B if bearing to B is within 90 degrees of A's heading
        if (diffA < 90)
        {
            ApplyMinOverride(overrides, a.Callsign, 0);
            ApplyMinOverride(overrides, b.Callsign, 0);
        }
    }

    private static void ApplyMinOverride(
        Dictionary<string, double> overrides, string callsign, double maxSpeed)
    {
        if (overrides.TryGetValue(callsign, out double existing))
        {
            overrides[callsign] = Math.Min(existing, maxSpeed);
        }
        else
        {
            overrides[callsign] = maxSpeed;
        }
    }

    private static double HeadingDifference(double h1, double h2)
    {
        double diff = Math.Abs(h1 - h2);
        if (diff > 180)
        {
            diff = 360 - diff;
        }

        return diff;
    }
}
