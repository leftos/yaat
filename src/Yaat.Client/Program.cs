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

        // Phase 6 Option B GPU runtime wire-up. Must all run before any LLamaSharp / Whisper.net
        // native library load. Three steps, in order:
        //   1. Detect a CUDA Toolkit 12.x install and apply it to this process (sets CUDA_PATH
        //      + prepends bin to PATH). Required for any CUDA backend because LLamaSharp /
        //      Whisper.net CUDA natives depend on cudart64_12.dll / cublas64_12.dll / etc. from
        //      the toolkit's bin dir — these aren't in any NuGet backend package.
        //   2. Configure LLamaSharp via NativeLibraryConfig (WithCuda/WithVulkan/WithAutoFallback
        //      + WithSearchDirectory pointing at the downloaded GPU natives if present).
        //   3. Configure Whisper.net via RuntimeOptions.LibraryPath (which the managed loader
        //      calls GetDirectoryName on to find extra search roots).
        ConfigureCudaToolkit(log);
        ConfigureLlamaSharpNative(log);
        ConfigureWhisperNetNative(log);

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

    private static void ConfigureCudaToolkit(ILogger log)
    {
        try
        {
            var toolkit = GpuRuntimeDownloader.FindCuda12Toolkit();
            if (toolkit is null)
            {
                log.LogInformation("No CUDA Toolkit 12.x installation detected — CUDA backends will not be usable");
                return;
            }

            if (GpuRuntimeDownloader.ApplyCudaToolkitToProcess(toolkit))
            {
                log.LogInformation(
                    "CUDA Toolkit 12.{Minor} detected at {Path}; CUDA_PATH + PATH updated for this process",
                    toolkit.MinorVersion,
                    toolkit.InstallPath
                );
            }
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "CUDA toolkit detection failed (non-fatal; GPU backends may fall back to CPU)");
        }
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

            // If the user has downloaded a GPU runtime via Settings, add that folder to the search
            // path so LLamaSharp picks up the GPU natives instead of (or in addition to) the CPU
            // defaults that ship with the installer. Extraction lays files at
            // {LlamaSearchRoot}/runtimes/win-x64/native/{backend}/llama.dll — WithSearchDirectory
            // just adds a second root to probe, and WithAutoFallback ensures CPU still works when
            // the GPU backend can't load (e.g. missing driver).
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

    private static void ConfigureWhisperNetNative(ILogger log)
    {
        try
        {
            // Whisper.net's loader derives its extra search root via Path.GetDirectoryName on
            // RuntimeOptions.LibraryPath. The file itself doesn't have to exist — only the directory
            // part matters. Point it at a placeholder under our Whisper runtime root so the loader
            // probes {WhisperSearchRoot}/runtimes/{runtime}/{os-arch}/whisper.dll in addition to
            // the app bin directory. Default RuntimeLibraryOrder is [Cuda, Cuda12, Vulkan,
            // CoreML, OpenVino, Cpu, CpuNoAvx] so GPU backends beat CPU automatically.
            if (Directory.Exists(GpuRuntimeDownloader.WhisperSearchRoot))
            {
                Whisper.net.LibraryLoader.RuntimeOptions.LibraryPath = Path.Combine(GpuRuntimeDownloader.WhisperSearchRoot, "whisper.placeholder");
                log.LogInformation("Whisper.net RuntimeOptions.LibraryPath points under {Path}", GpuRuntimeDownloader.WhisperSearchRoot);
            }
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Whisper.net RuntimeOptions setup failed (non-fatal; will run with defaults)");
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
