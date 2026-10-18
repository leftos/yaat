using System.IO.Compression;
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
        var options = new CifpResolveOptions(
            BundledGzPath: Path.Combine(testDataDir, "FAACIFP18.gz"),
            BundledManifestPath: Path.Combine(testDataDir, "cifp-manifest.json"),
            AllowDownload: string.IsNullOrEmpty(Environment.GetEnvironmentVariable("YAAT_SKIP_CIFP_DOWNLOAD"))
        );

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
        Assert.Contains(File.ReadLines(result), line => line.Contains("NARCOLLN", StringComparison.Ordinal));
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

        // The message has to say how to recover, not just that something is wrong.
        Assert.Contains("FAACIFP18.gz", ex.Message, StringComparison.Ordinal);
        Assert.Contains("YAAT_SKIP_CIFP_DOWNLOAD", ex.Message, StringComparison.Ordinal);
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
