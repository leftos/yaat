using System.Text.RegularExpressions;

namespace Yaat.Sim;

public static partial class MetarParser
{
    public enum CloudCover
    {
        Few,
        Scattered,
        Broken,
        Overcast,
    }

    public record CloudLayer(CloudCover Cover, int BaseFeetAgl);

    public record ParsedMetar(
        string StationId,
        int? CeilingFeetAgl,
        IReadOnlyList<CloudLayer> Layers,
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
        var (layers, ceiling) = ParseLayers(metar);
        var (windDir, windSpd, windGust) = ParseWind(metar);
        double? altimeter = ParseAltimeter(metar);

        return new ParsedMetar(stationId, ceiling, layers, visibility, windDir, windSpd, windGust, altimeter);
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

    private static (IReadOnlyList<CloudLayer> Layers, int? CeilingFeetAgl) ParseLayers(string metar)
    {
        // CLR/SKC = no layers, no ceiling
        if (ClearSkyRegex().IsMatch(metar))
        {
            return (Array.Empty<CloudLayer>(), null);
        }

        var layers = new List<CloudLayer>();
        int? lowestCeiling = null;

        foreach (Match match in CloudLayerRegex().Matches(metar))
        {
            var coverage = match.Groups[1].Value;
            if (!int.TryParse(match.Groups[2].Value, out int hundreds))
            {
                continue;
            }

            CloudCover? cover = coverage switch
            {
                "FEW" => CloudCover.Few,
                "SCT" => CloudCover.Scattered,
                "BKN" => CloudCover.Broken,
                "OVC" => CloudCover.Overcast,
                _ => null,
            };
            if (cover is null)
            {
                continue;
            }

            int altFeet = hundreds * 100;
            layers.Add(new CloudLayer(cover.Value, altFeet));

            if (cover is CloudCover.Broken or CloudCover.Overcast)
            {
                if (lowestCeiling is null || altFeet < lowestCeiling)
                {
                    lowestCeiling = altFeet;
                }
            }
        }

        // VV (vertical visibility / indefinite ceiling) — total obscuration; modeled as a synthetic OVC layer
        // so the multi-layer obstruction logic in VisualDetection treats it consistently with regular OVC.
        var vvMatch = VerticalVisibilityRegex().Match(metar);
        if (vvMatch.Success && int.TryParse(vvMatch.Groups[1].Value, out int vvHundreds))
        {
            int vvFeet = vvHundreds * 100;
            layers.Add(new CloudLayer(CloudCover.Overcast, vvFeet));
            if (lowestCeiling is null || vvFeet < lowestCeiling)
            {
                lowestCeiling = vvFeet;
            }
        }

        layers.Sort((a, b) => a.BaseFeetAgl.CompareTo(b.BaseFeetAgl));

        return (layers, lowestCeiling);
    }

    /// <summary>
    /// Returns the lowest BKN/OVC layer base from a layer list, or null if none.
    /// Used by interpolation consumers to derive a fresh <c>CeilingFeetAgl</c>
    /// from an interpolated layer list rather than lerping the old scalar.
    /// </summary>
    public static int? CeilingFromLayers(IReadOnlyList<CloudLayer> layers)
    {
        int? lowest = null;
        foreach (var layer in layers)
        {
            if (layer.Cover is CloudCover.Broken or CloudCover.Overcast)
            {
                if (lowest is null || layer.BaseFeetAgl < lowest)
                {
                    lowest = layer.BaseFeetAgl;
                }
            }
        }
        return lowest;
    }

    /// <summary>
    /// Pairwise interpolation of two cloud layer lists between weather periods.
    /// Pairs by index (both lists are sorted ascending by base altitude); base
    /// altitudes lerp linearly while cover types step-change at t=0.5 (cover is
    /// discrete and cannot be averaged). Extras on the longer side pass through
    /// unchanged.
    /// </summary>
    public static IReadOnlyList<CloudLayer> InterpolateLayers(IReadOnlyList<CloudLayer> from, IReadOnlyList<CloudLayer> to, double t)
    {
        int paired = Math.Min(from.Count, to.Count);
        var result = new List<CloudLayer>(Math.Max(from.Count, to.Count));

        for (int i = 0; i < paired; i++)
        {
            int baseAlt = (int)Math.Round(from[i].BaseFeetAgl + t * (to[i].BaseFeetAgl - from[i].BaseFeetAgl));
            var cover = t < 0.5 ? from[i].Cover : to[i].Cover;
            result.Add(new CloudLayer(cover, baseAlt));
        }

        for (int i = paired; i < from.Count; i++)
        {
            result.Add(from[i]);
        }
        for (int i = paired; i < to.Count; i++)
        {
            result.Add(to[i]);
        }

        result.Sort((a, b) => a.BaseFeetAgl.CompareTo(b.BaseFeetAgl));
        return result;
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
