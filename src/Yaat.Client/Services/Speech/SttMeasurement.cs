namespace Yaat.Client.Services.Speech;

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
