using Avalonia;
using Velopack;
using Yaat.Client.Logging;

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
/// from this project can self-update independently of the main trainer.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        VelopackApp.Build().Run();

        AppLog.Initialize();

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
