using Avalonia;
using Avalonia.Headless;
using Velopack;
using Yaat.Client;
using Yaat.Client.UI.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace Yaat.Client.UI.Tests;

// Hosts the real Yaat.Client.App under a headless Avalonia platform so UI tests run
// in CI without a display/GPU. Reuses Program.BuildAvaloniaApp? No: Program's builder
// calls UsePlatformDetect() which would try to start a real windowing subsystem.
// Instead we replicate the same App+fonts config and swap UsePlatformDetect for
// UseHeadless. UseHeadlessDrawing=true picks the software renderer so Window.Show()
// and custom draw ops work without SkiaSharp GPU backends.
public static class TestAppBuilder
{
    private static int _velopackInitialized;

    public static AppBuilder BuildAvaloniaApp()
    {
        // MainViewModel constructs UpdateService eagerly, which requires the
        // VelopackLocator to be initialized. VelopackApp.Build().Run() is idempotent
        // in test context (no install hook args), so doing it here lets the real VM
        // construct without patching production code.
        if (Interlocked.CompareExchange(ref _velopackInitialized, 1, 0) == 0)
        {
            VelopackApp.Build().Run();
        }

        return AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true }).WithInterFont();
    }
}
