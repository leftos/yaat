using System.Runtime.InteropServices;
using Xunit;

namespace Yaat.ClientDriver.Mcp.Tests;

/// <summary>
/// Whether this process has an interactive desktop to put windows on. The Desktop-trait tests ask
/// <see cref="MissingReason"/> and skip on the reason it answers, so a builder without a desktop (session 0, a locked or
/// disconnected session, Linux) reports those tests as skips instead of failures. The decision itself is
/// <see cref="Describe"/>, a pure function the unit tests cover directly, because a real headless run cannot be automated
/// here.
/// </summary>
public static class InteractiveDesktop
{
    private const int UoiFlags = 1;
    private const uint WsfVisible = 1;
    private const uint DesktopReadObjects = 0x0001;

    /// <summary>
    /// The reason this process cannot drive desktop windows, or null when it can. A probe that fails counts as the fact it
    /// was probing for being missing, so this never throws.
    /// </summary>
    public static string? MissingReason()
    {
        if (!OperatingSystem.IsWindows())
        {
            return Describe(isWindows: false, sessionId: 0, hasInputDesktop: false, windowStationVisible: false);
        }

        uint sessionId = ProbeSessionId();
        bool windowStationVisible = ProbeWindowStationVisible();
        bool hasInputDesktop = ProbeInputDesktop();
        return Describe(isWindows: true, sessionId: sessionId, hasInputDesktop: hasInputDesktop, windowStationVisible: windowStationVisible);
    }

    /// <summary>
    /// The decision, from already-probed facts. The checks run in order and the first match wins; null means an interactive
    /// desktop is present.
    /// </summary>
    public static string? Describe(bool isWindows, uint sessionId, bool hasInputDesktop, bool windowStationVisible)
    {
        if (!isWindows)
        {
            return "The Desktop tests drive Windows windows; this is not Windows.";
        }

        if (sessionId == 0)
        {
            return "No interactive desktop: the process runs in session 0 (a service session).";
        }

        if (!windowStationVisible)
        {
            return "No interactive desktop: the process's window station is not visible.";
        }

        if (!hasInputDesktop)
        {
            return "No interactive desktop: no input desktop can be opened (locked, disconnected or headless session).";
        }

        return null;
    }

    /// <summary>This process's session id; 0, which no interactive process has, when the probe fails.</summary>
    private static uint ProbeSessionId()
    {
        try
        {
            return ProcessIdToSessionId(GetCurrentProcessId(), out uint sessionId) ? sessionId : 0;
        }
        catch (Exception ex)
        {
            Report($"The session id probe failed: {ex.Message}");
            return 0;
        }
    }

    /// <summary>Whether the process's window station carries <c>WSF_VISIBLE</c>; false when the probe fails.</summary>
    private static bool ProbeWindowStationVisible()
    {
        try
        {
            nint station = GetProcessWindowStation();
            if (station == 0)
            {
                return false;
            }

            if (!GetUserObjectInformation(station, UoiFlags, out UserObjectFlags flags, Marshal.SizeOf<UserObjectFlags>(), out _))
            {
                return false;
            }

            return (flags.Flags & WsfVisible) != 0;
        }
        catch (Exception ex)
        {
            Report($"The window station visibility probe failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Whether an input desktop can be opened; false when the probe fails or the desktop cannot be opened.</summary>
    private static bool ProbeInputDesktop()
    {
        try
        {
            nint desktop = OpenInputDesktop(0, false, DesktopReadObjects);
            if (desktop == 0)
            {
                return false;
            }

            if (!CloseDesktop(desktop))
            {
                Report($"CloseDesktop failed for the input desktop 0x{desktop:X}: Win32 error {Marshal.GetLastWin32Error()}");
            }

            return true;
        }
        catch (Exception ex)
        {
            Report($"The input desktop probe failed: {ex.Message}");
            return false;
        }
    }

    private static void Report(string message) => TestContext.Current.TestOutputHelper?.WriteLine(message);

    /// <summary><c>USEROBJECTFLAGS</c>, as <c>GetUserObjectInformation(UOI_FLAGS)</c> fills it in.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct UserObjectFlags
    {
        public int Inherit;
        public int Reserved;
        public uint Flags;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentProcessId();

    [DllImport("user32.dll")]
    private static extern nint GetProcessWindowStation();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserObjectInformation(nint handle, int index, out UserObjectFlags info, int length, out uint lengthNeeded);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint desiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(nint desktop);
}
