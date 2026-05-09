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
                "fromCallsign": "nct_app",
                "toCallsign": "sfo_twr",
                "contactAllowedWhen": "noHandoffNecessary"
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
            Assert.True(
                catalog.AllowsInitialContact("ZOA", ["KSFO"], "APP", "NCT_APP", "TWR", "SFO_TWR", InitialContactTransferTiming.NoHandoffNecessary)
            );
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
