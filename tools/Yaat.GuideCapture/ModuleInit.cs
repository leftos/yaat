using System.Diagnostics;
using System.Globalization;
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
//
// Cleanup: a .yaat-pid file inside each dir records the owning process. The
// sweep at startup deletes any sibling dir whose owning process has exited;
// concurrent runs are safe because their PIDs are still live. ProcessExit also
// makes a best-effort cleanup pass — but YAAT's logger holds files open at
// exit so the in-process recursive delete sometimes leaves the dir behind;
// the next run's PID-aware sweep then reaps it. The vNAS cache that lands in
// this dir grows to multiple GB per run, so capturing that on every run is
// what made this leak so painful before.
internal static class ModuleInit
{
    private const string RootName = "yaat-guide-capture";
    private const string PidMarker = ".yaat-pid";

    [ModuleInitializer]
    public static void Initialize()
    {
        SweepStaleDirs();

        var dir = Path.Combine(Path.GetTempPath(), RootName, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, PidMarker), Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        Environment.SetEnvironmentVariable("YAAT_APPDATA_DIR", dir);

        var prefsPath = Path.Combine(dir, "preferences.json");
        File.WriteAllText(
            prefsPath,
            """
            {
              "vatsimCid": "9999999",
              "userInitials": "1M",
              "artccId": "ZOA",
              "assignmentTintEnabled": true,
              "assignmentTintColor": "#0080FF"
            }
            """
        );

        AppDomain.CurrentDomain.ProcessExit += (_, _) => TryDeleteDir(dir);
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
        // we're gone. A concurrently-running capture process holds a live PID,
        // so its dir is skipped before reaching this method.
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
