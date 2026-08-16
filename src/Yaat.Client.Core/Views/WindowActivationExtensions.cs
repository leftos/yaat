using Avalonia.Controls;

namespace Yaat.Client.Views;

public static class WindowActivationExtensions
{
    /// <summary>
    /// Brings an already-open window to the front, un-minimizing it first if needed.
    /// <see cref="Window.Activate"/> alone does not restore a minimized window, so every
    /// reuse-and-activate window (Flight Plan Editor, Favorites Panel, ...) must go through
    /// this instead (#360).
    /// </summary>
    public static void RestoreAndActivate(this Window window)
    {
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }
        window.Activate();
    }
}
