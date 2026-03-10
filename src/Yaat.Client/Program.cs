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

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            log.LogCritical(e.ExceptionObject as Exception, "Unhandled exception (AppDomain)");
            AppLog.Flush();
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            log.LogError(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };

        int autoIdx = Array.FindIndex(args, a => a.Equals("--autoconnect", StringComparison.OrdinalIgnoreCase));
        if (autoIdx >= 0 && autoIdx + 1 < args.Length)
        {
            App.AutoConnectTarget = args[autoIdx + 1];
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            log.LogCritical(ex, "Fatal exception in Main");
            AppLog.Flush();
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
    }
}
