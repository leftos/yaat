using Xunit;
using Yaat.Sim.Data;

namespace Yaat.Sim.Tests;

public sealed class InitialContactTransferLoaderTests
{
    [Fact]
    public void LoadAll_LoadsArtccScopedTransferRules()
    {
        var root = Path.Combine(Path.GetTempPath(), "yaat-initial-contact-transfer-tests", Guid.NewGuid().ToString("N"));
        var category = Path.Combine(root, "ZOA", "InitialContactTransfers");
        Directory.CreateDirectory(category);
        File.WriteAllText(
            Path.Combine(category, "sfo.json"),
            """
            [
              {
                "airportId": "SFO",
                "fromPositionType": "DEP",
                "toPositionType": "LC",
                "allowsWithoutTrackHandoff": true
              }
            ]
            """
        );

        try
        {
            var result = InitialContactTransferLoader.LoadAll(root);
            var catalog = new InitialContactTransferCatalog(result.Rules);

            Assert.Empty(result.Warnings);
            Assert.Single(result.Rules);
            Assert.True(catalog.AllowsWithoutTrackHandoff("ZOA", "KSFO", "APP", "TWR"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
