using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
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
    private static readonly JsonSerializerOptions ManifestJsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly Lock EnsureLock = new();

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
    public static string? EnsureCurrentCycle(CifpResolveOptions options)
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

            (string? path, string? cycleId) = ResolveCore(options, YaatPaths.AppDataRoot);
            _cachedPath = path;
            _resolvedCycleId = cycleId;
            _ensured = true;
            return _cachedPath;
        }
    }

    public static async Task<string?> EnsureCurrentCycleAsync(CifpResolveOptions options, CancellationToken cancellationToken)
    {
        if (_ensured)
        {
            return _cachedPath;
        }

        if (options.PinToBundled)
        {
            return EnsureCurrentCycle(options);
        }

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

        string cycleId = AiracCycle.GetCurrentCycleId();
        string cachePath = GetCachePathForCycle(YaatPaths.AppDataRoot, cycleId);
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
                    (_cachedPath, _resolvedCycleId) = TryBundledOrStaleCache(options, YaatPaths.AppDataRoot, cycleId);
                    _ensured = true;
                }
            }

            return _cachedPath;
        }

        string? downloaded = await DownloadCurrentCycleAsync(cachePath, cycleId, cancellationToken).ConfigureAwait(false);
        lock (EnsureLock)
        {
            if (!_ensured)
            {
                if (downloaded is not null)
                {
                    _cachedPath = downloaded;
                    _resolvedCycleId = cycleId;
                }
                else
                {
                    (_cachedPath, _resolvedCycleId) = TryBundledOrStaleCache(options, YaatPaths.AppDataRoot, cycleId);
                }

                _ensured = true;
            }
        }

        return _cachedPath;
    }

    /// <summary>
    /// Resolves CIFP under <paramref name="options"/> against the user cache under <paramref name="cacheRoot"/>, and
    /// returns the file with the AIRAC cycle it carries: the explicit path reports none, a cache hit or a download its
    /// own, and the bundled fallback its manifest's. A <see cref="CifpResolveOptions.PinToBundled"/> resolve reads
    /// the committed bundle and manifest alone. <see cref="EnsureCurrentCycle"/> passes
    /// <see cref="YaatPaths.AppDataRoot"/>; a test calls this directly with a temp root.
    /// </summary>
    public static (string? Path, string? CycleId) ResolveCore(CifpResolveOptions options, string cacheRoot)
    {
        if (options.PinToBundled)
        {
            string pinnedCycleId = ReadPinnedCycle(options);
            return (PinToBundledFile(options, pinnedCycleId), pinnedCycleId);
        }

        if (options.ExplicitPath is { } explicitPath)
        {
            if (!File.Exists(explicitPath))
            {
                Log.LogWarning("Explicit CIFP path does not exist: {Path}", explicitPath);
                return (null, null);
            }

            return (explicitPath, null);
        }

        string cycleId = AiracCycle.GetCurrentCycleId();
        string cachePath = GetCachePathForCycle(cacheRoot, cycleId);
        if (File.Exists(cachePath))
        {
            Log.LogDebug("Using cached CIFP for AIRAC cycle {Cycle}: {Path}", cycleId, cachePath);
            return (cachePath, cycleId);
        }

        if (options.AllowDownload && !IsDownloadSkipped())
        {
            string? downloaded = DownloadCurrentCycleAsync(cachePath, cycleId, CancellationToken.None).GetAwaiter().GetResult();
            if (downloaded is not null)
            {
                return (downloaded, cycleId);
            }
        }

        return TryBundledOrStaleCache(options, cacheRoot, cycleId);
    }

    /// <summary>
    /// Decompresses the committed bundle to a content-keyed file under the temp folder, never the user cache. A
    /// missing bundle leaves the pin nothing to fall back to, so it fails with the path to restore rather than
    /// resolving null.
    /// </summary>
    private static string PinToBundledFile(CifpResolveOptions options, string cycleId)
    {
        if (options.BundledGzPath is not { } bundledGz)
        {
            throw new InvalidOperationException(
                "CifpResolveOptions.PinToBundled needs BundledGzPath: the committed tests/Yaat.Sim.Tests/TestData/FAACIFP18.gz."
            );
        }

        if (!File.Exists(bundledGz))
        {
            throw new FileNotFoundException(
                $"The pinned CIFP bundle {bundledGz} does not exist; it is committed under tests/Yaat.Sim.Tests/TestData, "
                    + "so a missing one usually means an incomplete checkout. Restore it with "
                    + "`git checkout -- tests/Yaat.Sim.Tests/TestData/FAACIFP18.gz`, or run `python tools/refresh-navdata.py` to repin it.",
                bundledGz
            );
        }

        return DecompressBundledGzip(bundledGz, PinnedDecompressedPath(bundledGz, cycleId));
    }

    /// <summary>
    /// The content-keyed file a pinned resolve decompresses to: the manifest cycle and the bundle's SHA-256 keep two
    /// bundles apart, while identical bytes land on one name concurrent test processes share.
    /// </summary>
    private static string PinnedDecompressedPath(string bundledGz, string cycleId) =>
        Path.Combine(Path.GetTempPath(), "yaat-test-cifp", $"FAACIFP18-{cycleId}-{BundleHash(bundledGz)}.dat");

    /// <summary>The first 16 lowercase hex characters of the bundle's SHA-256.</summary>
    private static string BundleHash(string bundledGz)
    {
        using FileStream stream = File.OpenRead(bundledGz);
        return Convert.ToHexStringLower(SHA256.HashData(stream))[..16];
    }

    /// <summary>The AIRAC cycle the committed manifest records. The pin has no other source for it.</summary>
    private static string ReadPinnedCycle(CifpResolveOptions options)
    {
        if (options.BundledManifestPath is not { } manifestPath)
        {
            throw new InvalidOperationException(
                "CifpResolveOptions.PinToBundled needs BundledManifestPath: the committed tests/Yaat.Sim.Tests/TestData/cifp-manifest.json."
            );
        }

        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException(
                $"The pinned CIFP manifest {manifestPath} does not exist; it is committed under tests/Yaat.Sim.Tests/TestData, "
                    + "so a missing one usually means an incomplete checkout. Restore it with "
                    + "`git checkout -- tests/Yaat.Sim.Tests/TestData/cifp-manifest.json`, or run `python tools/refresh-navdata.py` to repin it.",
                manifestPath
            );
        }

        string? cycleId = ReadBundledCycle(options, out Exception? parseError);
        if (cycleId is not null)
        {
            return cycleId;
        }

        throw new InvalidOperationException(
            parseError is null
                ? $"The pinned CIFP manifest {manifestPath} has no airacCycle."
                : $"The pinned CIFP manifest {manifestPath} has no readable airacCycle: {parseError.Message}"
        );
    }

    private static string CacheDirectory(string cacheRoot) => Path.Combine(cacheRoot, "cache", "cifp");

    private static string GetCachePathForCycle(string cacheRoot, string cycleId) => Path.Combine(CacheDirectory(cacheRoot), $"FAACIFP18-{cycleId}");

    private static bool IsDownloadSkipped()
    {
        string? v = Environment.GetEnvironmentVariable("YAAT_SKIP_CIFP_DOWNLOAD");
        return string.Equals(v, "1", StringComparison.Ordinal) || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string?> DownloadCurrentCycleAsync(string cachePath, string cycleId, CancellationToken cancellationToken)
    {
        string cacheDir = Path.GetDirectoryName(cachePath)!;
        Directory.CreateDirectory(cacheDir);

        DateOnly cycleDate = AiracCycle.GetCycleDate(cycleId);
        string dateStr = cycleDate.ToString("yyMMdd");
        string url = $"{CifpBaseUrl}CIFP_{dateStr}.zip";

        try
        {
            Log.LogInformation("Downloading CIFP for AIRAC cycle {Cycle} from {Url}", cycleId, url);
            Console.Error.WriteLine($"[CifpPathResolver] Downloading CIFP for AIRAC {cycleId}...");

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
            byte[] zipBytes = await http.GetByteArrayAsync(url, cancellationToken).ConfigureAwait(false);

            using var zipStream = new MemoryStream(zipBytes);
            using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);

            ZipArchiveEntry? cifpEntry = archive.Entries.FirstOrDefault(e => e.Name.StartsWith("FAACIFP", StringComparison.Ordinal));
            if (cifpEntry is null)
            {
                Log.LogWarning("FAACIFP file not found in zip archive from {Url}", url);
                return null;
            }

            await using Stream entryStream = cifpEntry.Open();
            await using FileStream fileStream = File.Create(cachePath);
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

    private static (string? Path, string? CycleId) TryBundledOrStaleCache(CifpResolveOptions options, string cacheRoot, string currentCycleId)
    {
        if (options.BundledGzPath is { } bundledGz && File.Exists(bundledGz))
        {
            string? bundledCycle = ReadBundledCycle(options, out _);
            if (bundledCycle is not null && bundledCycle != currentCycleId)
            {
                Log.LogWarning(
                    "Using bundled CIFP cycle {BundledCycle} (current AIRAC {CurrentCycle}); procedure ids may differ from charts",
                    bundledCycle,
                    currentCycleId
                );
                Console.Error.WriteLine($"[CifpPathResolver] Warning: bundled CIFP is cycle {bundledCycle}, current AIRAC is {currentCycleId}.");
            }

            return (DecompressBundledGzip(bundledGz, BundledCachePath(cacheRoot)), bundledCycle);
        }

        string cacheDir = CacheDirectory(cacheRoot);
        if (Directory.Exists(cacheDir))
        {
            string? newest = Directory.EnumerateFiles(cacheDir, "FAACIFP18-*").OrderDescending(StringComparer.Ordinal).FirstOrDefault();
            if (newest is not null)
            {
                Log.LogWarning("Using stale CIFP cache file {Path} (current cycle {Cycle} not cached)", newest, currentCycleId);
                return (newest, CycleFromCacheFile(newest));
            }
        }

        return (null, null);
    }

    /// <summary>The AIRAC cycle a cache file is named for (<c>FAACIFP18-2611</c>), or null for a file that names none.</summary>
    private static string? CycleFromCacheFile(string path)
    {
        string name = Path.GetFileName(path);
        if (!name.StartsWith("FAACIFP18-", StringComparison.Ordinal))
        {
            return null;
        }

        string cycle = name["FAACIFP18-".Length..];
        return (cycle.Length == 4) && cycle.All(char.IsAsciiDigit) ? cycle : null;
    }

    private static string? ReadBundledCycle(CifpResolveOptions options, out Exception? parseError)
    {
        parseError = null;
        if (options.BundledManifestPath is not { } manifestPath || !File.Exists(manifestPath))
        {
            return null;
        }

        try
        {
            string json = File.ReadAllText(manifestPath);
            CifpManifest? doc = JsonSerializer.Deserialize<CifpManifest>(json, ManifestJsonOptions);
            return doc?.AiracCycle;
        }
        catch (Exception ex)
        {
            Log.LogDebug(ex, "Could not read CIFP manifest {Path}", manifestPath);
            parseError = ex;
            return null;
        }
    }

    /// <summary>
    /// Default recency cap for the supplementary CIFP chain: a procedure may be resurrected from a cached
    /// cycle at most this many AIRAC cycles (~1 year) older than the current one. Bounds how stale a
    /// resurrected procedure definition can be — an old cycle could encode an initial heading or crossing
    /// restriction the FAA retired <em>because</em> the airspace changed.
    /// </summary>
    public const int MaxSupplementaryLookbackCycles = 13;

    /// <summary>
    /// Resolves the supplementary CIFP chain from the local cache: cached AIRAC cycles strictly older than
    /// <paramref name="currentCycleId"/> and within <paramref name="maxLookbackCycles"/> cycle-age, ordered
    /// newest→oldest. A procedure absent from the current cycle's file — e.g. a SID dropped during an
    /// amendment/rename gap (NIMI5 → NIMI6), or retired a few cycles back — is resolved by walking this
    /// chain to the most recent cycle that still carries it. The app caches each cycle's CIFP it downloads,
    /// so the chain auto-accumulates with no shipped data. The recency cap is measured by cycle age (not
    /// cache contents), so an unexpectedly-old cached cycle can't silently resurrect a long-retired
    /// procedure. Returns an empty list when no eligible older cached cycle exists (e.g. a fresh install).
    /// </summary>
    public static IReadOnlyList<string> ResolveSupplementaryChainFromCache(string currentCycleId, int maxLookbackCycles) =>
        ResolveSupplementaryChainFromCache(currentCycleId, maxLookbackCycles, YaatPaths.Combine("cache", "cifp"));

    internal static IReadOnlyList<string> ResolveSupplementaryChainFromCache(string currentCycleId, int maxLookbackCycles, string cacheDir)
    {
        if (!Directory.Exists(cacheDir))
        {
            return [];
        }

        var cycles = new List<string>();
        foreach (string path in Directory.EnumerateFiles(cacheDir, "FAACIFP18-*"))
        {
            string cycle = Path.GetFileName(path)["FAACIFP18-".Length..];

            // AIRAC cycle ids are 4 ASCII digits (YYNN); skip the bundled file and any other artifacts.
            if (cycle.Length != 4 || !cycle.All(char.IsAsciiDigit))
            {
                continue;
            }

            // Strictly older than current, and within the recency cap — measured by cycle age (handles
            // the year wrap), not string distance.
            int age = AiracCycle.CyclesBetween(cycle, currentCycleId);
            if (age <= 0 || age > maxLookbackCycles)
            {
                continue;
            }

            cycles.Add(cycle);
        }

        // Newest→oldest (same-length 4-digit YYNN ids order correctly as strings).
        cycles.Sort((a, b) => string.CompareOrdinal(b, a));

        if (cycles.Count > 0)
        {
            Log.LogInformation(
                "Supplementary CIFP chain resolved from {Count} cached prior cycle(s) [{Cycles}] (current {Current})",
                cycles.Count,
                string.Join(", ", cycles),
                currentCycleId
            );
        }

        return [.. cycles.Select(c => Path.Combine(cacheDir, "FAACIFP18-" + c))];
    }

    /// <summary>
    /// Decompresses <see cref="CifpResolveOptions.BundledGzPath"/> for supplementary procedure lookup
    /// (retired SIDs absent from the current FAA cycle). Returns null when no bundle is configured. A pinned
    /// resolve writes the same content-keyed temp file <see cref="PinToBundledFile"/> does, never the user cache.
    /// </summary>
    public static string? ResolveSupplementaryBundledPath(CifpResolveOptions options)
    {
        if (options.BundledGzPath is not { } gzPath || !File.Exists(gzPath))
        {
            return null;
        }

        string outPath = options.PinToBundled ? PinnedDecompressedPath(gzPath, ReadPinnedCycle(options)) : BundledCachePath(YaatPaths.AppDataRoot);
        return DecompressBundledGzip(gzPath, outPath);
    }

    /// <summary>
    /// Decompresses <paramref name="gzPath"/> into <paramref name="outPath"/>, creating the output folder. An existing
    /// output file is reused only when it is at least as new as the bundle and its length matches the bundle's
    /// uncompressed length (the gzip ISIZE trailer), so a truncated cache file is replaced.
    /// </summary>
    /// <param name="gzPath">The gzipped CIFP bundle.</param>
    /// <param name="outPath">The decompressed file to reuse or write.</param>
    public static string DecompressBundledGzip(string gzPath, string outPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);

        long? expectedLength = ReadUncompressedLength(gzPath);
        if (File.Exists(outPath))
        {
            FileInfo cached = new(outPath);
            bool newerThanBundle = new FileInfo(gzPath).LastWriteTimeUtc <= cached.LastWriteTimeUtc;
            bool lengthMatches = expectedLength is not { } expected || cached.Length == expected;
            if ((newerThanBundle) && (lengthMatches))
            {
                return outPath;
            }

            if ((newerThanBundle) && (expectedLength is { } size))
            {
                Log.LogInformation(
                    "Replacing cached bundled CIFP {Path}: it holds {Actual} bytes, but the bundle decompresses to {Expected}",
                    outPath,
                    cached.Length,
                    size
                );
            }
        }

        DecompressToFile(gzPath, outPath, expectedLength);
        return outPath;
    }

    /// <summary>Reads the gzip ISIZE trailer: the uncompressed length modulo 2^32, stored little-endian.</summary>
    /// <param name="gzPath">The gzip file.</param>
    /// <returns>The uncompressed length, or null when the file is too short to hold a gzip trailer.</returns>
    private static long? ReadUncompressedLength(string gzPath)
    {
        using FileStream stream = File.OpenRead(gzPath);
        if (stream.Length < 4)
        {
            return null;
        }

        Span<byte> trailer = stackalloc byte[4];
        stream.Seek(-4, SeekOrigin.End);
        stream.ReadExactly(trailer);
        return BinaryPrimitives.ReadUInt32LittleEndian(trailer);
    }

    // Mirrors Yaat.Client.Core's AtomicFile, which Yaat.Sim cannot reference: a replacing move is denied while another
    // process holds the target open without delete sharing, as an antivirus scan of a just-written file does for a few
    // milliseconds, so it is retried with a growing wait (about 0.7 s in all) before the last failure is thrown.
    private const int MaxMoveAttempts = 8;
    private const int RetryStepMs = 25;

    private static void DecompressToFile(string gzPath, string outPath, long? expectedLength)
    {
        string tmpPath = outPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (FileStream inputStream = File.OpenRead(gzPath))
            using (var gzipStream = new GZipStream(inputStream, CompressionMode.Decompress))
            using (FileStream outputStream = File.Create(tmpPath))
            {
                gzipStream.CopyTo(outputStream);
            }

            bool replaced = ReplaceWithRetry(tmpPath, outPath, expectedLength);
            if (!replaced)
            {
                Log.LogDebug("{Path} was completed by another process; kept its {Expected} bytes", outPath, expectedLength);
            }
        }
        finally
        {
            DeleteTemp(tmpPath);
        }
    }

    /// <summary>
    /// Moves <paramref name="tmpPath"/> over <paramref name="outPath"/>, retrying a denied move. Returns false when
    /// another process completed the target with the expected length while this one was retrying.
    /// </summary>
    private static bool ReplaceWithRetry(string tmpPath, string outPath, long? expectedLength)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(tmpPath, outPath, overwrite: true);
                return true;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                if (AnotherWriterCompleted(outPath, expectedLength))
                {
                    Log.LogDebug(ex, "Replacing {Path} was denied, but it now holds the expected {Expected} bytes", outPath, expectedLength);
                    return false;
                }

                if (attempt >= MaxMoveAttempts)
                {
                    throw;
                }

                Log.LogDebug(ex, "Replacing {Path} failed on attempt {Attempt}; retrying", outPath, attempt);
                Thread.Sleep(RetryStepMs * attempt);
            }
        }
    }

    private static bool AnotherWriterCompleted(string outPath, long? expectedLength) =>
        (expectedLength is { } expected) && (File.Exists(outPath)) && (new FileInfo(outPath).Length == expected);

    private static void DeleteTemp(string tmpPath)
    {
        if (!File.Exists(tmpPath))
        {
            return;
        }

        try
        {
            File.Delete(tmpPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cleanup of a temp file: rethrowing here would mask the outcome of the move that already happened.
            Log.LogWarning(ex, "Could not delete temporary file {Path}", tmpPath);
        }
    }

    private static string BundledCachePath(string cacheRoot) => Path.Combine(CacheDirectory(cacheRoot), "FAACIFP18-bundled.dat");

    private sealed class CifpManifest
    {
        public string? AiracCycle { get; init; }
    }
}
