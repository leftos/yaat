using System.Text.Json;
using Yaat.Sim.ControllerAi.Knowledge;
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
    private static readonly Lock _lock = new();

    private static NavigationDatabase? _navigationDatabase;
    private static string? _cifpPath;
    private static bool _procedureDbAttempted;

    /// <summary>
    /// Sets the directory containing test data files (NavData.dat, FAACIFP18.gz, etc.).
    /// Must be called before <see cref="NavigationDb"/> or <see cref="EnsureInitialized"/>.
    /// </summary>
    public static void SetTestDataDir(string path) => _testDataDir = path;

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

                string? path = ResolveNavDataPath();
                if (path is null || !File.Exists(path))
                {
                    return null;
                }

                byte[] bytes = File.ReadAllBytes(path);
                NavDataSet navData = NavDataSet.Parser.ParseFrom(bytes);

                string? cifpPath = ResolveCifpPath();
                if (cifpPath is null)
                {
                    return null;
                }

                string bundledGz = Path.Combine(_testDataDir, "FAACIFP18.gz");
                string? supplementaryCifp = null;
                if (File.Exists(bundledGz))
                {
                    supplementaryCifp = CifpPathResolver.ResolveSupplementaryBundledPath(PinnedCifpOptions(_testDataDir));
                }

                // Use the default artccsBaseDir (null) so NavigationDatabase loads bundled
                // per-ARTCC data from AppContext.BaseDirectory/Data/ARTCCs — the Yaat.Sim csproj
                // copies that folder into the test output directory. This gives tests access to
                // OAK30NUM, TOLLPLAZA, etc. which are needed when CommandParser.Parse resolves
                // DCT args (e.g. PhraseologyMapper's rule validator).
                _navigationDatabase = new NavigationDatabase(
                    navData,
                    cifpPath,
                    supplementaryCifpFilePaths: supplementaryCifp is null ? null : [supplementaryCifp]
                );
                return _navigationDatabase;
            }
        }
    }

    /// <summary>
    /// Returns the path to the decompressed committed FAACIFP18 bundle for tests. Resolves once per process and
    /// caches the resulting path: the committed <c>TestData/FAACIFP18.gz</c> pinned through its manifest, never the
    /// user cache and never the network. Thread-safe; safe to call from any test setup.
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
        if (NavDataPathResolver.CachedPath is { } alreadyResolved)
        {
            return alreadyResolved;
        }

        // Tests pin the committed copy: never the user cache, never the network. A developer whose
        // cache holds another serial must not change what the suite runs. Test assemblies without
        // their own ModuleInitializer (yaat-server's) reach NavData through here.
        return NavDataPathResolver.EnsureCurrent(PinnedNavDataOptions(_testDataDir));
    }

    private static string? ResolveCifpPath()
    {
        if (_procedureDbAttempted)
        {
            return _cifpPath;
        }

        _procedureDbAttempted = true;

        _cifpPath = CifpPathResolver.CachedPath ?? CifpPathResolver.EnsureCurrentCycle(PinnedCifpOptions(_testDataDir));

        return _cifpPath;
    }

    /// <summary>
    /// The pinned NavData options: the committed copy and manifest under <paramref name="testDataDir"/> alone, never the
    /// user cache and never the network.
    /// </summary>
    /// <param name="testDataDir">The folder holding <c>NavData.dat</c> and <c>navdata-manifest.json</c>.</param>
    public static NavDataResolveOptions PinnedNavDataOptions(string testDataDir) =>
        new(
            BundledPath: Path.Combine(testDataDir, "NavData.dat"),
            BundledManifestPath: Path.Combine(testDataDir, "navdata-manifest.json"),
            AllowDownload: false
        )
        {
            PinToBundled = true,
        };

    /// <summary>
    /// The pinned CIFP options: the committed bundle and manifest under <paramref name="testDataDir"/> alone, never the
    /// user cache and never the network.
    /// </summary>
    /// <param name="testDataDir">The folder holding <c>FAACIFP18.gz</c> and <c>cifp-manifest.json</c>.</param>
    public static CifpResolveOptions PinnedCifpOptions(string testDataDir) =>
        new(
            BundledGzPath: Path.Combine(testDataDir, "FAACIFP18.gz"),
            BundledManifestPath: Path.Combine(testDataDir, "cifp-manifest.json"),
            AllowDownload: false
        )
        {
            PinToBundled = true,
        };

    public static void EnsureInitialized()
    {
        // Always re-set the NavigationDatabase singleton. Other tests (e.g. parser tests)
        // may have replaced it with a synthetic ForTesting() instance.
        if (LoadRealDataWithoutInstalling() is { } navDb)
        {
            NavigationDatabase.SetInstance(navDb);
        }
    }

    /// <summary>
    /// Loads the aircraft tables, the real navigation database and the facility operations data once per process,
    /// and leaves the <see cref="NavigationDatabase"/> singleton alone, for test hosts that install the database
    /// per test rather than as the process-wide default.
    /// </summary>
    /// <returns>The real navigation database, or <c>null</c> when NavData.dat is not present.</returns>
    public static NavigationDatabase? LoadRealDataWithoutInstalling()
    {
        EnsureAircraftDataInitialized();
        if (NavigationDb is not { } navDb)
        {
            return null;
        }

        lock (_lock)
        {
            if (!FacilityOpsDatabase.IsInitialized)
            {
                FacilityOpsDatabase.Initialize(Path.Combine(AppContext.BaseDirectory, "Data", "FacilityOps"), navDb);
            }
        }

        return navDb;
    }

    /// <summary>
    /// Points <see cref="TestVnasData"/> at the committed pins under <paramref name="testDataDir"/>, resolves NavData and
    /// CIFP from them (pinned: never the user cache, never a download), and registers the real database as
    /// <see cref="NavigationDatabase"/>'s default loader. A test assembly calls this from its <c>ModuleInitializer</c>,
    /// so a missing pin fails at load with the committed path named rather than as a null database in the first test.
    /// </summary>
    /// <param name="testDataDir">The checkout's <c>tests/Yaat.Sim.Tests/TestData</c> folder.</param>
    /// <exception cref="InvalidOperationException">The pinned NavData or CIFP could not be resolved.</exception>
    /// <exception cref="FileNotFoundException">
    /// A pinned NavData copy, bundle or manifest is missing from <paramref name="testDataDir"/>.
    /// </exception>
    public static void PinCommittedData(string testDataDir)
    {
        SetTestDataDir(testDataDir);

        string navDataPath =
            NavDataPathResolver.EnsureCurrent(PinnedNavDataOptions(testDataDir))
            ?? throw new InvalidOperationException($"The pinned NavData under {testDataDir} could not be resolved.");

        _ =
            CifpPathResolver.EnsureCurrentCycle(PinnedCifpOptions(testDataDir))
            ?? throw new InvalidOperationException($"The pinned CIFP under {testDataDir} could not be resolved.");

        // A class that reads NavigationDatabase.Instance without installing one gets the real database on its
        // first read, rather than only when an earlier class happened to call EnsureInitialized().
        NavigationDatabase.SetTestDefaultLoader(() =>
            LoadRealDataWithoutInstalling()
            ?? throw new InvalidOperationException($"The real navigation database could not be loaded from the resolved NavData at {navDataPath}.")
        );
    }

    /// <summary>
    /// Loads the aircraft tables (categories, CWT, FAA ACD, performance profiles) once per process and leaves the
    /// <see cref="NavigationDatabase"/> singleton alone, for harnesses that install their own navigation database.
    /// </summary>
    public static void EnsureAircraftDataInitialized()
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
    }

    /// <summary>
    /// Reads one of the committed aircraft tables from the test data folder. A missing file or a table with no entries is a
    /// broken checkout or a wrong <see cref="SetTestDataDir"/>, never a state to run tests in.
    /// </summary>
    private static T ReadRequiredAircraftTable<T>(string fileName)
        where T : System.Collections.ICollection
    {
        string path = Path.Combine(_testDataDir, fileName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Aircraft test data not found at {path}; it is committed under tests/Yaat.Sim.Tests/TestData, so point SetTestDataDir there.",
                path
            );
        }

        T? table = JsonSerializer.Deserialize<T>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if ((table is null) || (table.Count == 0))
        {
            throw new InvalidOperationException($"Aircraft test data at {path} holds no entries.");
        }

        return table;
    }

    private static void LoadAircraftSpecs()
    {
        List<AircraftSpecEntry> specs = ReadRequiredAircraftTable<List<AircraftSpecEntry>>("AircraftSpecs.json");
        var catLookup = new Dictionary<string, AircraftCategory>(StringComparer.OrdinalIgnoreCase);

        foreach (AircraftSpecEntry spec in specs)
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
        List<AircraftCwtEntry> entries = ReadRequiredAircraftTable<List<AircraftCwtEntry>>("AircraftCwt.json");
        var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (AircraftCwtEntry entry in entries)
        {
            if (!string.IsNullOrEmpty(entry.TypeCode) && !string.IsNullOrEmpty(entry.CwtCode))
            {
                lookup.TryAdd(entry.TypeCode, entry.CwtCode);
            }
        }

        WakeTurbulenceData.Initialize(lookup);
    }

    private static void LoadFaaAcd() =>
        FaaAircraftDatabase.Initialize(ReadRequiredAircraftTable<Dictionary<string, FaaAircraftRecord>>("FaaAcd.json"));

    private static void LoadAircraftProfiles()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Data", "AircraftProfiles.json");
        if (!File.Exists(path))
        {
            return;
        }

        // Sibling map first: profile-override merge resolves no-base types via the sibling map
        // and the category baseline (categorization is already loaded by LoadAircraftSpecs).
        string siblingPath = Path.Combine(AppContext.BaseDirectory, "Data", "AircraftProfileSiblings.json");
        if (File.Exists(siblingPath))
        {
            Dictionary<string, string> siblings = AircraftSiblingMap.LoadFromFile(siblingPath);
            AircraftSiblingMap.Initialize(siblings);
        }

        string envelopeClassPath = Path.Combine(AppContext.BaseDirectory, "Data", "AircraftSpeedEnvelopeClasses.json");
        if (File.Exists(envelopeClassPath))
        {
            AircraftSpeedEnvelope.Initialize(AircraftSpeedEnvelope.LoadFromFile(envelopeClassPath));
        }

        Dictionary<string, AircraftProfile> profiles = AircraftProfileDatabase.LoadFromFile(path);
        string overridePath = Path.Combine(AppContext.BaseDirectory, "Data", "AircraftProfileOverrides.json");
        IReadOnlyList<AircraftProfileOverride> overrides = AircraftProfileDatabase.LoadOverridesFromFile(overridePath);
        AircraftProfileDatabase.Initialize(profiles, overrides);

        AircraftPerformance.SetProfileCorrectionAdapter(new OverrideAwareProfileCorrectionAdapter(new EurocontrolProfileCorrectionAdapter()));
    }
}
