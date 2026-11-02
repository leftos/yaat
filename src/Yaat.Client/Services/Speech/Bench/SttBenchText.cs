using System.Globalization;
using Yaat.Client.Models;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Services.Speech.Bench;

/// <summary>How many rows of a run ran, failed and were listed.</summary>
/// <param name="Ran">Rows that measured every clip.</param>
/// <param name="Failed">Rows that ran and failed.</param>
/// <param name="Total">Every row, skipped ones included.</param>
public sealed record SttBenchRunCounts(int Ran, int Failed, int Total);

/// <summary>The figures and lines the speech benchmark window and the Settings > Speech line show, in invariant culture.</summary>
public static class SttBenchText
{
    /// <summary>What a figure that was not measured shows.</summary>
    public const string Dash = "—";

    /// <summary>The load and warm-up time, as <c>6.2 s</c>.</summary>
    public static string Load(CorpusWerTiming timing) => timing.LoadSeconds.ToString("F1", CultureInfo.InvariantCulture) + " s";

    /// <summary>A latency, as whole milliseconds.</summary>
    public static string Milliseconds(double value) => value.ToString("F0", CultureInfo.InvariantCulture);

    /// <summary>A memory figure, as <c>1.1 GB</c>; a dash when it was not measured.</summary>
    public static string Bytes(long bytes) => (bytes <= 0) ? Dash : (bytes / 1e9).ToString("F1", CultureInfo.InvariantCulture) + " GB";

    /// <summary>A word error rate, as <c>7.8%</c>; a dash when there is none.</summary>
    public static string Wer(double? value) => value is { } v ? (v * 100).ToString("F1", CultureInfo.InvariantCulture) + "%" : Dash;

    /// <summary>A callsign recall, as <c>96%</c>; a dash when there is none.</summary>
    public static string Recall(double? value) => value is { } v ? (v * 100).ToString("F0", CultureInfo.InvariantCulture) + "%" : Dash;

    /// <summary>The CPU or GPU label of a device.</summary>
    public static string Device(SttDevice device) => (device == SttDevice.Gpu) ? "GPU" : "CPU";

    /// <summary>How many of <paramref name="rows"/> ran, failed and were listed.</summary>
    public static SttBenchRunCounts RunCounts(IReadOnlyList<SttBenchLastRow> rows) =>
        new(rows.Count(r => r.Result?.Succeeded == true), rows.Count(r => r.Result is { Succeeded: false }), rows.Count);

    /// <summary>When the last run finished, in <paramref name="zone"/>, and how many of its rows ran.</summary>
    /// <param name="run">The last finished run.</param>
    /// <param name="zone">The zone to show the time in.</param>
    public static string LastRunLine(SttBenchLastRun run, TimeZoneInfo zone)
    {
        DateTimeOffset local = TimeZoneInfo.ConvertTime(run.FinishedUtc, zone);
        SttBenchRunCounts counts = RunCounts(run.Rows);
        string when = local.ToString("d MMM yyyy, HH:mm", CultureInfo.InvariantCulture);
        return $"Last run {when} · {counts.Ran} of {counts.Total} ran";
    }

    /// <summary>Which row of how many is running, and what it runs.</summary>
    public static string RunningLine(SttBenchProgress progress) =>
        progress.Current is { } row
            ? $"Running {progress.Done + 1} of {progress.Total}: {SttModelCardViewModel.TitleOf(row.Engine)} · {row.Mode} · {Device(row.Device)}"
            : $"Running {progress.Done} of {progress.Total}";
}
