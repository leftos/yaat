namespace Yaat.Sim.Data;

public record ResolvedPosition(double Latitude, double Longitude);

public static class FrdResolver
{
    private const double NmToRadians = Math.PI / (180.0 * 60.0);

    public static ResolvedPosition? Resolve(string frdString, IFixLookup fixes)
    {
        var parsed = ParseFrd(frdString);
        if (parsed is null)
        {
            return null;
        }

        var (fixName, radial, distance) = parsed.Value;

        var fixPos = fixes.GetFixPosition(fixName);
        if (fixPos is null)
        {
            return null;
        }

        if (radial is null || distance is null)
        {
            return new ResolvedPosition(fixPos.Value.Lat, fixPos.Value.Lon);
        }

        return ProjectPosition(
            fixPos.Value.Lat, fixPos.Value.Lon,
            radial.Value, distance.Value);
    }

    internal static (string Fix, int? Radial, int? Distance)? ParseFrd(
        string frdString)
    {
        if (string.IsNullOrWhiteSpace(frdString))
        {
            return null;
        }

        var s = frdString.Trim();

        // Format: {FIX}{radial:3}{distance:3} — fix names are 2-5 chars
        if (s.Length >= 8)
        {
            var suffix = s[^6..];
            if (suffix.All(char.IsDigit))
            {
                var fixName = s[..^6];
                if (fixName.Length >= 2)
                {
                    int radial = int.Parse(suffix[..3]);
                    int distance = int.Parse(suffix[3..]);
                    return (fixName, radial, distance);
                }
            }
        }

        // Bare fix name (no radial/distance)
        return (s, null, null);
    }

    private static ResolvedPosition ProjectPosition(
        double latDeg, double lonDeg,
        int radialDeg, int distanceNm)
    {
        double lat1 = latDeg * Math.PI / 180.0;
        double lon1 = lonDeg * Math.PI / 180.0;
        double bearing = radialDeg * Math.PI / 180.0;
        double angularDist = distanceNm * NmToRadians;

        double lat2 = Math.Asin(
            Math.Sin(lat1) * Math.Cos(angularDist)
            + Math.Cos(lat1) * Math.Sin(angularDist) * Math.Cos(bearing));

        double lon2 = lon1 + Math.Atan2(
            Math.Sin(bearing) * Math.Sin(angularDist) * Math.Cos(lat1),
            Math.Cos(angularDist) - Math.Sin(lat1) * Math.Sin(lat2));

        return new ResolvedPosition(
            lat2 * 180.0 / Math.PI,
            lon2 * 180.0 / Math.PI);
    }
}
