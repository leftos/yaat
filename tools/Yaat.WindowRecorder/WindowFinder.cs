using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Yaat.WindowRecorder;

/// <summary>Finds a top-level window by its exact title and says whether it is minimized or hidden.</summary>
internal static class WindowFinder
{
    private delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    /// <summary>
    /// Returns the handle of the top-level window titled exactly <paramref name="title"/>, or zero when there is none.
    /// EnumWindows is asked first; the owning process's main window handle second, because some engines' windows are
    /// not among the ones FindWindow and EnumWindows report while the process still lists the title.
    /// </summary>
    public static nint FindByTitle(string title)
    {
        nint found = nint.Zero;
        EnumWindows(
            (hwnd, _) =>
            {
                if (string.Equals(GetTitle(hwnd), title, StringComparison.Ordinal))
                {
                    found = hwnd;
                    return false;
                }
                return true;
            },
            nint.Zero
        );
        if (found != nint.Zero)
        {
            return found;
        }

        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                if (string.Equals(process.MainWindowTitle, title, StringComparison.Ordinal) && process.MainWindowHandle != nint.Zero)
                {
                    return process.MainWindowHandle;
                }
            }
        }
        return nint.Zero;
    }

    public static bool IsMinimized(nint hwnd) => IsIconic(hwnd);

    /// <summary>
    /// A window hidden with ShowWindow(SW_HIDE) keeps its handle but presents no surface to capture. DWM cloaking
    /// (DWMWA_CLOAK) leaves WS_VISIBLE set, so a cloaked window is still visible here and is not hidden.
    /// </summary>
    public static bool IsHidden(nint hwnd) => !IsWindowVisible(hwnd);

    /// <summary>False once the window has been destroyed; a dead handle reads as neither visible nor minimized.</summary>
    public static bool Exists(nint hwnd) => IsWindow(hwnd);

    private static string GetTitle(nint hwnd)
    {
        int length = GetWindowTextLengthW(hwnd);
        if (length == 0)
        {
            return string.Empty;
        }
        char[] buffer = new char[length + 1];
        int copied = GetWindowTextW(hwnd, buffer, buffer.Length);
        return new string(buffer, 0, copied);
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetWindowTextW(nint hwnd, [Out] char[] text, int maxCount);

    [DllImport("user32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetWindowTextLengthW(nint hwnd);

    [DllImport("user32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool IsIconic(nint hwnd);

    [DllImport("user32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool IsWindowVisible(nint hwnd);

    [DllImport("user32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool IsWindow(nint hwnd);
}
