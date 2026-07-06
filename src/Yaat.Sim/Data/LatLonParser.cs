using System.Globalization;
using System.Text.RegularExpressions;

namespace Yaat.Sim.Data;

/// <summary>
/// Parses ERAM lat/long location strings of the form <c>4220N/7110W</c> or <c>3730N12200W</c>
/// (docs/crc/eram.md §LF Command — <c>//4220N/7110W</c> is a valid CRR group location). Latitude is
/// DDMM followed by N/S; longitude is [D]DDMM followed by E/W (2- or 3-digit degrees); the two halves may
/// be separated by an optional slash. Minutes are the last two digits of each half. Returns null when the
/// string is not a well-formed coordinate pair. The <c>//</c> location prefix is stripped by the caller.
/// </summary>
public static class LatLonParser
{
    private static readonly Regex CoordRegex = new(
        @"^(\d{2})(\d{2})([NS])/?(\d{2,3})(\d{2})([EW])$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    );

    public static LatLon? Parse(string s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return null;
        }

        var m = CoordRegex.Match(s.Trim());
        if (!m.Success)
        {
            return null;
        }

        var latMin = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        var lonMin = int.Parse(m.Groups[5].Value, CultureInfo.InvariantCulture);
        if (latMin >= 60 || lonMin >= 60)
        {
            return null;
        }

        var lat = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) + (latMin / 60.0);
        var lon = int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture) + (lonMin / 60.0);
        if (m.Groups[3].Value.Equals("S", StringComparison.OrdinalIgnoreCase))
        {
            lat = -lat;
        }
        if (m.Groups[6].Value.Equals("W", StringComparison.OrdinalIgnoreCase))
        {
            lon = -lon;
        }

        if (lat < -90.0 || lat > 90.0 || lon < -180.0 || lon > 180.0)
        {
            return null;
        }

        return new LatLon(lat, lon);
    }
}
