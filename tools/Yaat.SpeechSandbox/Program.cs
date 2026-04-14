using Avalonia;
using LMKit.Licensing;

namespace Yaat.SpeechSandbox;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // LM-Kit Community Edition setup — empty key signals community tier. Mirrors the
        // production main app's startup. LM-Kit owns backend selection at model load time
        // (CUDA / Vulkan / CPU) via the LM-Kit.NET.Backend.* packages, so there is no
        // NativeLibraryConfig dance to perform.
        try
        {
            LicenseManager.SetLicenseKey("");
        }
        catch
        {
            // Already initialized — swallow.
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
