using Xunit;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim.Tests;

public class NavDataPathResolverTests
{
    [Fact]
    public void EnsureCurrent_SecondCall_ReturnsSamePathWithoutReDownload()
    {
        var testDataDir = Path.Combine(AppContext.BaseDirectory, "TestData");
        var options = new NavDataResolveOptions(
            BundledPath: Path.Combine(testDataDir, "NavData.dat"),
            BundledManifestPath: Path.Combine(testDataDir, "navdata-manifest.json"),
            AllowDownload: string.IsNullOrEmpty(Environment.GetEnvironmentVariable("YAAT_SKIP_NAVDATA_DOWNLOAD"))
        );

        var first = NavDataPathResolver.EnsureCurrent(options);
        var second = NavDataPathResolver.EnsureCurrent(options);

        Assert.Equal(first, second);
        if (first is not null)
        {
            Assert.True(File.Exists(first));
        }
    }
}
