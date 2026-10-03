using System.Runtime.InteropServices;
using Avalonia.Platform;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;

namespace Yaat.Client.Views;

/// <summary>
/// The Core-side switch for automation mode: while it is on, the client never brings a window
/// forward on its own. Window code in this assembly (geometry restore, the group raiser, the
/// reuse-and-activate helper) shows windows without activating them, skips every
/// <see cref="Avalonia.Controls.Window.Activate"/> call and never sets
/// <see cref="Avalonia.Controls.Window.Topmost"/>, so a driven client stays behind whatever
/// the user is working in. It also never changes <see cref="Avalonia.Controls.Window.WindowState"/>:
/// windows stay Normal, because Avalonia's Win32 backend activates a visible window on every state
/// change except to Minimized, and shows a window that is Maximized before its first show activated.
/// Every window shown under it also carries <c>WS_EX_NOACTIVATE</c>: <c>ShowActivated=false</c> shows a
/// window with <c>SW_SHOWNOACTIVATE</c>, which leaves it activatable, and when the window in front
/// minimizes Windows activates the next top-level window in Z-order — the never-activated client
/// qualifies. Only <c>WS_EX_NOACTIVATE</c> stops that hand-off. In automation mode a real click does not
/// activate the client either, so a person cannot type into it, while a driver's
/// <c>SetForegroundWindow</c> still works.
/// Every window is also moved to the bottom of the desktop's Z order, so it opens behind whatever the user is
/// working in rather than over it; an owned window is then placed directly above its owner, which the bottom
/// move would otherwise put it below.
/// <c>Yaat.Client</c>'s <c>AutomationMode.IsEnabled</c> is the only
/// writer outside tests; this assembly cannot reference <c>Yaat.Client</c>, so the flag lives here.
/// </summary>
public static class AutomationGate
{
    private static readonly ILogger Log = AppLog.CreateLogger("AutomationGate");

    private const uint WsExNoActivate = 0x08000000;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private const uint GwHwndPrev = 0x0003;
    private const string HwndDescriptor = "HWND";

    /// <summary><c>HWND_BOTTOM</c>: the Z-order position one past the last window on the desktop.</summary>
    private static readonly IntPtr HwndBottom = new(1);

    /// <summary>True while self-activation (activate, topmost, activated show) is suppressed.</summary>
    public static bool SuppressActivation { get; set; }

    /// <summary>
    /// Returns the Win32 window styles a window carries while self-activation is suppressed: every bit of
    /// <paramref name="style"/> and <paramref name="exStyle"/> is kept and <c>WS_EX_NOACTIVATE</c> is added.
    /// <c>WS_EX_APPWINDOW</c> is kept with the rest, so the window's taskbar button stays.
    /// </summary>
    public static (uint Style, uint ExStyle) NoActivateStyles(uint style, uint exStyle) => (style, exStyle | WsExNoActivate);

    /// <summary>
    /// Makes <paramref name="window"/> show without taking activation when automation mode is on, gives it the
    /// extended style that keeps Windows from handing it the foreground later, and, on Windows, moves it to the
    /// bottom of the desktop's Z order. Call it before the window is first shown; it leaves the window untouched
    /// otherwise. The Z-order move is what keeps the window behind the windows already on screen: a process that
    /// may set the foreground creates its top-level windows above every other window, and a never-activated show
    /// leaves a window where it was created.
    /// </summary>
    public static void ApplyShowActivated(Avalonia.Controls.Window window)
    {
        if (SuppressActivation)
        {
            window.ShowActivated = false;
            // Removed before adding, so a window that passes through here twice registers the callback once.
            Avalonia.Controls.Win32Properties.RemoveWindowStylesCallback(window, NoActivateStyles);
            Avalonia.Controls.Win32Properties.AddWindowStylesCallback(window, NoActivateStyles);
            SendToBottomOfZOrder(window);
            // Removed before adding, so a window that passes through here twice registers the handler once.
            window.Opened -= OnWindowOpened;
            window.Opened += OnWindowOpened;
        }
    }

    /// <summary>
    /// An owned window opens below its owner: the gate sends every window to the bottom of the Z order before its first
    /// show, and Avalonia attaches the owner at <c>Show(owner)</c> without re-ordering anything. The window is placed
    /// directly above its owner instead, which keeps a dialog above the window it belongs to and still below whatever was
    /// above that window.
    /// </summary>
    private static void OnWindowOpened(object? sender, EventArgs e)
    {
        if (sender is Avalonia.Controls.Window { Owner: { } owner } window)
        {
            PlaceAboveOwner(window, owner);
        }
    }

    private static void PlaceAboveOwner(Avalonia.Controls.Window window, Avalonia.Controls.WindowBase owner)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        if (!TryGetHwnd(window, "to place it above its owner", out IntPtr hwnd) || !TryGetHwnd(owner, "to read its Z position", out IntPtr ownerHwnd))
        {
            return;
        }

        // The window directly above the owner, so this one lands between the two. A NULL with a Win32 error recorded is the
        // call failing; a NULL with none is HWND_TOP, the right insert-after for an owner that is the top window.
        IntPtr insertAfter = GetWindow(ownerHwnd, GwHwndPrev);
        if (insertAfter == IntPtr.Zero)
        {
            int getWindowError = Marshal.GetLastWin32Error();
            if (getWindowError != 0)
            {
                Log.LogError(
                    "Automation mode: could not read the window above {Window}'s owner; it may sit below the owner. Win32 error {Error}",
                    window.GetType().Name,
                    getWindowError
                );
                return;
            }
        }

        if (!SetWindowPos(hwnd, insertAfter, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder))
        {
            int error = Marshal.GetLastWin32Error();
            Log.LogError(
                "Automation mode: could not place {Window} above its owner; it may sit below it. Win32 error {Error}",
                window.GetType().Name,
                error
            );
        }
    }

    /// <summary>
    /// Moves <paramref name="window"/> to the bottom of the desktop's Z order without activating it or letting
    /// Windows re-order its owner, so the window never paints over what the user is working in. Only meaningful
    /// on Windows, where the platform handle is the HWND; a platform handle that does not exist yet (or is not an
    /// HWND) is reported rather than worked around, because it means the window keeps the Z position it was
    /// created at.
    /// </summary>
    private static void SendToBottomOfZOrder(Avalonia.Controls.Window window)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        if (!TryGetHwnd(window, "before its first show", out IntPtr hwnd))
        {
            return;
        }

        if (!SetWindowPos(hwnd, HwndBottom, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder))
        {
            int error = Marshal.GetLastWin32Error();
            Log.LogError(
                "Automation mode: could not move {Window} to the bottom of the Z order; it may open above the user's windows. Win32 error {Error}",
                window.GetType().Name,
                error
            );
        }
    }

    /// <summary>
    /// The window's Win32 HWND, or false with a logged warning when the platform has none for it. A handle that does not
    /// exist yet and one that is not an HWND (a headless test's) both mean the window keeps the Z position it was created
    /// at, so the caller leaves the window alone and says so rather than working around it.
    /// </summary>
    private static bool TryGetHwnd(Avalonia.Controls.WindowBase window, string purpose, out IntPtr hwnd)
    {
        hwnd = IntPtr.Zero;
        IPlatformHandle? handle = window.TryGetPlatformHandle();
        if ((handle is not null) && (handle.Handle != IntPtr.Zero) && (handle.HandleDescriptor == HwndDescriptor))
        {
            hwnd = handle.Handle;
            return true;
        }

        string descriptor = (handle is null) ? "none" : (handle.HandleDescriptor ?? "none");
        Log.LogWarning(
            "Automation mode: {Window} has no HWND {Purpose} (platform handle: {Descriptor}); it may sit at the wrong Z position",
            window.GetType().Name,
            purpose,
            descriptor
        );
        return false;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
}
