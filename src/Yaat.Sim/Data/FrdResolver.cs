namespace Yaat.Sim.Data;

public record ResolvedPosition(double Latitude, double Longitude);

public static class FrdResolver
{
    private const double NmToRadians = Math.PI / (180.0 * 60.0);

    public static ResolvedPosition? Resolve(string frdString, NavigationDatabase navDb)
    {
        var parsed = ParseFrd(frdString);
        if (parsed is null)
        {
            return null;
        }

        var (fixName, radial, distance) = parsed.Value;

        var fixPos = navDb.GetFixPosition(fixName);
        if (fixPos is null)
        {
            return null;
        }

        if (radial is null || distance is null)
        {
            return new ResolvedPosition(fixPos.Value.Lat, fixPos.Value.Lon);
        }

        return ProjectPosition(fixPos.Value.Lat, fixPos.Value.Lon, radial.Value, distance.Value);
    }

    public static (string Fix, int? Radial, int? Distance)? ParseFrd(string frdString)
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

        // Format: {FIX}{radial:3} — fix names are 2+ chars, radial is 3 digits
        if (s.Length >= 5)
        {
            var suffix = s[^3..];
            if (suffix.All(char.IsDigit))
            {
                var fixName = s[..^3];
                if (fixName.Length >= 2)
                {
                    int radial = int.Parse(suffix);
                    return (fixName, radial, null);
                }
            }
        }

        // Bare fix name (no radial/distance)
        return (s, null, null);
    }

    public static string? ToFrd(double lat, double lon, IReadOnlyList<(string Name, double Lat, double Lon)> fixes, double maxNm = 50.0)
    {
        string? bestName = null;
        double bestDist = maxNm;

        foreach (var fix in fixes)
        {
            var dist = GeoMath.DistanceNm(lat, lon, fix.Lat, fix.Lon);
            if (dist < bestDist)
            {
                bestDist = dist;
                bestName = fix.Name;
            }
        }

        if (bestName is null)
        {
            return null;
        }

        // Find the fix position again for bearing calculation
        double fixLat = 0,
            fixLon = 0;
        foreach (var fix in fixes)
        {
            if (fix.Name == bestName)
            {
                fixLat = fix.Lat;
                fixLon = fix.Lon;
                break;
            }
        }

        if (bestDist < 0.1)
        {
            return bestName;
        }

        int radial = (int)Math.Round(GeoMath.BearingTo(fixLat, fixLon, lat, lon));
        if (radial <= 0)
        {
            radial = 360;
        }

        int distance = (int)Math.Round(bestDist);
        if (distance > 999)
        {
            return null;
        }

        if (distance == 0)
        {
            return bestName;
        }

        return $"{bestName}{radial:D3}{distance:D3}";
    }

    private static ResolvedPosition ProjectPosition(double latDeg, double lonDeg, int radialDeg, int distanceNm)
    {
        double lat1 = latDeg * Math.PI / 180.0;
        double lon1 = lonDeg * Math.PI / 180.0;
        double bearing = radialDeg * Math.PI / 180.0;
        double angularDist = distanceNm * NmToRadians;

        double lat2 = Math.Asin(Math.Sin(lat1) * Math.Cos(angularDist) + Math.Cos(lat1) * Math.Sin(angularDist) * Math.Cos(bearing));

        double lon2 =
            lon1 + Math.Atan2(Math.Sin(bearing) * Math.Sin(angularDist) * Math.Cos(lat1), Math.Cos(angularDist) - Math.Sin(lat1) * Math.Sin(lat2));

        return new ResolvedPosition(lat2 * 180.0 / Math.PI, lon2 * 180.0 / Math.PI);
    }
}
