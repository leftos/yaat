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

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _cacheDir = Path.Combine(localAppData, "yaat", "cache");
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_cacheDir);

        var airac = AiracCycle.GetCurrentCycleId();
        CurrentAiracCycle = airac;
        Log.LogInformation("Current AIRAC cycle: {Cycle}", airac);

        var nextDate = AiracCycle.GetNextCycleDate(DateOnly.FromDateTime(DateTime.UtcNow));
        var daysUntilNext = nextDate.DayNumber - DateOnly.FromDateTime(DateTime.UtcNow).DayNumber;
        Log.LogInformation("Next AIRAC cycle effective in {Days} days ({Date:yyyy-MM-dd})", daysUntilNext, nextDate);

        var manifest = LoadManifest();
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

        var json = await _http.GetStringAsync(ConfigUrl);
        return JsonSerializer.Deserialize<VnasConfig>(json, JsonOptions);
    }

    private async Task LoadNavDataAsync(VnasConfig? config, CacheManifest? manifest)
    {
        var cachePath = Path.Combine(_cacheDir, "NavData.dat");

        bool needsDownload = config is not null && (manifest is null || config.NavDataSerial != manifest.NavDataSerial || !File.Exists(cachePath));

        if (needsDownload && config is not null)
        {
            try
            {
                Log.LogInformation("Downloading NavData.dat (serial {Serial})", config.NavDataSerial);

                var bytes = await _http.GetByteArrayAsync(config.NavDataUrl);
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
                var bytes = await File.ReadAllBytesAsync(cachePath);
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
        var cachePath = Path.Combine(_cacheDir, "AircraftSpecs.json");

        bool needsDownload =
            config is not null && (manifest is null || config.AircraftSpecsSerial != manifest.AircraftSpecsSerial || !File.Exists(cachePath));

        if (needsDownload && config is not null)
        {
            try
            {
                Log.LogInformation("Downloading AircraftSpecs.json (serial {Serial})", config.AircraftSpecsSerial);

                var json = await _http.GetStringAsync(config.AircraftSpecsUrl);
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
                var json = await File.ReadAllTextAsync(cachePath);
                var specs = JsonSerializer.Deserialize<List<AircraftSpecEntry>>(json, JsonOptions);
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
        var cachePath = Path.Combine(_cacheDir, "AircraftCwt.json");

        bool needsDownload =
            config is not null && (manifest is null || config.AircraftCwtSerial != manifest.AircraftCwtSerial || !File.Exists(cachePath));

        if (needsDownload && config is not null)
        {
            try
            {
                Log.LogInformation("Downloading AircraftCwt.json (serial {Serial})", config.AircraftCwtSerial);

                var json = await _http.GetStringAsync(config.AircraftCwtUrl);
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
                var json = await File.ReadAllTextAsync(cachePath);
                var cwt = JsonSerializer.Deserialize<List<AircraftCwtEntry>>(json, JsonOptions);
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
        var path = Path.Combine(_cacheDir, "manifest.json");

        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(path);
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

        var path = Path.Combine(_cacheDir, "manifest.json");
        var json = JsonSerializer.Serialize(manifest, IndentedJsonOptions);
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

        foreach (var spec in AircraftSpecs)
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
        foreach (var entry in AircraftCwt)
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
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Data", "AircraftProfiles.json");
            if (!File.Exists(path))
            {
                Log.LogWarning("AircraftProfiles.json not found at {Path}; category defaults will be used", path);
                return;
            }

            var profiles = AircraftProfileDatabase.LoadFromFile(path);
            AircraftProfileDatabase.Initialize(profiles);
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "Failed to load aircraft profiles; category defaults will be used");
        }
    }

    private void LogSummary()
    {
        var hasNav = NavData is not null;
        var hasSpecs = AircraftSpecs.Count > 0;
        var hasCwt = AircraftCwt.Count > 0;

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
