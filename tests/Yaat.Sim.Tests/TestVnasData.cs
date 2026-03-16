using System.Text.Json;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Proto;

namespace Yaat.Sim.Tests;

/// <summary>
/// Loads AircraftSpecs.json, AircraftCwt.json, FaaAcd.json, AircraftProfiles.json, and NavData.dat
/// and initializes <see cref="AircraftCategorization"/>, <see cref="WakeTurbulenceData"/>,
/// <see cref="AircraftProfileDatabase"/>, and <see cref="NavigationDatabase"/>. Thread-safe; only initializes once per process.
///
/// Call <see cref="EnsureInitialized"/> at the top of any test that needs
/// accurate aircraft data. Safe to call multiple times.
/// Use <see cref="NavigationDb"/> for tests that require real nav data (fixes, runways, approaches, procedures).
/// </summary>
internal static class TestVnasData
{
    private const string TestDataDir = "TestData";
    private static bool _initialized;
    private static readonly object _lock = new();

    private static NavigationDatabase? _navigationDatabase;
    private static string? _cifpPath;
    private static bool _procedureDbAttempted;

    /// <summary>
    /// Returns a <see cref="NavigationDatabase"/> loaded from NavData.dat (and optionally CIFP),
    /// or null if NavData.dat is not present. Loads lazily and caches for the process lifetime.
    /// Thread-safe: uses double-check locking to avoid publishing a partially-initialized
    /// instance (without CIFP) to concurrent test classes.
    /// </summary>
    internal static NavigationDatabase? NavigationDb
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

                var path = Path.Combine(TestDataDir, "NavData.dat");
                if (!File.Exists(path))
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

                _navigationDatabase = new NavigationDatabase(navData, cifpPath, customFixesBaseDir: "");
                return _navigationDatabase;
            }
        }
    }

    private static string? ResolveCifpPath()
    {
        if (_procedureDbAttempted)
        {
            // Already resolved — return cached path (may be null if CIFP not found)
            return _cifpPath;
        }

        _procedureDbAttempted = true;

        // Try bundled gzipped CIFP in TestData first (works in CI)
        var bundledGz = Path.Combine(TestDataDir, "FAACIFP18.gz");
        if (File.Exists(bundledGz))
        {
            _cifpPath = DecompressGzip(bundledGz);
            return _cifpPath;
        }

        // Fall back to system CIFP cache
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var cacheDir = Path.Combine(localAppData, "yaat", "cache", "cifp");
        if (!Directory.Exists(cacheDir))
        {
            return null;
        }

        var cifpFile = Directory.EnumerateFiles(cacheDir, "FAACIFP18-*").OrderDescending().FirstOrDefault();
        _cifpPath = cifpFile;
        return _cifpPath;
    }

    private static string DecompressGzip(string gzPath)
    {
        var decompressedPath = Path.Combine(Path.GetTempPath(), "FAACIFP18-test");
        using var inputStream = File.OpenRead(gzPath);
        using var gzipStream = new System.IO.Compression.GZipStream(inputStream, System.IO.Compression.CompressionMode.Decompress);
        using var outputStream = File.Create(decompressedPath);
        gzipStream.CopyTo(outputStream);
        return decompressedPath;
    }

    internal static void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        lock (_lock)
        {
            if (_initialized)
            {
                return;
            }

            LoadAircraftSpecs();
            LoadAircraftCwt();
            LoadFaaAcd();
            LoadAircraftProfiles();
            _initialized = true;
        }
    }

    private static void LoadAircraftSpecs()
    {
        var path = Path.Combine(TestDataDir, "AircraftSpecs.json");
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
        var path = Path.Combine(TestDataDir, "AircraftCwt.json");
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
        var path = Path.Combine(TestDataDir, "FaaAcd.json");
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

        var profiles = AircraftProfileDatabase.LoadFromFile(path);
        AircraftProfileDatabase.Initialize(profiles);
    }
}
