using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using Yaat.Client.Logging;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Testing;

namespace Yaat.Client.UI.Tests;

// Runs when the test assembly loads, before xUnit reflects TestAppBuilder and
// before any YAAT static that reads YaatPaths.AppDataRoot. Redirects every
// path YAAT derives from %LOCALAPPDATA%/yaat to a unique temp folder so tests
// never touch the developer's real prefs, logs, or vNAS cache.
//
// Cleanup: a .yaat-pid file inside each dir records the owning process. The
// sweep at startup deletes any sibling dir whose owning process has exited, and
// any dir that has sat without a usable marker for MarkerlessGraceAge: a run
// creates its dir a moment before the marker, so concurrent test runs are safe.
// ProcessExit also makes a best-effort cleanup pass — but YAAT's logger holds
// files open at exit so the in-process recursive delete sometimes leaves the
// dir behind; the next run's PID-aware sweep then reaps it.
internal static class ModuleInit
{
    private const string RootName = "yaat-ui-tests";
    private const string PidMarker = ".yaat-pid";

    // A run creates its dir before it writes the marker, and the other test assemblies sweep concurrently, so a dir
    // with no usable marker is reaped only once nothing has written it for this long.
    private static readonly TimeSpan MarkerlessGraceAge = TimeSpan.FromHours(1);

    [ModuleInitializer]
    public static void Initialize()
    {
        SweepStaleDirs(Path.Combine(Path.GetTempPath(), RootName));

        string testDir = Path.Combine(Path.GetTempPath(), RootName, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDir);
        File.WriteAllText(Path.Combine(testDir, PidMarker), Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        Environment.SetEnvironmentVariable("YAAT_APPDATA_DIR", testDir);

        // The menu goldens load the committed CIFP through TestVnasData; resolving it offline keeps them pinned to
        // that cycle instead of whatever the FAA publishes on the day of the run.
        Environment.SetEnvironmentVariable("YAAT_SKIP_CIFP_DOWNLOAD", "1");
        ResolveCommittedNavData();

        // Logging is set up once, before any test or YAAT static runs, so every logger a class creates on first use (the
        // automation dispatcher's among them) writes to the per-run log and feeds AppLog.RecentErrors, whatever the order.
        AppLog.Initialize("yaat-ui-tests.log");

        AppDomain.CurrentDomain.ProcessExit += (_, _) => TryDeleteDir(testDir);
    }

    /// <summary>
    /// Resolves NavData and CIFP against the pins committed in <c>tests/Yaat.Sim.Tests/TestData</c> of this checkout before
    /// any test runs; every assembly that links this file reads them there, offline. Both resolvers keep their first answer
    /// for the whole process, so without this a <c>MainViewModel</c>'s background navdata load, which finds no cache in the
    /// per-process app-data folder and may not download, could store a null that the menu goldens'
    /// <see cref="TestVnasData"/> would then read.
    /// </summary>
    private static void ResolveCommittedNavData()
    {
        string testDataDir = Path.Combine(FindRepoRoot(), "tests", "Yaat.Sim.Tests", "TestData");
        TestVnasData.SetTestDataDir(testDataDir);

        string? cifpPath = CifpPathResolver.EnsureCurrentCycle(
            new CifpResolveOptions(
                BundledGzPath: Path.Combine(testDataDir, "FAACIFP18.gz"),
                BundledManifestPath: Path.Combine(testDataDir, "cifp-manifest.json"),
                AllowDownload: false
            )
        );
        string? navDataPath = NavDataPathResolver.EnsureCurrent(
            new NavDataResolveOptions(
                BundledPath: Path.Combine(testDataDir, "NavData.dat"),
                BundledManifestPath: Path.Combine(testDataDir, "navdata-manifest.json"),
                AllowDownload: false
            )
        );

        if ((cifpPath is null) || (navDataPath is null))
        {
            throw new InvalidOperationException(
                $"The committed NavData or CIFP under {testDataDir} could not be resolved (CIFP: {cifpPath ?? "none"}, NavData: "
                    + $"{navDataPath ?? "none"}); restore them with git checkout."
            );
        }

        // A class that reads NavigationDatabase.Instance without installing one gets the real database on its
        // first read, rather than only when an earlier class happened to call TestVnasData.EnsureInitialized().
        NavigationDatabase.SetTestDefaultLoader(() =>
            TestVnasData.LoadRealDataWithoutInstalling()
            ?? throw new InvalidOperationException($"The real navigation database could not be loaded from the resolved NavData at {navDataPath}.")
        );
    }

    /// <summary>
    /// The yaat checkout this test assembly reads from, the first that holds yaat.slnx of: the <c>YaatRoot</c> assembly metadata
    /// (set by a test project built outside yaat; a build on another machine bakes that machine's path); the nearest folder
    /// above the output holding yaat.slnx; the yaat checkout beside, or at <c>extern/yaat</c> inside, the nearest folder above
    /// the output holding yaat-server.slnx.
    /// </summary>
    /// <exception cref="InvalidOperationException">None of those places holds a yaat checkout.</exception>
    internal static string FindRepoRoot()
    {
        string? metadataRoot = typeof(ModuleInit)
            .Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .SingleOrDefault(attribute => attribute.Key == "YaatRoot")
            ?.Value;
        if (!string.IsNullOrEmpty(metadataRoot) && IsYaatCheckout(metadataRoot))
        {
            return metadataRoot;
        }

        return FindYaatAboveOutput()
            ?? throw new InvalidOperationException(
                $"Cannot find the yaat checkout: YaatRoot metadata '{metadataRoot ?? "none"}' has no yaat.slnx, and no yaat.slnx or "
                    + $"yaat-server.slnx (with ../yaat or extern/yaat) is above {AppContext.BaseDirectory}."
            );
    }

    private static string? FindYaatAboveOutput()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (IsYaatCheckout(dir.FullName))
            {
                return dir.FullName;
            }

            if (File.Exists(Path.Combine(dir.FullName, "yaat-server.slnx")))
            {
                string[] candidates = [Path.GetFullPath(Path.Combine(dir.FullName, "..", "yaat")), Path.Combine(dir.FullName, "extern", "yaat")];
                return candidates.FirstOrDefault(IsYaatCheckout);
            }
        }

        return null;
    }

    private static bool IsYaatCheckout(string dir) => File.Exists(Path.Combine(dir, "yaat.slnx"));

    internal static void SweepStaleDirs(string rootDir)
    {
        if (!Directory.Exists(rootDir))
        {
            return;
        }

        try
        {
            foreach (string subdir in Directory.EnumerateDirectories(rootDir))
            {
                if (!IsProvablyStale(subdir))
                {
                    continue;
                }
                TryDeleteDir(subdir);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// True when nothing can still be using <paramref name="dir"/>: its marker names a process that has exited, or it has
    /// no usable marker and nothing has written the folder for <see cref="MarkerlessGraceAge"/>.
    /// </summary>
    private static bool IsProvablyStale(string dir)
    {
        string pidFile = Path.Combine(dir, PidMarker);
        try
        {
            string pidText = File.ReadAllText(pidFile).Trim();
            if (int.TryParse(pidText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid))
            {
                return !IsProcessAlive(pid);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        return (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(dir)) > MarkerlessGraceAge;
    }

    private static bool IsProcessAlive(int pid)
    {
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
