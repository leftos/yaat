using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Settings;

namespace Yaat.Client.UI.Tests.ViewModels;

/// <summary>
/// The "ERAM field menus" setting (<see cref="ScopeFieldMenuStyle"/>) and the remembered tab its tabbed menus open on
/// (<see cref="ScopeFieldMenuMode"/>): their defaults, the saved preferences, the settings bundle, and the Settings row.
/// Each test runs on its own empty preferences file (<c>[PreferencesFilePerTest]</c>).
/// </summary>
public class ScopeFieldMenuStyleSettingsTests
{
    [AvaloniaFact]
    public void Defaults_Tabbed_AndTheEramMode()
    {
        var preferences = new UserPreferences();

        Assert.Equal(ScopeFieldMenuStyle.Tabbed, preferences.ScopeFieldMenuStyle);
        Assert.Equal(ScopeFieldMenuMode.Eram, preferences.ScopeFieldMenuLastMode);
        Assert.Equal(ScopeFieldMenuStyle.Tabbed, UserPreferences.CreateDefaults().ScopeFieldMenuStyle);
        Assert.Equal(ScopeFieldMenuMode.Eram, UserPreferences.CreateDefaults().ScopeFieldMenuLastMode);
    }

    [AvaloniaFact]
    public void Prefs_RoundTripTheStyleAndTheLastMode()
    {
        var preferences = new UserPreferences();

        preferences.SetScopeFieldMenuStyle(ScopeFieldMenuStyle.SideBySide);
        preferences.SetScopeFieldMenuLastMode(ScopeFieldMenuMode.Pilot);

        var reloaded = new UserPreferences();
        Assert.Equal(ScopeFieldMenuStyle.SideBySide, reloaded.ScopeFieldMenuStyle);
        Assert.Equal(ScopeFieldMenuMode.Pilot, reloaded.ScopeFieldMenuLastMode);
    }

    [AvaloniaFact]
    public void Prefs_AnUnknownStyleOrMode_ReadsAsTheDefault()
    {
        var file = new JsonObject { ["scopeFieldMenuStyle"] = "Detailed", ["scopeFieldMenuLastMode"] = 7 };
        File.WriteAllText(UserPreferences.FilePath, file.ToJsonString());

        var preferences = new UserPreferences();

        Assert.Equal(ScopeFieldMenuStyle.Tabbed, preferences.ScopeFieldMenuStyle);
        Assert.Equal(ScopeFieldMenuMode.Eram, preferences.ScopeFieldMenuLastMode);
    }

    [AvaloniaFact]
    public void Bundle_CarriesTheStyle_AndNotTheLastMode()
    {
        var preferences = new UserPreferences();
        preferences.SetScopeFieldMenuStyle(ScopeFieldMenuStyle.Simple);
        preferences.SetScopeFieldMenuLastMode(ScopeFieldMenuMode.Pilot);

        JsonObject exported = preferences.ExportBundlePreferences();
        Assert.Equal("Simple", exported["scopeFieldMenuStyle"]?.GetValue<string>());
        Assert.False(exported.ContainsKey("scopeFieldMenuLastMode"));
        Assert.Contains("scopeFieldMenuLastMode", UserPreferences.UnbundledPreferenceKeys.Keys);

        PreferencesImportResult result = preferences.ImportBundlePreferences(new JsonObject { ["scopeFieldMenuStyle"] = "SideBySide" });

        Assert.Equal(["scopeFieldMenuStyle"], result.AppliedKeys);
        Assert.Equal(ScopeFieldMenuStyle.SideBySide, preferences.ScopeFieldMenuStyle);
        Assert.Equal(
            ["scopeFieldMenuStyle"],
            preferences.ImportBundlePreferences(new JsonObject { ["scopeFieldMenuStyle"] = "Detailed" }).IgnoredKeys
        );

        // The Settings window stages an import into its field and exports what it shows.
        var settings = new SettingsViewModel(new UserPreferences());
        settings.StagePreferences(new JsonObject { ["scopeFieldMenuStyle"] = "SideBySide" });
        Assert.Equal((int)ScopeFieldMenuStyle.SideBySide, settings.SelectedScopeFieldMenuStyleIndex);
        JsonObject staged = JsonNode.Parse(settings.ExportStagedPreferences().Content)!.AsObject();
        Assert.Equal("SideBySide", staged["scopeFieldMenuStyle"]?.GetValue<string>());
    }

    [AvaloniaFact]
    public void SettingsViewModel_StagesAndAppliesTheStyle()
    {
        var preferences = new UserPreferences();
        var settings = new SettingsViewModel(preferences);
        Assert.Equal(["Simple (CRC)", "Tabbed", "Side by side"], SettingsViewModel.ScopeFieldMenuStyleOptions);
        Assert.Equal((int)ScopeFieldMenuStyle.Tabbed, settings.SelectedScopeFieldMenuStyleIndex);

        settings.SelectedScopeFieldMenuStyleIndex = (int)ScopeFieldMenuStyle.SideBySide;
        Assert.Equal(ScopeFieldMenuStyle.Tabbed, preferences.ScopeFieldMenuStyle);

        settings.ApplyCommand.Execute(null);

        Assert.Equal(ScopeFieldMenuStyle.SideBySide, preferences.ScopeFieldMenuStyle);
        Assert.Equal(ScopeFieldMenuStyle.SideBySide, new UserPreferences().ScopeFieldMenuStyle);
    }

    [AvaloniaFact]
    public void TheSettingsRow_IsInTheRadarSection_AndFoundByItsLabel()
    {
        SettingsSearchEntry entry = SettingsSearchCatalog.Entries.Single(e =>
            (e.Section == SettingsSectionId.Radar) && (e.Key == "SelectedScopeFieldMenuStyleIndex")
        );

        Assert.Equal("ERAM field menus", entry.Label);
        Assert.Contains(entry, SettingsSearch.Run("eram field menus", SettingsSearchCatalog.Entries).Matches);
    }
}
