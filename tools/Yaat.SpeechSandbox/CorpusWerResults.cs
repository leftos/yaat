using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Yaat.SpeechSandbox;

/// <summary>Pooled word error counts of one row within one prompt arm.</summary>
/// <param name="Role">
/// <c>all</c>, <c>pilot</c>, <c>controller</c>, <c>mixed</c> or <c>unknown</c>, or a provenance or feed row's label.
/// </param>
/// <param name="Wer">Total errors over total reference words.</param>
public sealed record CorpusWerRoleTotals(string Role, int Segments, int ReferenceWords, int Substitutions, int Deletions, int Insertions, double Wer)
{
    /// <summary>The row of <paramref name="role"/> from its tally.</summary>
    public static CorpusWerRoleTotals From(string role, WerTally tally) =>
        new(role, tally.Segments, tally.Total.ReferenceWords, tally.Total.Substitutions, tally.Total.Deletions, tally.Total.Insertions, tally.Rate);
}

/// <summary>One prompt arm of a run: per-role totals (<c>all</c> first), the audio it transcribed and the wall time it took.</summary>
/// <param name="Prompt"><c>default</c> (the production biasing prompt) or <c>none</c>.</param>
/// <param name="NoTranscript">
/// Segments the engine returned no transcript for, scored as empty; 0 when a results document predates the field.
/// </param>
/// <param name="Timing">The arm's measured speed and memory; null in a committed baseline and in documents that predate it.</param>
/// <param name="Provenance">
/// Pooled totals of the segments whose reference is <c>owner</c>-checked and of those <c>owner</c>- or
/// <c>agreed</c>-checked, in that order; null when no segment carries a provenance.
/// </param>
/// <param name="Feeds">Pooled totals per feed, ordinal by feed name with the unfed segments pooled last under <c>unknown</c>; null when no segment carries one.</param>
public sealed record CorpusWerArm(
    string Prompt,
    IReadOnlyList<CorpusWerRoleTotals> Roles,
    double AudioSeconds,
    double WallSeconds,
    int NoTranscript,
    CorpusWerTiming? Timing,
    IReadOnlyList<CorpusWerRoleTotals>? Provenance,
    IReadOnlyList<CorpusWerRoleTotals>? Feeds
)
{
    /// <summary>The console and report line naming the segments without a transcript.</summary>
    public string NoTranscriptLine() => $"{Prompt}: {NoTranscript} of {Roles[0].Segments} segments returned no transcript";
}

/// <summary>One arm's speed and memory: the model load, the latency percentiles and the working set.</summary>
/// <param name="Device">The device the engine ran on: <c>auto</c>, <c>cpu</c> or <c>cloud</c>.</param>
/// <param name="LoadSeconds">How long the model load took before the first segment.</param>
/// <param name="TimedSegments">Segments whose latency the percentiles cover.</param>
/// <param name="CachedSegments">Segments answered from a cloud cache, excluded from the percentiles.</param>
/// <param name="LatencyP50Ms">The median of the timed segments' latency; 0 when none.</param>
/// <param name="LatencyP90Ms">The 90th percentile of the timed segments' latency; 0 when none.</param>
/// <param name="LatencyMaxMs">The slowest timed segment; 0 when none.</param>
/// <param name="PreLoadWorkingSetBytes">The process's working set before the model loaded.</param>
/// <param name="PeakWorkingSetBytes">The process's peak working set at the end of the arm.</param>
/// <param name="PreLoadGpuBytes">GPU memory before the load; -1 means it was not measured.</param>
/// <param name="PeakGpuBytes">Peak GPU memory; -1 means it was not measured.</param>
/// <param name="DeviceWarning">
/// What the GPU memory figures said about the device next to what it asked for, when worth reporting; null
/// when there is nothing to say.
/// </param>
/// <remarks>Timing is measured per run and is never written to a committed baseline.</remarks>
public sealed record CorpusWerTiming(
    string Device,
    double LoadSeconds,
    int TimedSegments,
    int CachedSegments,
    double LatencyP50Ms,
    double LatencyP90Ms,
    double LatencyMaxMs,
    long PreLoadWorkingSetBytes,
    long PeakWorkingSetBytes,
    long PreLoadGpuBytes,
    long PeakGpuBytes,
    string? DeviceWarning
);

/// <summary>The working-set and GPU figures a run measured around the model load.</summary>
/// <param name="PreLoadWorkingSetBytes">The process's working set before the model loaded.</param>
/// <param name="PeakWorkingSetBytes">The process's peak working set at the end of the arm.</param>
/// <param name="PreLoadGpuBytes">GPU memory before the load; -1 means it was not measured.</param>
/// <param name="PeakGpuBytes">Peak GPU memory; -1 means it was not measured.</param>
public sealed record CorpusWerMemory(long PreLoadWorkingSetBytes, long PeakWorkingSetBytes, long PreLoadGpuBytes, long PeakGpuBytes);

/// <summary>
/// The <c>results.json</c> of one <c>--corpus-wer</c> run, and the shape of a committed baseline.
/// </summary>
/// <param name="Corpus"><c>uwb-atcc</c>, or a folder corpus's lowercased directory name.</param>
/// <param name="Revision">The dataset revision; empty for a folder corpus.</param>
/// <param name="Sha256">The dataset file's hash, or a folder corpus's <see cref="LocalCorpus.Fingerprint"/>.</param>
/// <param name="Subset">How many segments the run drew; null for every segment.</param>
/// <param name="SubsetFingerprint">The drawn ids' <see cref="CorpusSubset.Fingerprint"/>.</param>
/// <param name="SttModel">The <c>--stt</c> engine as <see cref="CorpusSttEngines.RecordedModel"/> records it; never a machine path.</param>
public sealed record CorpusWerBaseline(
    string Corpus,
    string Revision,
    string Sha256,
    int Seed,
    int? Subset,
    string SubsetFingerprint,
    string SttModel,
    IReadOnlyList<CorpusWerArm> Arms
);

/// <summary>One line of <c>results.jsonl</c>: one segment scored under one prompt arm.</summary>
/// <param name="Reference">The normalised reference.</param>
/// <param name="Hypothesis">Whisper's raw transcript; null when it returned nothing.</param>
/// <param name="HypothesisNormalized">The normalised transcript; empty when there was none.</param>
/// <param name="LatencyMs">The segment's latency: the engine's own figure when it reported one, else the caller's stopwatch.</param>
/// <param name="Cached">True when a cloud engine answered from its cache.</param>
public sealed record CorpusWerSegmentLine(
    string Prompt,
    string Id,
    string Role,
    string Reference,
    string? Hypothesis,
    string HypothesisNormalized,
    int Substitutions,
    int Deletions,
    int Insertions,
    int ReferenceWords,
    double LatencyMs,
    bool Cached
);

/// <summary>How a run compares with the baseline.</summary>
/// <param name="Skipped">True when the run was measured differently from the baseline, so nothing was compared.</param>
/// <param name="SkipReason">Each differing field with both values, when skipped.</param>
/// <param name="Lines">One verdict line per arm.</param>
public sealed record CorpusWerComparison(bool Skipped, string? SkipReason, bool HasRegression, IReadOnlyList<string> Lines);

/// <summary>Source-generated JSON for <c>results.json</c> and the baseline.</summary>
[JsonSerializable(typeof(CorpusWerBaseline))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    NewLine = "\n"
)]
public sealed partial class CorpusWerJsonContext : JsonSerializerContext;

/// <summary>Source-generated JSON for the one-line-per-segment <c>results.jsonl</c>; nulls are kept.</summary>
[JsonSerializable(typeof(CorpusWerSegmentLine))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class CorpusWerJsonlContext : JsonSerializerContext;

/// <summary>Nearest-rank latency percentiles, and the per-arm timing built from them.</summary>
public static class CorpusWerLatency
{
    /// <summary>
    /// The element at rank <c>ceil(percentile / 100 · n)</c> of the values sorted ascending, clamped to the
    /// list's ends; 0 when there are no values.
    /// </summary>
    /// <param name="values">The latencies in milliseconds, in any order.</param>
    /// <param name="percentile">The percentile to take, from 1 through 100.</param>
    /// <exception cref="ArgumentOutOfRangeException">The percentile is outside 1–100.</exception>
    public static double Percentile(IReadOnlyList<double> values, int percentile)
    {
        if ((percentile < 1) || (percentile > 100))
        {
            throw new ArgumentOutOfRangeException(nameof(percentile), percentile, "a percentile runs from 1 through 100");
        }
        if (values.Count == 0)
        {
            return 0;
        }

        double[] sorted = [.. values];
        Array.Sort(sorted);
        int rank = (int)Math.Ceiling((percentile / 100.0) * sorted.Length) - 1;
        return sorted[Math.Clamp(rank, 0, sorted.Length - 1)];
    }

    /// <summary>
    /// One arm's timing: the load it paid, the percentiles over its non-cached segments and the memory
    /// figures measured around it. Failed segments are included, since they cost latency too.
    /// </summary>
    /// <param name="device">The device the engine ran on.</param>
    /// <param name="loadSeconds">How long the model load took.</param>
    /// <param name="segments">Every segment's latency and whether it came from a cache.</param>
    /// <param name="memory">The working-set and GPU figures measured around the load.</param>
    /// <remarks>The device warning is set by the caller with <c>with</c>: most summaries carry none.</remarks>
    public static CorpusWerTiming Summarize(
        string device,
        double loadSeconds,
        IReadOnlyList<(double LatencyMs, bool Cached)> segments,
        CorpusWerMemory memory
    )
    {
        double[] timed = [.. segments.Where(s => !s.Cached).Select(s => s.LatencyMs)];
        return new CorpusWerTiming(
            device,
            loadSeconds,
            timed.Length,
            segments.Count(s => s.Cached),
            Percentile(timed, 50),
            Percentile(timed, 90),
            (timed.Length == 0) ? 0 : timed.Max(),
            memory.PreLoadWorkingSetBytes,
            memory.PeakWorkingSetBytes,
            memory.PreLoadGpuBytes,
            memory.PeakGpuBytes,
            null
        );
    }
}

/// <summary>Pure comparison of a <c>--corpus-wer</c> run with its baseline.</summary>
public static class CorpusWerBaselineComparer
{
    /// <summary>How many percentage points an arm's overall WER may rise before it counts as a regression.</summary>
    public const double RegressionThresholdPoints = 1.0;

    /// <summary>
    /// Skips (no regression) when the two were measured on a different corpus, revision, fingerprint,
    /// seed, subset, drawn ids or model; otherwise compares the <c>all</c> row of every arm present in both.
    /// </summary>
    public static CorpusWerComparison Compare(CorpusWerBaseline baseline, CorpusWerBaseline run)
    {
        string? skipReason = SkipReason(baseline, run);
        if (skipReason is not null)
        {
            return new CorpusWerComparison(true, skipReason, false, []);
        }

        var lines = new List<string>();
        bool regression = false;
        foreach (CorpusWerArm arm in run.Arms)
        {
            CorpusWerArm? before = baseline.Arms.FirstOrDefault(a => a.Prompt == arm.Prompt);
            if (before is null)
            {
                lines.Add($"{arm.Prompt}: not in the baseline, nothing to compare");
                continue;
            }
            double was = OverallWer(before);
            double now = OverallWer(arm);
            bool regressed = (now - was) > (RegressionThresholdPoints / 100);
            regression |= regressed;
            lines.Add($"{arm.Prompt}: {Percent(was)} → {Percent(now)} ({Points(now - was)} pt) {(regressed ? "REGRESSION" : "ok")}");
        }
        foreach (CorpusWerArm gone in baseline.Arms.Where(b => run.Arms.All(a => a.Prompt != b.Prompt)))
        {
            lines.Add($"{gone.Prompt}: in the baseline, not in this run");
        }
        return new CorpusWerComparison(false, null, regression, lines);
    }

    /// <summary>A WER as a percentage with one decimal, e.g. <c>23.4%</c>.</summary>
    public static string Percent(double wer) => (wer * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%";

    /// <summary>
    /// The document to commit as a baseline: the same run with every arm's timing stripped, since timing
    /// is measured per machine and per run.
    /// </summary>
    public static CorpusWerBaseline ForBaseline(CorpusWerBaseline results) =>
        results with
        {
            Arms = [.. results.Arms.Select(a => a with { Timing = null })],
        };

    public static string Serialize(CorpusWerBaseline results) => JsonSerializer.Serialize(results, CorpusWerJsonContext.Default.CorpusWerBaseline);

    public static CorpusWerBaseline? Deserialize(string json) => JsonSerializer.Deserialize(json, CorpusWerJsonContext.Default.CorpusWerBaseline);

    private static string? SkipReason(CorpusWerBaseline baseline, CorpusWerBaseline run)
    {
        var differences = new List<string>();
        AddIfDifferent(differences, "Corpus", baseline.Corpus, run.Corpus);
        AddIfDifferent(differences, "Revision", baseline.Revision, run.Revision);
        AddIfDifferent(differences, "Sha256", baseline.Sha256, run.Sha256);
        AddIfDifferent(differences, "Seed", baseline.Seed.ToString(CultureInfo.InvariantCulture), run.Seed.ToString(CultureInfo.InvariantCulture));
        AddIfDifferent(differences, "Subset", SubsetText(baseline.Subset), SubsetText(run.Subset));
        AddIfDifferent(differences, "SubsetFingerprint", baseline.SubsetFingerprint, run.SubsetFingerprint);
        AddIfDifferent(differences, "SttModel", baseline.SttModel, run.SttModel);
        return (differences.Count == 0) ? null : string.Join("; ", differences);
    }

    private static void AddIfDifferent(List<string> differences, string field, string baseline, string run)
    {
        if (!string.Equals(baseline, run, StringComparison.Ordinal))
        {
            differences.Add($"{field}: baseline {baseline}, run {run}");
        }
    }

    private static string SubsetText(int? subset) => subset?.ToString(CultureInfo.InvariantCulture) ?? "all";

    private static double OverallWer(CorpusWerArm arm) =>
        arm.Roles.FirstOrDefault(r => r.Role == "all")?.Wer ?? throw new InvalidDataException($"Arm '{arm.Prompt}' has no 'all' row.");

    private static string Points(double delta) => (delta * 100).ToString("+0.0;-0.0;+0.0", CultureInfo.InvariantCulture);
}
