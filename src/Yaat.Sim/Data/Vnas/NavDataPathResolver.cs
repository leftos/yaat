using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Data.Vnas;

/// <summary>
/// Resolves NavData.dat from the vNAS configuration API: cache hit when serial matches,
/// download when stale, then TestData bundled fallback. Idempotent per process (single-flight).
/// Shares <c>%LOCALAPPDATA%/yaat/cache/NavData.dat</c> with <see cref="VnasDataService"/>.
/// </summary>
public static class NavDataPathResolver
{
    private const string ConfigUrl = "https://configuration.vnas.vatsim.net/";

    private static readonly ILogger Log = SimLog.CreateLogger("NavDataPathResolver");
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions IndentedJsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private static readonly object EnsureLock = new();

    private static volatile bool _ensured;
    private static string? _cachedPath;
    private static long? _resolvedNavDataSerial;
    private static int _configFetchCount;

    /// <summary>Path set by the last successful <see cref="EnsureCurrent"/>.</summary>
    public static string? CachedPath => _cachedPath;

    /// <summary>
    /// How many times this process has contacted the vNAS configuration API. The test assembly
    /// asserts this stays zero: the fetch costs ~240 ms of TLS handshake on every process start and
    /// would make the suite depend on VATSIM infrastructure being reachable.
    /// </summary>
    public static int ConfigFetchCount => Volatile.Read(ref _configFetchCount);

    /// <summary>vNAS NavData serial of <see cref="CachedPath"/>, if known.</summary>
    public static long? ResolvedNavDataSerial => _resolvedNavDataSerial;

    /// <summary>
    /// True when NavData can be resolved from disk — the shared cache or the caller's bundled copy —
    /// so no vNAS round-trip is needed. Callers use this to decide whether to permit a download
    /// rather than paying a config fetch to find out.
    /// </summary>
    public static bool HasLocalCopy(NavDataResolveOptions options) =>
        File.Exists(GetCachePath()) || (options.BundledPath is { } bundled && File.Exists(bundled));

    /// <summary>
    /// Ensures current vNAS NavData.dat is available. Subsequent calls return immediately.
    /// </summary>
    public static string? EnsureCurrent(NavDataResolveOptions? options = null)
    {
        if (_ensured)
        {
            return _cachedPath;
        }

        lock (EnsureLock)
        {
            if (_ensured)
            {
                return _cachedPath;
            }

            options ??= new NavDataResolveOptions();
            var (path, serial) = ResolveCore(options);
            _cachedPath = path;
            _resolvedNavDataSerial = serial;
            _ensured = true;
            return _cachedPath;
        }
    }

    private static (string? Path, long? Serial) ResolveCore(NavDataResolveOptions options)
    {
        if (options.ExplicitPath is { } explicitPath)
        {
            if (!File.Exists(explicitPath))
            {
                Log.LogWarning("Explicit NavData path does not exist: {Path}", explicitPath);
                return (null, null);
            }

            return (explicitPath, null);
        }

        // The config is only ever used to decide whether to download and to warn that a local copy
        // is behind what vNAS publishes, so fetching it when downloading is disabled buys nothing
        // and costs a TLS round-trip (~240 ms) on every process start. Callers that resolve
        // offline — the test assembly above all — must not pay that, nor depend on VATSIM being
        // reachable. Downstream, a null config simply means "no live serial to compare against".
        var wantsDownload = options.AllowDownload && !IsDownloadSkipped();

        VnasConfig? config = null;
        if (wantsDownload)
        {
            try
            {
                config = FetchConfigAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Log.LogWarning(ex, "Failed to fetch VNAS config for NavData resolve");
                config = null;
            }
        }

        var cachePath = GetCachePath();
        var cachedSerial = ReadCacheManifestSerial();

        if (config is not null)
        {
            bool needsDownload = !File.Exists(cachePath) || cachedSerial != config.NavDataSerial;
            if (needsDownload)
            {
                var downloaded = DownloadNavDataAsync(config, cachePath, CancellationToken.None).GetAwaiter().GetResult();
                if (downloaded)
                {
                    UpdateCacheManifestSerial(config.NavDataSerial);
                    cachedSerial = config.NavDataSerial;
                }
            }
        }

        var path = PickResolvedPath(options, config, cachePath, cachedSerial);
        var serial = ResolveSerialForPath(path, options, config, cachePath, cachedSerial);
        return (path, serial);
    }

    private static long? ResolveSerialForPath(string? path, NavDataResolveOptions options, VnasConfig? config, string cachePath, long? cachedSerial)
    {
        if (path is null)
        {
            return null;
        }

        if (string.Equals(path, cachePath, StringComparison.OrdinalIgnoreCase))
        {
            return cachedSerial ?? config?.NavDataSerial;
        }

        return ReadBundledManifestSerial(options) ?? config?.NavDataSerial;
    }

    private static string? PickResolvedPath(NavDataResolveOptions options, VnasConfig? config, string cachePath, long? cachedSerial)
    {
        if (File.Exists(cachePath) && cachedSerial is not null)
        {
            if (config is not null && cachedSerial != config.NavDataSerial)
            {
                Log.LogWarning(
                    "Using cached NavData.dat serial {CachedSerial} but vNAS publishes {LiveSerial} — run tools/refresh-navdata.py to update TestData",
                    cachedSerial,
                    config.NavDataSerial
                );
            }
            else
            {
                Log.LogDebug("Using cached NavData.dat (serial {Serial})", cachedSerial);
            }

            return cachePath;
        }

        if (File.Exists(cachePath))
        {
            Log.LogDebug("Using cached NavData.dat (serial unknown)");
            return cachePath;
        }

        return TryBundledFallback(options, config);
    }

    private static string? TryBundledFallback(NavDataResolveOptions options, VnasConfig? config)
    {
        if (options.BundledPath is not { } bundledPath || !File.Exists(bundledPath))
        {
            Log.LogWarning("No NavData.dat in cache or bundled TestData");
            return null;
        }

        var bundledSerial = ReadBundledManifestSerial(options);
        var bundledCycle = ReadBundledManifestCycle(options);
        var currentCycle = AiracCycle.GetCurrentCycleId();

        if (bundledCycle is not null && bundledCycle != currentCycle)
        {
            Log.LogWarning(
                "Using bundled NavData cycle {BundledCycle} (current AIRAC {CurrentCycle}); procedure ids may differ from live vNAS",
                bundledCycle,
                currentCycle
            );
        }

        if (config is not null && bundledSerial is not null && bundledSerial != config.NavDataSerial)
        {
            Log.LogWarning(
                "Using bundled NavData.dat serial {BundledSerial} (vNAS publishes {LiveSerial}); run tools/refresh-navdata.py",
                bundledSerial,
                config.NavDataSerial
            );
            Console.Error.WriteLine(
                $"[NavDataPathResolver] Warning: bundled NavData is serial {bundledSerial}, vNAS publishes {config.NavDataSerial}."
            );
        }

        return bundledPath;
    }

    private static string GetCachePath() => Path.Combine(YaatPaths.Combine("cache"), "NavData.dat");

    private static string GetCacheManifestPath() => Path.Combine(YaatPaths.Combine("cache"), "manifest.json");

    private static bool IsDownloadSkipped()
    {
        var v = Environment.GetEnvironmentVariable("YAAT_SKIP_NAVDATA_DOWNLOAD");
        return string.Equals(v, "1", StringComparison.Ordinal) || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<VnasConfig?> FetchConfigAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _configFetchCount);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var json = await http.GetStringAsync(ConfigUrl, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<VnasConfig>(json, JsonOptions);
    }

    private static async Task<bool> DownloadNavDataAsync(VnasConfig config, string cachePath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(config.NavDataUrl))
        {
            return false;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            Log.LogInformation("Downloading NavData.dat (serial {Serial}) from vNAS", config.NavDataSerial);
            Console.Error.WriteLine($"[NavDataPathResolver] Downloading NavData.dat (serial {config.NavDataSerial})...");

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
            var bytes = await http.GetByteArrayAsync(config.NavDataUrl, cancellationToken).ConfigureAwait(false);
            await File.WriteAllBytesAsync(cachePath, bytes, cancellationToken).ConfigureAwait(false);

            Log.LogInformation("NavData.dat cached ({Size:N0} bytes)", bytes.Length);
            Console.Error.WriteLine($"[NavDataPathResolver] NavData.dat cached ({bytes.Length:N0} bytes).");
            return true;
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "Failed to download NavData.dat for serial {Serial}", config.NavDataSerial);
            return false;
        }
    }

    private static long? ReadCacheManifestSerial()
    {
        var manifest = ReadCacheManifest();
        return manifest?.NavDataSerial;
    }

    private static void UpdateCacheManifestSerial(long navDataSerial)
    {
        var path = GetCacheManifestPath();
        CacheManifest manifest;
        if (File.Exists(path))
        {
            try
            {
                var json = File.ReadAllText(path);
                manifest = JsonSerializer.Deserialize<CacheManifest>(json, JsonOptions) ?? new CacheManifest();
            }
            catch
            {
                manifest = new CacheManifest();
            }
        }
        else
        {
            manifest = new CacheManifest();
        }

        manifest.NavDataSerial = navDataSerial;
        manifest.AiracCycle = AiracCycle.GetCurrentCycleId();
        manifest.LastUpdated = DateTime.UtcNow;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(manifest, IndentedJsonOptions));
    }

    private static CacheManifest? ReadCacheManifest()
    {
        var path = GetCacheManifestPath();
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<CacheManifest>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            Log.LogDebug(ex, "Could not read cache manifest {Path}", path);
            return null;
        }
    }

    private static long? ReadBundledManifestSerial(NavDataResolveOptions options)
    {
        var doc = ReadBundledManifest(options);
        return doc?.NavDataSerial;
    }

    private static string? ReadBundledManifestCycle(NavDataResolveOptions options)
    {
        var doc = ReadBundledManifest(options);
        return doc?.AiracCycle;
    }

    private static NavDataManifest? ReadBundledManifest(NavDataResolveOptions options)
    {
        if (options.BundledManifestPath is not { } manifestPath || !File.Exists(manifestPath))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(manifestPath);
            return JsonSerializer.Deserialize<NavDataManifest>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            Log.LogDebug(ex, "Could not read NavData manifest {Path}", manifestPath);
            return null;
        }
    }

    private sealed class NavDataManifest
    {
        public long NavDataSerial { get; init; }
        public string? AiracCycle { get; init; }
    }
}
