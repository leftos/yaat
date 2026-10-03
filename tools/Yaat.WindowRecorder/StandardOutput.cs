using System.Runtime.InteropServices;

namespace Yaat.WindowRecorder;

/// <summary>
/// Closes the process's stdout handle. Disposing the stream <see cref="Console.OpenStandardOutput()"/> returns leaves the
/// OS handle open, so the reader at the other end of the pipe sees no end of input until the process exits.
/// </summary>
internal static class StandardOutput
{
    private const int StdOutputHandle = -11;

    public static void Close()
    {
        nint handle = GetStdHandle(StdOutputHandle);
        if ((handle != 0) && (handle != -1) && !CloseHandle(handle))
        {
            Console.Error.WriteLine($"Yaat.WindowRecorder: closing stdout failed (Win32 error {Marshal.GetLastWin32Error()}).");
        }
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint GetStdHandle(int stdHandle);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
