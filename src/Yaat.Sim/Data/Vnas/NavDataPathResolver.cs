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
    private static readonly ILogger Log = SimLog.CreateLogger("NavDataPathResolver");
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions IndentedJsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private static readonly Lock EnsureLock = new();

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
        File.Exists(GetCachePath(YaatPaths.AppDataRoot)) || (options.BundledPath is { } bundled && File.Exists(bundled));

    /// <summary>
    /// Ensures current vNAS NavData.dat is available. Subsequent calls return immediately.
    /// </summary>
    public static string? EnsureCurrent(NavDataResolveOptions options)
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

            (string? path, long? serial) = ResolveCore(options, YaatPaths.AppDataRoot);
            _cachedPath = path;
            _resolvedNavDataSerial = serial;
            _ensured = true;
            return _cachedPath;
        }
    }

    /// <summary>
    /// Resolves NavData under <paramref name="options"/> against the user cache under <paramref name="cacheRoot"/>, and
    /// returns the file with the vNAS serial it carries. A <see cref="NavDataResolveOptions.PinToBundled"/> resolve
    /// reads the committed bundled file and manifest alone, never the cache or the network.
    /// <see cref="EnsureCurrent"/> passes <see cref="YaatPaths.AppDataRoot"/>; a test calls this directly with a temp
    /// root.
    /// </summary>
    public static (string? Path, long? Serial) ResolveCore(NavDataResolveOptions options, string cacheRoot)
    {
        if (options.PinToBundled)
        {
            long pinnedSerial = ReadPinnedSerial(options);
            return (PinToBundledFile(options), pinnedSerial);
        }

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
        bool wantsDownload = options.AllowDownload && !IsDownloadSkipped();

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

        string cachePath = GetCachePath(cacheRoot);
        long? cachedSerial = ReadCacheManifestSerial(cacheRoot);

        if (config is not null)
        {
            bool needsDownload = !File.Exists(cachePath) || cachedSerial != config.NavDataSerial;
            if (needsDownload)
            {
                bool downloaded = DownloadNavDataAsync(config, cachePath, CancellationToken.None).GetAwaiter().GetResult();
                if (downloaded)
                {
                    UpdateCacheManifestSerial(config.NavDataSerial, cacheRoot);
                    cachedSerial = config.NavDataSerial;
                }
            }
        }

        string? path = PickResolvedPath(options, config, cachePath, cachedSerial);
        long? serial = ResolveSerialForPath(path, options, config, cachePath, cachedSerial);
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

        return ReadBundledManifestSerial(options, out _) ?? config?.NavDataSerial;
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

        long? bundledSerial = ReadBundledManifestSerial(options, out _);
        string? bundledCycle = ReadBundledManifestCycle(options);
        string currentCycle = AiracCycle.GetCurrentCycleId();

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

    /// <summary>
    /// The committed copy a pinned resolve reads. The pin has no fallback, so a missing one fails with the path to
    /// restore rather than resolving null.
    /// </summary>
    private static string PinToBundledFile(NavDataResolveOptions options)
    {
        if (options.BundledPath is not { } bundledPath)
        {
            throw new InvalidOperationException(
                "NavDataResolveOptions.PinToBundled needs BundledPath: the committed tests/Yaat.Sim.Tests/TestData/NavData.dat."
            );
        }

        if (!File.Exists(bundledPath))
        {
            throw new FileNotFoundException(
                $"The pinned NavData copy {bundledPath} does not exist; it is committed under tests/Yaat.Sim.Tests/TestData, "
                    + "so a missing one usually means an incomplete checkout. Restore it with "
                    + "`git checkout -- tests/Yaat.Sim.Tests/TestData/NavData.dat`, or run `python tools/refresh-navdata.py` to repin it.",
                bundledPath
            );
        }

        return bundledPath;
    }

    /// <summary>The vNAS serial the committed manifest records. The pin has no other source for it.</summary>
    private static long ReadPinnedSerial(NavDataResolveOptions options)
    {
        if (options.BundledManifestPath is not { } manifestPath)
        {
            throw new InvalidOperationException(
                "NavDataResolveOptions.PinToBundled needs BundledManifestPath: the committed tests/Yaat.Sim.Tests/TestData/navdata-manifest.json."
            );
        }

        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException(
                $"The pinned NavData manifest {manifestPath} does not exist; it is committed under tests/Yaat.Sim.Tests/TestData, "
                    + "so a missing one usually means an incomplete checkout. Restore it with "
                    + "`git checkout -- tests/Yaat.Sim.Tests/TestData/navdata-manifest.json`, or run `python tools/refresh-navdata.py` to repin it.",
                manifestPath
            );
        }

        long? serial = ReadBundledManifestSerial(options, out Exception? parseError);
        if (serial is not > 0)
        {
            throw new InvalidOperationException(
                parseError is null
                    ? $"The pinned NavData manifest {manifestPath} has no navDataSerial."
                    : $"The pinned NavData manifest {manifestPath} has no readable navDataSerial: {parseError.Message}"
            );
        }

        return serial.Value;
    }

    private static string GetCachePath(string cacheRoot) => Path.Combine(cacheRoot, "cache", "NavData.dat");

    private static string GetCacheManifestPath(string cacheRoot) => Path.Combine(cacheRoot, "cache", "manifest.json");

    private static bool IsDownloadSkipped()
    {
        string? v = Environment.GetEnvironmentVariable("YAAT_SKIP_NAVDATA_DOWNLOAD");
        return string.Equals(v, "1", StringComparison.Ordinal) || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<VnasConfig?> FetchConfigAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _configFetchCount);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        string json = await http.GetStringAsync(VnasConfig.Url, cancellationToken).ConfigureAwait(false);
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
            byte[] bytes = await http.GetByteArrayAsync(config.NavDataUrl, cancellationToken).ConfigureAwait(false);
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

    private static long? ReadCacheManifestSerial(string cacheRoot)
    {
        CacheManifest? manifest = ReadCacheManifest(cacheRoot);
        return manifest?.NavDataSerial;
    }

    private static void UpdateCacheManifestSerial(long navDataSerial, string cacheRoot)
    {
        string path = GetCacheManifestPath(cacheRoot);
        CacheManifest manifest;
        if (File.Exists(path))
        {
            try
            {
                string json = File.ReadAllText(path);
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

    private static CacheManifest? ReadCacheManifest(string cacheRoot)
    {
        string path = GetCacheManifestPath(cacheRoot);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<CacheManifest>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            Log.LogDebug(ex, "Could not read cache manifest {Path}", path);
            return null;
        }
    }

    private static long? ReadBundledManifestSerial(NavDataResolveOptions options, out Exception? parseError)
    {
        NavDataManifest? doc = ReadBundledManifest(options, out parseError);
        return doc?.NavDataSerial;
    }

    private static string? ReadBundledManifestCycle(NavDataResolveOptions options)
    {
        NavDataManifest? doc = ReadBundledManifest(options, out _);
        return doc?.AiracCycle;
    }

    private static NavDataManifest? ReadBundledManifest(NavDataResolveOptions options, out Exception? parseError)
    {
        parseError = null;
        if (options.BundledManifestPath is not { } manifestPath || !File.Exists(manifestPath))
        {
            return null;
        }

        try
        {
            string json = File.ReadAllText(manifestPath);
            return JsonSerializer.Deserialize<NavDataManifest>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            Log.LogDebug(ex, "Could not read NavData manifest {Path}", manifestPath);
            parseError = ex;
            return null;
        }
    }

    private sealed class NavDataManifest
    {
        public long NavDataSerial { get; init; }
        public string? AiracCycle { get; init; }
    }
}
