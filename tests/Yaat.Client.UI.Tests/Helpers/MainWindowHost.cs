using Avalonia.Headless;
using Avalonia.Threading;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;
using Yaat.Client.Views.Settings;

namespace Yaat.Client.UI.Tests.Helpers;

/// <summary>
/// Boots the main window for a UI test and tears it down afterwards. Tests that drive the window's menus need a shown,
/// laid-out <see cref="MainWindow"/> whose nested views have run their <c>OnLoaded</c> handlers, and every one of them has
/// to close the Settings window a click opens and hide rather than close the main window.
/// </summary>
internal static class MainWindowHost
{
    /// <summary>Shows a fresh main window, runs a layout pass and returns it with the view model from its DataContext.</summary>
    public static (MainWindow Main, MainViewModel Vm) Boot()
    {
        var main = new MainWindow();
        main.Show();
        Dispatcher.UIThread.RunJobs();
        main.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        return (main, (MainViewModel)main.DataContext!);
    }

    /// <summary>
    /// Flushes the dispatcher and forces a headless render tick, then flushes again: a control's effective visibility and an
    /// open flyout settle across the render pass, which otherwise only runs on the headless platform's timer.
    /// </summary>
    public static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// Closes the Settings and speech-debug windows a click opened and hides the main window. The main window is never
    /// closed: a close that goes through latches the process-wide AppLifetime.IsShuttingDown flag, after which every later
    /// test's pop-out windows treat their own close as an app shutdown.
    /// </summary>
    public static void CloseAll(MainWindow main)
    {
        foreach (SettingsWindow dialog in main.OwnedWindows.OfType<SettingsWindow>().ToList())
        {
            dialog.Close();
            Dispatcher.UIThread.RunJobs();
        }

        main.OpenSpeechDebugWindow?.Close();
        main.Hide();
        Dispatcher.UIThread.RunJobs();
    }
}
