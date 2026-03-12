using System.Text.Json;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Proto;

namespace Yaat.Sim.Tests;

/// <summary>
/// Loads AircraftSpecs.json, AircraftCwt.json, FaaAcd.json, and NavData.dat from TestData/ and
/// initializes <see cref="AircraftCategorization"/>, <see cref="WakeTurbulenceData"/>,
/// <see cref="AircraftApproachSpeed"/>, and <see cref="FixDatabase"/>. Thread-safe; only initializes once per process.
///
/// Call <see cref="EnsureInitialized"/> at the top of any test that needs
/// accurate aircraft data. Safe to call multiple times.
/// Use <see cref="FixDatabase"/> for tests that require real nav data (fixes, runways).
/// </summary>
internal static class TestVnasData
{
    private const string TestDataDir = "TestData";
    private static bool _initialized;
    private static readonly object _lock = new();

    private static FixDatabase? _fixDatabase;
    private static ProcedureDatabase? _procedureDatabase;
    private static bool _procedureDbAttempted;

    /// <summary>
    /// Returns a <see cref="FixDatabase"/> loaded from NavData.dat, or null if the file is not present.
    /// Loads lazily and caches for the process lifetime.
    /// </summary>
    internal static FixDatabase? FixDatabase
    {
        get
        {
            if (_fixDatabase is not null)
            {
                return _fixDatabase;
            }

            var path = Path.Combine(TestDataDir, "NavData.dat");
            if (!File.Exists(path))
            {
                return null;
            }

            var bytes = File.ReadAllBytes(path);
            var navData = NavDataSet.Parser.ParseFrom(bytes);
            _fixDatabase = new FixDatabase(navData);
            return _fixDatabase;
        }
    }

    /// <summary>
    /// Returns a <see cref="ProcedureDatabase"/> backed by the system CIFP cache
    /// (%LOCALAPPDATA%/yaat/cache/cifp/), or null if no CIFP file is cached.
    /// Loads lazily and caches for the process lifetime.
    /// </summary>
    internal static ProcedureDatabase? ProcedureDatabase
    {
        get
        {
            if (_procedureDatabase is not null)
            {
                return _procedureDatabase;
            }

            if (_procedureDbAttempted)
            {
                return null;
            }

            _procedureDbAttempted = true;

            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var cacheDir = Path.Combine(localAppData, "yaat", "cache", "cifp");
            if (!Directory.Exists(cacheDir))
            {
                return null;
            }

            var cifpFile = Directory.EnumerateFiles(cacheDir, "FAACIFP18-*").OrderDescending().FirstOrDefault();
            if (cifpFile is null)
            {
                return null;
            }

            _procedureDatabase = new ProcedureDatabase(cifpFile);
            return _procedureDatabase;
        }
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

        // New format: full records keyed by ICAO code
        var records = JsonSerializer.Deserialize<Dictionary<string, FaaAircraftRecord>>(
            json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
        );
        if (records is { Count: > 0 })
        {
            FaaAircraftDatabase.Initialize(records);

            var approachSpeeds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var (icao, record) in records)
            {
                if (record.ApproachSpeedKnot is { } speed)
                {
                    approachSpeeds[icao] = speed;
                }
            }

            AircraftApproachSpeed.Initialize(approachSpeeds);
            return;
        }

        // Legacy format: flat approach speed lookup
        var legacy = JsonSerializer.Deserialize<Dictionary<string, int>>(json);
        if (legacy is not null)
        {
            AircraftApproachSpeed.Initialize(legacy);
        }
    }
}
