using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Yaat.Client.ViewModels;

namespace Yaat.VStrips;

public class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Reuse MainViewModel wholesale: it owns the ServerConnection, VStripsViewModel,
            // and every room/scenario lifecycle method we need for the standalone app. The
            // Ground / Radar / Speech services it constructs are never bound to any view and
            // stay idle for the app's lifetime — acceptable overhead for guaranteed parity
            // with the embedded experience inside the main Yaat.Client window.
            var vm = new MainViewModel();
            desktop.MainWindow = new MainWindow { DataContext = vm };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
