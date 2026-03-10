using System.Text.Json;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim.Tests;

/// <summary>
/// Loads AircraftSpecs.json, AircraftCwt.json, and FaaAcd.json from TestData/ and initializes
/// <see cref="AircraftCategorization"/>, <see cref="WakeTurbulenceData"/>,
/// and <see cref="AircraftApproachSpeed"/>. Thread-safe; only initializes once per process.
///
/// Call <see cref="EnsureInitialized"/> at the top of any test that needs
/// accurate aircraft data. Safe to call multiple times.
/// </summary>
internal static class TestVnasData
{
    private const string TestDataDir = "TestData";
    private static bool _initialized;
    private static readonly object _lock = new();

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
        var lookup = JsonSerializer.Deserialize<Dictionary<string, int>>(json);
        if (lookup is null)
        {
            return;
        }

        AircraftApproachSpeed.Initialize(lookup);
    }
}
