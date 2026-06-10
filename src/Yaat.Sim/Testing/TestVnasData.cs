using System.Text.Json;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Proto;

namespace Yaat.Sim.Testing;

/// <summary>
/// Loads AircraftSpecs.json, AircraftCwt.json, FaaAcd.json, AircraftProfiles.json, and NavData.dat
/// and initializes <see cref="AircraftCategorization"/>, <see cref="WakeTurbulenceData"/>,
/// <see cref="AircraftProfileDatabase"/>, and <see cref="NavigationDatabase"/>. Thread-safe; only initializes once per process.
///
/// Call <see cref="EnsureInitialized"/> at the top of any test that needs
/// accurate aircraft data. Safe to call multiple times.
/// Use <see cref="NavigationDb"/> for tests that require real nav data (fixes, runways, approaches, procedures).
/// </summary>
public static class TestVnasData
{
    private static string _testDataDir = Path.Combine(AppContext.BaseDirectory, "TestData");
    private static bool _initialized;
    private static readonly object _lock = new();

    private static NavigationDatabase? _navigationDatabase;
    private static string? _cifpPath;
    private static bool _procedureDbAttempted;
    private static bool _exitHandlerRegistered;
    private static FileStream? _cifpSentinel;

    /// <summary>
    /// Sets the directory containing test data files (NavData.dat, FAACIFP18.gz, etc.).
    /// Must be called before <see cref="NavigationDb"/> or <see cref="EnsureInitialized"/>.
    /// </summary>
    public static void SetTestDataDir(string path)
    {
        _testDataDir = path;
    }

    /// <summary>
    /// Returns a <see cref="NavigationDatabase"/> loaded from NavData.dat (and optionally CIFP),
    /// or null if NavData.dat is not present. Loads lazily and caches for the process lifetime.
    /// Thread-safe: uses double-check locking to avoid publishing a partially-initialized
    /// instance (without CIFP) to concurrent test classes.
    /// </summary>
    public static NavigationDatabase? NavigationDb
    {
        get
        {
            if (_navigationDatabase is not null)
            {
                return _navigationDatabase;
            }

            lock (_lock)
            {
                if (_navigationDatabase is not null)
                {
                    return _navigationDatabase;
                }

                var path = ResolveNavDataPath();
                if (path is null || !File.Exists(path))
                {
                    return null;
                }

                var bytes = File.ReadAllBytes(path);
                var navData = NavDataSet.Parser.ParseFrom(bytes);

                var cifpPath = ResolveCifpPath();
                if (cifpPath is null)
                {
                    return null;
                }

                var bundledGz = Path.Combine(_testDataDir, "FAACIFP18.gz");
                string? supplementaryCifp = null;
                if (File.Exists(bundledGz))
                {
                    supplementaryCifp = CifpPathResolver.ResolveSupplementaryBundledPath(new CifpResolveOptions(BundledGzPath: bundledGz));
                }

                // Use the default artccsBaseDir (null) so NavigationDatabase loads bundled
                // per-ARTCC data from AppContext.BaseDirectory/Data/ARTCCs — the Yaat.Sim csproj
                // copies that folder into the test output directory. This gives tests access to
                // OAK30NUM, TOLLPLAZA, etc. which are needed when CommandParser.Parse resolves
                // DCT args (e.g. PhraseologyMapper's rule validator).
                _navigationDatabase = new NavigationDatabase(navData, cifpPath, supplementaryCifpFilePath: supplementaryCifp);
                return _navigationDatabase;
            }
        }
    }

    /// <summary>
    /// Returns the path to a decompressed FAACIFP18 file for tests, or null if no source CIFP is available.
    /// Decompresses the bundled <c>TestData/FAACIFP18.gz</c> on first call and caches the resulting path
    /// for the lifetime of the test process. Falls back to the system CIFP cache if no bundled gz exists.
    /// Thread-safe; safe to call from any test setup.
    /// </summary>
    public static string? GetCifpPath()
    {
        if (_procedureDbAttempted)
        {
            return _cifpPath;
        }

        lock (_lock)
        {
            return ResolveCifpPath();
        }
    }

    private static string? ResolveNavDataPath()
    {
        var allowDownload = !IsNavDataDownloadSkipped();
        return NavDataPathResolver.CachedPath
            ?? NavDataPathResolver.EnsureCurrent(
                new NavDataResolveOptions(
                    BundledPath: Path.Combine(_testDataDir, "NavData.dat"),
                    BundledManifestPath: Path.Combine(_testDataDir, "navdata-manifest.json"),
                    AllowDownload: allowDownload
                )
            );
    }

    private static bool IsNavDataDownloadSkipped()
    {
        var v = Environment.GetEnvironmentVariable("YAAT_SKIP_NAVDATA_DOWNLOAD");
        return string.Equals(v, "1", StringComparison.Ordinal) || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ResolveCifpPath()
    {
        if (_procedureDbAttempted)
        {
            return _cifpPath;
        }

        _procedureDbAttempted = true;

        var allowDownload = !IsDownloadSkipped();
        _cifpPath =
            CifpPathResolver.CachedPath
            ?? CifpPathResolver.EnsureCurrentCycle(
                new CifpResolveOptions(
                    BundledGzPath: Path.Combine(_testDataDir, "FAACIFP18.gz"),
                    BundledManifestPath: Path.Combine(_testDataDir, "cifp-manifest.json"),
                    AllowDownload: allowDownload
                )
            );

        return _cifpPath;
    }

    private static bool IsDownloadSkipped()
    {
        var v = Environment.GetEnvironmentVariable("YAAT_SKIP_CIFP_DOWNLOAD");
        return string.Equals(v, "1", StringComparison.Ordinal) || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static string DecompressGzip(string gzPath)
    {
        SweepStaleCifpTempFiles();

        var decompressedPath = Path.Combine(Path.GetTempPath(), $"yaat-test-FAACIFP18-{Environment.ProcessId}");

        if (!File.Exists(decompressedPath))
        {
            using var inputStream = File.OpenRead(gzPath);
            using var gzipStream = new System.IO.Compression.GZipStream(inputStream, System.IO.Compression.CompressionMode.Decompress);
            using var outputStream = File.Create(decompressedPath);
            gzipStream.CopyTo(outputStream);
        }

        // Hold a read-only sentinel handle WITHOUT FileShare.Delete for the test
        // process's lifetime. This blocks any other concurrent test process's sweep
        // from deleting our live file (Windows refuses File.Delete on a handle that
        // wasn't opened with FileShare.Delete). NavigationDatabase opens the file
        // independently with FileShare.Read, which is compatible. On clean process
        // exit we dispose the handle and delete the file. On a hard kill the OS
        // releases the handle and the next sweep picks it up.
        _cifpSentinel ??= new FileStream(decompressedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        RegisterCleanupOnExit(decompressedPath);

        return decompressedPath;
    }

    private static void RegisterCleanupOnExit(string decompressedPath)
    {
        if (_exitHandlerRegistered)
        {
            return;
        }

        _exitHandlerRegistered = true;
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            _cifpSentinel?.Dispose();
            _cifpSentinel = null;
            TryDelete(decompressedPath);
        };
    }

    private static void SweepStaleCifpTempFiles()
    {
        // Clean up files leaked by prior test processes that crashed or were killed before
        // ProcessExit could fire. Patterns: the current yaat-prefixed name plus four legacy
        // patterns from sites that used to have their own DecompressGzip helpers. All
        // exclusively yaat-owned, so this won't touch unrelated files.
        var tempDir = Path.GetTempPath();
        string[] patterns =
        [
            "yaat-test-FAACIFP18-*",
            "FAACIFP18-test-*",
            "FAACIFP18-fp-test-*",
            "FAACIFP18-customfix-test",
            "FAACIFP18-taxiroutes-test",
        ];

        foreach (var pattern in patterns)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(tempDir, pattern))
                {
                    TryDelete(file);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void TryDelete(string path)
    {
        // Files held open by a concurrently-running test process will fail to delete on
        // Windows; that's fine — the owning process will clean its own file on exit, or
        // a later sweep will pick it up.
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static void EnsureInitialized()
    {
        if (!_initialized)
        {
            lock (_lock)
            {
                if (!_initialized)
                {
                    LoadAircraftSpecs();
                    LoadAircraftCwt();
                    LoadFaaAcd();
                    LoadAircraftProfiles();
                    _initialized = true;
                }
            }
        }

        // Always re-set the NavigationDatabase singleton. Other tests (e.g. parser tests)
        // may have replaced it with a synthetic ForTesting() instance.
        if (NavigationDb is { } navDb)
        {
            NavigationDatabase.SetInstance(navDb);
        }
    }

    private static void LoadAircraftSpecs()
    {
        var path = Path.Combine(_testDataDir, "AircraftSpecs.json");
        if (!File.Exists(path))
        {
            return;
        }

        var json = File.ReadAllText(path);
        var specs = JsonSerializer.Deserialize<List<AircraftSpecEntry>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (specs is null)
        {
            return;
        }

        var catLookup = new Dictionary<string, AircraftCategory>(StringComparer.OrdinalIgnoreCase);

        foreach (var spec in specs)
        {
            if (string.IsNullOrEmpty(spec.Designator))
            {
                continue;
            }

            AircraftCategory cat;
            if (spec.AircraftDescription.Equals("Helicopter", StringComparison.OrdinalIgnoreCase))
            {
                cat = AircraftCategory.Helicopter;
            }
            else
            {
                cat = spec.EngineType switch
                {
                    "Piston" => AircraftCategory.Piston,
                    "Turboprop" or "Turboprop/Turboshaft" => AircraftCategory.Turboprop,
                    "Jet" => AircraftCategory.Jet,
                    _ => AircraftCategory.Jet,
                };
            }

            catLookup.TryAdd(spec.Designator, cat);
        }

        AircraftCategorization.Initialize(catLookup);
    }

    private static void LoadAircraftCwt()
    {
        var path = Path.Combine(_testDataDir, "AircraftCwt.json");
        if (!File.Exists(path))
        {
            return;
        }

        var json = File.ReadAllText(path);
        var entries = JsonSerializer.Deserialize<List<AircraftCwtEntry>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (entries is null)
        {
            return;
        }

        var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (!string.IsNullOrEmpty(entry.TypeCode) && !string.IsNullOrEmpty(entry.CwtCode))
            {
                lookup.TryAdd(entry.TypeCode, entry.CwtCode);
            }
        }

        WakeTurbulenceData.Initialize(lookup);
    }

    private static void LoadFaaAcd()
    {
        var path = Path.Combine(_testDataDir, "FaaAcd.json");
        if (!File.Exists(path))
        {
            return;
        }

        var json = File.ReadAllText(path);

        var records = JsonSerializer.Deserialize<Dictionary<string, FaaAircraftRecord>>(
            json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
        );
        if (records is { Count: > 0 })
        {
            FaaAircraftDatabase.Initialize(records);
        }
    }

    private static void LoadAircraftProfiles()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Data", "AircraftProfiles.json");
        if (!File.Exists(path))
        {
            return;
        }

        // Sibling map first: profile-override merge resolves no-base types via the sibling map
        // and the category baseline (categorization is already loaded by LoadAircraftSpecs).
        var siblingPath = Path.Combine(AppContext.BaseDirectory, "Data", "AircraftProfileSiblings.json");
        if (File.Exists(siblingPath))
        {
            var siblings = AircraftSiblingMap.LoadFromFile(siblingPath);
            AircraftSiblingMap.Initialize(siblings);
        }

        var profiles = AircraftProfileDatabase.LoadFromFile(path);
        var overridePath = Path.Combine(AppContext.BaseDirectory, "Data", "AircraftProfileOverrides.json");
        var overrides = AircraftProfileDatabase.LoadOverridesFromFile(overridePath);
        AircraftProfileDatabase.Initialize(profiles, overrides);

        AircraftPerformance.SetProfileCorrectionAdapter(new OverrideAwareProfileCorrectionAdapter(new EurocontrolProfileCorrectionAdapter()));
    }
}
