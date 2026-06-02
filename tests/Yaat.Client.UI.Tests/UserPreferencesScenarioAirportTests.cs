using Xunit;
using Yaat.Client.Services;

namespace Yaat.Client.UI.Tests;

// UserPreferences writes to YaatPaths.AppDataRoot, redirected by ModuleInit to a per-process
// temp directory. A fresh UserPreferences instance reads preferences.json from disk and proves
// the round-trip. Unique scenario ids keep tests independent of ordering.
public class UserPreferencesScenarioAirportTests
{
    [Fact]
    public void GetScenarioAirport_NoSavedValue_ReturnsNull()
    {
        var prefs = new UserPreferences();

        Assert.Null(prefs.GetScenarioAirport("TEST-airport-unknown-id"));
    }

    [Fact]
    public void SetScenarioAirport_PersistsAcrossInstances()
    {
        const string scenarioId = "TEST-airport-roundtrip-ABC";
        var prefs = new UserPreferences();
        prefs.SetScenarioAirport(scenarioId, "KOAK");

        Assert.Equal("KOAK", prefs.GetScenarioAirport(scenarioId));

        var reader = new UserPreferences();
        Assert.Equal("KOAK", reader.GetScenarioAirport(scenarioId));
    }

    [Fact]
    public void SetScenarioAirport_EmptyArguments_AreNoOps()
    {
        var prefs = new UserPreferences();

        prefs.SetScenarioAirport("", "KSFO");
        prefs.SetScenarioAirport("TEST-airport-empty-airport-DEF", "");

        Assert.Null(prefs.GetScenarioAirport("TEST-airport-empty-airport-DEF"));
    }

    [Fact]
    public void SetScenarioAirport_Overwrites_WithNewValue()
    {
        const string scenarioId = "TEST-airport-overwrite-GHI";
        var prefs = new UserPreferences();
        prefs.SetScenarioAirport(scenarioId, "KOAK");
        prefs.SetScenarioAirport(scenarioId, "KSFO");

        Assert.Equal("KSFO", prefs.GetScenarioAirport(scenarioId));
    }
}
