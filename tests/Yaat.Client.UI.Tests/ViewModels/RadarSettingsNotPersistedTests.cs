using System.Text.Json.Nodes;
using Xunit;
using Yaat.Client.Services;

namespace Yaat.Client.UI.Tests.ViewModels;

/// <summary>
/// An old preferences file's saved Radar settings are neither offered as another scenario's views nor lost when the file
/// is saved again.
/// </summary>
public class RadarSettingsNotPersistedTests
{
    [Fact]
    public void AnOldFilesRadarSettings_AreNotOffered_AndSurviveASave()
    {
        var file = new JsonObject
        {
            ["preferencesVersion"] = 1,
            ["radarSettings"] = new JsonObject { ["old-scenario"] = new JsonObject { ["rangeNm"] = 55 } },
        };
        File.WriteAllText(UserPreferences.FilePath, file.ToJsonString());

        var prefs = new UserPreferences();

        Assert.Empty(prefs.GetSavedViewScenarioIds());

        prefs.SetScenarioName("old-scenario", "Old scenario");

        JsonObject written = JsonNode.Parse(File.ReadAllText(UserPreferences.FilePath))!.AsObject();
        Assert.Equal(55, written["radarSettings"]!["old-scenario"]!["rangeNm"]!.GetValue<double>());
    }
}
