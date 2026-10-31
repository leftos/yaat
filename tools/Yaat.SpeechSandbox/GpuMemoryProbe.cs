using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;

namespace Yaat.SpeechSandbox;

/// <summary>
/// The GPU memory one process holds, read from Windows' per-process <c>GPU Process Memory</c> counters
/// (the only platform that publishes them):
/// the dedicated bytes of every instance named <c>pid_&lt;pid&gt;_…</c> for this process. The instances
/// come and go as the process opens GPU contexts, so every sample re-reads them.
/// </summary>
public sealed class GpuMemoryProbe : IDisposable
{
    /// <summary>The performance counter category Windows publishes per-process GPU memory under.</summary>
    public const string CategoryName = "GPU Process Memory";

    /// <summary>The counter inside <see cref="CategoryName"/> holding a process's dedicated GPU bytes.</summary>
    public const string CounterName = "Dedicated Usage";

    private static readonly ILogger Log = AppLog.CreateLogger<GpuMemoryProbe>();

    private readonly int _pid = Environment.ProcessId;
    private PerformanceCounterCategory? _category;
    private bool _failureLogged;
    private bool _disposed;

    /// <summary>
    /// The process's dedicated GPU bytes now, summed over every counter instance the process owns, or -1
    /// when the category does not exist or a counter cannot be read. Never throws.
    /// </summary>
    public long Sample()
    {
        if (!OperatingSystem.IsWindows() || _disposed)
        {
            return -1;
        }

        try
        {
            PerformanceCounterCategory category = _category ??= new PerformanceCounterCategory(CategoryName);
            long total = 0;
            foreach (string instance in category.GetInstanceNames())
            {
                if (!MatchesProcess(instance, _pid))
                {
                    continue;
                }

                using var counter = new PerformanceCounter(CategoryName, CounterName, instance, readOnly: true);
                total += counter.RawValue;
            }

            return total;
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or Win32Exception)
        {
            LogFailure(ex);
            return -1;
        }
    }

    /// <summary>True when a counter instance name belongs to <paramref name="pid"/>: <c>pid_&lt;pid&gt;_…</c>.</summary>
    public static bool MatchesProcess(string instanceName, int pid) => instanceName.StartsWith($"pid_{pid}_", StringComparison.Ordinal);

    /// <summary>Stops sampling; every later <see cref="Sample"/> reads -1.</summary>
    public void Dispose()
    {
        _disposed = true;
        _category = null;
    }

    private void LogFailure(Exception ex)
    {
        if (_failureLogged)
        {
            return;
        }

        _failureLogged = true;
        Log.LogWarning(ex, "the {Category} counters are unavailable, so GPU memory reads as -1", CategoryName);
    }
}

/// <summary>
/// What a run's GPU memory figures say about the device it asked for: a GPU run must show the process
/// gaining GPU memory through the model load, and a CPU run that gains it anyway is warned about.
/// </summary>
public static class GpuUseCheck
{
    /// <summary>How many bytes a model load must add before it counts as having reached the GPU: 100 MB.</summary>
    public const long MinRiseBytes = 100L * 1024 * 1024;

    private const string UnreadableMessage = "GPU mode asked, but the GPU memory counter could not be read, so GPU use cannot be verified";

    /// <summary>
    /// The verdict on a run's device from its GPU memory figures: an error when a GPU run cannot show the
    /// GPU, a warning when a CPU run touched it anyway, and null when there is nothing to report.
    /// </summary>
    /// <param name="device">The device text the run records: <c>gpu</c>, <c>cpu</c>, <c>auto</c> or <c>cloud</c>.</param>
    /// <param name="preLoadBytes">The process's GPU memory before the model loaded; -1 when unreadable.</param>
    /// <param name="afterLoadBytes">The process's GPU memory after the load; -1 when unreadable.</param>
    /// <param name="isError">True when the run must stop rather than measure the wrong device.</param>
    public static string? Verdict(string device, long preLoadBytes, long afterLoadBytes, out bool isError)
    {
        isError = false;
        if (device is not ("gpu" or "cpu"))
        {
            return null;
        }

        if ((preLoadBytes < 0) || (afterLoadBytes < 0))
        {
            isError = device == "gpu";
            return isError ? UnreadableMessage : null;
        }

        long rise = afterLoadBytes - preLoadBytes;
        if (device == "gpu")
        {
            isError = rise < MinRiseBytes;
            return isError ? GpuFallbackMessage(rise) : null;
        }

        return (rise >= MinRiseBytes) ? $"CPU mode, but the process's GPU memory rose {Megabytes(rise)} MB through model load" : null;
    }

    private static string GpuFallbackMessage(long riseBytes) =>
        $"GPU mode asked, but the process's GPU memory rose only {Megabytes(riseBytes)} MB through model load: "
        + "the native library is CPU-only or the provider fell back to the CPU (see %LOCALAPPDATA%/yaat/yaat-speech-sandbox.log)";

    private static string Megabytes(long bytes) => ((double)bytes / 1048576).ToString("0", CultureInfo.InvariantCulture);
}
