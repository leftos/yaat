using Xunit;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim.Tests;

public class NavDataPathResolverTests
{
    [Fact]
    public void EnsureCurrent_SecondCall_ReturnsSamePathWithoutReDownload()
    {
        var testDataDir = Path.Combine(AppContext.BaseDirectory, "TestData");
        // AllowDownload: false, matching ModuleInit. It is inert here either way — ModuleInit has
        // already resolved, so EnsureCurrent short-circuits — but leaving it true would mean this
        // test issues a vNAS config fetch the moment that stops being true, breaking
        // TestProcess_ResolvesNavData_WithoutContactingVnas below from a sibling test.
        var options = new NavDataResolveOptions(
            BundledPath: Path.Combine(testDataDir, "NavData.dat"),
            BundledManifestPath: Path.Combine(testDataDir, "navdata-manifest.json"),
            AllowDownload: false
        );

        var first = NavDataPathResolver.EnsureCurrent(options);
        var second = NavDataPathResolver.EnsureCurrent(options);

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
        var ex = Assert.Throws<InvalidOperationException>(() => ModuleInit.RequireNavData(null));

        // The message has to say how to recover, not just that something is wrong.
        Assert.Contains("refresh-navdata.py", ex.Message, StringComparison.Ordinal);
        Assert.Contains("TestData/NavData.dat", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RequireNavData_Throws_WhenResolvedPathDoesNotExist()
    {
        // A non-null path that points at nothing is the more dangerous case: it looks resolved.
        var missing = Path.Combine(Path.GetTempPath(), $"yaat-absent-navdata-{Guid.NewGuid():N}.dat");

        Assert.Throws<InvalidOperationException>(() => ModuleInit.RequireNavData(missing));
    }

    [Fact]
    public void RequireNavData_ReturnsPath_WhenPresent()
    {
        var resolved = NavDataPathResolver.CachedPath;
        Assert.NotNull(resolved);

        Assert.Equal(resolved, ModuleInit.RequireNavData(resolved));
    }

    [Fact]
    public void HasLocalCopy_IsTrue_ForTheCommittedBundledCopy()
    {
        // TestData/NavData.dat is committed, so a healthy checkout always resolves offline. This is
        // what keeps the config fetch (and its ~240 ms) off the normal path.
        var testDataDir = Path.Combine(AppContext.BaseDirectory, "TestData");
        var options = new NavDataResolveOptions(
            BundledPath: Path.Combine(testDataDir, "NavData.dat"),
            BundledManifestPath: Path.Combine(testDataDir, "navdata-manifest.json"),
            AllowDownload: false
        );

        Assert.True(NavDataPathResolver.HasLocalCopy(options));
    }
}
