using System.Runtime.InteropServices;
using Avalonia;
using Microsoft.Extensions.Logging;
using Velopack;
using Yaat.Client.Logging;
using Yaat.Client.Services;

namespace Yaat.Client;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var builder = VelopackApp.Build();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            builder.OnAfterInstallFastCallback(v => CrcInstallPrompt.Show());
        }

        builder.Run();

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

        // LM-Kit licensing must run before any LM construction so the licensing layer is
        // initialized. The helper resolves the key from LMKIT_LICENSE_KEY or the solution-root
        // .env file, falling back to empty string (Community Edition). LM-Kit owns
        // CUDA / Vulkan / Metal backend selection at model load time via the
        // LM-Kit.NET.Backend.Cuda13.Windows package; no manual NativeLibraryConfig dance needed
        // (that was the LLamaSharp 0.26 + Whisper.net dual-stack story this replaces).
        var licenseResult = LmKitLicense.Initialize();
        if (licenseResult.Error is { } licenseError)
        {
            log.LogWarning(licenseError, "LM-Kit license setup failed (non-fatal; will run with defaults)");
        }
        else
        {
            log.LogInformation("LM-Kit {Tier} initialized from {Source}", licenseResult.Tier, licenseResult.Source);
        }

        int autoIdx = Array.FindIndex(args, a => a.Equals("--autoconnect", StringComparison.OrdinalIgnoreCase));
        if (autoIdx >= 0 && autoIdx + 1 < args.Length)
        {
            App.AutoConnectTarget = args[autoIdx + 1];
        }

        int scenarioIdx = Array.FindIndex(args, a => a.Equals("--scenario", StringComparison.OrdinalIgnoreCase));
        if (scenarioIdx >= 0 && scenarioIdx + 1 < args.Length)
        {
            App.AutoLoadScenarioId = args[scenarioIdx + 1];
            // --scenario implies --autoconnect to localhost if not already set
            App.AutoConnectTarget ??= "http://localhost:5000";
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
