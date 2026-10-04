using Avalonia;
using Avalonia.Headless;
using Velopack;
using Yaat.Client;
using Yaat.Client.UI.Render.Tests;

[assembly: AvaloniaTestApplication(typeof(RenderTestAppBuilder))]

namespace Yaat.Client.UI.Render.Tests;

/// <summary>
/// Hosts the real <see cref="App"/> on Avalonia's headless platform with the Skia renderer, as Yaat.GuideCapture does, so
/// <c>RenderTargetBitmap</c> produces real pixels; Yaat.Client.UI.Tests uses headless drawing, which renders none.
/// </summary>
public static class RenderTestAppBuilder
{
    private static int _velopackInitialized;

    public static AppBuilder BuildAvaloniaApp()
    {
        // App construction reaches UpdateService, which needs the VelopackLocator initialized; Run() is idempotent here.
        if (Interlocked.CompareExchange(ref _velopackInitialized, 1, 0) == 0)
        {
            VelopackApp.Build().Run();
        }

        return AppBuilder
            .Configure<App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseSkia()
            .UseHarfBuzz()
            .WithInterFont();
    }
}
