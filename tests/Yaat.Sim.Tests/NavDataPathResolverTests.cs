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
}
