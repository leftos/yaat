using Avalonia;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;

namespace Yaat.Client;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        AppLog.Initialize();
        var log = AppLog.CreateLogger("Program");
        log.LogInformation("Log file: {LogPath}", AppLog.LogPath);

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
    }
}
