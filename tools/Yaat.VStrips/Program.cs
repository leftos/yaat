using System.Runtime.InteropServices;
using Avalonia;
using Velopack;
using Yaat.Client;
using Yaat.Client.Logging;
using Yaat.Sim;

namespace Yaat.VStrips;

/// <summary>
/// Entry point for the standalone vStrips app. Students run this alongside CRC
/// during training — it connects to yaat-server via SignalR, joins a room, and
/// renders flight-strip bays with the same drag/drop and shortcut surface as the
/// embedded Strips tab in the full Yaat.Client trainer.
///
/// No LM-Kit, no Whisper, no speech pipeline — the standalone references only
/// <c>Yaat.Client.Core</c> and ships a ~30 MB binary instead of the full
/// trainer's ~200+ MB footprint. Velopack is wired up so the installer built
/// from this project can self-update independently of the main trainer
/// (separate install dir, separate appdata, separate Velopack channel).
/// </summary>
public static class Program
{
    /// <summary>
    /// Per-platform Velopack channel used when packing and update-checking. Must match
    /// the <c>--channel</c> argument passed to <c>vpk pack</c> in the release workflow.
    /// </summary>
    public static string VStripsChannel
    {
        get
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return "vstrips-win";
            }
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return "vstrips-osx";
            }
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                return "vstrips-linux";
            }
            throw new PlatformNotSupportedException();
        }
    }

    [STAThread]
    public static int Main(string[] args)
    {
        VelopackApp.Build().Run();

        YaatPaths.Initialize("yaat-vstrips");
        AppLog.Initialize("yaat-vstrips.log");

        int autoIdx = Array.FindIndex(args, a => a.Equals("--autoconnect", StringComparison.OrdinalIgnoreCase));
        if (autoIdx >= 0 && autoIdx + 1 < args.Length)
        {
            App.AutoConnectTarget = args[autoIdx + 1];
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().WithJetBrainsMonoFont().LogToTrace();
}
