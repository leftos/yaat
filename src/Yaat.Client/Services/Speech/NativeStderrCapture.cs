using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using Yaat.Client.Logging;

namespace Yaat.Client.Services.Speech;

/// <summary>
/// The latest sherpa-onnx <c>Failed to enable CUDA</c> line seen on native stderr: its sequence number (0 before any)
/// and the parsed reason. One immutable value, so the number and the reason are always read together.
/// </summary>
/// <param name="Sequence">How many fallback lines had arrived when this one did; 0 when none has.</param>
/// <param name="Reason">The ONNX Runtime status the line carried; null when no line has arrived.</param>
public sealed record NativeFallbackLine(long Sequence, string? Reason)
{
    /// <summary>The value before any fallback line arrived.</summary>
    public static NativeFallbackLine None { get; } = new(0, null);
}

/// <summary>Where a sherpa engine reads the latest CUDA fallback line native stderr carried.</summary>
public interface INativeFallbackSource
{
    /// <summary>The latest fallback line, or <see cref="NativeFallbackLine.None"/> when none has arrived.</summary>
    NativeFallbackLine Latest { get; }
}

/// <summary>
/// What a sherpa engine reads to tell whether a CUDA load really reached the GPU: this process's GPU memory and the native
/// fallback line.
/// </summary>
/// <param name="SampleGpuBytes">This process's dedicated GPU bytes now, or -1 when they cannot be read; must never throw.</param>
/// <param name="Fallback">The latest sherpa-onnx CUDA fallback line.</param>
public sealed record SherpaGpuFallbackProbes(Func<long> SampleGpuBytes, INativeFallbackSource Fallback);

/// <summary>
/// Captures the process's native standard error into the client log. The client is a WinExe, so whatever a native library
/// prints there is otherwise lost, sherpa-onnx's silent CUDA-to-CPU fallback line included. Installed once at startup,
/// before any sherpa-onnx native loads (its statically linked C runtime binds stderr when the DLL loads): on Windows the
/// process's standard-error handle is replaced by a pipe, on Linux fd 2 is. A background thread forwards each line to the
/// log, copies it back to the original stderr when there was one (a terminal, journald), and keeps the latest
/// <c>Failed to enable CUDA</c> line. macOS runs sherpa-onnx on the CPU only, so it gets no capture.
/// </summary>
public sealed class NativeStderrCapture : INativeFallbackSource
{
    private const string FallbackPrefix = "Failed to enable CUDA: ";
    private const string ProvidersSeparator = ". Available providers";
    private const string FallbackSuffix = "Fallback to cpu";
    private const string OrtErrorMarker = "[ONNXRuntimeError] : ";
    private const string OrtFieldSeparator = " : ";
    private const int OrtFieldsBeforeStatus = 3;
    private const int StdErrorHandle = -12;
    private const int StdErrorFd = 2;
    private const int FcntlSetFd = 2;
    private const int FdCloseOnExec = 1;

    private static readonly ILogger Log = AppLog.CreateLogger("NativeStderr");

    private static NativeStderrCapture? _installed;

    private readonly Func<byte[], bool>? _copyToOriginal;
    private NativeFallbackLine _latest = NativeFallbackLine.None;
    private bool _copyFailed;

    private NativeStderrCapture(Func<byte[], bool>? copyToOriginal) => _copyToOriginal = copyToOriginal;

    /// <summary>The capture installed at startup, or a source that never sees a line when none is.</summary>
    public static INativeFallbackSource Current => (INativeFallbackSource?)Volatile.Read(ref _installed) ?? NoCapture.Instance;

    /// <inheritdoc/>
    public NativeFallbackLine Latest => Volatile.Read(ref _latest);

    /// <summary>
    /// Redirects native stderr into the log. A platform without a capture, or a redirect that fails, logs why and leaves
    /// stderr as it was; startup never stops on it. Harmless when called again.
    /// </summary>
    public static void Install()
    {
        if (Volatile.Read(ref _installed) is not null)
        {
            return;
        }
        try
        {
            Redirect? redirect =
                OperatingSystem.IsWindows() ? RedirectWindows()
                : OperatingSystem.IsLinux() ? RedirectLinux()
                : null;
            if (redirect is null)
            {
                Log.LogInformation("Native stderr is not captured on this platform");
                return;
            }
            var capture = new NativeStderrCapture(redirect.CopyToOriginal);
            var reader = new Thread(() => capture.ReadLines(redirect.Read)) { IsBackground = true, Name = "NativeStderr" };
            reader.Start();
            Volatile.Write(ref _installed, capture);
            Log.LogInformation(
                "Native stderr is captured into this log; copied to the original stderr too: {Copied}",
                redirect.CopyToOriginal is not null
            );
        }
        catch (Exception ex) when (ex is IOException or ExternalException or DllNotFoundException or EntryPointNotFoundException)
        {
            Log.LogWarning(ex, "Native stderr could not be captured; a GPU fallback's native reason will not be logged");
        }
    }

    /// <summary>
    /// The reason in sherpa-onnx's <c>Failed to enable CUDA: {status}. Available providers: … Fallback to cpu</c> line, as
    /// the bare ONNX Runtime status. The status runs to <c>. Available providers</c>, else to <c>Fallback to cpu</c>, else to
    /// the end of the line (on Windows the status ends in a newline, so the providers list arrives as a line of its own).
    /// A status carrying <c>[ONNXRuntimeError] : code : name : </c> loses everything up to its message. Null for any
    /// other line.
    /// </summary>
    /// <param name="line">One line of native stderr.</param>
    internal static string? CudaFallbackReasonFrom(string line)
    {
        int prefix = line.IndexOf(FallbackPrefix, StringComparison.Ordinal);
        if (prefix < 0)
        {
            return null;
        }
        int start = prefix + FallbackPrefix.Length;
        int end = line.IndexOf(ProvidersSeparator, start, StringComparison.Ordinal);
        if (end < 0)
        {
            end = line.IndexOf(FallbackSuffix, start, StringComparison.Ordinal);
        }
        string reason = BareOrtStatus(((end < 0) ? line[start..] : line[start..end]).Trim());
        return (reason.Length == 0) ? null : reason;
    }

    /// <summary>Keeps a fallback line, then logs the line.</summary>
    /// <param name="line">One line of native stderr.</param>
    internal void OnLine(string line)
    {
        if (CudaFallbackReasonFrom(line) is { } reason)
        {
            NativeFallbackLine previous = Volatile.Read(ref _latest);
            Volatile.Write(ref _latest, new NativeFallbackLine(previous.Sequence + 1, reason));
        }
        Log.LogInformation("{Line}", line);
    }

    /// <summary>The text after the third <c> : </c> from <c>[ONNXRuntimeError]</c>, or the status as it is without one.</summary>
    private static string BareOrtStatus(string status)
    {
        int marker = status.IndexOf(OrtErrorMarker, StringComparison.Ordinal);
        if (marker < 0)
        {
            return status;
        }
        int cut = marker;
        for (int field = 0; field < OrtFieldsBeforeStatus; field++)
        {
            int separator = status.IndexOf(OrtFieldSeparator, cut, StringComparison.Ordinal);
            if (separator < 0)
            {
                return status;
            }
            cut = separator + OrtFieldSeparator.Length;
        }
        return status[cut..].Trim();
    }

    private void ReadLines(Stream stream)
    {
        try
        {
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (reader.ReadLine() is { } line)
            {
                OnLine(line);
                CopyToOriginal(line);
            }
            Log.LogWarning("Native stderr closed; nothing more it prints is logged");
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Reading native stderr failed; nothing more it prints is logged");
        }
    }

    private void CopyToOriginal(string line)
    {
        if ((_copyToOriginal is null) || _copyFailed)
        {
            return;
        }
        if (!_copyToOriginal(Encoding.UTF8.GetBytes(line + Environment.NewLine)))
        {
            _copyFailed = true;
            Log.LogWarning(
                "Copying native stderr to the original stderr failed (error {Error}); later lines go to this log only",
                Marshal.GetLastPInvokeError()
            );
        }
    }

    private static Redirect RedirectWindows()
    {
        IntPtr original = GetStdHandle(StdErrorHandle);
        if (!CreatePipe(out SafeFileHandle read, out IntPtr write, IntPtr.Zero, 0))
        {
            throw new ExternalException("CreatePipe failed for native stderr", Marshal.GetLastPInvokeError());
        }
        if (!SetStdHandle(StdErrorHandle, write))
        {
            int error = Marshal.GetLastPInvokeError();
            read.Dispose();
            CloseHandle(write);
            throw new ExternalException("SetStdHandle(STD_ERROR_HANDLE) failed for native stderr", error);
        }
        // A WinExe started from Explorer has no standard error to copy back to.
        bool hasOriginal = (original != IntPtr.Zero) && (original != new IntPtr(-1));
        Func<byte[], bool>? copy = hasOriginal
            ? bytes => WriteFile(original, bytes, bytes.Length, out int written, IntPtr.Zero) && (written == bytes.Length)
            : null;
        return new Redirect(new FileStream(read, FileAccess.Read, 1), copy);
    }

    private static Redirect RedirectLinux()
    {
        // -1 when fd 2 was already closed: then there is nothing to copy back to.
        int original = Dup(StdErrorFd);
        if (original >= 0)
        {
            SetCloseOnExec(original, original);
        }
        int[] fds = OpenReadEndPipe(original);
        if (fds[1] != StdErrorFd)
        {
            if (Dup2(fds[1], StdErrorFd) < 0)
            {
                int error = Marshal.GetLastPInvokeError();
                CloseAll(fds[0], fds[1], original);
                throw new ExternalException("dup2() onto fd 2 failed for native stderr", error);
            }
            CloseFd(fds[1]);
        }
        Func<byte[], bool>? copy = (original >= 0) ? bytes => WriteFd(original, bytes, bytes.Length) == bytes.Length : null;
        return new Redirect(new FileStream(new SafeFileHandle(new IntPtr(fds[0]), ownsHandle: true), FileAccess.Read, 1), copy);
    }

    /// <summary>A pipe whose read end is not fd 2 and is closed on exec, so a child process never holds the log's pipe.</summary>
    private static int[] OpenReadEndPipe(int original)
    {
        int[] fds = new int[2];
        if (Pipe(fds) != 0)
        {
            int error = Marshal.GetLastPInvokeError();
            CloseAll(original);
            throw new ExternalException("pipe() failed for native stderr", error);
        }
        if (fds[0] == StdErrorFd)
        {
            CloseAll(fds[0], fds[1], original);
            throw new IOException("pipe() returned fd 2 as its read end: stderr was closed, so native stderr is not captured");
        }
        SetCloseOnExec(fds[0], fds[1], original);
        return fds;
    }

    private static void SetCloseOnExec(int fd, params int[] closeOnFailure)
    {
        if (Fcntl(fd, FcntlSetFd, FdCloseOnExec) >= 0)
        {
            return;
        }
        int error = Marshal.GetLastPInvokeError();
        CloseAll([fd, .. closeOnFailure]);
        throw new ExternalException($"fcntl(F_SETFD, FD_CLOEXEC) failed on fd {fd} for native stderr", error);
    }

    private static void CloseAll(params int[] fds)
    {
        foreach (int fd in fds.Where(fd => fd >= 0).Distinct())
        {
            CloseFd(fd);
        }
    }

    /// <summary>The pipe's read end, and how to write a line to the stderr the process had before, when it had one.</summary>
    private sealed record Redirect(Stream Read, Func<byte[], bool>? CopyToOriginal);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int stdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(out SafeFileHandle readPipe, out IntPtr writePipe, IntPtr attributes, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetStdHandle(int stdHandle, IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteFile(IntPtr file, byte[] buffer, int count, out int written, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("libc", EntryPoint = "pipe", SetLastError = true)]
    private static extern int Pipe(int[] fds);

    [DllImport("libc", EntryPoint = "dup", SetLastError = true)]
    private static extern int Dup(int fd);

    [DllImport("libc", EntryPoint = "dup2", SetLastError = true)]
    private static extern int Dup2(int oldFd, int newFd);

    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int Fcntl(int fd, int command, int argument);

    [DllImport("libc", EntryPoint = "write", SetLastError = true)]
    private static extern nint WriteFd(int fd, byte[] buffer, nint count);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int CloseFd(int fd);

    /// <summary>The source when nothing captures native stderr: no fallback line ever arrives.</summary>
    private sealed class NoCapture : INativeFallbackSource
    {
        public static NoCapture Instance { get; } = new();

        public NativeFallbackLine Latest => NativeFallbackLine.None;
    }
}
