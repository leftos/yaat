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
}
