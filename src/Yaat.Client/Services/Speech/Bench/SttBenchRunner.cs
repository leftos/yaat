using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Client.Models;
using Yaat.Sim.Speech;

namespace Yaat.Client.Services.Speech.Bench;

/// <summary>The engine a benchmark row loads, and why it cannot run in Accurate mode when the source found that out.</summary>
/// <param name="Engine">The engine, not yet warmed up.</param>
/// <param name="AccurateUnavailableReason">Why the engine is not an Accurate one; null when nothing stood in the way.</param>
public sealed record SttBenchEngine(ISttEngine Engine, string? AccurateUnavailableReason);

/// <summary>Creates the engine a benchmark row loads, for the job's model, mode and device.</summary>
public interface ISttBenchEngineSource
{
    /// <summary>The engine for <paramref name="job"/>; the time this takes counts toward the row's load time.</summary>
    Task<SttBenchEngine> CreateAsync(SttBenchJob job, CancellationToken ct);
}

/// <summary>Reads this process's working set.</summary>
public interface IProcessMemorySampler
{
    /// <summary>The process's working set now, in bytes.</summary>
    long WorkingSetBytes();

    /// <summary>The process's peak working set since it started, in bytes, as the platform reports it.</summary>
    long PeakWorkingSetBytes();
}

/// <summary>This process's working-set readings, refreshed on every read.</summary>
public sealed class CurrentProcessMemory : IProcessMemorySampler, IDisposable
{
    private readonly Process _self = Process.GetCurrentProcess();

    /// <inheritdoc />
    public long WorkingSetBytes()
    {
        _self.Refresh();
        return _self.WorkingSet64;
    }

    /// <inheritdoc />
    public long PeakWorkingSetBytes()
    {
        _self.Refresh();
        return _self.PeakWorkingSet64;
    }

    /// <summary>Releases the process handle.</summary>
    public void Dispose() => _self.Dispose();
}

/// <summary>What a benchmark row measures with.</summary>
/// <param name="Gpu">Reads this process's GPU memory, before the load, after it and after every clip.</param>
/// <param name="Memory">Reads this process's working set and peak working set.</param>
/// <param name="Time">The clock the load time and every clip's latency are taken on.</param>
public sealed record SttBenchMeters(IGpuMemorySampler Gpu, IProcessMemorySampler Memory, TimeProvider Time);

/// <summary>
/// Measures one benchmark row in this process: creates and loads the job's engine, checks it reached the mode and device
/// asked for, transcribes the bench clips once untimed and once timed, and summarises load time, latency and memory.
/// </summary>
public static class SttBenchRunner
{
    private const string AccurateUnavailablePrefix = "accurate mode unavailable: ";
    private const string LoadedFast = "the model loaded in Fast mode";
    private const string UnreadableGpu = "GPU not used: could not read this process's GPU memory";

    private static readonly ILogger Log = AppLog.CreateLogger("SttBenchRunner");

    /// <summary>
    /// The row's figures, or the one line saying why it has none. The load time covers creating the engine and warming it
    /// up. The first clip is then transcribed once untimed, so first-call graph set-up never lands in the percentiles. The
    /// peak working set reported is the larger of the process's own peak and every working set read after the load and
    /// after each clip, since not every platform tracks the peak.
    /// </summary>
    /// <param name="job">The row to measure.</param>
    /// <param name="engines">Creates the job's engine.</param>
    /// <param name="meters">The GPU memory, working-set and clock readings the row is measured with.</param>
    /// <param name="ct">Checked between clips; cancelling ends the row with no result.</param>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    /// <exception cref="InvalidDataException">The clip list or a clip is malformed.</exception>
    public static async Task<SttBenchResult> RunAsync(SttBenchJob job, ISttBenchEngineSource engines, SttBenchMeters meters, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        IReadOnlyList<BenchClip> clips = LoadClips(job.ClipsDirectory);
        if (clips.Count == 0)
        {
            return Fail(job, $"no clips found in {job.ClipsDirectory}", 0, null);
        }

        long preLoadWorkingSetBytes = meters.Memory.WorkingSetBytes();
        long preLoadGpuBytes = meters.Gpu.Sample();
        long loadStarted = meters.Time.GetTimestamp();
        SttBenchEngine created;
        try
        {
            created = await engines.CreateAsync(job, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return LoadFailed(job, clips.Count, ex);
        }

        using (created.Engine)
        {
            var row = new Row(job, clips, created, meters, preLoadWorkingSetBytes, preLoadGpuBytes, loadStarted);
            return await MeasureAsync(row, ct).ConfigureAwait(false);
        }
    }

    private static IReadOnlyList<BenchClip> LoadClips(string directory) =>
        File.Exists(Path.Combine(directory, BenchClips.ManifestFileName)) ? BenchClips.Load(directory) : [];

    private static async Task<SttBenchResult> MeasureAsync(Row row, CancellationToken ct)
    {
        try
        {
            await row.Engine.WarmUpAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return LoadFailed(row.Job, row.Clips.Count, ex);
        }
        double loadSeconds = row.Meters.Time.GetElapsedTime(row.LoadStarted).TotalSeconds;

        SttMode loadedMode = (row.Engine.Mode == TranscriptionMode.Accurate) ? SttMode.Accurate : SttMode.Fast;
        long afterLoadGpuBytes = row.Meters.Gpu.Sample();
        long afterLoadWorkingSetBytes = row.Meters.Memory.WorkingSetBytes();
        string? setupFailure = SetupFailure(row, loadedMode, afterLoadGpuBytes, out string? deviceWarning);
        if (setupFailure is not null)
        {
            return Fail(row.Job, setupFailure, row.Clips.Count, loadedMode);
        }

        ct.ThrowIfCancellationRequested();
        ClipRun warmUp = await TranscribeAsync(row, row.Clips[0], ct).ConfigureAwait(false);
        if (warmUp.Failure is not null)
        {
            return Fail(row.Job, warmUp.Failure, row.Clips.Count, loadedMode);
        }

        var loaded = new Loaded(loadSeconds, afterLoadGpuBytes, afterLoadWorkingSetBytes, loadedMode, deviceWarning);
        return await TimeClipsAsync(row, loaded, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Why the loaded engine cannot stand for the job, or null when it can: an Accurate job whose source gave a reason or
    /// whose engine loaded Fast, or a GPU job that cannot show this process's GPU memory rising through the load.
    /// </summary>
    private static string? SetupFailure(Row row, SttMode loadedMode, long afterLoadGpuBytes, out string? deviceWarning)
    {
        deviceWarning = null;
        string? accurateFailure = AccurateFailure(row, loadedMode);
        if (accurateFailure is not null)
        {
            return accurateFailure;
        }
        if ((row.Job.Device == SttDevice.Gpu) && ((row.PreLoadGpuBytes < 0) || (afterLoadGpuBytes < 0)))
        {
            return UnreadableGpu;
        }

        string? verdict = GpuUseCheck.Verdict(DeviceText(row.Job.Device), row.PreLoadGpuBytes, afterLoadGpuBytes, out bool isError);
        if (isError)
        {
            return $"GPU not used: {verdict}";
        }
        deviceWarning = verdict;
        return null;
    }

    private static string? AccurateFailure(Row row, SttMode loadedMode)
    {
        if (row.Job.Mode != SttMode.Accurate)
        {
            return null;
        }
        if (row.Created.AccurateUnavailableReason is not null)
        {
            return AccurateUnavailablePrefix + row.Created.AccurateUnavailableReason;
        }
        return (loadedMode != SttMode.Accurate) ? AccurateUnavailablePrefix + LoadedFast : null;
    }

    private static async Task<SttBenchResult> TimeClipsAsync(Row row, Loaded loaded, CancellationToken ct)
    {
        var latencies = new List<(double LatencyMs, bool Cached)>(row.Clips.Count);
        long peakGpuBytes = loaded.AfterLoadGpuBytes;
        long peakSampledWorkingSetBytes = loaded.AfterLoadWorkingSetBytes;
        foreach (BenchClip clip in row.Clips)
        {
            ct.ThrowIfCancellationRequested();
            ClipRun run = await TranscribeAsync(row, clip, ct).ConfigureAwait(false);
            if (run.Failure is not null)
            {
                return Fail(row.Job, run.Failure, row.Clips.Count, loaded.Mode);
            }
            latencies.Add((run.LatencyMs, false));
            peakGpuBytes = Math.Max(peakGpuBytes, row.Meters.Gpu.Sample());
            peakSampledWorkingSetBytes = Math.Max(peakSampledWorkingSetBytes, row.Meters.Memory.WorkingSetBytes());
        }

        long peakWorkingSetBytes = Math.Max(row.Meters.Memory.PeakWorkingSetBytes(), peakSampledWorkingSetBytes);
        var memory = new CorpusWerMemory(row.PreLoadWorkingSetBytes, peakWorkingSetBytes, row.PreLoadGpuBytes, peakGpuBytes);
        CorpusWerTiming timing = CorpusWerLatency.Summarize(DeviceText(row.Job.Device), loaded.LoadSeconds, latencies, memory) with
        {
            DeviceWarning = loaded.DeviceWarning,
        };
        return SttBenchResult.Measured(timing, row.Clips.Count, loaded.Mode);
    }

    /// <summary>Transcribes one clip on the row's clock; a throw other than cancellation becomes the clip's failure line.</summary>
    private static async Task<ClipRun> TranscribeAsync(Row row, BenchClip clip, CancellationToken ct)
    {
        long started = row.Meters.Time.GetTimestamp();
        try
        {
            await row.Engine.TranscribeAsync(clip.Samples, null, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ClipRun(0, new RowFailure($"transcribe failed on {clip.Id}: {ex.Message}", ex));
        }
        return new ClipRun(row.Meters.Time.GetElapsedTime(started).TotalMilliseconds, null);
    }

    private static SttBenchResult LoadFailed(SttBenchJob job, int clipCount, Exception ex) =>
        Fail(job, new RowFailure($"load failed: {ex.Message}", ex), clipCount, null);

    private static SttBenchResult Fail(SttBenchJob job, string failure, int clipCount, SttMode? loadedMode) =>
        Fail(job, new RowFailure(failure, null), clipCount, loadedMode);

    /// <summary>Logs the row's failure once, with the exception behind it when there is one, and returns the failed row.</summary>
    private static SttBenchResult Fail(SttBenchJob job, RowFailure failure, int clipCount, SttMode? loadedMode)
    {
        Log.LogWarning(failure.Error, "bench row {Engine} {Mode} on {Device} failed: {Failure}", job.Engine, job.Mode, job.Device, failure.Text);
        return SttBenchResult.Failed(failure.Text, clipCount, loadedMode);
    }

    private static string DeviceText(SttDevice device) => (device == SttDevice.Gpu) ? "gpu" : "cpu";

    /// <summary>What a row holds while it is measured.</summary>
    private sealed record Row(
        SttBenchJob Job,
        IReadOnlyList<BenchClip> Clips,
        SttBenchEngine Created,
        SttBenchMeters Meters,
        long PreLoadWorkingSetBytes,
        long PreLoadGpuBytes,
        long LoadStarted
    )
    {
        public ISttEngine Engine => Created.Engine;
    }

    /// <summary>What the load left: how long it took, the GPU memory after it, the mode it reached and any device warning.</summary>
    private sealed record Loaded(double LoadSeconds, long AfterLoadGpuBytes, long AfterLoadWorkingSetBytes, SttMode Mode, string? DeviceWarning);

    /// <summary>One clip's latency, or why it failed.</summary>
    private sealed record ClipRun(double LatencyMs, RowFailure? Failure);

    /// <summary>The line a failed row shows, and the exception behind it when there is one.</summary>
    private sealed record RowFailure(string Text, Exception? Error);
}
