using Avalonia.Controls;

namespace Yaat.Client.Views;

public static class WindowActivationExtensions
{
    /// <summary>
    /// Brings an already-open window to the front, un-minimizing it first if needed.
    /// <see cref="Window.Activate"/> alone does not restore a minimized window, so every
    /// reuse-and-activate window (Flight Plan Editor, Favorites Panel, ...) must go through
    /// this instead (#360). In automation mode this helper skips its own
    /// <see cref="Window.Activate"/> (<see cref="AutomationGate"/>), but un-minimizing still
    /// activates the window on Windows: Avalonia's Win32 backend restores a visible window with
    /// <c>SW_RESTORE</c> and then calls <c>SetFocus</c> and <c>SetForegroundWindow</c> for every
    /// <see cref="Window.WindowState"/> change other than to Minimized.
    /// </summary>
    public static void RestoreAndActivate(this Window window)
    {
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        if (!AutomationGate.SuppressActivation)
        {
            window.Activate();
        }
    }
}
