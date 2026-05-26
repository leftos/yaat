using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
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

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
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
