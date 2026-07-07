using System;
using System.Diagnostics;
using System.Threading;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;

namespace Yaat.Client.Services;

/// <summary>
/// Detects and logs UI-thread stalls (freezes). A dedicated background thread posts a heartbeat
/// to the Avalonia dispatcher on a fixed cadence and measures how long each one takes to run. When
/// the UI thread cannot service a heartbeat for longer than <see cref="StallWarnThresholdMs"/>, the
/// watchdog logs a <c>[warn]</c> with a runtime snapshot (working set, managed heap, .NET memory-load
/// pressure, last GC pause, thread count); when the thread frees up it logs the total stall duration
/// and the GC collections that occurred during the freeze.
///
/// This exists because a UI-thread freeze leaves no other trace in <c>yaat-client.log</c> — the sim,
/// SignalR, and rendering all appear healthy because the stall is on the dispatcher itself. The
/// captured snapshot discriminates the likely causes: a jump in gen2 collections points at a GC
/// pause, while a high memory-load percentage points at OS memory pressure (the classic macOS
/// compressed-memory / swap thrash pattern). It does not capture the frozen thread's managed call
/// stack — .NET has no reliable cross-thread stack API off Windows; the OS hang report
/// (<c>~/Library/Logs/DiagnosticReports/*.hang</c> on macOS) covers that.
///
/// The heartbeat is posted at the dispatcher's default priority, so it measures responsiveness to
/// ordinary work rather than to high-priority render/input frames. Only genuine multi-second stalls
/// trip it; normal per-frame rendering never does.
/// </summary>
public sealed class UiThreadWatchdog : IDisposable
{
    private static readonly ILogger Log = AppLog.CreateLogger<UiThreadWatchdog>();

    private const int PollIntervalMs = 500;
    private const int StallWarnThresholdMs = 2000;
    private const long OneMb = 1024 * 1024;

    private readonly object _gate = new();
    private Thread? _thread;
    private volatile bool _running;

    // Guarded by _gate. A single heartbeat is outstanding at a time: the watchdog thread posts one
    // and marks it pending; the UI thread clears the flag when the callback finally runs.
    private bool _heartbeatPending;
    private long _pendingSinceMs;
    private bool _stallReported;
    private int _gen0AtStall;
    private int _gen1AtStall;
    private int _gen2AtStall;

    /// <summary>Launches the watchdog background thread. Idempotent.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_running)
            {
                return;
            }

            _running = true;
            _thread = new Thread(Loop)
            {
                IsBackground = true,
                Name = "UiThreadWatchdog",
                Priority = ThreadPriority.AboveNormal,
            };
            _thread.Start();
        }

        Log.LogInformation("UI-thread watchdog started (poll {PollMs} ms, stall threshold {ThresholdMs} ms)", PollIntervalMs, StallWarnThresholdMs);
    }

    private void Loop()
    {
        while (_running)
        {
            try
            {
                Tick();
            }
            catch (Exception ex)
            {
                Log.LogError(ex, "UI-thread watchdog tick failed");
            }

            Thread.Sleep(PollIntervalMs);
        }
    }

    private void Tick()
    {
        long now = Environment.TickCount64;
        bool postHeartbeat = false;
        bool reportStall = false;
        long stallMs = 0;

        lock (_gate)
        {
            if (!_heartbeatPending)
            {
                _heartbeatPending = true;
                _pendingSinceMs = now;
                postHeartbeat = true;
            }
            else
            {
                long pendingForMs = now - _pendingSinceMs;
                if (!_stallReported && (pendingForMs >= StallWarnThresholdMs))
                {
                    _stallReported = true;
                    _gen0AtStall = GC.CollectionCount(0);
                    _gen1AtStall = GC.CollectionCount(1);
                    _gen2AtStall = GC.CollectionCount(2);
                    reportStall = true;
                    stallMs = pendingForMs;
                }
            }
        }

        if (reportStall)
        {
            LogStall(stallMs);
        }

        if (postHeartbeat)
        {
            Dispatcher.UIThread.Post(OnHeartbeat);
        }
    }

    private void OnHeartbeat()
    {
        long now = Environment.TickCount64;
        bool recovered = false;
        long durationMs = 0;
        int gen0 = 0;
        int gen1 = 0;
        int gen2 = 0;

        lock (_gate)
        {
            _heartbeatPending = false;
            if (_stallReported)
            {
                recovered = true;
                durationMs = now - _pendingSinceMs;
                gen0 = GC.CollectionCount(0) - _gen0AtStall;
                gen1 = GC.CollectionCount(1) - _gen1AtStall;
                gen2 = GC.CollectionCount(2) - _gen2AtStall;
                _stallReported = false;
            }
        }

        if (recovered)
        {
            Log.LogInformation(
                "UI thread responsive again after {DurationMs} ms ({DurationSec:F1}s). GC during stall: gen0+{Gen0} gen1+{Gen1} gen2+{Gen2}.",
                durationMs,
                durationMs / 1000.0,
                gen0,
                gen1,
                gen2
            );
        }
    }

    private static void LogStall(long stallMs)
    {
        try
        {
            GCMemoryInfo gc = GC.GetGCMemoryInfo();
            long availableBytes = gc.TotalAvailableMemoryBytes;
            double memLoadPct = availableBytes > 0 ? (100.0 * gc.MemoryLoadBytes / availableBytes) : 0;
            long lastPauseMs = gc.PauseDurations.Length > 0 ? (long)gc.PauseDurations[^1].TotalMilliseconds : -1;
            long managedMb = GC.GetTotalMemory(false) / OneMb;

            long workingSetMb;
            int threadCount;
            using (Process self = Process.GetCurrentProcess())
            {
                workingSetMb = self.WorkingSet64 / OneMb;
                threadCount = self.Threads.Count;
            }

            Log.LogWarning(
                "UI thread STALLED: unresponsive ~{StallMs} ms. workingSet={WorkingSetMB}MB managedHeap={ManagedMB}MB memLoad={MemLoadPct:F0}% ({LoadMB}/{AvailMB}MB, highThreshold={HighMB}MB) lastGCPause={LastPauseMs}ms threads={Threads}",
                stallMs,
                workingSetMb,
                managedMb,
                memLoadPct,
                gc.MemoryLoadBytes / OneMb,
                availableBytes / OneMb,
                gc.HighMemoryLoadThresholdBytes / OneMb,
                lastPauseMs,
                threadCount
            );
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "UI thread STALLED: unresponsive ~{StallMs} ms (diagnostics capture failed)", stallMs);
        }
    }

    public void Dispose()
    {
        _running = false;
    }
}
