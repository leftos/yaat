using System.IO.Compression;
using System.Text.Json;
using Xunit;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim.Tests;

public class CifpPathResolverTests : IDisposable
{
    private static readonly string BundledGz = Path.Combine(AppContext.BaseDirectory, "TestData", "FAACIFP18.gz");

    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        foreach (string dir in _tempDirs)
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void EnsureCurrentCycle_SecondCall_ReturnsSamePathWithoutReDownload()
    {
        string testDataDir = Path.Combine(AppContext.BaseDirectory, "TestData");
        // Pinned, matching ModuleInit; inert here because ModuleInit already resolved.
        var options = new CifpResolveOptions(
            BundledGzPath: Path.Combine(testDataDir, "FAACIFP18.gz"),
            BundledManifestPath: Path.Combine(testDataDir, "cifp-manifest.json"),
            AllowDownload: false
        )
        {
            PinToBundled = true,
        };

        string? first = CifpPathResolver.EnsureCurrentCycle(options);
        string? second = CifpPathResolver.EnsureCurrentCycle(options);

        Assert.Equal(first, second);
        if (first is not null)
        {
            Assert.True(File.Exists(first));
        }
    }

    [Fact]
    public void DecompressBundledGzip_TruncatedCacheNewerThanTheGz_IsReplaced()
    {
        string dir = NewTempDir();
        string outPath = Path.Combine(dir, "FAACIFP18-bundled.dat");

        byte[] full = Decompress(BundledGz);
        const int truncatedLength = 1024 * 1024;
        Assert.True(full.Length > truncatedLength);
        File.WriteAllBytes(outPath, full[..truncatedLength]);
        File.SetLastWriteTimeUtc(outPath, new FileInfo(BundledGz).LastWriteTimeUtc.AddMinutes(1));

        string result = CifpPathResolver.DecompressBundledGzip(BundledGz, outPath);

        Assert.Equal(outPath, result);
        Assert.Equal(full.Length, new FileInfo(result).Length);
        Assert.Contains(File.ReadLines(result), line => line.Contains("NARASHLY", StringComparison.Ordinal));
    }

    [Fact]
    public void DecompressBundledGzip_ValidCache_IsReused()
    {
        string dir = NewTempDir();
        string outPath = Path.Combine(dir, "FAACIFP18-bundled.dat");
        CifpPathResolver.DecompressBundledGzip(BundledGz, outPath);

        DateTime written = new FileInfo(BundledGz).LastWriteTimeUtc.AddMinutes(1);
        File.SetLastWriteTimeUtc(outPath, written);

        CifpPathResolver.DecompressBundledGzip(BundledGz, outPath);

        Assert.Equal(written, File.GetLastWriteTimeUtc(outPath));
    }

    [Fact]
    public void DecompressBundledGzip_LeavesNoTempFiles()
    {
        string dir = NewTempDir();
        string outPath = Path.Combine(dir, "FAACIFP18-bundled.dat");

        CifpPathResolver.DecompressBundledGzip(BundledGz, outPath);

        Assert.Equal(new[] { outPath }, Directory.GetFiles(dir));
    }

    // Missing CIFP is fatal for this assembly rather than a silent skip: procedures, approaches and
    // SIDs come from it, and the tests that exercise them return early in its absence, so the run
    // would report green having proved nothing.

    [Fact]
    public void RequireCifp_Throws_WhenNothingResolved()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => ModuleInit.RequireCifp(null));

        // The message has to say how to recover, not just that something is wrong: the committed
        // bundle the suite pins, and the refresh that moves the pin.
        Assert.Contains("FAACIFP18.gz", ex.Message, StringComparison.Ordinal);
        Assert.Contains("refresh-navdata.py", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RequireCifp_Throws_WhenResolvedPathDoesNotExist()
    {
        // A non-null path that points at nothing is the more dangerous case: it looks resolved.
        string missing = Path.Combine(Path.GetTempPath(), $"yaat-absent-cifp-{Guid.NewGuid():N}");

        Assert.Throws<InvalidOperationException>(() => ModuleInit.RequireCifp(missing));
    }

    [Fact]
    public void RequireCifp_ReturnsPath_WhenPresent()
    {
        // ModuleInit resolved CIFP at assembly load, or this assembly would not have loaded.
        string? resolved = CifpPathResolver.CachedPath;
        Assert.NotNull(resolved);

        Assert.Equal(resolved, ModuleInit.RequireCifp(resolved));
    }

    [Fact]
    public void PinnedCifp_IgnoresACachedCycleFile()
    {
        // A pinned resolve reads only the committed bundle. The cache file an unpinned resolve would
        // take first is the current cycle's, so that is the decoy: the pin must not touch it.
        string testDataDir = Path.Combine(AppContext.BaseDirectory, "TestData");
        string dir = NewTempDir();
        string cacheDir = Path.Combine(dir, "cache", "cifp");
        Directory.CreateDirectory(cacheDir);
        string cachedCycleFile = Path.Combine(cacheDir, $"FAACIFP18-{AiracCycle.GetCurrentCycleId()}");
        File.WriteAllText(cachedCycleFile, "decoy: not the committed bundle");

        var options = new CifpResolveOptions(
            BundledGzPath: Path.Combine(testDataDir, "FAACIFP18.gz"),
            BundledManifestPath: Path.Combine(testDataDir, "cifp-manifest.json"),
            AllowDownload: false
        )
        {
            PinToBundled = true,
        };

        (string? path, string? cycleId) = CifpPathResolver.ResolveCore(options, dir);

        Assert.NotNull(path);
        Assert.NotEqual(cachedCycleFile, path);
        Assert.Equal(ReadManifestCycle(Path.Combine(testDataDir, "cifp-manifest.json")), cycleId);
        Assert.Contains(File.ReadLines(path), line => line.Contains("NARASHLY", StringComparison.Ordinal));
    }

    [Fact]
    public void PinnedCifp_DecompressesOutsideTheUserCache_ToAContentKeyedFile()
    {
        // The pin's decompressed copy is a second home for the bundle: it must never land in the cache root the
        // unpinned path writes, nor in the user's real app-data folder, or two test processes would race on one
        // file. The name carries the manifest cycle and the bundle's SHA-256.
        string testDataDir = Path.Combine(AppContext.BaseDirectory, "TestData");
        string dir = NewTempDir();
        string cacheRoot = Path.Combine(dir, "cache-root");
        Directory.CreateDirectory(cacheRoot);
        string manifestCycle = ReadManifestCycle(Path.Combine(testDataDir, "cifp-manifest.json"));

        var options = new CifpResolveOptions(
            BundledGzPath: Path.Combine(testDataDir, "FAACIFP18.gz"),
            BundledManifestPath: Path.Combine(testDataDir, "cifp-manifest.json"),
            AllowDownload: false
        )
        {
            PinToBundled = true,
        };

        (string? path, string? cycleId) = CifpPathResolver.ResolveCore(options, cacheRoot);

        string resolved = Path.GetFullPath(Assert.IsType<string>(path));
        Assert.False(IsUnder(resolved, Path.GetFullPath(cacheRoot)));
        Assert.False(IsUnder(resolved, Path.GetFullPath(YaatPaths.AppDataRoot)));
        Assert.True(IsUnder(resolved, Path.GetFullPath(Path.Combine(Path.GetTempPath(), "yaat-test-cifp"))));
        Assert.Equal(manifestCycle, cycleId);

        string fileName = Path.GetFileName(resolved);
        Assert.Contains(manifestCycle, fileName, StringComparison.Ordinal);
        Assert.Matches("-[0-9a-f]{16}\\.dat$", fileName);
        Assert.True(File.Exists(resolved));
    }

    [Fact]
    public void SupplementaryBundledPath_Pinned_IsThePinnedPrimaryFile_NotTheUserCache()
    {
        // The supplementary chain's bundled fallback is a second home for the same bundle: a pinned resolve must
        // reuse the content-keyed temp file the primary pin wrote, never the user's app-data cache.
        string testDataDir = Path.Combine(AppContext.BaseDirectory, "TestData");
        var options = new CifpResolveOptions(
            BundledGzPath: Path.Combine(testDataDir, "FAACIFP18.gz"),
            BundledManifestPath: Path.Combine(testDataDir, "cifp-manifest.json"),
            AllowDownload: false
        )
        {
            PinToBundled = true,
        };

        string supplementary = Path.GetFullPath(Assert.IsType<string>(CifpPathResolver.ResolveSupplementaryBundledPath(options)));

        Assert.False(IsUnder(supplementary, Path.GetFullPath(YaatPaths.AppDataRoot)));
        Assert.Equal(Path.GetFullPath(Assert.IsType<string>(CifpPathResolver.CachedPath)), supplementary);
    }

    [Fact]
    public void PinnedCifp_DifferentBundleContent_LandsOnADifferentFile()
    {
        // A content-keyed name keeps two bundles apart: a pin over any other committed bundle must not reuse the
        // file the committed one already wrote.
        string testDataDir = Path.Combine(AppContext.BaseDirectory, "TestData");
        string dir = NewTempDir();
        string cacheRoot = Path.Combine(dir, "cache-root");
        string manifestPath = Path.Combine(testDataDir, "cifp-manifest.json");
        string otherGz = Path.Combine(dir, "FAACIFP18-other.gz");
        using (FileStream output = File.Create(otherGz))
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize))
        {
            gzip.Write("another bundle, not the committed one"u8);
        }

        var committedOptions = new CifpResolveOptions(
            BundledGzPath: Path.Combine(testDataDir, "FAACIFP18.gz"),
            BundledManifestPath: manifestPath,
            AllowDownload: false
        )
        {
            PinToBundled = true,
        };
        var otherOptions = new CifpResolveOptions(BundledGzPath: otherGz, BundledManifestPath: manifestPath, AllowDownload: false)
        {
            PinToBundled = true,
        };

        (string? committedPath, _) = CifpPathResolver.ResolveCore(committedOptions, cacheRoot);
        (string? otherPath, _) = CifpPathResolver.ResolveCore(otherOptions, cacheRoot);

        Assert.NotNull(committedPath);
        Assert.NotNull(otherPath);
        Assert.NotEqual(committedPath, otherPath);
    }

    [Fact]
    public void ModuleInit_PinsTheCommittedCifp()
    {
        // ModuleInit resolved at assembly load: the file is the committed bundle's decompressed copy, outside the
        // user's app-data folder, and it reports the committed manifest's cycle rather than today's.
        string testDataDir = Path.Combine(AppContext.BaseDirectory, "TestData");
        string resolved = Path.GetFullPath(Assert.IsType<string>(CifpPathResolver.CachedPath));

        Assert.True(File.Exists(resolved));
        Assert.False(IsUnder(resolved, Path.GetFullPath(YaatPaths.AppDataRoot)));
        Assert.Equal(ReadManifestCycle(Path.Combine(testDataDir, "cifp-manifest.json")), CifpPathResolver.ResolvedCycleId);
    }

    [Fact]
    public void UnpinnedCifp_BundledFallback_ReportsTheCopiedManifestsCycle()
    {
        // The bundled fallback's reported cycle is the one its manifest records, whatever today's AIRAC is.
        string testDataDir = Path.Combine(AppContext.BaseDirectory, "TestData");
        string dir = NewTempDir();
        string cacheRoot = Path.Combine(dir, "cache-root");
        Directory.CreateDirectory(cacheRoot);
        string manifestCopy = Path.Combine(dir, "cifp-manifest.json");
        File.WriteAllText(manifestCopy, "{\"airacCycle\": \"2501\"}");

        var options = new CifpResolveOptions(
            BundledGzPath: Path.Combine(testDataDir, "FAACIFP18.gz"),
            BundledManifestPath: manifestCopy,
            AllowDownload: false
        )
        {
            PinToBundled = false,
        };

        (string? path, string? cycleId) = CifpPathResolver.ResolveCore(options, cacheRoot);

        Assert.Equal("2501", cycleId);
        Assert.True(File.Exists(Assert.IsType<string>(path)));
    }

    [Fact]
    public void UnpinnedCifp_ExplicitPath_ReportsNoCycle()
    {
        string dir = NewTempDir();
        string explicitPath = Path.Combine(dir, "FAACIFP18-explicit.dat");
        File.WriteAllText(explicitPath, "an explicit CIFP file names no cycle");

        var options = new CifpResolveOptions(ExplicitPath: explicitPath, AllowDownload: false) { PinToBundled = false };

        (string? path, string? cycleId) = CifpPathResolver.ResolveCore(options, Path.Combine(dir, "cache-root"));

        Assert.Equal(explicitPath, path);
        Assert.Null(cycleId);
    }

    [Fact]
    public void BundledCifp_ReportsTheManifestCycle_NotTodays()
    {
        // The reported cycle is the one the resolved file carries. A manifest copied with a cycle three
        // AIRAC periods from today's separates it from the date arithmetic the resolver used to report.
        string testDataDir = Path.Combine(AppContext.BaseDirectory, "TestData");
        string dir = NewTempDir();
        string today = AiracCycle.GetCurrentCycleId();
        string otherCycle = AiracCycle.GetCycleId(AiracCycle.GetCycleDate(today).AddDays(28 * 3));
        Assert.NotEqual(today, otherCycle);

        string manifestCopy = Path.Combine(dir, "cifp-manifest.json");
        File.WriteAllText(manifestCopy, $"{{\"airacCycle\": \"{otherCycle}\"}}");

        var options = new CifpResolveOptions(
            BundledGzPath: Path.Combine(testDataDir, "FAACIFP18.gz"),
            BundledManifestPath: manifestCopy,
            AllowDownload: false
        )
        {
            PinToBundled = true,
        };

        (string? path, string? cycleId) = CifpPathResolver.ResolveCore(options, dir);

        Assert.Equal(otherCycle, cycleId);
        Assert.NotNull(path);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void PinManifests_AgreeOnTheCycle()
    {
        // The two pins must move together: a NavData repin that leaves the CIFP manifest on the old
        // cycle would run the suite on mismatched procedure and route data.
        string testDataDir = Path.Combine(AppContext.BaseDirectory, "TestData");

        Assert.Equal(
            ReadManifestCycle(Path.Combine(testDataDir, "navdata-manifest.json")),
            ReadManifestCycle(Path.Combine(testDataDir, "cifp-manifest.json"))
        );
    }

    private static string ReadManifestCycle(string manifestPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        return Assert.IsType<string>(doc.RootElement.GetProperty("airacCycle").GetString());
    }

    private static bool IsUnder(string path, string folder) =>
        path.StartsWith(folder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"yaat-cifp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private static byte[] Decompress(string gzPath)
    {
        using FileStream input = File.OpenRead(gzPath);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }
}
