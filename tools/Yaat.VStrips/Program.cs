using Avalonia;
using Velopack;
using Yaat.Client.Services;

namespace Yaat.VStrips;

/// <summary>
/// Entry point for the standalone vStrips app. Students run this alongside CRC and
/// their browser/radar client during training. The app is a thin shell over
/// <see cref="Yaat.Client.ViewModels.MainViewModel"/> + <see cref="Yaat.Client.ViewModels.VStripsViewModel"/>:
/// it connects to yaat-server via SignalR, joins a training room, and renders the
/// same strip bays the embedded tab inside Yaat.Client does — drag/drop, shortcuts,
/// and canonical-command dispatch all reuse the main app's code.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Velopack locator bootstrap — Yaat.Client's UpdateService constructor (called
        // from MainViewModel..ctor) instantiates a Velopack UpdateManager which resolves
        // VelopackLocator.Current. Without VelopackApp.Build().Run() the standalone
        // crashes with "No VelopackLocator has been set". Mirrors Yaat.Client's
        // Program.Main so dev and installed builds behave identically.
        VelopackApp.Build().Run();

        // LM-Kit license bootstrap runs here for parity with Yaat.Client — MainViewModel
        // constructs the speech pipeline unconditionally (prewarm is gated on the prefs
        // flag). Without a valid key the standalone falls back to Community Edition,
        // which is fine for the vStrips path since speech is idle in this host.
        LmKitLicense.Initialize();

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
