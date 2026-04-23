using System.Runtime.CompilerServices;

namespace Yaat.Client.UI.Tests;

// Runs when the test assembly loads, before xUnit reflects TestAppBuilder and
// before any YAAT static that reads YaatPaths.AppDataRoot. Redirects every
// path YAAT derives from %LOCALAPPDATA%/yaat to a unique temp folder so tests
// never touch the developer's real prefs, logs, or vNAS cache.
//
// The folder is not cleaned up on exit — inspect .tmp/... after a run if a
// test misbehaves. Each test process gets a fresh Guid-keyed dir so stale
// state cannot leak between runs.
internal static class ModuleInit
{
    [ModuleInitializer]
    public static void Initialize()
    {
        var testDir = Path.Combine(Path.GetTempPath(), "yaat-ui-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDir);
        Environment.SetEnvironmentVariable("YAAT_APPDATA_DIR", testDir);
    }
}
