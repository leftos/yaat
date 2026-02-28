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
}
