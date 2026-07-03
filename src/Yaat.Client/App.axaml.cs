using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Client.Services;
using Yaat.Client.Views;

namespace Yaat.Client;

public class App : Application
{
    public static string? AutoConnectTarget { get; set; }
    public static string? AutoLoadScenarioId { get; set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// Pushes the user's Interface font size into the application-level dynamic
    /// resources the global control styles bind to. Updates propagate live to every
    /// window plus the embedded Strips/TDLS panels (one shared Application). The
    /// subtle-text size tracks one point below the base, floored at 8.
    /// </summary>
    public static void ApplyInterfaceFontSize(int size)
    {
        if (Current is null)
        {
            return;
        }

        int clamped = Math.Clamp(size, 8, 24);
        Current.Resources["UiFontSize"] = (double)clamped;
        Current.Resources["UiFontSizeSubtle"] = (double)Math.Max(8, clamped - 1);
        Current.Resources["UiFontSizeHeader"] = (double)(clamped + 1);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Register app-wide window hotkeys (focus command input, always on top) so they work from any
        // YAAT window, not just MainWindow. Idempotent; sits outside the desktop-lifetime block so it
        // also runs under the headless test host.
        Yaat.Client.Views.WindowHotkeys.EnsureRegistered();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // A single unhandled exception on the UI dispatcher otherwise tears down the whole
            // client (GitHub #237). Log it and keep the message loop alive so a transient fault
            // degrades gracefully instead of crashing. Wired only for the real desktop app — the
            // headless test host never enters this block, so tests still observe UI-thread faults.
            var dispatcherLog = AppLog.CreateLogger("Dispatcher");
            Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                dispatcherLog.LogError(e.Exception, "Unhandled UI-thread exception (recovered)");
                e.Handled = true;
            };

            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            desktop.MainWindow = new MainWindow();

            // Graceful Ctrl+C: when the launch script is configured to send a real CTRL_C
            // signal (rather than TerminateProcess via Stop-Process -Force), flush any
            // in-memory window geometry and signal app shutdown before letting the runtime
            // tear down. Stop-Process -Force in start.ps1 bypasses this entirely — that path
            // is covered by the throttled save in WindowGeometryHelper.
            System.Console.CancelKeyPress += (_, args) =>
            {
                AppLifetime.MarkShuttingDown();
                Yaat.Client.Views.WindowGeometryHelper.FlushAllSavedGeometries();
                args.Cancel = false;
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
