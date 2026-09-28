using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Data;

/// <summary>
/// Resolves an ERAM fix field to a position: a fix name <c>aa(a)(a)(a)</c>, a fix radial distance
/// <c>aa(a)(a)(a)ddd1ddd2</c>, or a latitude/longitude <c>dddd(L1)/(d)dddd(L2)</c> (the forms of AM.yaml field 06 and of
/// the Fix in field 08's <c>(d)dd/Fix/(d)dd</c>). The format checks ERAM answers with an error stay in yaat-server; this
/// only turns a checked field into a position.
/// </summary>
public static class EramFixResolver
{
    private static readonly ILogger Log = SimLog.CreateLogger("EramFixResolver");

    private const int MaxFixNameLength = 5;

    /// <summary>
    /// The position of <paramref name="fix"/>: a lat/long when it holds a <c>/</c>, a fix looked up in
    /// <paramref name="navDb"/> for a name of up to five characters, otherwise an FRD through <see cref="FrdResolver"/>.
    /// </summary>
    /// <param name="fix">The fix field, upper case.</param>
    /// <param name="navDb">The navigation data fixes are looked up in.</param>
    /// <returns>The position, or null when the field names nothing the navigation data knows or is not a valid lat/long.</returns>
    public static LatLon? Resolve(string fix, NavigationDatabase navDb)
    {
        LatLon? position;
        if (fix.Contains('/'))
        {
            position = ParseLatLong(fix);
        }
        else if (fix.Length <= MaxFixNameLength)
        {
            position = navDb.GetFixPosition(fix) is { } found ? new LatLon(found.Lat, found.Lon) : null;
        }
        else
        {
            position = FrdResolver.Resolve(fix, navDb);
        }

        if (position is null)
        {
            Log.LogDebug("ERAM fix {Fix} does not resolve to a position", fix);
        }
        return position;
    }

    /// <summary>
    /// Parses an ERAM latitude/longitude <c>dddd(L1)/(d)dddd(L2)</c>: degrees and minutes, the latitude at most 90 and the
    /// longitude at most 180, minutes at most 59 and 0 at 90 or 180, <c>N</c>/<c>S</c> and <c>E</c>/<c>W</c> given both or
    /// neither, and N latitude and W longitude implied when omitted.
    /// </summary>
    /// <param name="upper">The field, upper case.</param>
    /// <returns>The position, or null for any other form or an out-of-bounds value.</returns>
    public static LatLon? ParseLatLong(string upper)
    {
        string[] parts = upper.Split('/');
        if (parts.Length != 2)
        {
            return null;
        }

        (string latDigits, char? latHemisphere) = SplitHemisphere(parts[0], 'N', 'S');
        (string lonDigits, char? lonHemisphere) = SplitHemisphere(parts[1], 'E', 'W');
        if ((latHemisphere is null) != (lonHemisphere is null))
        {
            return null;
        }

        // Latitude dddd (two degree digits), longitude (d)dddd (two or three).
        double? lat = ParseDegreesMinutes(latDigits, 4, 90);
        double? lon = ParseDegreesMinutes(lonDigits, 5, 180);
        if ((lat is null) || (lon is null))
        {
            return null;
        }

        // N latitude and W longitude are implied when the letters are omitted.
        double signedLat = latHemisphere == 'S' ? -lat.Value : lat.Value;
        double signedLon = lonHemisphere == 'E' ? lon.Value : -lon.Value;
        return new LatLon(signedLat, signedLon);
    }

    /// <summary>
    /// True when <paramref name="upper"/> is the Fix of field 08's <c>(d)dd/Fix/(d)dd</c> (AM.yaml field 17 for 08): a fix
    /// name <c>aa(a)(a)(a)</c> of which a four-character one holds a letter, a fix radial distance
    /// <c>aa(a)(a)(a)ddd1ddd2</c>, or a lat/long <see cref="ParseLatLong"/> accepts. A check of form only: a well-formed fix
    /// the navigation data does not know passes.
    /// </summary>
    /// <param name="upper">The fix, upper case.</param>
    public static bool IsAltitudeFixForm(string upper)
    {
        if (upper.Contains('/'))
        {
            return ParseLatLong(upper) is not null;
        }
        if (IsFixName(upper))
        {
            return (upper.Length != 4) || upper.Any(char.IsAsciiLetter);
        }
        return IsFrdForm(upper);
    }

    /// <summary>True when <paramref name="text"/> is a fix name <c>aa(a)(a)(a)</c>: two to five ASCII letters or digits.</summary>
    public static bool IsFixName(ReadOnlySpan<char> text)
    {
        if (text.Length is < 2 or > MaxFixNameLength)
        {
            return false;
        }
        foreach (char c in text)
        {
            if (!char.IsAsciiLetterOrDigit(c))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>True when <paramref name="upper"/> is a fix radial distance <c>aa(a)(a)(a)ddd1ddd2</c>, radial 001–360 and distance 001–999.</summary>
    public static bool IsFrdForm(string upper)
    {
        if ((upper.Length < 8) || !IsFixName(upper.AsSpan(0, upper.Length - 6)) || upper.AsSpan(upper.Length - 6).ContainsAnyExceptInRange('0', '9'))
        {
            return false;
        }

        int radial = int.Parse(upper.AsSpan(upper.Length - 6, 3), NumberStyles.None, CultureInfo.InvariantCulture);
        int distance = int.Parse(upper.AsSpan(upper.Length - 3, 3), NumberStyles.None, CultureInfo.InvariantCulture);
        return (radial is >= 1 and <= 360) && (distance is >= 1 and <= 999);
    }

    /// <summary>An ERAM altitude <c>(d)dd</c> in hundreds of feet, 1–999: two or three ASCII digits, not zero.</summary>
    /// <param name="text">The altitude as typed.</param>
    /// <returns>The altitude in feet, or null for any other form or zero.</returns>
    public static int? ParseAltitudeHundreds(string text)
    {
        if ((text.Length is not (2 or 3)) || text.AsSpan().ContainsAnyExceptInRange('0', '9'))
        {
            return null;
        }
        int hundreds = int.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture);
        return (hundreds > 0) ? hundreds * 100 : null;
    }

    private static (string Digits, char? Hemisphere) SplitHemisphere(string part, char first, char second) =>
        (part.Length > 0) && ((part[^1] == first) || (part[^1] == second)) ? (part[..^1], part[^1]) : (part, null);

    private static double? ParseDegreesMinutes(string digits, int maxLength, int maxDegrees)
    {
        if ((digits.Length < 4) || (digits.Length > maxLength) || !digits.All(char.IsAsciiDigit))
        {
            return null;
        }

        int degrees = int.Parse(digits.AsSpan(0, digits.Length - 2), NumberStyles.None, CultureInfo.InvariantCulture);
        int minutes = int.Parse(digits.AsSpan(digits.Length - 2), NumberStyles.None, CultureInfo.InvariantCulture);
        if ((degrees > maxDegrees) || (minutes > 59) || ((degrees == maxDegrees) && (minutes != 0)))
        {
            return null;
        }
        return degrees + (minutes / 60.0);
    }
}
