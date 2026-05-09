using Xunit;
using Yaat.Sim.Data;

namespace Yaat.Sim.Tests;

public sealed class WakeDirectiveLoaderTests
{
    [Fact]
    public void LoadAll_LoadsArtccScopedWakeDirectiveRules()
    {
        var root = Path.Combine(Path.GetTempPath(), "yaat-wake-directive-tests", Guid.NewGuid().ToString("N"));
        var category = Path.Combine(root, "ZOA", "WakeDirectives");
        Directory.CreateDirectory(category);
        File.WriteAllText(
            Path.Combine(category, "oak.json"),
            """
            [
              {
                "id": "oak-local-waiver",
                "airportId": "OAK",
                "runways": ["28R"],
                "operation": "departureBehindDeparture",
                "relation": "sameRunway",
                "precedingCwt": ["B"],
                "succeedingCwt": ["F"],
                "sourceRuleReferences": ["7110.65 §3-9-6(f)"],
                "effects": ["suppressWakeInterval", "requireWakeAdvisory"],
                "ruleReference": "7110.65 §2-1-20; facility directive",
                "notes": "Test directive"
              }
            ]
            """
        );

        try
        {
            var result = WakeDirectiveLoader.LoadAll(root);
            var catalog = new WakeDirectiveCatalog(result.Rules);

            Assert.Empty(result.Warnings);
            var rule = Assert.Single(result.Rules);
            Assert.Equal("ZOA", rule.ArtccId);
            Assert.Equal("OAK", rule.AirportId);
            Assert.Contains(WakeDirectiveEffect.SuppressWakeInterval, rule.Effects);
            Assert.Contains(WakeDirectiveEffect.RequireWakeAdvisory, rule.Effects);

            var context = new WakeDirectiveContext(
                "WAKE_TEST",
                "ZOA",
                "KOAK",
                "28R",
                "28R",
                "BAW1",
                "SWA2",
                WakeDirectiveOperation.DepartureBehindDeparture,
                WakeDirectiveRelation.SameRunway,
                'B',
                'F',
                "7110.65 §3-9-6(f)"
            );
            Assert.Contains(catalog.FindMatches(context), match => match.Id == "oak-local-waiver");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void LoadAll_RejectsInvalidWakeDirectiveRules()
    {
        var root = Path.Combine(Path.GetTempPath(), "yaat-wake-directive-tests", Guid.NewGuid().ToString("N"));
        var category = Path.Combine(root, "ZOA", "WakeDirectives");
        Directory.CreateDirectory(category);
        File.WriteAllText(
            Path.Combine(category, "bad.json"),
            """
            [
              {
                "id": "bad-effect",
                "operation": "departureBehindDeparture",
                "effects": ["notARealEffect"]
              },
              {
                "id": "bad-cwt",
                "operation": "departureBehindDeparture",
                "precedingCwt": ["Z"],
                "effects": ["requireWakeAdvisory"]
              }
            ]
            """
        );

        try
        {
            var result = WakeDirectiveLoader.LoadAll(root);

            Assert.Empty(result.Rules);
            Assert.Contains(result.Warnings, warning => warning.Contains("invalid effect", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(result.Warnings, warning => warning.Contains("invalid precedingCwt", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
