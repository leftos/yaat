using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Data.Vnas;

/// <summary>
/// Resolves the FAACIFP18 file for the current AIRAC cycle: cache hit, FAA download,
/// then bundled/offline fallback. Idempotent per process (single-flight).
/// </summary>
public static class CifpPathResolver
{
    private const string CifpBaseUrl = "https://aeronav.faa.gov/Upload_313-d/cifp/";

    private static readonly ILogger Log = SimLog.CreateLogger("CifpPathResolver");
    private static readonly object EnsureLock = new();

    private static volatile bool _ensured;
    private static string? _cachedPath;
    private static string? _resolvedCycleId;

    /// <summary>Path set by the last successful <see cref="EnsureCurrentCycle"/>.</summary>
    public static string? CachedPath => _cachedPath;

    /// <summary>AIRAC cycle id of <see cref="CachedPath"/>, if known.</summary>
    public static string? ResolvedCycleId => _resolvedCycleId;

    /// <summary>
    /// Ensures CIFP for the current AIRAC cycle is available. Subsequent calls return immediately.
    /// </summary>
    public static string? EnsureCurrentCycle(CifpResolveOptions? options = null)
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

            options ??= new CifpResolveOptions();
            _cachedPath = ResolveCore(options);
            _resolvedCycleId = _cachedPath is not null ? AiracCycle.GetCurrentCycleId() : null;
            _ensured = true;
            return _cachedPath;
        }
    }

    public static async Task<string?> EnsureCurrentCycleAsync(CifpResolveOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (_ensured)
        {
            return _cachedPath;
        }

        options ??= new CifpResolveOptions();
        if (options.ExplicitPath is { } explicitPath && File.Exists(explicitPath))
        {
            lock (EnsureLock)
            {
                if (!_ensured)
                {
                    _cachedPath = explicitPath;
                    _resolvedCycleId = null;
                    _ensured = true;
                }
            }

            return _cachedPath;
        }

        var cycleId = AiracCycle.GetCurrentCycleId();
        var cachePath = GetCachePathForCycle(cycleId);
        if (File.Exists(cachePath))
        {
            lock (EnsureLock)
            {
                if (!_ensured)
                {
                    _cachedPath = cachePath;
                    _resolvedCycleId = cycleId;
                    _ensured = true;
                }
            }

            return _cachedPath;
        }

        if (!options.AllowDownload || IsDownloadSkipped())
        {
            lock (EnsureLock)
            {
                if (!_ensured)
                {
                    _cachedPath = TryBundledOrStaleCache(options, cycleId);
                    _resolvedCycleId = ReadBundledCycle(options) ?? _resolvedCycleId;
                    _ensured = true;
                }
            }

            return _cachedPath;
        }

        var downloaded = await DownloadCurrentCycleAsync(cachePath, cycleId, cancellationToken).ConfigureAwait(false);
        lock (EnsureLock)
        {
            if (!_ensured)
            {
                _cachedPath = downloaded ?? TryBundledOrStaleCache(options, cycleId);
                _resolvedCycleId = downloaded is not null ? cycleId : ReadBundledCycle(options);
                _ensured = true;
            }
        }

        return _cachedPath;
    }

    private static string? ResolveCore(CifpResolveOptions options)
    {
        if (options.ExplicitPath is { } explicitPath)
        {
            if (!File.Exists(explicitPath))
            {
                Log.LogWarning("Explicit CIFP path does not exist: {Path}", explicitPath);
                return null;
            }

            return explicitPath;
        }

        var cycleId = AiracCycle.GetCurrentCycleId();
        var cachePath = GetCachePathForCycle(cycleId);
        if (File.Exists(cachePath))
        {
            Log.LogDebug("Using cached CIFP for AIRAC cycle {Cycle}: {Path}", cycleId, cachePath);
            _resolvedCycleId = cycleId;
            return cachePath;
        }

        if (options.AllowDownload && !IsDownloadSkipped())
        {
            var downloaded = DownloadCurrentCycleAsync(cachePath, cycleId, CancellationToken.None).GetAwaiter().GetResult();
            if (downloaded is not null)
            {
                _resolvedCycleId = cycleId;
                return downloaded;
            }
        }

        return TryBundledOrStaleCache(options, cycleId);
    }

    private static string GetCachePathForCycle(string cycleId)
    {
        var cacheDir = YaatPaths.Combine("cache", "cifp");
        return Path.Combine(cacheDir, $"FAACIFP18-{cycleId}");
    }

    private static bool IsDownloadSkipped()
    {
        var v = Environment.GetEnvironmentVariable("YAAT_SKIP_CIFP_DOWNLOAD");
        return string.Equals(v, "1", StringComparison.Ordinal) || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string?> DownloadCurrentCycleAsync(string cachePath, string cycleId, CancellationToken cancellationToken)
    {
        var cacheDir = Path.GetDirectoryName(cachePath)!;
        Directory.CreateDirectory(cacheDir);

        var cycleDate = AiracCycle.GetCycleDate(cycleId);
        var dateStr = cycleDate.ToString("yyMMdd");
        var url = $"{CifpBaseUrl}CIFP_{dateStr}.zip";

        try
        {
            Log.LogInformation("Downloading CIFP for AIRAC cycle {Cycle} from {Url}", cycleId, url);
            Console.Error.WriteLine($"[CifpPathResolver] Downloading CIFP for AIRAC {cycleId}...");

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
            var zipBytes = await http.GetByteArrayAsync(url, cancellationToken).ConfigureAwait(false);

            using var zipStream = new MemoryStream(zipBytes);
            using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);

            var cifpEntry = archive.Entries.FirstOrDefault(e => e.Name.StartsWith("FAACIFP", StringComparison.Ordinal));
            if (cifpEntry is null)
            {
                Log.LogWarning("FAACIFP file not found in zip archive from {Url}", url);
                return null;
            }

            await using var entryStream = cifpEntry.Open();
            await using var fileStream = File.Create(cachePath);
            await entryStream.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);

            Log.LogInformation("CIFP cached for cycle {Cycle} ({Size:N0} bytes)", cycleId, new FileInfo(cachePath).Length);
            Console.Error.WriteLine($"[CifpPathResolver] CIFP cached for AIRAC {cycleId}.");
            return cachePath;
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "Failed to download CIFP for cycle {Cycle}", cycleId);
            return null;
        }
    }

    private static string? TryBundledOrStaleCache(CifpResolveOptions options, string currentCycleId)
    {
        if (options.BundledGzPath is { } bundledGz && File.Exists(bundledGz))
        {
            var bundledCycle = ReadBundledCycle(options);
            if (bundledCycle is not null && bundledCycle != currentCycleId)
            {
                Log.LogWarning(
                    "Using bundled CIFP cycle {BundledCycle} (current AIRAC {CurrentCycle}); procedure ids may differ from charts",
                    bundledCycle,
                    currentCycleId
                );
                Console.Error.WriteLine($"[CifpPathResolver] Warning: bundled CIFP is cycle {bundledCycle}, current AIRAC is {currentCycleId}.");
            }

            return DecompressBundledGzip(bundledGz);
        }

        var cacheDir = YaatPaths.Combine("cache", "cifp");
        if (Directory.Exists(cacheDir))
        {
            var newest = Directory.EnumerateFiles(cacheDir, "FAACIFP18-*").OrderDescending(StringComparer.Ordinal).FirstOrDefault();
            if (newest is not null)
            {
                Log.LogWarning("Using stale CIFP cache file {Path} (current cycle {Cycle} not cached)", newest, currentCycleId);
                return newest;
            }
        }

        return null;
    }

    private static string? ReadBundledCycle(CifpResolveOptions options)
    {
        if (options.BundledManifestPath is not { } manifestPath || !File.Exists(manifestPath))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(manifestPath);
            var doc = JsonSerializer.Deserialize<CifpManifest>(json);
            return doc?.AiracCycle;
        }
        catch (Exception ex)
        {
            Log.LogDebug(ex, "Could not read CIFP manifest {Path}", manifestPath);
            return null;
        }
    }

    /// <summary>
    /// Decompresses <see cref="CifpResolveOptions.BundledGzPath"/> for supplementary procedure lookup
    /// (retired SIDs absent from the current FAA cycle). Returns null when no bundle is configured.
    /// </summary>
    public static string? ResolveSupplementaryBundledPath(CifpResolveOptions options)
    {
        if (options.BundledGzPath is not { } gzPath || !File.Exists(gzPath))
        {
            return null;
        }

        return DecompressBundledGzip(gzPath);
    }

    private static string DecompressBundledGzip(string gzPath)
    {
        var cacheDir = YaatPaths.Combine("cache", "cifp");
        Directory.CreateDirectory(cacheDir);
        var outPath = Path.Combine(cacheDir, "FAACIFP18-bundled.dat");

        if (!File.Exists(outPath) || new FileInfo(gzPath).LastWriteTimeUtc > new FileInfo(outPath).LastWriteTimeUtc)
        {
            using var inputStream = File.OpenRead(gzPath);
            using var gzipStream = new GZipStream(inputStream, CompressionMode.Decompress);
            using var outputStream = File.Create(outPath);
            gzipStream.CopyTo(outputStream);
        }

        return outPath;
    }

    private sealed class CifpManifest
    {
        public string? AiracCycle { get; init; }
    }
}
