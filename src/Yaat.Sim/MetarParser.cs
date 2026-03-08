using System.Text.RegularExpressions;

namespace Yaat.Sim;

public static partial class MetarParser
{
    public record ParsedMetar(
        string StationId,
        int? CeilingFeetAgl,
        double? VisibilityStatuteMiles,
        int? WindDirectionDeg = null,
        int? WindSpeedKts = null,
        int? WindGustKts = null,
        double? AltimeterInHg = null
    );

    // Visibility patterns: "M1/4SM", "P6SM", "10SM", "3SM", "1/2SM", "1 1/2SM"
    [GeneratedRegex(@"(?<!\S)([PM]?\d+(?:\s+\d+/\d+)?(?:/\d+)?)\s*SM(?!\S)", RegexOptions.Compiled)]
    private static partial Regex VisibilityRegex();

    // Cloud layer: FEW/SCT/BKN/OVC followed by 3-digit hundreds of feet
    [GeneratedRegex(@"\b(FEW|SCT|BKN|OVC)(\d{3})\b", RegexOptions.Compiled)]
    private static partial Regex CloudLayerRegex();

    // Vertical visibility (indefinite ceiling): VV followed by 3-digit hundreds of feet
    [GeneratedRegex(@"\bVV(\d{3})\b", RegexOptions.Compiled)]
    private static partial Regex VerticalVisibilityRegex();

    // Clear sky: CLR or SKC
    [GeneratedRegex(@"\b(CLR|SKC)\b", RegexOptions.Compiled)]
    private static partial Regex ClearSkyRegex();

    // Wind: dddssKT or dddssGggKT or VRBssKT
    [GeneratedRegex(@"\b(\d{3}|VRB)(\d{2,3})(G(\d{2,3}))?KT\b", RegexOptions.Compiled)]
    private static partial Regex WindRegex();

    // Altimeter: A followed by 4 digits (e.g., A2992 = 29.92 inHg)
    [GeneratedRegex(@"\bA(\d{4})\b", RegexOptions.Compiled)]
    private static partial Regex AltimeterRegex();

    public static ParsedMetar? Parse(string? metar)
    {
        if (string.IsNullOrWhiteSpace(metar))
        {
            return null;
        }

        var tokens = metar.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2)
        {
            return null;
        }

        // Station ID: first 4-char token starting with a letter (ICAO: KSFO, KE16, etc.)
        // Skip known prefixes: METAR, SPECI
        string? stationId = null;
        foreach (var token in tokens)
        {
            if (token.Length == 4 && token[0] is >= 'A' and <= 'Z' && token.All(c => c is (>= 'A' and <= 'Z') or (>= '0' and <= '9')))
            {
                if (token is "AUTO")
                {
                    continue;
                }
                stationId = token;
                break;
            }
        }

        if (stationId is null)
        {
            return null;
        }

        double? visibility = ParseVisibility(metar);
        int? ceiling = ParseCeiling(metar);
        var (windDir, windSpd, windGust) = ParseWind(metar);
        double? altimeter = ParseAltimeter(metar);

        return new ParsedMetar(stationId, ceiling, visibility, windDir, windSpd, windGust, altimeter);
    }

    public static ParsedMetar? FindStation(IEnumerable<string> metars, string airportId)
    {
        string icao = ToIcao(airportId);

        foreach (var metar in metars)
        {
            var parsed = Parse(metar);
            if (parsed is not null && parsed.StationId.Equals(icao, StringComparison.OrdinalIgnoreCase))
            {
                return parsed;
            }
        }

        return null;
    }

    internal static string ToIcao(string airportId)
    {
        // If already 4 chars starting with K, assume ICAO
        if (airportId.Length == 4 && airportId[0] is 'K' or 'k')
        {
            return airportId.ToUpperInvariant();
        }

        // 3-letter FAA ID → prepend K
        if (airportId.Length == 3)
        {
            return "K" + airportId.ToUpperInvariant();
        }

        return airportId.ToUpperInvariant();
    }

    private static double? ParseVisibility(string metar)
    {
        var match = VisibilityRegex().Match(metar);
        if (!match.Success)
        {
            return null;
        }

        var raw = match.Groups[1].Value.Trim();

        // P6SM → greater than 6
        if (raw.StartsWith('P'))
        {
            if (double.TryParse(raw[1..], out double pVal))
            {
                return pVal;
            }
            return null;
        }

        // M1/4SM → less than 1/4 mile; parse the fraction after M
        if (raw.StartsWith('M'))
        {
            var mRaw = raw[1..];
            if (mRaw.Contains('/'))
            {
                return TryParseFraction(mRaw, out double mVal) ? mVal : null;
            }
            return double.TryParse(mRaw, out double mWholeVal) ? mWholeVal : null;
        }

        // Mixed fraction: "1 1/2" → 1.5
        if (raw.Contains(' ') && raw.Contains('/'))
        {
            var parts = raw.Split(' ');
            if (parts.Length == 2 && int.TryParse(parts[0], out int whole) && TryParseFraction(parts[1], out double frac))
            {
                return whole + frac;
            }
            return null;
        }

        // Pure fraction: "1/2" → 0.5
        if (raw.Contains('/'))
        {
            return TryParseFraction(raw, out double fracVal) ? fracVal : null;
        }

        // Whole number: "10", "3"
        return double.TryParse(raw, out double val) ? val : null;
    }

    private static bool TryParseFraction(string s, out double result)
    {
        result = 0;
        var parts = s.Split('/');
        if (parts.Length != 2)
        {
            return false;
        }
        if (int.TryParse(parts[0], out int num) && int.TryParse(parts[1], out int den) && den != 0)
        {
            result = (double)num / den;
            return true;
        }
        return false;
    }

    private static int? ParseCeiling(string metar)
    {
        // CLR/SKC = no ceiling
        if (ClearSkyRegex().IsMatch(metar))
        {
            return null;
        }

        int? lowestCeiling = null;

        foreach (Match match in CloudLayerRegex().Matches(metar))
        {
            var coverage = match.Groups[1].Value;
            if (coverage is not ("BKN" or "OVC"))
            {
                continue;
            }

            if (int.TryParse(match.Groups[2].Value, out int hundreds))
            {
                int altFeet = hundreds * 100;
                if (lowestCeiling is null || altFeet < lowestCeiling)
                {
                    lowestCeiling = altFeet;
                }
            }
        }

        // VV (vertical visibility / indefinite ceiling) — total obscuration
        var vvMatch = VerticalVisibilityRegex().Match(metar);
        if (vvMatch.Success && int.TryParse(vvMatch.Groups[1].Value, out int vvHundreds))
        {
            int vvFeet = vvHundreds * 100;
            if (lowestCeiling is null || vvFeet < lowestCeiling)
            {
                lowestCeiling = vvFeet;
            }
        }

        return lowestCeiling;
    }

    private static (int? Direction, int? Speed, int? Gust) ParseWind(string metar)
    {
        var match = WindRegex().Match(metar);
        if (!match.Success)
        {
            return (null, null, null);
        }

        int? direction = match.Groups[1].Value == "VRB" ? null : int.Parse(match.Groups[1].Value);
        int speed = int.Parse(match.Groups[2].Value);
        int? gust = match.Groups[4].Success ? int.Parse(match.Groups[4].Value) : null;

        return (direction, speed, gust);
    }

    private static double? ParseAltimeter(string metar)
    {
        var match = AltimeterRegex().Match(metar);
        if (!match.Success)
        {
            return null;
        }

        return int.Parse(match.Groups[1].Value) / 100.0;
    }
}
