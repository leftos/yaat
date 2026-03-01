namespace Yaat.Sim;

public static class GeoMath
{
    private const double EarthRadiusNm = 3440.065;

    /// <summary>
    /// Haversine distance between two lat/lon points, in nautical miles.
    /// </summary>
    public static double DistanceNm(
        double lat1, double lon1, double lat2, double lon2)
    {
        double dLat = (lat2 - lat1) * Math.PI / 180.0;
        double dLon = (lon2 - lon1) * Math.PI / 180.0;
        double rLat1 = lat1 * Math.PI / 180.0;
        double rLat2 = lat2 * Math.PI / 180.0;

        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
            + Math.Cos(rLat1) * Math.Cos(rLat2)
            * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        double c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));

        return EarthRadiusNm * c;
    }

    /// <summary>
    /// Initial bearing (degrees true, 0-360) from point 1 to point 2.
    /// </summary>
    public static double BearingTo(
        double lat1, double lon1, double lat2, double lon2)
    {
        double dLon = (lon2 - lon1) * Math.PI / 180.0;
        double rLat1 = lat1 * Math.PI / 180.0;
        double rLat2 = lat2 * Math.PI / 180.0;
        double y = Math.Sin(dLon) * Math.Cos(rLat2);
        double x = Math.Cos(rLat1) * Math.Sin(rLat2)
            - Math.Sin(rLat1) * Math.Cos(rLat2) * Math.Cos(dLon);
        double bearing = Math.Atan2(y, x) * 180.0 / Math.PI;
        return (bearing + 360.0) % 360.0;
    }

    /// <summary>
    /// Turn current heading toward target bearing by at most maxTurnDeg.
    /// Returns the new heading (0-360).
    /// </summary>
    public static double TurnHeadingToward(
        double currentHeading, double targetBearing, double maxTurnDeg)
    {
        double diff = targetBearing - currentHeading;
        while (diff > 180) diff -= 360;
        while (diff < -180) diff += 360;

        if (Math.Abs(diff) <= maxTurnDeg)
        {
            return targetBearing;
        }

        return (currentHeading + Math.Sign(diff) * maxTurnDeg + 360) % 360;
    }
}
