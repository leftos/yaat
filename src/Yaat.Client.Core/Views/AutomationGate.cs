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
/// <c>Yaat.Client</c>'s <c>AutomationMode.IsEnabled</c> is the only
/// writer outside tests; this assembly cannot reference <c>Yaat.Client</c>, so the flag lives here.
/// </summary>
public static class AutomationGate
{
    /// <summary>True while self-activation (activate, topmost, activated show) is suppressed.</summary>
    public static bool SuppressActivation { get; set; }

    /// <summary>
    /// Makes <paramref name="window"/> show without taking activation when automation mode is on.
    /// Call it before the window is first shown; it leaves the window untouched otherwise.
    /// </summary>
    public static void ApplyShowActivated(Avalonia.Controls.Window window)
    {
        if (SuppressActivation)
        {
            window.ShowActivated = false;
        }
    }
}
