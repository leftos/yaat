using System.Runtime.CompilerServices;

namespace Yaat.GuideCapture;

// Runs before any Yaat.Client static reads YaatPaths.AppDataRoot. Redirects
// every per-user path YAAT derives from %LOCALAPPDATA%/yaat to a unique temp
// folder so the capture run never reads the developer's real preferences,
// logs, or vNAS cache. Identical pattern to tests/Yaat.Client.UI.Tests/ModuleInit.cs.
internal static class ModuleInit
{
    [ModuleInitializer]
    public static void Initialize()
    {
        var dir = Path.Combine(Path.GetTempPath(), "yaat-guide-capture", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable("YAAT_APPDATA_DIR", dir);
    }
}
