using Avalonia.Controls;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;

namespace Yaat.Client.Views;

public static class WindowActivationExtensions
{
    private static readonly ILogger Log = AppLog.CreateLogger("WindowActivation");

    /// <summary>
    /// Brings an already-open window to the front, un-minimizing it first if needed.
    /// <see cref="Window.Activate"/> alone does not restore a minimized window, so every
    /// reuse-and-activate window (Flight Plan Editor, Favorites Panel, ...) must go through
    /// this instead (#360). In automation mode (<see cref="AutomationGate"/>) it does nothing:
    /// it skips <see cref="Window.Activate"/>, and it leaves a minimized window minimized, because
    /// Avalonia's Win32 backend restores a visible window with <c>SW_RESTORE</c> and then calls
    /// <c>SetFocus</c> and <c>SetForegroundWindow</c> for every <see cref="Window.WindowState"/>
    /// change other than to Minimized. Nothing in automation mode minimizes a window, so a
    /// minimized one is logged.
    /// </summary>
    public static void RestoreAndActivate(this Window window)
    {
        if (AutomationGate.SuppressActivation)
        {
            LogMinimizedInAutomationMode(window);
            return;
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
    }

    private static void LogMinimizedInAutomationMode(Window window)
    {
        if (window.WindowState == WindowState.Minimized)
        {
            Log.LogWarning("'{Title}' is minimized in automation mode; left minimized, since restoring it would activate it", window.Title);
        }
    }
}
