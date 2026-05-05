using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Yaat.Client.UI.Tests;

// Runs when the test assembly loads, before xUnit reflects TestAppBuilder and
// before any YAAT static that reads YaatPaths.AppDataRoot. Redirects every
// path YAAT derives from %LOCALAPPDATA%/yaat to a unique temp folder so tests
// never touch the developer's real prefs, logs, or vNAS cache.
//
// Cleanup: a .yaat-pid file inside each dir records the owning process. The
// sweep at startup deletes any sibling dir whose owning process has exited;
// concurrent test runs are safe because their PIDs are still live. ProcessExit
// also makes a best-effort cleanup pass — but YAAT's logger holds files open
// at exit so the in-process recursive delete sometimes leaves the dir behind;
// the next run's PID-aware sweep then reaps it.
internal static class ModuleInit
{
    private const string RootName = "yaat-ui-tests";
    private const string PidMarker = ".yaat-pid";

    [ModuleInitializer]
    public static void Initialize()
    {
        SweepStaleDirs();

        var testDir = Path.Combine(Path.GetTempPath(), RootName, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDir);
        File.WriteAllText(Path.Combine(testDir, PidMarker), Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        Environment.SetEnvironmentVariable("YAAT_APPDATA_DIR", testDir);

        AppDomain.CurrentDomain.ProcessExit += (_, _) => TryDeleteDir(testDir);
    }

    private static void SweepStaleDirs()
    {
        var rootDir = Path.Combine(Path.GetTempPath(), RootName);
        if (!Directory.Exists(rootDir))
        {
            return;
        }

        try
        {
            foreach (var subdir in Directory.EnumerateDirectories(rootDir))
            {
                if (IsOwnedByLiveProcess(subdir))
                {
                    continue;
                }
                TryDeleteDir(subdir);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static bool IsOwnedByLiveProcess(string dir)
    {
        var pidFile = Path.Combine(dir, PidMarker);
        if (!File.Exists(pidFile))
        {
            return false;
        }

        try
        {
            var pidText = File.ReadAllText(pidFile).Trim();
            if (!int.TryParse(pidText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid))
            {
                return false;
            }

            try
            {
                using var process = Process.GetProcessById(pid);
                return !process.HasExited;
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryDeleteDir(string path)
    {
        // Best-effort delete. YAAT's logger sometimes holds files open at process
        // exit; that's fine — the next run's PID-aware sweep cleans up after
        // we're gone. A concurrently-running test process holds a live PID, so
        // its dir is skipped before reaching this method.
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
