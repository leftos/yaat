using Avalonia;
using Avalonia.Headless;
using Velopack;
using Yaat.Client;
using Yaat.ClientDriver.Mcp.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace Yaat.ClientDriver.Mcp.Tests;

/// <summary>
/// Hosts the real <see cref="App"/> under a headless Avalonia platform so these tests run without a display, as
/// Yaat.Client.UI.Tests does: <c>UseHeadlessDrawing = true</c> picks the software renderer, so showing a window and
/// listing it over the pipe need no GPU.
/// </summary>
public static class TestAppBuilder
{
    private static int _velopackInitialized;

    public static AppBuilder BuildAvaloniaApp()
    {
        // App construction reaches UpdateService, which needs the VelopackLocator initialized; Run() is idempotent here.
        if (Interlocked.CompareExchange(ref _velopackInitialized, 1, 0) == 0)
        {
            VelopackApp.Build().Run();
        }

        return AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true }).WithInterFont();
    }
}
