namespace Yaat.Sim;

public static class GeoMath
{
    private const double EarthRadiusNm = 3440.065;
    private const double DegToRad = Math.PI / 180.0;
    private const double NmPerDegLat = 60.0;

    public const double FeetPerNm = 6076.12;

    /// <summary>
    /// Haversine distance between two lat/lon points, in nautical miles.
    /// </summary>
    public static double DistanceNm(double lat1, double lon1, double lat2, double lon2)
    {
        double dLat = (lat2 - lat1) * Math.PI / 180.0;
        double dLon = (lon2 - lon1) * Math.PI / 180.0;
        double rLat1 = lat1 * Math.PI / 180.0;
        double rLat2 = lat2 * Math.PI / 180.0;

        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(rLat1) * Math.Cos(rLat2) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        double c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));

        return EarthRadiusNm * c;
    }

    /// <summary>
    /// Initial bearing (degrees true, 0-360) from point 1 to point 2.
    /// </summary>
    public static double BearingTo(double lat1, double lon1, double lat2, double lon2)
    {
        double dLon = (lon2 - lon1) * Math.PI / 180.0;
        double rLat1 = lat1 * Math.PI / 180.0;
        double rLat2 = lat2 * Math.PI / 180.0;
        double y = Math.Sin(dLon) * Math.Cos(rLat2);
        double x = Math.Cos(rLat1) * Math.Sin(rLat2) - Math.Sin(rLat1) * Math.Cos(rLat2) * Math.Cos(dLon);
        double bearing = Math.Atan2(y, x) * 180.0 / Math.PI;
        return (bearing + 360.0) % 360.0;
    }

    /// <summary>
    /// Turn current heading toward target bearing by at most maxTurnDeg.
    /// Returns the new heading (0-360).
    /// </summary>
    public static double TurnHeadingToward(double currentHeading, double targetBearing, double maxTurnDeg)
    {
        double diff = targetBearing - currentHeading;
        while (diff > 180)
        {
            diff -= 360;
        }

        while (diff < -180)
        {
            diff += 360;
        }

        if (Math.Abs(diff) <= maxTurnDeg)
        {
            return targetBearing;
        }

        return (currentHeading + Math.Sign(diff) * maxTurnDeg + 360) % 360;
    }

    /// <summary>
    /// Projects a point from a given lat/lon along a heading for a given distance.
    /// </summary>
    public static (double Lat, double Lon) ProjectPoint(double lat, double lon, double headingDeg, double distanceNm)
    {
        double headingRad = headingDeg * DegToRad;
        double latRad = lat * DegToRad;

        double newLat = lat + (distanceNm * Math.Cos(headingRad) / NmPerDegLat);
        double newLon = lon + (distanceNm * Math.Sin(headingRad) / (NmPerDegLat * Math.Cos(latRad)));

        return (newLat, newLon);
    }

    /// <summary>
    /// Generates intermediate waypoints along a circular arc from startBearing to endBearing,
    /// centered at (centerLat, centerLon) with the given radius.
    /// Does NOT include the start point; DOES include the end point.
    /// </summary>
    public static List<(double Lat, double Lon)> GenerateArcPoints(
        double centerLat,
        double centerLon,
        double radiusNm,
        double startBearingDeg,
        double endBearingDeg,
        bool turnRight,
        double stepDeg = 5.0
    )
    {
        var points = new List<(double Lat, double Lon)>();

        double current = startBearingDeg;
        double totalSweep;

        if (turnRight)
        {
            totalSweep = endBearingDeg - startBearingDeg;
            if (totalSweep <= 0)
            {
                totalSweep += 360.0;
            }
        }
        else
        {
            totalSweep = startBearingDeg - endBearingDeg;
            if (totalSweep <= 0)
            {
                totalSweep += 360.0;
            }
        }

        double swept = 0;
        while (swept + stepDeg < totalSweep)
        {
            swept += stepDeg;
            current = turnRight ? startBearingDeg + swept : startBearingDeg - swept;
            current = ((current % 360.0) + 360.0) % 360.0;

            var pt = ProjectPoint(centerLat, centerLon, current, radiusNm);
            points.Add(pt);
        }

        // Always include the end point at exact end bearing
        var endPt = ProjectPoint(centerLat, centerLon, endBearingDeg, radiusNm);
        points.Add(endPt);

        return points;
    }

    /// <summary>
    /// Signed perpendicular distance from a point to a line defined by
    /// a reference point and heading. Positive = right of heading, negative = left.
    /// </summary>
    public static double SignedCrossTrackDistanceNm(double pointLat, double pointLon, double refLat, double refLon, double headingDeg)
    {
        double bearing = BearingTo(refLat, refLon, pointLat, pointLon);
        double dist = DistanceNm(refLat, refLon, pointLat, pointLon);
        double angleDiff = (bearing - headingDeg) * DegToRad;
        return dist * Math.Sin(angleDiff);
    }

    /// <summary>
    /// Signed distance along a heading from a reference point to a target point.
    /// Positive = ahead (in heading direction), negative = behind.
    /// </summary>
    public static double AlongTrackDistanceNm(double pointLat, double pointLon, double refLat, double refLon, double headingDeg)
    {
        double bearing = BearingTo(refLat, refLon, pointLat, pointLon);
        double dist = DistanceNm(refLat, refLon, pointLat, pointLon);
        double angleDiff = (bearing - headingDeg) * DegToRad;
        return dist * Math.Cos(angleDiff);
    }
}
