namespace Yaat.Sim;

/// <summary>
/// Detects and resolves ground conflicts between taxiing aircraft.
/// Called once per tick before physics. Writes GroundSpeedLimit onto
/// each affected AircraftState so phases and physics respect it.
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
    /// Detect ground conflicts and set GroundSpeedLimit on affected aircraft.
    /// Clears all limits first, then sets new ones based on proximity.
    /// </summary>
    public static void ApplySpeedLimits(List<AircraftState> aircraft)
    {
        // Clear previous limits
        for (int i = 0; i < aircraft.Count; i++)
        {
            aircraft[i].GroundSpeedLimit = null;
        }

        for (int i = 0; i < aircraft.Count; i++)
        {
            var a = aircraft[i];
            if (!a.IsOnGround)
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
                    ResolveSameDirection(a, b, distFt);
                }
                else if (headingDiff > OppositeDirectionMinDeg)
                {
                    ResolveOppositeDirection(a, b, distFt);
                }
            }
        }
    }

    private static void ResolveSameDirection(
        AircraftState a, AircraftState b, double distFt)
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
            ApplyTrailLimit(a, b, distFt);
        }
        else if (diffBtoA < 90)
        {
            // B is behind A — B should slow down
            ApplyTrailLimit(b, a, distFt);
        }
    }

    private static void ApplyTrailLimit(
        AircraftState trailer, AircraftState leader, double distFt)
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

        ApplyMinLimit(trailer, maxSpeed);
    }

    private static void ResolveOppositeDirection(
        AircraftState a, AircraftState b, double distFt)
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
            ApplyMinLimit(a, 0);
            ApplyMinLimit(b, 0);
        }
    }

    private static void ApplyMinLimit(AircraftState aircraft, double maxSpeed)
    {
        if (aircraft.GroundSpeedLimit is { } existing)
        {
            aircraft.GroundSpeedLimit = Math.Min(existing, maxSpeed);
        }
        else
        {
            aircraft.GroundSpeedLimit = maxSpeed;
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
