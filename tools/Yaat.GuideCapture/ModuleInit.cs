using System.Runtime.CompilerServices;

namespace Yaat.GuideCapture;

// Runs before any Yaat.Client static reads YaatPaths.AppDataRoot. Redirects
// every per-user path YAAT derives from %LOCALAPPDATA%/yaat to a unique temp
// folder so the capture run never reads the developer's real preferences,
// logs, or vNAS cache. Same pattern as tests/Yaat.Client.UI.Tests/ModuleInit.cs.
//
// Also seeds a minimal preferences.json so MainViewModel.AttemptConnectAsync
// passes its identity validation gates (UserInitials, VatsimCid, ArtccId all
// required before a connection attempt is made).
internal static class ModuleInit
{
    [ModuleInitializer]
    public static void Initialize()
    {
        var dir = Path.Combine(Path.GetTempPath(), "yaat-guide-capture", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable("YAAT_APPDATA_DIR", dir);

        var prefsPath = Path.Combine(dir, "preferences.json");
        File.WriteAllText(
            prefsPath,
            """
            {
              "vatsimCid": "9999999",
              "userInitials": "GC",
              "artccId": "ZOA"
            }
            """
        );
    }
}
