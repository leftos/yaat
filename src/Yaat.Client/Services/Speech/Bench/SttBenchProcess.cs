using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;

namespace Yaat.Client.Services.Speech.Bench;

/// <summary>How a row ended: it ran and gave a result, or it was skipped with a reason.</summary>
/// <param name="Result">What the row measured or why it failed; null when it was skipped.</param>
/// <param name="SkipReason">Why it was not run; null when it ran.</param>
public sealed record SttBenchRowOutcome(SttBenchResult? Result, string? SkipReason)
{
    /// <summary>A row that ran.</summary>
    public static SttBenchRowOutcome Ran(SttBenchResult result) => new(result, null);

    /// <summary>A row that was not run.</summary>
    public static SttBenchRowOutcome Skip(string reason) => new(null, reason);
}

/// <summary>A started benchmark child process.</summary>
public interface ISttBenchChildProcess : IDisposable
{
    /// <summary>The exit code; read after <see cref="WaitForExitAsync"/> completes.</summary>
    int ExitCode { get; }

    /// <summary>The lines the child wrote to stderr so far.</summary>
    IReadOnlyList<string> StderrLines { get; }

    /// <summary>Completes when the child has exited.</summary>
    /// <param name="ct">Stops waiting; the child keeps running.</param>
    Task WaitForExitAsync(CancellationToken ct);

    /// <summary>Kills the child; a child that already exited is left alone.</summary>
    /// <param name="entireProcessTree">Whether its descendants die with it.</param>
    void Kill(bool entireProcessTree);
}

/// <summary>Starts a benchmark child process.</summary>
public interface ISttBenchLauncher
{
    /// <summary>Starts <paramref name="exePath"/> on the job in <paramref name="jobPath"/>.</summary>
    /// <param name="exePath">The executable.</param>
    /// <param name="jobPath">The job file.</param>
    ISttBenchChildProcess Start(string exePath, string jobPath);
}

/// <summary>Runs one benchmark row to its end.</summary>
public interface ISttBenchRowRunner
{
    /// <summary>Runs <paramref name="job"/>; a row that fails or times out still returns an outcome.</summary>
    /// <param name="job">The row. The folder of its result path is this row's run folder.</param>
    /// <param name="deadline">How long the row may take.</param>
    /// <param name="ct">Stops the row; it then throws <see cref="OperationCanceledException"/>.</param>
    Task<SttBenchRowOutcome> RunRowAsync(SttBenchJob job, TimeSpan deadline, CancellationToken ct);
}

/// <summary>
/// Runs a row in its own process. The row's run folder holds the job and result files and exists only while the row runs.
/// A deadline or a cancel kills the child's whole tree on the thread that fired it; the row then waits a bounded time for the
/// child to be gone, so a child that will not die cannot hang the run.
/// </summary>
/// <param name="launcher">Starts the child.</param>
/// <param name="timeProvider">The clock the deadline runs on.</param>
public sealed class SttBenchProcess(ISttBenchLauncher launcher, TimeProvider timeProvider) : ISttBenchRowRunner
{
    private const string StuckFailure = "could not stop the benchmark process";

    private static readonly TimeSpan KillWait = TimeSpan.FromSeconds(10);
    private static readonly ILogger Log = AppLog.CreateLogger<SttBenchProcess>();

    public async Task<SttBenchRowOutcome> RunRowAsync(SttBenchJob job, TimeSpan deadline, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string runFolder = Path.GetDirectoryName(job.ResultPath) ?? throw new ArgumentException($"{job.ResultPath} has no folder", nameof(job));
        Directory.CreateDirectory(runFolder);
        try
        {
            string jobPath = Path.Combine(runFolder, "job.json");
            File.WriteAllText(jobPath, JsonSerializer.Serialize(job, SttBenchJsonContext.Default.SttBenchJob));
            string exePath = Environment.ProcessPath ?? throw new InvalidOperationException("This process has no path to start the benchmark from");

            using var pastDeadline = new CancellationTokenSource(deadline, timeProvider);
            var killer = new RowKiller(launcher.Start(exePath, jobPath), timeProvider);
            // Unregister, never Dispose: a dispose waits for a kill that is still running, so a kill that blocks would hang the row.
            CancellationTokenRegistration onCancel = default;
            CancellationTokenRegistration onDeadline = default;
            try
            {
                onCancel = ct.Register(killer.Fire);
                onDeadline = pastDeadline.Token.Register(killer.Fire);
                return await AwaitRowAsync(job, deadline, killer, pastDeadline, ct).ConfigureAwait(false);
            }
            finally
            {
                onCancel.Unregister();
                onDeadline.Unregister();
                killer.Release();
            }
        }
        finally
        {
            DeleteRunFolder(runFolder);
        }
    }

    private static async Task<SttBenchRowOutcome> AwaitRowAsync(
        SttBenchJob job,
        TimeSpan deadline,
        RowKiller killer,
        CancellationTokenSource pastDeadline,
        CancellationToken ct
    )
    {
        ISttBenchChildProcess child = killer.Child;
        bool exited = await WaitForExitAsync(child, killer.KillWait).ConfigureAwait(false);

        if (!exited)
        {
            LogStderr(child, StuckFailure);
            ct.ThrowIfCancellationRequested();
            return SttBenchRowOutcome.Ran(SttBenchResult.Failed(StuckFailure, 0, null));
        }
        ct.ThrowIfCancellationRequested();
        if (pastDeadline.IsCancellationRequested)
        {
            string failure = $"timed out after {deadline.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture)} s";
            LogStderr(child, failure);
            return SttBenchRowOutcome.Ran(SttBenchResult.Failed(failure, 0, null));
        }
        return SttBenchRowOutcome.Ran(ReadResult(job.ResultPath) ?? FailedRow(child));
    }

    // Runs inside a token's cancel, on the thread that fired it: an exception here would surface in whoever called Cancel.
    private static void TryKill(ISttBenchChildProcess child)
    {
        try
        {
            child.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "The benchmark child could not be killed");
        }
    }

    private static async Task<bool> WaitForExitAsync(ISttBenchChildProcess child, CancellationToken killWait)
    {
        try
        {
            await child.WaitForExitAsync(killWait).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (killWait.IsCancellationRequested)
        {
            Log.LogError("The benchmark child was still running {KillWait} after it was killed", KillWait);
            return false;
        }
    }

    private static SttBenchResult? ReadResult(string resultPath)
    {
        if (!File.Exists(resultPath))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(resultPath), SttBenchJsonContext.Default.SttBenchResult);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Log.LogError(ex, "The benchmark result {ResultPath} could not be read", resultPath);
            return null;
        }
    }

    private static SttBenchResult FailedRow(ISttBenchChildProcess child)
    {
        string? firstLine = child.StderrLines.FirstOrDefault(line => !string.IsNullOrWhiteSpace(line));
        string failure = (firstLine is null) ? $"failed: exit {child.ExitCode}" : $"failed: exit {child.ExitCode}: {firstLine}";
        LogStderr(child, failure);
        return SttBenchResult.Failed(failure, 0, null);
    }

    private static void LogStderr(ISttBenchChildProcess child, string failure) =>
        Log.LogError(
            "Benchmark row {Failure}; the child's stderr:{NewLine}{Stderr}",
            failure,
            Environment.NewLine,
            string.Join(Environment.NewLine, child.StderrLines)
        );

    /// <summary>
    /// Kills a row's child once, from whichever cancel fires first, and owns the child and the kill wait. The kill wait
    /// starts before the kill, so a kill that blocks is bounded too. Neither is disposed while a kill runs: when the row
    /// ends first, the kill disposes them as it returns.
    /// </summary>
    private sealed class RowKiller(ISttBenchChildProcess child, TimeProvider timeProvider)
    {
        private readonly Lock _gate = new();
        private readonly CancellationTokenSource _killWait = new(Timeout.InfiniteTimeSpan, timeProvider);
        private bool _fired;
        private bool _killing;
        private bool _released;

        public ISttBenchChildProcess Child => child;

        public CancellationToken KillWait => _killWait.Token;

        public void Fire()
        {
            lock (_gate)
            {
                if (_fired || _released)
                {
                    return;
                }
                _fired = true;
                _killing = true;
                _killWait.CancelAfter(SttBenchProcess.KillWait);
            }
            TryKill(child);
            bool dispose;
            lock (_gate)
            {
                _killing = false;
                dispose = _released;
            }
            if (dispose)
            {
                DisposeAll();
            }
        }

        /// <summary>The row is over; disposes now, or leaves it to the kill still running.</summary>
        public void Release()
        {
            lock (_gate)
            {
                _released = true;
                if (_killing)
                {
                    return;
                }
            }
            DisposeAll();
        }

        private void DisposeAll()
        {
            _killWait.Dispose();
            child.Dispose();
        }
    }

    private static void DeleteRunFolder(string runFolder)
    {
        try
        {
            Directory.Delete(runFolder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.LogWarning(ex, "The benchmark run folder {RunFolder} could not be deleted", runFolder);
        }
    }
}

/// <summary>Starts the real child: this app again, windowless, on one job, with its stderr kept.</summary>
public sealed class SttBenchChildLauncher : ISttBenchLauncher
{
    public ISttBenchChildProcess Start(string exePath, string jobPath)
    {
        var startInfo = new ProcessStartInfo(exePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        if (Path.GetFileNameWithoutExtension(exePath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, typeof(SttBenchChild).Assembly.GetName().Name + ".dll"));
        }
        startInfo.ArgumentList.Add(SttBenchChild.Flag);
        startInfo.ArgumentList.Add(jobPath);
        return new Child(startInfo);
    }

    private sealed class Child : ISttBenchChildProcess
    {
        private static readonly ILogger Log = AppLog.CreateLogger<Child>();

        private readonly Process _process;
        private readonly List<string> _stderr = [];

        public Child(ProcessStartInfo startInfo)
        {
            _process = new Process { StartInfo = startInfo };
            _process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    lock (_stderr)
                    {
                        _stderr.Add(e.Data);
                    }
                }
            };
            try
            {
                _process.Start();
                _process.BeginErrorReadLine();
            }
            catch
            {
                _process.Dispose();
                throw;
            }
        }

        public int ExitCode => _process.ExitCode;

        public IReadOnlyList<string> StderrLines
        {
            get
            {
                lock (_stderr)
                {
                    return [.. _stderr];
                }
            }
        }

        public Task WaitForExitAsync(CancellationToken ct) => _process.WaitForExitAsync(ct);

        public void Kill(bool entireProcessTree)
        {
            try
            {
                _process.Kill(entireProcessTree);
            }
            catch (InvalidOperationException ex)
            {
                Log.LogDebug(ex, "The benchmark child had already exited when it was killed");
            }
            catch (Exception ex) when (ex is Win32Exception or AggregateException)
            {
                Log.LogError(ex, "The benchmark child's process tree could not be killed");
            }
        }

        public void Dispose() => _process.Dispose();
    }
}
