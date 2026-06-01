using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Yaat.Sim;

/// <summary>
/// Reconstructs a reported METAR string by patching the modeled dynamic groups (report type,
/// timestamp, wind, visibility, sky/ceiling, altimeter) into a base METAR while preserving the
/// groups the sim does not model (temperature/dewpoint, present weather, and most remarks).
/// Stale period-relative and pressure remarks that would contradict the patched groups are
/// stripped. Encoding follows AIM 7-1-28 (e.g. visibility caps at 10SM, automated sky uses CLR,
/// altimeter is truncated to hundredths).
/// </summary>
public static partial class MetarComposer
{
    [GeneratedRegex(@"^(?:METAR|SPECI)\s+", RegexOptions.Compiled)]
    private static partial Regex LeadingTypeRegex();

    [GeneratedRegex(@"\b\d{6}Z\b", RegexOptions.Compiled)]
    private static partial Regex TimestampRegex();

    [GeneratedRegex(@"\b(?:\d{3}|VRB)\d{2,3}(?:G\d{2,3})?KT\b", RegexOptions.Compiled)]
    private static partial Regex WindRegex();

    [GeneratedRegex(@"\b\d{3}V\d{3}\b", RegexOptions.Compiled)]
    private static partial Regex WindVariabilityRegex();

    [GeneratedRegex(@"(?<!\S)[PM]?\d+(?:\s+\d+/\d+)?(?:/\d+)?SM(?!\S)", RegexOptions.Compiled)]
    private static partial Regex VisibilityRegex();

    [GeneratedRegex(
        @"(?:(?:FEW|SCT|BKN|OVC)\d{3}(?:CB|TCU)?|CLR|SKC|NSC|VV\d{3})(?:\s+(?:(?:FEW|SCT|BKN|OVC)\d{3}(?:CB|TCU)?|VV\d{3}))*",
        RegexOptions.Compiled
    )]
    private static partial Regex SkyRunRegex();

    [GeneratedRegex(@"\bA\d{4}\b", RegexOptions.Compiled)]
    private static partial Regex AltimeterRegex();

    [GeneratedRegex(@"\bM?\d{2}/M?\d{2}\b", RegexOptions.Compiled)]
    private static partial Regex TempDewRegex();

    [GeneratedRegex(@"\bSLP\d{3}\b", RegexOptions.Compiled)]
    private static partial Regex SlpRemarkRegex();

    [GeneratedRegex(@"\bPK WND \d{5,6}/\d{2,4}\b", RegexOptions.Compiled)]
    private static partial Regex PeakWindRemarkRegex();

    [GeneratedRegex(@"\bWSHFT \d{2,4}\b", RegexOptions.Compiled)]
    private static partial Regex WindShiftRemarkRegex();

    [GeneratedRegex(@"\bVIS \d+(?:/\d+)?V\d+(?:/\d+)?\b", RegexOptions.Compiled)]
    private static partial Regex VariableVisRemarkRegex();

    [GeneratedRegex(@"\bCIG \d{3}V\d{3}\b", RegexOptions.Compiled)]
    private static partial Regex VariableCeilingRemarkRegex();

    [GeneratedRegex(@" {2,}", RegexOptions.Compiled)]
    private static partial Regex MultiSpaceRegex();

    /// <summary>
    /// Produces a reported METAR string from <paramref name="baseMetar"/> using the conditions in
    /// <paramref name="conditions"/>, stamped at <paramref name="observationUtc"/> and prefixed
    /// METAR (routine) or SPECI (special) per <paramref name="isSpeci"/>.
    /// </summary>
    public static string Compose(string baseMetar, ReportedConditions conditions, DateTime observationUtc, bool isSpeci)
    {
        string body;
        string? remarks = null;
        int rmkIndex = baseMetar.IndexOf(" RMK ", StringComparison.Ordinal);
        if (rmkIndex >= 0)
        {
            body = baseMetar[..rmkIndex];
            remarks = baseMetar[(rmkIndex + 1)..];
        }
        else
        {
            body = baseMetar;
        }

        body = LeadingTypeRegex().Replace(body.Trim(), "");

        body = PatchTimestamp(body, observationUtc);
        body = PatchWind(body, conditions);
        body = PatchVisibility(body, conditions);
        body = PatchSky(body, conditions);
        body = PatchAltimeter(body, conditions);
        body = MultiSpaceRegex().Replace(body, " ").Trim();

        var result = (isSpeci ? "SPECI " : "METAR ") + body;

        if (remarks is not null)
        {
            var cleaned = StripStaleRemarks(remarks);
            if (!string.IsNullOrWhiteSpace(cleaned) && !string.Equals(cleaned, "RMK", StringComparison.Ordinal))
            {
                result += " " + cleaned;
            }
        }

        return result;
    }

    private static string PatchTimestamp(string body, DateTime observationUtc)
    {
        var stamp = observationUtc.ToString("ddHHmm", CultureInfo.InvariantCulture) + "Z";
        if (TimestampRegex().IsMatch(body))
        {
            return TimestampRegex().Replace(body, stamp, 1);
        }

        int firstSpace = body.IndexOf(' ');
        return firstSpace > 0 ? body[..firstSpace] + " " + stamp + body[firstSpace..] : body + " " + stamp;
    }

    private static string PatchWind(string body, ReportedConditions c)
    {
        if (!WindRegex().IsMatch(body))
        {
            return body;
        }

        body = WindRegex().Replace(body, ComposeWind(c), 1);
        // We do not model a variable-direction spread; drop any dddVddd group from the base.
        return WindVariabilityRegex().Replace(body, "", 1);
    }

    private static string PatchVisibility(string body, ReportedConditions c)
    {
        if (c.VisibilityStatuteMiles is not { } vis || !VisibilityRegex().IsMatch(body))
        {
            return body;
        }

        return VisibilityRegex().Replace(body, ComposeVisibility(vis), 1);
    }

    private static string PatchSky(string body, ReportedConditions c)
    {
        var sky = ComposeSky(c.Layers);

        var run = SkyRunRegex().Match(body);
        if (run.Success)
        {
            return body[..run.Index] + sky + body[(run.Index + run.Length)..];
        }

        var tempDew = TempDewRegex().Match(body);
        if (tempDew.Success)
        {
            return body[..tempDew.Index] + sky + " " + body[tempDew.Index..];
        }

        var altimeter = AltimeterRegex().Match(body);
        if (altimeter.Success)
        {
            return body[..altimeter.Index] + sky + " " + body[altimeter.Index..];
        }

        return body + " " + sky;
    }

    private static string PatchAltimeter(string body, ReportedConditions c)
    {
        if (c.AltimeterInHg is not { } altimeter || !AltimeterRegex().IsMatch(body))
        {
            return body;
        }

        return AltimeterRegex().Replace(body, ComposeAltimeter(altimeter), 1);
    }

    private static string ComposeWind(ReportedConditions c)
    {
        if (c.Calm)
        {
            return "00000KT";
        }

        var direction = c.WindDirTrueDeg.ToString("D3", CultureInfo.InvariantCulture);
        var speed = c.WindSpeedKt.ToString(c.WindSpeedKt >= 100 ? "D3" : "D2", CultureInfo.InvariantCulture);
        var gust = c.WindGustKt is { } g ? "G" + g.ToString(g >= 100 ? "D3" : "D2", CultureInfo.InvariantCulture) : "";
        return direction + speed + gust + "KT";
    }

    private static string ComposeVisibility(double vis)
    {
        if (vis >= 10.0)
        {
            return "10SM";
        }

        if (vis < 0.25)
        {
            return "M1/4SM";
        }

        if (vis < 3.0)
        {
            // Quarter-mile increments below 3 SM. Clamp to 2 3/4 so a sub-3 visibility never
            // rounds up to "3SM" — that would contradict a SPECI issued for crossing below 3.
            int quarters = Math.Min(11, (int)Math.Round(vis * 4.0, MidpointRounding.AwayFromZero));
            int whole = quarters / 4;
            int remainder = quarters % 4;
            var fraction = remainder switch
            {
                1 => "1/4",
                2 => "1/2",
                3 => "3/4",
                _ => "",
            };

            if (whole == 0)
            {
                return fraction + "SM";
            }

            return remainder == 0
                ? whole.ToString(CultureInfo.InvariantCulture) + "SM"
                : whole.ToString(CultureInfo.InvariantCulture) + " " + fraction + "SM";
        }

        int miles = Math.Min(10, (int)Math.Round(vis, MidpointRounding.AwayFromZero));
        return miles.ToString(CultureInfo.InvariantCulture) + "SM";
    }

    // Automated stations report clouds only up to 12,000 ft AGL; above that the sky is reported clear.
    private const int MaxReportableCloudFeetAgl = 12000;

    private static string ComposeSky(IReadOnlyList<MetarParser.CloudLayer> layers)
    {
        // Note: a VV (indefinite ceiling / total obscuration) base is modeled by the parser as a
        // synthetic OVC layer, so it is re-reported here as OVC rather than VV.
        var reportable = layers.Where(l => l.BaseFeetAgl <= MaxReportableCloudFeetAgl).OrderBy(l => l.BaseFeetAgl).ToList();
        if (reportable.Count == 0)
        {
            return "CLR";
        }

        var sb = new StringBuilder();
        foreach (var layer in reportable)
        {
            var cover = layer.Cover switch
            {
                MetarParser.CloudCover.Few => "FEW",
                MetarParser.CloudCover.Scattered => "SCT",
                MetarParser.CloudCover.Broken => "BKN",
                MetarParser.CloudCover.Overcast => "OVC",
                _ => "OVC",
            };

            if (sb.Length > 0)
            {
                sb.Append(' ');
            }

            sb.Append(cover).Append((layer.BaseFeetAgl / 100).ToString("D3", CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }

    private static string ComposeAltimeter(double inHg)
    {
        // METARs truncate (not round) the altimeter to hundredths of an inch (AIM 7-1-28).
        int hundredths = (int)(inHg * 100.0 + 1e-6);
        return "A" + hundredths.ToString("D4", CultureInfo.InvariantCulture);
    }

    private static string StripStaleRemarks(string remarks)
    {
        remarks = SlpRemarkRegex().Replace(remarks, "");
        remarks = PeakWindRemarkRegex().Replace(remarks, "");
        remarks = WindShiftRemarkRegex().Replace(remarks, "");
        remarks = VariableVisRemarkRegex().Replace(remarks, "");
        remarks = VariableCeilingRemarkRegex().Replace(remarks, "");
        return MultiSpaceRegex().Replace(remarks, " ").Trim();
    }
}
