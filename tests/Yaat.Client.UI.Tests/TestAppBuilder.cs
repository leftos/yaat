using Avalonia;
using Avalonia.Headless;
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
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true }).WithInterFont();
}
