using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Settings;

namespace Yaat.Client.UI.Tests.ViewModels;

/// <summary>
/// The "STARS field menus" setting (<see cref="UserPreferences.ScopeStarsFieldMenus"/>): off by default, the saved preferences,
/// the settings bundle, and its Settings row beside "ERAM field menus". Each test runs on its own empty preferences file
/// (<c>[PreferencesFilePerTest]</c>).
/// </summary>
public class ScopeStarsFieldMenusSettingsTests
{
    [AvaloniaFact]
    public void Default_IsOff()
    {
        Assert.False(new UserPreferences().ScopeStarsFieldMenus);
        Assert.False(UserPreferences.CreateDefaults().ScopeStarsFieldMenus);
    }

    [AvaloniaFact]
    public void Prefs_RoundTripTheSetting()
    {
        new UserPreferences().SetScopeStarsFieldMenus(true);

        Assert.True(new UserPreferences().ScopeStarsFieldMenus);
    }

    [AvaloniaFact]
    public void Bundle_CarriesTheSetting()
    {
        var preferences = new UserPreferences();
        preferences.SetScopeStarsFieldMenus(true);

        JsonObject exported = preferences.ExportBundlePreferences();
        Assert.True(exported["scopeStarsFieldMenus"]?.GetValue<bool>());

        var other = new UserPreferences();
        other.SetScopeStarsFieldMenus(false);
        PreferencesImportResult result = other.ImportBundlePreferences(new JsonObject { ["scopeStarsFieldMenus"] = true });

        Assert.Equal(["scopeStarsFieldMenus"], result.AppliedKeys);
        Assert.True(other.ScopeStarsFieldMenus);

        // The Settings window stages an import into its field and exports what it shows.
        var settings = new SettingsViewModel(new UserPreferences());
        settings.StagePreferences(new JsonObject { ["scopeStarsFieldMenus"] = false });
        Assert.False(settings.ScopeStarsFieldMenus);
        JsonObject staged = JsonNode.Parse(settings.ExportStagedPreferences().Content)!.AsObject();
        Assert.False(staged["scopeStarsFieldMenus"]?.GetValue<bool>());
    }

    [AvaloniaFact]
    public void SettingsViewModel_StagesAndAppliesTheSetting()
    {
        var preferences = new UserPreferences();
        var settings = new SettingsViewModel(preferences);
        Assert.False(settings.ScopeStarsFieldMenus);

        settings.ScopeStarsFieldMenus = true;
        Assert.False(preferences.ScopeStarsFieldMenus);

        settings.ApplyCommand.Execute(null);

        Assert.True(preferences.ScopeStarsFieldMenus);
        Assert.True(new UserPreferences().ScopeStarsFieldMenus);
    }

    [AvaloniaFact]
    public void TheSettingsRow_IsInTheRadarSection_AndFoundByItsLabel()
    {
        SettingsSearchEntry entry = SettingsSearchCatalog.Entries.Single(e =>
            (e.Section == SettingsSectionId.Radar) && (e.Key == "ScopeStarsFieldMenus")
        );

        Assert.Equal("STARS field menus", entry.Label);
        Assert.Contains(entry, SettingsSearch.Run("stars field menus", SettingsSearchCatalog.Entries).Matches);

        var section = new RadarSection { DataContext = new SettingsViewModel(new UserPreferences()) };
        CheckBox box = Assert.Single(section.GetLogicalDescendants().OfType<CheckBox>(), c => (c.Content as string) == "STARS field menus");
        Assert.False(box.IsChecked);
    }
}
