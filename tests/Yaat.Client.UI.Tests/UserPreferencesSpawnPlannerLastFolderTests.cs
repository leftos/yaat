using Xunit;
using Yaat.Client.Services;
using Yaat.Sim;

namespace Yaat.Client.UI.Tests;

// UserPreferences writes to this test's own preferences file; a fresh instance reads it back from disk.
public class UserPreferencesSpawnPlannerLastFolderTests
{
    [Fact]
    public void SpawnPlannerLastFolder_NoSavedValue_IsNull()
    {
        var prefs = new UserPreferences();

        Assert.Null(prefs.SpawnPlannerLastFolder);
    }

    [Fact]
    public void SetSpawnPlannerLastFolder_SurvivesSaveAndLoad()
    {
        string folder = YaatPaths.Combine("spawn-plans");
        var prefs = new UserPreferences();
        prefs.SetSpawnPlannerLastFolder(folder);

        Assert.Equal(folder, prefs.SpawnPlannerLastFolder);
        Assert.Equal(folder, new UserPreferences().SpawnPlannerLastFolder);
    }

    [Fact]
    public void SetSpawnPlannerLastFolder_Null_ForgetsIt()
    {
        var prefs = new UserPreferences();
        prefs.SetSpawnPlannerLastFolder(YaatPaths.Combine("spawn-plans"));
        prefs.SetSpawnPlannerLastFolder(null);

        Assert.Null(new UserPreferences().SpawnPlannerLastFolder);
    }
}
