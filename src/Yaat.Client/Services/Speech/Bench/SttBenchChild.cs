using System.Text.Json;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Client.Models;

namespace Yaat.Client.Services.Speech.Bench;

/// <summary>
/// The benchmark child process, <c>Yaat.Client --stt-bench-one &lt;job.json&gt;</c>: reads one <see cref="SttBenchJob"/>,
/// loads the sherpa-onnx native set for the job's device, measures the row with <see cref="SttBenchRunner"/> and writes
/// its <see cref="SttBenchResult"/> to the job's result path. Its log is <c>yaat-stt-bench.log</c>; it never starts the app.
/// </summary>
public static class SttBenchChild
{
    /// <summary>The argument that starts the child; the job file's path follows it.</summary>
    public const string Flag = "--stt-bench-one";

    /// <summary>A result file was written, whether the row passed or failed.</summary>
    public const int ExitResultWritten = 0;

    /// <summary>The job file could not be read; no result was written.</summary>
    public const int ExitJobUnreadable = 2;

    /// <summary>The result could not be written.</summary>
    public const int ExitResultUnwritable = 3;

    private const string LogCategory = "SttBenchChild";

    /// <summary>True, with the job file's path, when <paramref name="args"/> is exactly the flag and one path.</summary>
    /// <param name="args">The process's arguments.</param>
    /// <param name="jobPath">The job file's path; empty when the arguments do not start the child.</param>
    public static bool TryParseArgs(string[] args, out string jobPath)
    {
        if (args is [Flag, string path])
        {
            jobPath = path;
            return true;
        }
        jobPath = string.Empty;
        return false;
    }

    /// <summary>
    /// Runs the job in <paramref name="jobPath"/> in this process: starts the child's log and the native stderr capture,
    /// then loads the job's native set and measures the row.
    /// </summary>
    /// <param name="jobPath">The job file.</param>
    /// <returns>
    /// The process exit code: <see cref="ExitResultWritten"/>, <see cref="ExitJobUnreadable"/> or <see cref="ExitResultUnwritable"/>.
    /// </returns>
    public static int Run(string jobPath)
    {
        AppLog.Initialize("yaat-stt-bench.log");
        ILogger log = AppLog.CreateLogger(LogCategory);
        try
        {
            log.LogInformation("{BuildSummary}", BuildInfo.LogSummary);
            log.LogInformation("Benchmark child for {JobPath}; log file: {LogPath}", jobPath, AppLog.LogPath);

            // Before anything touches sherpa-onnx: its statically linked C runtime binds stderr when the DLL loads.
            NativeStderrCapture.Install();
            using var gpu = new GpuMemoryProbe();
            using var memory = new CurrentProcessMemory();
            var meters = new SttBenchMeters(gpu, memory, TimeProvider.System);
            var source = new FactorySttBenchEngineSource(engine => engine.NativeProblem());
            int exitCode = RunJobAsync(jobPath, device => SetUpNatives(log, device), source, meters).GetAwaiter().GetResult();
            log.LogInformation("Benchmark child exits with {ExitCode}", exitCode);
            AppLog.Flush();
            return exitCode;
        }
        catch (Exception ex)
        {
            log.LogCritical(ex, "The benchmark child for {JobPath} failed outside its row", jobPath);
            AppLog.Flush();
            throw;
        }
    }

    /// <summary>
    /// Reads the job, sets up its native set through <paramref name="setUpNatives"/>, measures the row and writes the
    /// result. A GPU job whose process ended on the CPU set is not run; any throw after the job is read becomes a
    /// <c>bench failed</c> result, so a readable job always gets a result file.
    /// </summary>
    /// <param name="jobPath">The job file.</param>
    /// <param name="setUpNatives">Loads the native set for a device and returns the set loaded and why a GPU pick fell back.</param>
    /// <param name="source">Creates the job's engine.</param>
    /// <param name="meters">What the row is measured with.</param>
    /// <returns>The process exit code.</returns>
    internal static async Task<int> RunJobAsync(
        string jobPath,
        Func<SttDevice, (SherpaNativeSet Set, string? CpuFallbackReason)> setUpNatives,
        ISttBenchEngineSource source,
        SttBenchMeters meters
    )
    {
        ILogger log = AppLog.CreateLogger(LogCategory);
        SttBenchJob? job = ReadJob(log, jobPath);
        if (job is null)
        {
            return ExitJobUnreadable;
        }

        SttBenchResult result;
        try
        {
            result = await MeasureAsync(job, setUpNatives, source, meters).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "The benchmark row {Engine} {Mode} on {Device} threw", job.Engine, job.Mode, job.Device);
            result = SttBenchResult.Failed($"bench failed: {ex.Message}", 0, null);
        }
        return WriteResult(log, job.ResultPath, result);
    }

    private static async Task<SttBenchResult> MeasureAsync(
        SttBenchJob job,
        Func<SttDevice, (SherpaNativeSet Set, string? CpuFallbackReason)> setUpNatives,
        ISttBenchEngineSource source,
        SttBenchMeters meters
    )
    {
        (SherpaNativeSet set, string? cpuFallbackReason) = setUpNatives(job.Device);
        if ((job.Device == SttDevice.Gpu) && ((set != SherpaNativeSet.Cuda) || (cpuFallbackReason is not null)))
        {
            string reason = cpuFallbackReason ?? "the GPU runtime is not installed";
            AppLog.CreateLogger(LogCategory).LogWarning("The GPU benchmark row is not run: {Reason}", reason);
            return SttBenchResult.Failed($"GPU not used: {reason}", 0, null);
        }
        return await SttBenchRunner.RunAsync(job, source, meters, CancellationToken.None).ConfigureAwait(false);
    }

    private static (SherpaNativeSet Set, string? CpuFallbackReason) SetUpNatives(ILogger log, SttDevice device)
    {
        Program.ConfigureSherpaNatives(log, device);
        return (SherpaNativeLoader.LoadedSet, SherpaNativeLoader.CpuFallbackReason);
    }

    private static SttBenchJob? ReadJob(ILogger log, string jobPath)
    {
        try
        {
            SttBenchJob? job = JsonSerializer.Deserialize(File.ReadAllText(jobPath), SttBenchJsonContext.Default.SttBenchJob);
            if (job is null)
            {
                log.LogError("The benchmark job {JobPath} holds no job", jobPath);
                return null;
            }
            if (string.IsNullOrEmpty(job.ModelDirectory) || string.IsNullOrEmpty(job.ClipsDirectory) || string.IsNullOrEmpty(job.ResultPath))
            {
                log.LogError("The benchmark job {JobPath} leaves its model folder, clips folder or result path empty", jobPath);
                return null;
            }
            return job;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            log.LogError(ex, "The benchmark job {JobPath} could not be read", jobPath);
            return null;
        }
    }

    /// <summary>Writes the result through a temporary sibling that then replaces <paramref name="resultPath"/>.</summary>
    private static int WriteResult(ILogger log, string resultPath, SttBenchResult result)
    {
        try
        {
            AtomicFile.WriteAllText(resultPath, JsonSerializer.Serialize(result, SttBenchJsonContext.Default.SttBenchResult));
            return ExitResultWritten;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            log.LogError(ex, "The benchmark result could not be written to {ResultPath}", resultPath);
            return ExitResultUnwritable;
        }
    }
}
