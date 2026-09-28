using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Yaat.Sim.Diagnostics;

/// <summary>
/// The CPU time (user plus kernel) the calling thread has consumed, for timing budgets that should be far less sensitive to
/// machine load than wall time. Wall time grows while a thread waits for a core; thread CPU time does not, though a busy
/// hyperthread sibling, cache and memory contention and a lower turbo clock still inflate it. Only work done on the calling
/// thread is counted, so work that hops threads (async continuations, <c>Parallel</c>, <c>Task.Run</c>) cannot be measured
/// this way. Garbage collections that run on server-GC threads are not charged to the calling thread either, so a budget
/// test adds the <see cref="GC.GetTotalPauseDuration"/> delta over the same span to the CPU figure.
/// </summary>
/// <remarks>
/// <para>
/// Windows reads <c>GetThreadTimes</c>, whose counters are in 100 ns units but advance only at the scheduler's clock tick
/// (about 15.6 ms), so a budget under about 50 ms is not meaningful there. Linux and macOS read
/// <c>clock_gettime(CLOCK_THREAD_CPUTIME_ID)</c>, which has nanosecond resolution. Any other platform throws
/// <see cref="PlatformNotSupportedException"/>; there is no wall-clock fallback.
/// </para>
/// </remarks>
public static partial class ThreadCpuTime
{
    /// <summary>
    /// <c>CLOCK_THREAD_CPUTIME_ID</c> on Linux: <c>#define CLOCK_THREAD_CPUTIME_ID 3</c> in the kernel's
    /// <c>include/uapi/linux/time.h</c> (github.com/torvalds/linux).
    /// </summary>
    private const int LinuxClockThreadCpuTimeId = 3;

    /// <summary>
    /// <c>CLOCK_THREAD_CPUTIME_ID</c> on macOS: <c>_CLOCK_THREAD_CPUTIME_ID = 16</c> in Libc's <c>include/_time.h</c>
    /// (github.com/apple-oss-distributions/Libc).
    /// </summary>
    private const int MacOsClockThreadCpuTimeId = 16;

    /// <summary>The calling thread's CPU time so far, user plus kernel.</summary>
    /// <returns>The CPU time the calling thread has consumed since it started.</returns>
    /// <exception cref="PlatformNotSupportedException">
    /// The OS is not Windows, Linux or macOS, or the Linux C library does not export <c>clock_gettime</c> under <c>libc.so.6</c>.
    /// </exception>
    /// <exception cref="Win32Exception">The OS call that reads the thread's CPU time failed; the message carries its error code.</exception>
    public static TimeSpan Current()
    {
        if (OperatingSystem.IsWindows())
        {
            return WindowsCurrent();
        }

        if (OperatingSystem.IsLinux())
        {
            return LinuxCurrent();
        }

        if (OperatingSystem.IsMacOS())
        {
            return PosixCurrent(MacOsClockGetTime(MacOsClockThreadCpuTimeId, out Timespec macTime), macTime, "macOS");
        }

        throw new PlatformNotSupportedException(
            $"ThreadCpuTime has no thread CPU clock on '{RuntimeInformation.OSDescription}'; it supports Windows, Linux and macOS. "
                + "Measure wall time with Stopwatch instead, and expect it to vary with machine load."
        );
    }

    /// <summary>Runs <paramref name="action"/> on the calling thread and returns the CPU time the thread spent in it.</summary>
    /// <param name="action">The synchronous work to measure; work it hands to other threads is not counted.</param>
    /// <returns>The calling thread's CPU time consumed by <paramref name="action"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="action"/> is null.</exception>
    /// <exception cref="PlatformNotSupportedException">
    /// The OS is not Windows, Linux or macOS, or the Linux C library does not export <c>clock_gettime</c> under <c>libc.so.6</c>.
    /// </exception>
    /// <exception cref="Win32Exception">The OS call that reads the thread's CPU time failed; the message carries its error code.</exception>
    public static TimeSpan Measure(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        TimeSpan start = Current();
        action();
        return Current() - start;
    }

    private static TimeSpan WindowsCurrent()
    {
        if (!GetThreadTimes(GetCurrentThread(), out _, out _, out long kernel100Ns, out long user100Ns))
        {
            int error = Marshal.GetLastPInvokeError();
            throw new Win32Exception(
                error,
                $"GetThreadTimes failed reading the current thread's CPU time (Win32 error {error}: {Marshal.GetPInvokeErrorMessage(error)})"
            );
        }

        return TimeSpan.FromTicks(kernel100Ns + user100Ns);
    }

    private static TimeSpan LinuxCurrent()
    {
        int result;
        Timespec time;
        try
        {
            result = LinuxClockGetTime(LinuxClockThreadCpuTimeId, out time);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            throw new PlatformNotSupportedException(
                $"ThreadCpuTime could not call clock_gettime in libc.so.6 on '{RuntimeInformation.OSDescription}' ({ex.GetType().Name}: "
                    + $"{ex.Message}); it needs glibc, so musl-based distributions such as Alpine are not supported.",
                ex
            );
        }

        return PosixCurrent(result, time, "Linux");
    }

    private static TimeSpan PosixCurrent(int result, Timespec time, string osName)
    {
        if (result != 0)
        {
            int errno = Marshal.GetLastPInvokeError();
            throw new Win32Exception(
                errno,
                $"clock_gettime(CLOCK_THREAD_CPUTIME_ID) failed on {osName} reading the current thread's CPU time "
                    + $"(returned {result}, errno {errno}: {Marshal.GetPInvokeErrorMessage(errno)})"
            );
        }

        return TimeSpan.FromTicks((time.Seconds * TimeSpan.TicksPerSecond) + (time.Nanoseconds / TimeSpan.NanosecondsPerTick));
    }

    /// <summary>
    /// <c>struct timespec { time_t tv_sec; long tv_nsec; }</c>: both fields are the platform's C <c>long</c> on Linux
    /// (<c>include/uapi/linux/time.h</c>, <c>__kernel_old_time_t</c> being a <c>long</c>) and macOS (xnu
    /// <c>bsd/sys/_types/_timespec.h</c>, <c>__darwin_time_t</c> being a <c>long</c>), hence <see cref="nint"/>.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Timespec
    {
        public nint Seconds;
        public nint Nanoseconds;
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentThread();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetThreadTimes(nint thread, out long creationTime, out long exitTime, out long kernelTime, out long userTime);

    [LibraryImport("libc.so.6", EntryPoint = "clock_gettime", SetLastError = true)]
    private static partial int LinuxClockGetTime(int clockId, out Timespec time);

    [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "clock_gettime", SetLastError = true)]
    private static partial int MacOsClockGetTime(int clockId, out Timespec time);
}
