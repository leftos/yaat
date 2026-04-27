using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Yaat.VStrips;

public class App : Application
{
    public static string? AutoConnectTarget { get; set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // MainWindow constructs its own StandaloneViewModel so it can share the VM's
            // UserPreferences instance with WindowGeometryHelper. A second prefs instance
            // here would race with the VM's instance on save and overwrite each other's
            // in-memory state.
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
