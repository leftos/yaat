using System;
using System.Text;
using Microsoft.Diagnostics.Runtime;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;

namespace Yaat.Client.Services;

/// <summary>
/// Captures the managed call stack of every thread in the current process by taking a ClrMD
/// snapshot of the process and attaching to it (<see cref="DataTarget.CreateSnapshotAndAttach"/>).
/// This is the only way to read another thread's managed stack from inside the process — the
/// in-process <c>StackTrace(Thread)</c> API was removed in .NET Core — and it is what turns a
/// UI-thread hard-freeze report (GitHub #347) from "the dispatcher was wedged somewhere" into an
/// actionable stack. Windows-only: the snapshot mechanism is PssCaptureSnapshot; on other
/// platforms the OS hang report (e.g. macOS <c>*.hang</c> diagnostics) is the equivalent.
/// </summary>
internal static class ManagedStackCapture
{
    private static readonly ILogger Log = AppLog.CreateLogger("ManagedStackCapture");

    private const int MaxFramesPerThread = 64;

    /// <summary>
    /// Formats the managed stacks of all live threads, the thread with managed id
    /// <paramref name="uiManagedThreadId"/> first and marked <c>[UI THREAD]</c>. Pass a negative id
    /// when the UI thread is unknown; every thread is then listed unmarked. Returns null when
    /// capture is unavailable (non-Windows) or fails — the failure is logged here, callers just
    /// skip the log line.
    /// </summary>
    internal static string? TryCaptureAllThreads(int uiManagedThreadId)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            using DataTarget dataTarget = DataTarget.CreateSnapshotAndAttach(Environment.ProcessId);
            if (dataTarget.ClrVersions.Length == 0)
            {
                Log.LogWarning("Stack capture found no CLR in the snapshot — cannot walk managed stacks");
                return null;
            }

            using ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
            var sb = new StringBuilder();
            int emptyThreads = 0;

            foreach (ClrThread thread in OrderUiThreadFirst(runtime, uiManagedThreadId))
            {
                if (!AppendThread(sb, thread, thread.ManagedThreadId == uiManagedThreadId))
                {
                    emptyThreads++;
                }
            }

            if (emptyThreads > 0)
            {
                sb.AppendLine($"({emptyThreads} thread(s) with no managed frames omitted)");
            }

            return sb.Length > 0 ? sb.ToString() : null;
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "Managed stack capture failed — freeze report will have no call stacks");
            return null;
        }
    }

    private static ClrThread[] OrderUiThreadFirst(ClrRuntime runtime, int uiManagedThreadId)
    {
        var threads = new List<ClrThread>();
        foreach (ClrThread thread in runtime.Threads)
        {
            if (!thread.IsAlive)
            {
                continue;
            }

            if (thread.ManagedThreadId == uiManagedThreadId)
            {
                threads.Insert(0, thread);
            }
            else
            {
                threads.Add(thread);
            }
        }

        return threads.ToArray();
    }

    /// <summary>Appends one thread's header and frames. Returns false when the thread had no managed frames (nothing appended).</summary>
    private static bool AppendThread(StringBuilder sb, ClrThread thread, bool isUiThread)
    {
        int frameCount = 0;
        var frames = new StringBuilder();
        foreach (ClrStackFrame frame in thread.EnumerateStackTrace())
        {
            string? text = frame.ToString();
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            frames.AppendLine($"    at {text}");
            frameCount++;
            if (frameCount >= MaxFramesPerThread)
            {
                frames.AppendLine($"    ... (truncated at {MaxFramesPerThread} frames)");
                break;
            }
        }

        if (frameCount == 0)
        {
            return false;
        }

        string marker = isUiThread ? " [UI THREAD]" : string.Empty;
        sb.AppendLine($"--- Thread managedId={thread.ManagedThreadId} osId=0x{thread.OSThreadId:X}{marker} ---");
        sb.Append(frames);
        return true;
    }
}
