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
/// <c>Yaat.Client</c>'s <c>AutomationMode.IsEnabled</c> is the only
/// writer outside tests; this assembly cannot reference <c>Yaat.Client</c>, so the flag lives here.
/// </summary>
public static class AutomationGate
{
    private const uint WsExNoActivate = 0x08000000;

    /// <summary>True while self-activation (activate, topmost, activated show) is suppressed.</summary>
    public static bool SuppressActivation { get; set; }

    /// <summary>
    /// Returns the Win32 window styles a window carries while self-activation is suppressed: every bit of
    /// <paramref name="style"/> and <paramref name="exStyle"/> is kept and <c>WS_EX_NOACTIVATE</c> is added.
    /// <c>WS_EX_APPWINDOW</c> is kept with the rest, so the window's taskbar button stays.
    /// </summary>
    public static (uint Style, uint ExStyle) NoActivateStyles(uint style, uint exStyle) => (style, exStyle | WsExNoActivate);

    /// <summary>
    /// Makes <paramref name="window"/> show without taking activation when automation mode is on, and gives
    /// it the extended style that keeps Windows from handing it the foreground later. Call it before the
    /// window is first shown; it leaves the window untouched otherwise.
    /// </summary>
    public static void ApplyShowActivated(Avalonia.Controls.Window window)
    {
        if (SuppressActivation)
        {
            window.ShowActivated = false;
            // Removed before adding, so a window that passes through here twice registers the callback once.
            Avalonia.Controls.Win32Properties.RemoveWindowStylesCallback(window, NoActivateStyles);
            Avalonia.Controls.Win32Properties.AddWindowStylesCallback(window, NoActivateStyles);
        }
    }
}
