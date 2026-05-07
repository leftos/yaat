using Xunit;
using Yaat.Client.Services;

namespace Yaat.Client.UI.Tests;

// UserPreferences writes to YaatPaths.AppDataRoot, which ModuleInit redirects to a
// per-process temp directory. Each test instance rewrites preferences.json from
// scratch through SetCommandHistory; isolation is guaranteed by xUnit creating a
// fresh fixture per test method, but tests still scope their data with unique
// scenario ids so they would not collide even if reordered into one process.
public class UserPreferencesCommandHistoryTests
{
    [Fact]
    public void GetCommandHistory_UnknownScenario_ReturnsEmpty()
    {
        var prefs = new UserPreferences();

        var history = prefs.GetCommandHistory("ZZZ-no-such-scenario");

        Assert.Empty(history);
    }

    [Fact]
    public void SetCommandHistory_RoundTripsThroughDisk()
    {
        const string scenarioId = "TEST-roundtrip-ABC";
        var entries = new[] { "fh 270", "DH 5000", "ERD 28R" };

        var writer = new UserPreferences();
        writer.SetCommandHistory(scenarioId, entries);

        // A fresh instance reads preferences.json from disk, proving persistence.
        var reader = new UserPreferences();
        var loaded = reader.GetCommandHistory(scenarioId);

        Assert.Equal(["FH 270", "DH 5000", "ERD 28R"], loaded);
    }

    [Fact]
    public void SetCommandHistory_UppercasesAndDedupesCaseVariants()
    {
        const string scenarioId = "TEST-normalize-case-GHI";

        var prefs = new UserPreferences();
        prefs.SetCommandHistory(scenarioId, ["cland", "CLAND", "fh 270"]);

        var loaded = new UserPreferences().GetCommandHistory(scenarioId);

        Assert.Equal(["CLAND", "FH 270"], loaded);
    }

    [Fact]
    public void SetCommandHistory_OverwritesPreviousSnapshot()
    {
        const string scenarioId = "TEST-overwrite-DEF";

        var prefs = new UserPreferences();
        prefs.SetCommandHistory(scenarioId, ["OLD1", "OLD2"]);
        prefs.SetCommandHistory(scenarioId, ["NEW"]);

        var loaded = new UserPreferences().GetCommandHistory(scenarioId);

        Assert.Equal(["NEW"], loaded);
    }

    [Fact]
    public void SetCommandHistory_KeepsScenariosIndependent()
    {
        const string scenarioA = "TEST-iso-A";
        const string scenarioB = "TEST-iso-B";

        var prefs = new UserPreferences();
        prefs.SetCommandHistory(scenarioA, ["A1", "A2"]);
        prefs.SetCommandHistory(scenarioB, ["B1"]);

        var reader = new UserPreferences();
        Assert.Equal(["A1", "A2"], reader.GetCommandHistory(scenarioA));
        Assert.Equal(["B1"], reader.GetCommandHistory(scenarioB));
    }
}
