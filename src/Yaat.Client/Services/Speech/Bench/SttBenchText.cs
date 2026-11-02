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

    /// <summary>
    /// The Settings > Speech benchmark line: the run going on, or the last run's date and counts, then the saved setting's own
    /// figures when that row succeeded in the last run.
    /// </summary>
    /// <param name="snapshot">The benchmark as it stands now.</param>
    /// <param name="current">The model, mode and device the preferences save.</param>
    /// <param name="zone">The zone the last run's date is shown in.</param>
    public static SttBenchSummaryLine SettingsSummary(
        SttBenchSnapshot snapshot,
        (SttEngine Engine, SttMode Mode, SttDevice Device) current,
        TimeZoneInfo zone
    )
    {
        if (snapshot.State is SttBenchState.Unloading or SttBenchState.Reloading)
        {
            return new SttBenchSummaryLine(snapshot.StatusMessage ?? "", "", "");
        }
        if (snapshot.State == SttBenchState.Running)
        {
            // The service reports every row done while the state is still Running.
            int running = Math.Min(snapshot.Progress.Done + 1, snapshot.Progress.Total);
            return new SttBenchSummaryLine($"Running {running} of {snapshot.Progress.Total}…", "", "");
        }
        if (snapshot.LastRun is not { } lastRun)
        {
            return new SttBenchSummaryLine(NeverRunText, "", "");
        }
        string date = TimeZoneInfo.ConvertTime(lastRun.FinishedUtc, zone).ToString("d MMM yyyy", CultureInfo.InvariantCulture);
        SttBenchRunCounts counts = RunCounts(lastRun.Rows);
        SttBenchLastRow? row = RowOf(lastRun, current.Engine, current.Mode, current.Device);
        string tail = (row?.Result is { Succeeded: true, Timing: { } timing }) ? $"· this setting {Figures(timing, current.Device)}" : "";
        if (counts.Failed > 0)
        {
            return new SttBenchSummaryLine($"Last run {date}: {counts.Ran} of {counts.Total} ran,", $"{counts.Failed} failed", tail);
        }
        string ran = (counts.Ran == counts.Total) ? Runs(counts.Total) : $"{counts.Ran} of {counts.Total} ran";
        return new SttBenchSummaryLine($"Last run {date}: {ran}", "", tail);
    }

    /// <summary>A model card's Measured row for the mode and device Settings has selected.</summary>
    /// <param name="lastRun">The last finished run; null when none has.</param>
    /// <param name="engine">The card's model.</param>
    /// <param name="mode">The mode Settings has selected.</param>
    /// <param name="device">The device Settings has selected.</param>
    public static string CardMeasured(SttBenchLastRun? lastRun, SttEngine engine, SttMode mode, SttDevice device) =>
        ((lastRun is null) ? null : RowOf(lastRun, engine, mode, device))?.Result switch
        {
            { Succeeded: true, Timing: { } timing } => $"{Figures(timing, device)} ({mode}, {Device(device)})",
            { Succeeded: false } => FailedInLastRunText,
            _ => SttModelCardViewModel.NotMeasuredText,
        };

    /// <summary>Which row of how many is running, and what it runs.</summary>
    public static string RunningLine(SttBenchProgress progress) =>
        progress.Current is { } row
            ? $"Running {progress.Done + 1} of {progress.Total}: {SttModelCardViewModel.TitleOf(row.Engine)} · {row.Mode} · {Device(row.Device)}"
            : $"Running {progress.Done} of {progress.Total}";

    private const string NeverRunText = "Never run on this machine";

    private const string FailedInLastRunText = "Failed in the last benchmark run";

    private static SttBenchLastRow? RowOf(SttBenchLastRun run, SttEngine engine, SttMode mode, SttDevice device) =>
        run.Rows.FirstOrDefault(r => (r.Engine == engine) && (r.Mode == mode) && (r.Device == device));

    // A GPU row's memory is the card's, a CPU row's the process's.
    private static string Figures(CorpusWerTiming timing, SttDevice device) =>
        (device == SttDevice.Gpu)
            ? $"{Milliseconds(timing.LatencyP50Ms)} ms p50, {Bytes(timing.PeakGpuBytes)} VRAM"
            : $"{Milliseconds(timing.LatencyP50Ms)} ms p50, {Bytes(timing.PeakWorkingSetBytes)} RAM";

    private static string Runs(int count) => (count == 1) ? "1 run" : $"{count} runs";
}

/// <summary>What the Settings > Speech benchmark line reads from the benchmark.</summary>
/// <param name="State">The benchmark's phase.</param>
/// <param name="Progress">How far the run going on has come.</param>
/// <param name="StatusMessage">What the benchmark says while the speech models unload or reload.</param>
/// <param name="LastRun">The last finished run; null when none has.</param>
public sealed record SttBenchSnapshot(SttBenchState State, SttBenchProgress Progress, string? StatusMessage, SttBenchLastRun? LastRun);

/// <summary>The Settings > Speech benchmark line, in the three parts it is drawn in.</summary>
/// <param name="Lead">The text before the failed count.</param>
/// <param name="Failed">How many rows failed, drawn as a warning; empty when none did.</param>
/// <param name="Tail">The saved setting's own figures; empty when that row did not succeed in the last run.</param>
public sealed record SttBenchSummaryLine(string Lead, string Failed, string Tail);
