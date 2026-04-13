using System.Runtime.InteropServices;
using Avalonia;
using LLama.Native;
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

        // LLamaSharp native library configuration. Must be called before any LLamaWeights load
        // (throws InvalidOperationException otherwise). Routes llama.cpp log messages to AppLog
        // so Phase 7 speech-debug logging can surface them, and declares WithAutoFallback so the
        // library transparently falls through to CPU when the configured GPU backend isn't
        // available. Yaat.Client currently ships LLamaSharp.Backend.Cpu only — Phase 6 Option B
        // will add a GpuRuntimeDownloader that drops the appropriate GPU natives into
        // %LOCALAPPDATA%/yaat/runtime/llama/ at user opt-in, and WithSearchDirectory will be
        // added here pointing at that folder.
        ConfigureLlamaSharpNative(log);

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

    private static void ConfigureLlamaSharpNative(ILogger log)
    {
        try
        {
            var config = NativeLibraryConfig
                .All.WithCuda(true)
                .WithVulkan(true)
                .WithAutoFallback(true)
                .WithLogCallback((level, message) => LogLlamaMessage(log, level, message));

            // Option B: if the user has downloaded a GPU runtime via Settings, add that folder to
            // the search path so LLamaSharp picks up the GPU natives instead of the CPU defaults
            // that ship with the installer. The downloader extracts files so that
            // {LlamaSearchRoot}/runtimes/win-x64/native/{backend}/llama.dll resolves the same way
            // as the in-bin CPU natives — WithSearchDirectory just adds a second root to probe.
            if (Directory.Exists(GpuRuntimeDownloader.LlamaSearchRoot))
            {
                config.WithSearchDirectory(GpuRuntimeDownloader.LlamaSearchRoot);
                log.LogInformation("LLamaSharp search directory includes {Path}", GpuRuntimeDownloader.LlamaSearchRoot);
            }
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "LLamaSharp NativeLibraryConfig setup failed (non-fatal; will run with defaults)");
        }
    }

    private static void LogLlamaMessage(ILogger log, LLamaLogLevel level, string message)
    {
        var trimmed = message?.TrimEnd('\n', '\r');
        if (string.IsNullOrEmpty(trimmed))
        {
            return;
        }

        // llama.cpp's log levels don't map 1:1 to ILogger; pick sensible equivalents.
        switch (level)
        {
            case LLamaLogLevel.Error:
                log.LogError("llama: {Message}", trimmed);
                break;
            case LLamaLogLevel.Warning:
                log.LogWarning("llama: {Message}", trimmed);
                break;
            case LLamaLogLevel.Info:
                log.LogInformation("llama: {Message}", trimmed);
                break;
            default:
                log.LogDebug("llama: {Message}", trimmed);
                break;
        }
    }
}
