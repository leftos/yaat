using Xunit;
using Yaat.Client.Services;

namespace Yaat.Client.UI.Tests;

// UserPreferences writes to YaatPaths.AppDataRoot, redirected by ModuleInit to a per-process temp
// directory. Random keys keep these tests independent of ordering and of any recents other tests add.
public class UserPreferencesRecentScenarioTests
{
    [Fact]
    public void RemoveRecentScenario_DropsTheEntry_AndPersists()
    {
        string key = $"TEST-recent-remove-{Guid.NewGuid():N}";
        var prefs = new UserPreferences();
        prefs.AddRecentScenario(key, "Remove Me");
        Assert.Contains(prefs.RecentScenarios, r => r.Key == key);

        prefs.RemoveRecentScenario(key);

        Assert.DoesNotContain(prefs.RecentScenarios, r => r.Key == key);
        Assert.DoesNotContain(new UserPreferences().RecentScenarios, r => r.Key == key);
    }

    [Fact]
    public void RemoveRecentScenario_UnknownKey_LeavesOthersIntact()
    {
        string keep = $"TEST-recent-keep-{Guid.NewGuid():N}";
        var prefs = new UserPreferences();
        prefs.AddRecentScenario(keep, "Keep Me");

        prefs.RemoveRecentScenario($"TEST-recent-unknown-{Guid.NewGuid():N}");

        Assert.Contains(prefs.RecentScenarios, r => r.Key == keep);
    }
}
