using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using Yaat.Client.Logging;

namespace Yaat.Client.Services;

/// <summary>
/// Writes a Windows minidump of the current process when the UI thread hard-freezes. The managed
/// stack capture (<see cref="ManagedStackCapture"/>) cannot see below native transitions — the
/// GitHub #347 recurrence froze in a runtime-native wait whose managed walk stopped at
/// <c>AvaloniaSynchronizationContext.Wait</c> with the actual blocker invisible — so the dump
/// carries the complete native stacks. Thread-info flags only (no heap memory): the file stays a
/// few MB and contains no scenario or user data, so it is safe to attach to a bug report.
/// Windows-only; other platforms rely on the OS hang report.
/// </summary>
internal static class FreezeDumpWriter
{
    private static readonly ILogger Log = AppLog.CreateLogger("FreezeDumpWriter");

    private const int KeepDumps = 3;

    [Flags]
    private enum MiniDumpType : uint
    {
        Normal = 0x0,
        WithHandleData = 0x4,
        WithUnloadedModules = 0x20,
        WithThreadInfo = 0x1000,
    }

    /// <summary>
    /// Writes <c>yaat-freeze-&lt;timestamp&gt;.dmp</c> into <paramref name="directory"/> and returns
    /// its full path, or null when capture is unavailable (non-Windows) or fails — the failure is
    /// logged here, callers just omit the dump from the freeze report. Keeps the newest
    /// <see cref="KeepDumps"/> freeze dumps and prunes the rest so repeated freezes across sessions
    /// don't accumulate.
    /// </summary>
    internal static string? TryWrite(string directory)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            string path = Path.Combine(directory, $"yaat-freeze-{DateTime.Now:yyyyMMdd-HHmmss}.dmp");
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            using Process self = Process.GetCurrentProcess();

            bool ok = MiniDumpWriteDump(
                self.Handle,
                (uint)self.Id,
                stream.SafeFileHandle,
                MiniDumpType.Normal | MiniDumpType.WithHandleData | MiniDumpType.WithUnloadedModules | MiniDumpType.WithThreadInfo,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero
            );

            if (!ok)
            {
                Log.LogWarning("MiniDumpWriteDump failed (error {Error}) — freeze report will have no native stacks", Marshal.GetLastWin32Error());
                stream.Close();
                File.Delete(path);
                return null;
            }

            // Prune only after a successful write so a failed capture never costs a prior dump.
            PruneOldDumps(directory, path);
            return path;
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "Freeze minidump capture failed — freeze report will have no native stacks");
            return null;
        }
    }

    private static void PruneOldDumps(string directory, string justWritten)
    {
        var existing = Directory.GetFiles(directory, "yaat-freeze-*.dmp");
        if (existing.Length <= KeepDumps)
        {
            return;
        }

        // Timestamped names sort chronologically, so the just-written dump is last; the guard is
        // defense in depth against a clock oddity putting it in the delete range.
        Array.Sort(existing, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < (existing.Length - KeepDumps); i++)
        {
            if (!string.Equals(existing[i], justWritten, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(existing[i]);
            }
        }
    }

    [DllImport("Dbghelp.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool MiniDumpWriteDump(
        IntPtr hProcess,
        uint processId,
        SafeFileHandle hFile,
        MiniDumpType dumpType,
        IntPtr exceptionParam,
        IntPtr userStreamParam,
        IntPtr callbackParam
    );
}
