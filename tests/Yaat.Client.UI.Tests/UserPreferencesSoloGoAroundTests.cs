using Xunit;
using Yaat.Client.Services;

namespace Yaat.Client.UI.Tests;

// UserPreferences writes to YaatPaths.AppDataRoot, which ModuleInit redirects to a
// per-process temp directory. Tests use unique scenario ids so they don't collide
// even if reordered, and a fresh UserPreferences instance proves disk round-trip.
public class UserPreferencesSoloGoAroundTests
{
    [Fact]
    public void GetSoloGoAroundProbability_NoSavedValue_ReturnsGlobalDefault()
    {
        var prefs = new UserPreferences();
        prefs.SetSoloGoAroundProbabilityGlobal(17);

        var value = prefs.GetSoloGoAroundProbability("TEST-unknown-id");

        Assert.Equal(17, value);
    }

    [Fact]
    public void GetSoloGoAroundProbability_PerScenarioOverridesGlobal()
    {
        const string scenarioId = "TEST-go-around-override-ABC";
        var prefs = new UserPreferences();
        prefs.SetSoloGoAroundProbabilityGlobal(5);
        prefs.SetSoloGoAroundProbabilityForScenario(scenarioId, 42);

        // Same-instance lookup.
        Assert.Equal(42, prefs.GetSoloGoAroundProbability(scenarioId));
        Assert.Equal(5, prefs.GetSoloGoAroundProbability("TEST-other-scenario-DEF"));

        // Fresh instance reads preferences.json from disk, proving persistence.
        var reader = new UserPreferences();
        Assert.Equal(42, reader.GetSoloGoAroundProbability(scenarioId));
        Assert.Equal(5, reader.GetSoloGoAroundProbability("TEST-other-scenario-DEF"));
    }

    [Fact]
    public void SetSoloGoAroundProbability_ClampsOutOfRangeValues()
    {
        var prefs = new UserPreferences();
        prefs.SetSoloGoAroundProbabilityGlobal(-30);
        Assert.Equal(0, prefs.SoloGoAroundProbabilityPercent);

        prefs.SetSoloGoAroundProbabilityGlobal(250);
        Assert.Equal(100, prefs.SoloGoAroundProbabilityPercent);

        prefs.SetSoloGoAroundProbabilityForScenario("TEST-clamp-XYZ", 250);
        Assert.Equal(100, prefs.GetSoloGoAroundProbability("TEST-clamp-XYZ"));
    }

    [Fact]
    public void SetSoloGoAroundProbabilityForScenario_EmptyId_IsNoOp()
    {
        var prefs = new UserPreferences();
        prefs.SetSoloGoAroundProbabilityGlobal(11);

        prefs.SetSoloGoAroundProbabilityForScenario("", 99);

        // No per-scenario entry written → still resolves to global default.
        Assert.Equal(11, prefs.GetSoloGoAroundProbability(""));
        Assert.Equal(11, prefs.GetSoloGoAroundProbability("TEST-empty-id-LMN"));
    }
}
