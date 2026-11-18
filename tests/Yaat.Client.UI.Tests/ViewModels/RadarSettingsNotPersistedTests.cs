using System.Text.Json.Nodes;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;

namespace Yaat.Client.UI.Tests.ViewModels;

/// <summary>
/// The Radar view no longer saves or reads per-scenario settings: changing its range, PTL or rings writes nothing to the
/// preferences, and an old preferences file's saved Radar settings are neither offered as another scenario's views nor
/// lost when the file is saved again.
/// </summary>
public class RadarSettingsNotPersistedTests
{
    private const string ScenarioId = "rsnp-scenario";

    [Fact]
    public void ChangingARadarSetting_WritesNoPerScenarioSettings()
    {
        var prefs = new UserPreferences();
        var vm = new RadarViewModel(new ServerConnection(), new VideoMapService(), (_, _, _) => Task.CompletedTask);
        vm.SetPreferences(prefs);
        vm.SetScenarioIdForTesting(ScenarioId);

        vm.AdjustRange(5);
        vm.AdjustPtlLength(1);
        vm.ToggleRangeRingsCommand.Execute(null);
        prefs.SetScenarioName(ScenarioId, "Radar scenario");

        Assert.Empty(new UserPreferences().GetSavedViewScenarioIds());
        JsonObject written = JsonNode.Parse(File.ReadAllText(UserPreferences.FilePath))!.AsObject();
        Assert.Empty(written["radarSettings"]!.AsObject());
    }

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
