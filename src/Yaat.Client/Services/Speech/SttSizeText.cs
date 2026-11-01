using System.Globalization;

namespace Yaat.Client.Services.Speech;

/// <summary>Download and on-disk sizes as Settings > Speech shows them: decimal gigabytes (10^9 bytes), invariant culture.</summary>
public static class SttSizeText
{
    private const double BytesPerGb = 1_000_000_000d;

    /// <summary>The size with two decimals: <c>2.55 GB</c>.</summary>
    /// <param name="bytes">The size in bytes.</param>
    public static string Total(long bytes) => $"{TotalGb(bytes).ToString("0.00", CultureInfo.InvariantCulture)} GB";

    /// <summary>
    /// How far a download is, the done part with one decimal: <c>1.1 of 2.55 GB</c>. A fraction of 1 or more shows the total
    /// as the done part (<c>2.55 of 2.55 GB</c>). Below that the done part rounds to the nearest tenth, or down when rounding
    /// would reach the total, so it never shows the total early; a negative fraction counts as 0.
    /// </summary>
    /// <param name="fraction">The installer's progress, 0.0 to 1.0.</param>
    /// <param name="totalBytes">The download's total size in bytes.</param>
    public static string Progress(double fraction, long totalBytes)
    {
        string total = Total(totalBytes);
        if (fraction >= 1.0)
        {
            return $"{total[..^" GB".Length]} of {total}";
        }
        double totalGb = TotalGb(totalBytes);
        double exactGb = Math.Max(fraction, 0.0) * totalGb;
        double doneGb = Math.Round(exactGb, 1, MidpointRounding.AwayFromZero);
        if (doneGb >= totalGb)
        {
            doneGb = Math.Floor(exactGb * 10) / 10;
        }
        return $"{doneGb.ToString("0.0", CultureInfo.InvariantCulture)} of {total}";
    }

    /// <summary>The idle line of an installed model: <c>Ready (2.55 GB on disk)</c>.</summary>
    /// <param name="bytes">The model's size on disk in bytes.</param>
    public static string Ready(long bytes) => $"Ready ({Total(bytes)} on disk)";

    /// <summary>The size in GB rounded to the two decimals <see cref="Total"/> shows, so the comparisons match the text.</summary>
    private static double TotalGb(long bytes) => Math.Round(bytes / BytesPerGb, 2, MidpointRounding.AwayFromZero);
}
