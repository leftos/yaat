using System.Runtime.InteropServices;
using Avalonia;
using LMKit.Global;
using Microsoft.Extensions.Logging;
using Velopack;
using Yaat.Client.Logging;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Sim;

namespace Yaat.Client;

public static class Program
{
    // Resolved from UserPreferences in Main before the Avalonia app is built, and consumed by
    // BuildAvaloniaApp to select the macOS rendering backend. Defaults to Auto so the Avalonia
    // designer (which calls BuildAvaloniaApp without running Main) gets the platform default.
    private static RendererMode _rendererMode = RendererMode.Auto;

    [STAThread]
    public static void Main(string[] args)
    {
        var builder = VelopackApp.Build();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            builder.OnAfterInstallFastCallback(v => CrcInstallPrompt.Show());
        }

        builder.Run();

        YaatPaths.Initialize("yaat");
        AppLog.Initialize("yaat-client.log");
        var log = AppLog.CreateLogger("Program");
        log.LogInformation("{BuildSummary}", BuildInfo.LogSummary);
        log.LogInformation("Log file: {LogPath}", AppLog.LogPath);

        // Read the rendering-backend preference before Avalonia is built — RenderingMode must be set
        // at AppBuilder time. On macOS this lets us default to Metal (avoiding the CPU-heavy
        // OpenGL-over-Metal emulation on Apple Silicon) while letting users fall back if it misbehaves.
        try
        {
            _rendererMode = new UserPreferences().RendererMode;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Failed to read renderer preference; using platform default");
        }

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

        // If the user opted into CUDA acceleration via Settings → Speech, CudaBackendInstaller
        // has dropped ~700 MB of CUDA 13 DLLs under %LOCALAPPDATA%/yaat/backends/cuda13/.
        // BackendDirectory must be set before Runtime.Initialize() runs — which happens lazily
        // on first LM-Kit touch, including LmKitLicense.Initialize() below on some paths — so
        // wire it here. Without a CUDA install, we leave BackendDirectory unset and LM-Kit
        // auto-selects Vulkan (included in the base package) or CPU.
        if (CudaBackendInstaller.IsInstalledOnDisk())
        {
            Runtime.BackendDirectory = CudaBackendInstaller.InstallRoot;
            log.LogInformation("CUDA backend directory set to {Path}", CudaBackendInstaller.InstallRoot);
        }

        // LM-Kit licensing must run before any LM construction so the licensing layer is
        // initialized. The helper resolves the key from LMKIT_LICENSE_KEY or the solution-root
        // .env file, falling back to empty string (Community Edition). LM-Kit picks the backend
        // (CUDA / Vulkan / CPU) at model load time based on the NuGet packages present plus the
        // BackendDirectory override set above.
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
        var builder = AppBuilder.Configure<App>().UsePlatformDetect();

        // macOS is the only backend where the renderer choice matters: the default OpenGL path is
        // emulated over Metal and saturates a CPU core on Apple Silicon. Windows and Linux keep
        // Avalonia's auto-detected backend.
        if (OperatingSystem.IsMacOS())
        {
            builder = builder.With(new AvaloniaNativePlatformOptions { RenderingMode = ResolveMacRenderingModes(_rendererMode) });
        }

        return builder.WithInterFont().LogToTrace();
    }

    private static IReadOnlyList<AvaloniaNativeRenderingMode> ResolveMacRenderingModes(RendererMode mode) =>
        mode switch
        {
            RendererMode.OpenGl => [AvaloniaNativeRenderingMode.OpenGl, AvaloniaNativeRenderingMode.Software],
            RendererMode.Software => [AvaloniaNativeRenderingMode.Software],
            // Auto and Metal both prefer Metal, falling back to OpenGL then software if it fails to init.
            _ => [AvaloniaNativeRenderingMode.Metal, AvaloniaNativeRenderingMode.OpenGl, AvaloniaNativeRenderingMode.Software],
        };
}
