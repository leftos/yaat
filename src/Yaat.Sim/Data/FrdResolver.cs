namespace Yaat.Sim.Data;

public static class FrdResolver
{
    private const double NmToRadians = Math.PI / (180.0 * 60.0);

    public static LatLon? Resolve(string frdString, NavigationDatabase navDb)
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
            return new LatLon(fixPos.Value.Lat, fixPos.Value.Lon);
        }

        // FRD radials are magnetic (AIM 4-2-10; 7110.65 4-4-3.a.1.2); project along the equivalent true bearing.
        double trueRadial = MagneticDeclination.MagneticToTrue(radial.Value, fixPos.Value.Lat, fixPos.Value.Lon);
        return ProjectPosition(fixPos.Value.Lat, fixPos.Value.Lon, trueRadial, distance.Value);
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

    /// <summary>
    /// True when the identifier is itself an FRD string (e.g. <c>OAK169001</c>) rather than a plain fix
    /// name. vNAS NavData publishes thousands of these adapted fixes, so they land in the nav database
    /// alongside real waypoints; anchoring a deduced FRD on one produces an unreadable FRD-of-an-FRD such
    /// as <c>OAK169001231001</c>. Typed identifiers stay valid — this only gates FRD deduction and the
    /// scope's fix overlay.
    /// </summary>
    /// <remarks>
    /// Matches the <c>{FIX}{radial:3}{distance:3}</c> shape only: a 2-5 letter anchor plus six digits whose
    /// leading three form a valid azimuth (001-360). The radial-only <c>{FIX}{radial:3}</c> shape is
    /// deliberately not matched — real 5-character letter+digit identifiers exist in NavData.
    /// </remarks>
    public static bool IsFrdIdentifier(string name)
    {
        // 2-5 character anchor + 6 digits.
        if (name.Length is < 8 or > 11)
        {
            return false;
        }

        for (int i = name.Length - 6; i < name.Length; i++)
        {
            if (!char.IsAsciiDigit(name[i]))
            {
                return false;
            }
        }

        for (int i = 0; i < name.Length - 6; i++)
        {
            if (!char.IsAsciiLetter(name[i]))
            {
                return false;
            }
        }

        int radial = int.Parse(name.AsSpan(name.Length - 6, 3), System.Globalization.CultureInfo.InvariantCulture);
        return radial is >= 1 and <= 360;
    }

    public static string? ToFrd(double lat, double lon, IReadOnlyList<(string Name, double Lat, double Lon)> fixes, double maxNm = 50.0)
    {
        // Sitting on a fix, name it directly — including an FRD-shaped identifier, which is a real,
        // resolvable fix. Building a *new* FRD on one is what must never happen.
        string? exactName = null;
        double exactDist = Math.Min(0.1, maxNm);

        string? anchorName = null;
        double anchorLat = 0,
            anchorLon = 0;
        double anchorDist = maxNm;

        foreach (var fix in fixes)
        {
            var dist = GeoMath.DistanceNm(lat, lon, fix.Lat, fix.Lon);
            if (dist < exactDist)
            {
                exactDist = dist;
                exactName = fix.Name;
            }

            if (dist < anchorDist && !IsFrdIdentifier(fix.Name))
            {
                anchorDist = dist;
                anchorName = fix.Name;
                anchorLat = fix.Lat;
                anchorLon = fix.Lon;
            }
        }

        if (exactName is not null)
        {
            return exactName;
        }

        if (anchorName is null)
        {
            return null;
        }

        // FRD radials are magnetic (AIM 4-2-10; 7110.65 4-4-3.a.1.2); convert the true bearing at the fix.
        double trueBearing = GeoMath.BearingTo(anchorLat, anchorLon, lat, lon);
        int radial = (int)Math.Round(MagneticDeclination.TrueToMagnetic(trueBearing, anchorLat, anchorLon));
        if (radial <= 0)
        {
            radial = 360;
        }

        int distance = (int)Math.Round(anchorDist);
        if (distance > 999)
        {
            return null;
        }

        if (distance == 0)
        {
            return anchorName;
        }

        return $"{anchorName}{radial:D3}{distance:D3}";
    }

    private static LatLon ProjectPosition(double latDeg, double lonDeg, double radialDeg, int distanceNm)
    {
        double lat1 = latDeg * Math.PI / 180.0;
        double lon1 = lonDeg * Math.PI / 180.0;
        double bearing = radialDeg * Math.PI / 180.0;
        double angularDist = distanceNm * NmToRadians;

        double lat2 = Math.Asin(Math.Sin(lat1) * Math.Cos(angularDist) + Math.Cos(lat1) * Math.Sin(angularDist) * Math.Cos(bearing));

        double lon2 =
            lon1 + Math.Atan2(Math.Sin(bearing) * Math.Sin(angularDist) * Math.Cos(lat1), Math.Cos(angularDist) - Math.Sin(lat1) * Math.Sin(lat2));

        return new LatLon(lat2 * 180.0 / Math.PI, lon2 * 180.0 / Math.PI);
    }
}
