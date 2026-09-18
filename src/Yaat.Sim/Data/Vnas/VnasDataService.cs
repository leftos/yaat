using System.Text.Json;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Proto;

namespace Yaat.Sim.Data.Vnas;

/// <summary>
/// Downloads and caches VNAS data files (NavData,
/// AircraftSpecs, AircraftCwt) with serial-based
/// staleness detection and AIRAC cycle awareness.
/// </summary>
public sealed class VnasDataService : IDisposable
{
    private const string ConfigUrl = "https://configuration.vnas.vatsim.net/";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static readonly JsonSerializerOptions IndentedJsonOptions = new() { WriteIndented = true };

    private static readonly ILogger Log = SimLog.CreateLogger<VnasDataService>();

    private readonly HttpClient _http;
    private readonly string _cacheDir;

    public NavDataSet? NavData { get; private set; }
    public IReadOnlyList<AircraftSpecEntry> AircraftSpecs { get; private set; } = [];
    public IReadOnlyList<AircraftCwtEntry> AircraftCwt { get; private set; } = [];
    public string CurrentAiracCycle { get; private set; } = "";

    public VnasDataService()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        _cacheDir = YaatPaths.Combine("cache");
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_cacheDir);

        string airac = AiracCycle.GetCurrentCycleId();
        CurrentAiracCycle = airac;
        Log.LogInformation("Current AIRAC cycle: {Cycle}", airac);

        DateOnly nextDate = AiracCycle.GetNextCycleDate(DateOnly.FromDateTime(DateTime.UtcNow));
        int daysUntilNext = nextDate.DayNumber - DateOnly.FromDateTime(DateTime.UtcNow).DayNumber;
        Log.LogInformation("Next AIRAC cycle effective in {Days} days ({Date:yyyy-MM-dd})", daysUntilNext, nextDate);

        CacheManifest? manifest = LoadManifest();
        VnasConfig? config = null;

        try
        {
            config = await FetchConfigAsync();
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "Failed to fetch VNAS config; " + "using cached data if available");
        }

        await LoadNavDataAsync(config, manifest);
        await LoadAircraftSpecsAsync(config, manifest);
        await LoadAircraftCwtAsync(config, manifest);

        InitializeAircraftCategorization();

        await InitializeFaaAcdAsync();

        InitializeAircraftProfiles();

        // Install the Eurocontrol/BADA profile correction adapter (adjusts speeds and climb rates
        // using FAA ACD approach speed as ground truth — see EurocontrolProfileCorrectionAdapter.cs)
        // wrapped so authoritative AircraftProfileOverrides.json fields bypass the rescaling.
        AircraftPerformance.SetProfileCorrectionAdapter(new OverrideAwareProfileCorrectionAdapter(new EurocontrolProfileCorrectionAdapter()));

        // Verify the AircraftGenerator type pool resolves end-to-end through the data
        // DBs we just loaded. Catches the next time someone adds a non-ICAO string
        // (the original 'PA28' instead of 'P28A') or a code lacking profile data.
        try
        {
            Yaat.Sim.Scenarios.AircraftGenerator.AssertEveryTypeResolves();
        }
        catch (InvalidOperationException ex)
        {
            Log.LogError(ex, "AircraftGenerator type validation failed — generated arrivals may use jet defaults");
        }

        SaveManifest(config, manifest);

        LogSummary();
    }

    public void Dispose()
    {
        _http.Dispose();
    }

    private async Task<VnasConfig?> FetchConfigAsync()
    {
        Log.LogInformation("Fetching VNAS config from {Url}", ConfigUrl);

        string json = await _http.GetStringAsync(ConfigUrl);
        return JsonSerializer.Deserialize<VnasConfig>(json, JsonOptions);
    }

    private async Task LoadNavDataAsync(VnasConfig? config, CacheManifest? manifest)
    {
        string cachePath = Path.Combine(_cacheDir, "NavData.dat");

        bool needsDownload = config is not null && (manifest is null || config.NavDataSerial != manifest.NavDataSerial || !File.Exists(cachePath));

        if (needsDownload && config is not null)
        {
            try
            {
                Log.LogInformation("Downloading NavData.dat (serial {Serial})", config.NavDataSerial);

                byte[] bytes = await _http.GetByteArrayAsync(config.NavDataUrl);
                await File.WriteAllBytesAsync(cachePath, bytes);

                Log.LogInformation("NavData.dat cached ({Size:N0} bytes)", bytes.Length);
            }
            catch (Exception ex)
            {
                Log.LogWarning(ex, "Failed to download NavData.dat");
            }
        }

        if (File.Exists(cachePath))
        {
            try
            {
                byte[] bytes = await File.ReadAllBytesAsync(cachePath);
                NavData = NavDataSet.Parser.ParseFrom(bytes);

                Log.LogInformation(
                    "NavData loaded: {Airports} airports, " + "{Fixes} fixes, {Airways} airways, " + "{Sids} SIDs, {Stars} STARs",
                    NavData.Airports.Count,
                    NavData.Fixes.Count,
                    NavData.Airways.Count,
                    NavData.Sids.Count,
                    NavData.Stars.Count
                );
            }
            catch (Exception ex)
            {
                Log.LogError(ex, "Failed to parse NavData.dat");
            }
        }
        else
        {
            Log.LogWarning("No NavData available (no cache, no download)");
        }
    }

    private async Task LoadAircraftSpecsAsync(VnasConfig? config, CacheManifest? manifest)
    {
        string cachePath = Path.Combine(_cacheDir, "AircraftSpecs.json");

        bool needsDownload =
            config is not null && (manifest is null || config.AircraftSpecsSerial != manifest.AircraftSpecsSerial || !File.Exists(cachePath));

        if (needsDownload && config is not null)
        {
            try
            {
                Log.LogInformation("Downloading AircraftSpecs.json (serial {Serial})", config.AircraftSpecsSerial);

                string json = await _http.GetStringAsync(config.AircraftSpecsUrl);
                await File.WriteAllTextAsync(cachePath, json);
            }
            catch (Exception ex)
            {
                Log.LogWarning(ex, "Failed to download AircraftSpecs.json");
            }
        }

        if (File.Exists(cachePath))
        {
            try
            {
                string json = await File.ReadAllTextAsync(cachePath);
                List<AircraftSpecEntry>? specs = JsonSerializer.Deserialize<List<AircraftSpecEntry>>(json, JsonOptions);
                AircraftSpecs = specs ?? [];

                Log.LogInformation("AircraftSpecs loaded: {Count} aircraft types", AircraftSpecs.Count);
            }
            catch (Exception ex)
            {
                Log.LogError(ex, "Failed to parse AircraftSpecs.json");
            }
        }
    }

    private async Task LoadAircraftCwtAsync(VnasConfig? config, CacheManifest? manifest)
    {
        string cachePath = Path.Combine(_cacheDir, "AircraftCwt.json");

        bool needsDownload =
            config is not null && (manifest is null || config.AircraftCwtSerial != manifest.AircraftCwtSerial || !File.Exists(cachePath));

        if (needsDownload && config is not null)
        {
            try
            {
                Log.LogInformation("Downloading AircraftCwt.json (serial {Serial})", config.AircraftCwtSerial);

                string json = await _http.GetStringAsync(config.AircraftCwtUrl);
                await File.WriteAllTextAsync(cachePath, json);
            }
            catch (Exception ex)
            {
                Log.LogWarning(ex, "Failed to download AircraftCwt.json");
            }
        }

        if (File.Exists(cachePath))
        {
            try
            {
                string json = await File.ReadAllTextAsync(cachePath);
                List<AircraftCwtEntry>? cwt = JsonSerializer.Deserialize<List<AircraftCwtEntry>>(json, JsonOptions);
                AircraftCwt = cwt ?? [];

                Log.LogInformation("AircraftCwt loaded: {Count} entries", AircraftCwt.Count);
            }
            catch (Exception ex)
            {
                Log.LogError(ex, "Failed to parse AircraftCwt.json");
            }
        }
    }

    private CacheManifest? LoadManifest()
    {
        string path = Path.Combine(_cacheDir, "manifest.json");

        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<CacheManifest>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private void SaveManifest(VnasConfig? config, CacheManifest? previous)
    {
        var manifest = new CacheManifest
        {
            NavDataSerial = config?.NavDataSerial ?? previous?.NavDataSerial ?? 0,
            AircraftSpecsSerial = config?.AircraftSpecsSerial ?? previous?.AircraftSpecsSerial ?? 0,
            AircraftCwtSerial = config?.AircraftCwtSerial ?? previous?.AircraftCwtSerial ?? 0,
            AiracCycle = CurrentAiracCycle,
            LastUpdated = DateTime.UtcNow,
        };

        string path = Path.Combine(_cacheDir, "manifest.json");
        string json = JsonSerializer.Serialize(manifest, IndentedJsonOptions);
        File.WriteAllText(path, json);
    }

    private void InitializeAircraftCategorization()
    {
        if (AircraftSpecs.Count == 0)
        {
            Log.LogWarning("No AircraftSpecs data — " + "categorization will default to Jet");
            return;
        }

        var lookup = new Dictionary<string, AircraftCategory>(StringComparer.OrdinalIgnoreCase);

        foreach (AircraftSpecEntry spec in AircraftSpecs)
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

            lookup.TryAdd(spec.Designator, cat);
        }

        AircraftCategorization.Initialize(lookup);

        Log.LogInformation("Aircraft categorization initialized: " + "{Count} type mappings", lookup.Count);

        InitializeCwtData();
    }

    private void InitializeCwtData()
    {
        if (AircraftCwt.Count == 0)
        {
            return;
        }

        var cwtLookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (AircraftCwtEntry entry in AircraftCwt)
        {
            if (!string.IsNullOrEmpty(entry.TypeCode) && !string.IsNullOrEmpty(entry.CwtCode))
            {
                cwtLookup.TryAdd(entry.TypeCode, entry.CwtCode);
            }
        }

        WakeTurbulenceData.Initialize(cwtLookup);

        Log.LogInformation("CWT/wake turbulence data initialized: " + "{Count} CWT mappings", cwtLookup.Count);
    }

    private async Task InitializeFaaAcdAsync()
    {
        try
        {
            using var faaService = new FaaAircraftDataService();
            await faaService.InitializeAsync();
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "FAA ACD initialization failed; category defaults will be used for approach speeds");
        }
    }

    private static void InitializeAircraftProfiles()
    {
        // Sibling map first: AircraftProfileDatabase.Initialize merges no-base overrides onto a
        // sibling's profile or a category baseline, so the sibling map and categorization (already
        // initialized above) must be ready before profiles are loaded.
        try
        {
            string siblingPath = Path.Combine(AppContext.BaseDirectory, "Data", "AircraftProfileSiblings.json");
            if (File.Exists(siblingPath))
            {
                Dictionary<string, string> siblings = AircraftSiblingMap.LoadFromFile(siblingPath);
                AircraftSiblingMap.Initialize(siblings);
            }
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "Failed to load aircraft sibling map; missing-profile types will fall through to category defaults");
        }

        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Data", "AircraftProfiles.json");
            if (!File.Exists(path))
            {
                Log.LogWarning("AircraftProfiles.json not found at {Path}; category defaults will be used", path);
                return;
            }

            Dictionary<string, AircraftProfile> profiles = AircraftProfileDatabase.LoadFromFile(path);
            string overridePath = Path.Combine(AppContext.BaseDirectory, "Data", "AircraftProfileOverrides.json");
            IReadOnlyList<AircraftProfileOverride> overrides = AircraftProfileDatabase.LoadOverridesFromFile(overridePath);
            AircraftProfileDatabase.Initialize(profiles, overrides);
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "Failed to load aircraft profiles; category defaults will be used");
        }
    }

    private void LogSummary()
    {
        bool hasNav = NavData is not null;
        bool hasSpecs = AircraftSpecs.Count > 0;
        bool hasCwt = AircraftCwt.Count > 0;

        if (hasNav && hasSpecs && hasCwt)
        {
            Log.LogInformation("VNAS data fully loaded (AIRAC {Cycle})", CurrentAiracCycle);
        }
        else
        {
            Log.LogWarning("VNAS data partially loaded — " + "NavData:{Nav}, Specs:{Specs}, CWT:{Cwt}", hasNav, hasSpecs, hasCwt);
        }
    }
}
