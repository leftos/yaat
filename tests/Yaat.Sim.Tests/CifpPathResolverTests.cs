using Xunit;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim.Tests;

public class CifpPathResolverTests
{
    [Fact]
    public void EnsureCurrentCycle_SecondCall_ReturnsSamePathWithoutReDownload()
    {
        var testDataDir = Path.Combine(AppContext.BaseDirectory, "TestData");
        var options = new CifpResolveOptions(
            BundledGzPath: Path.Combine(testDataDir, "FAACIFP18.gz"),
            BundledManifestPath: Path.Combine(testDataDir, "cifp-manifest.json"),
            AllowDownload: string.IsNullOrEmpty(Environment.GetEnvironmentVariable("YAAT_SKIP_CIFP_DOWNLOAD"))
        );

        var first = CifpPathResolver.EnsureCurrentCycle(options);
        var second = CifpPathResolver.EnsureCurrentCycle(options);

        Assert.Equal(first, second);
        if (first is not null)
        {
            Assert.True(File.Exists(first));
        }
    }

    // Missing CIFP is fatal for this assembly rather than a silent skip: procedures, approaches and
    // SIDs come from it, and the tests that exercise them return early in its absence, so the run
    // would report green having proved nothing.

    [Fact]
    public void RequireCifp_Throws_WhenNothingResolved()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ModuleInit.RequireCifp(null));

        // The message has to say how to recover, not just that something is wrong.
        Assert.Contains("FAACIFP18.gz", ex.Message, StringComparison.Ordinal);
        Assert.Contains("YAAT_SKIP_CIFP_DOWNLOAD", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RequireCifp_Throws_WhenResolvedPathDoesNotExist()
    {
        // A non-null path that points at nothing is the more dangerous case: it looks resolved.
        var missing = Path.Combine(Path.GetTempPath(), $"yaat-absent-cifp-{Guid.NewGuid():N}");

        Assert.Throws<InvalidOperationException>(() => ModuleInit.RequireCifp(missing));
    }

    [Fact]
    public void RequireCifp_ReturnsPath_WhenPresent()
    {
        // ModuleInit resolved CIFP at assembly load, or this assembly would not have loaded.
        var resolved = CifpPathResolver.CachedPath;
        Assert.NotNull(resolved);

        Assert.Equal(resolved, ModuleInit.RequireCifp(resolved));
    }
}
