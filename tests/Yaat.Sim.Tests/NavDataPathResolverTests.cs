using System.Globalization;
using System.Text.Json;
using Xunit;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim.Tests;

public class NavDataPathResolverTests
{
    [Fact]
    public void EnsureCurrent_SecondCall_ReturnsSamePathWithoutReDownload()
    {
        string testDataDir = Path.Combine(AppContext.BaseDirectory, "TestData");
        // Pinned, matching ModuleInit. It is inert here either way — ModuleInit has already resolved,
        // so EnsureCurrent short-circuits — but a test-path resolve that was not pinned would read the
        // developer's user cache the moment that stops being true.
        var options = new NavDataResolveOptions(
            BundledPath: Path.Combine(testDataDir, "NavData.dat"),
            BundledManifestPath: Path.Combine(testDataDir, "navdata-manifest.json"),
            AllowDownload: false
        )
        {
            PinToBundled = true,
        };

        string? first = NavDataPathResolver.EnsureCurrent(options);
        string? second = NavDataPathResolver.EnsureCurrent(options);

        Assert.Equal(first, second);
        if (first is not null)
        {
            Assert.True(File.Exists(first));
        }
    }

    [Fact]
    public void TestProcess_ResolvesNavData_WithoutContactingVnas()
    {
        // ModuleInit resolves NavData at assembly load, before any test runs. It must do that from
        // the bundled/cached copy alone: a live GET to the vNAS configuration API costs ~240 ms of
        // TLS handshake on EVERY test process (measured 2026-09-04, ~10% of the fixed cost of a
        // filtered run) and makes the whole suite depend on VATSIM infrastructure being reachable.
        //
        // The trade-off this locks in: tests no longer warn when the bundled NavData serial is
        // behind what vNAS publishes. `python tools/refresh-navdata.py` is the signal for that.
        Assert.Equal(0, NavDataPathResolver.ConfigFetchCount);

        // ...and the resolve still has to have produced a usable file, or the assertion above would
        // pass simply because nothing resolved at all.
        Assert.NotNull(NavDataPathResolver.CachedPath);
        Assert.True(File.Exists(NavDataPathResolver.CachedPath));
    }

    // Missing navdata is fatal for this assembly rather than a silent skip: most of the suite
    // asserts against real navdata, so a run without it proves nothing while reporting green.

    [Fact]
    public void RequireNavData_Throws_WhenNothingResolved()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => ModuleInit.RequireNavData(null));

        // The message has to say how to recover, not just that something is wrong.
        Assert.Contains("refresh-navdata.py", ex.Message, StringComparison.Ordinal);
        Assert.Contains("TestData/NavData.dat", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RequireNavData_Throws_WhenResolvedPathDoesNotExist()
    {
        // A non-null path that points at nothing is the more dangerous case: it looks resolved.
        string missing = Path.Combine(Path.GetTempPath(), $"yaat-absent-navdata-{Guid.NewGuid():N}.dat");

        Assert.Throws<InvalidOperationException>(() => ModuleInit.RequireNavData(missing));
    }

    [Fact]
    public void RequireNavData_ReturnsPath_WhenPresent()
    {
        string? resolved = NavDataPathResolver.CachedPath;
        Assert.NotNull(resolved);

        Assert.Equal(resolved, ModuleInit.RequireNavData(resolved));
    }

    [Fact]
    public void ModuleInit_PinsTheCommittedNavData()
    {
        // ModuleInit resolved at assembly load: the file is the committed TestData copy, and it reports the
        // committed manifest's serial.
        string testDataDir = Path.Combine(AppContext.BaseDirectory, "TestData");

        Assert.Equal(
            Path.GetFullPath(Path.Combine(testDataDir, "NavData.dat")),
            Path.GetFullPath(Assert.IsType<string>(NavDataPathResolver.CachedPath))
        );
        Assert.Equal(ReadManifestSerial(Path.Combine(testDataDir, "navdata-manifest.json")), NavDataPathResolver.ResolvedNavDataSerial);
    }

    [Fact]
    public void HasLocalCopy_IsTrue_ForTheCommittedBundledCopy()
    {
        // TestData/NavData.dat is committed, so a healthy checkout always resolves offline. This is
        // what keeps the config fetch (and its ~240 ms) off the normal path.
        string testDataDir = Path.Combine(AppContext.BaseDirectory, "TestData");
        var options = new NavDataResolveOptions(
            BundledPath: Path.Combine(testDataDir, "NavData.dat"),
            BundledManifestPath: Path.Combine(testDataDir, "navdata-manifest.json"),
            AllowDownload: false
        )
        {
            PinToBundled = true,
        };

        Assert.True(NavDataPathResolver.HasLocalCopy(options));
    }

    [Fact]
    public void PinnedNavData_IgnoresAUserCacheOfAnotherCycle()
    {
        // A pinned resolve reads only the committed TestData copy. A developer whose user cache holds
        // another serial must still get the committed file and the committed manifest's serial, or the
        // suite runs a different AIRAC cycle on every machine.
        string testDataDir = Path.Combine(AppContext.BaseDirectory, "TestData");
        string cacheRoot = Directory.CreateTempSubdirectory("yaat-navdata-pin-").FullName;
        try
        {
            string cacheDir = Path.Combine(cacheRoot, "cache");
            Directory.CreateDirectory(cacheDir);
            File.WriteAllBytes(Path.Combine(cacheDir, "NavData.dat"), [0x0A, 0x00, 0x00]);
            File.WriteAllText(Path.Combine(cacheDir, "manifest.json"), "{\"navDataSerial\": 2025010101}");

            var options = new NavDataResolveOptions(
                BundledPath: Path.Combine(testDataDir, "NavData.dat"),
                BundledManifestPath: Path.Combine(testDataDir, "navdata-manifest.json"),
                AllowDownload: false
            )
            {
                PinToBundled = true,
            };

            (string? path, long? serial) = NavDataPathResolver.ResolveCore(options, cacheRoot);

            Assert.Equal(Path.GetFullPath(Path.Combine(testDataDir, "NavData.dat")), Path.GetFullPath(Assert.IsType<string>(path)));
            Assert.Equal(ReadManifestSerial(Path.Combine(testDataDir, "navdata-manifest.json")), serial);
            Assert.NotEqual<long?>(2025010101L, serial);
        }
        finally
        {
            Directory.Delete(cacheRoot, recursive: true);
        }
    }

    private static long ReadManifestSerial(string manifestPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        return long.Parse(doc.RootElement.GetProperty("navDataSerial").GetRawText(), NumberStyles.Integer, CultureInfo.InvariantCulture);
    }
}
