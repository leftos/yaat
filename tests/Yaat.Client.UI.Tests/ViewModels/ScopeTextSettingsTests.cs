using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Settings;

namespace Yaat.Client.UI.Tests.ViewModels;

/// <summary>
/// The "Scope text" settings (<see cref="ScopeTextStyle"/> and the CRC text size, for STARS and for ERAM): the saved
/// preferences, the Settings window's staging, apply, reset and size-box enablement, the settings bundle, and the search rows.
/// Each test runs on its own empty preferences file (<c>[PreferencesFilePerTest]</c>).
/// </summary>
public class ScopeTextSettingsTests
{
    private static (ScopeTextStyle, int, ScopeTextStyle, int) ScopeTextOf(UserPreferences preferences) =>
        (preferences.StarsScopeTextStyle, preferences.StarsScopeTextSize, preferences.EramScopeTextStyle, preferences.EramScopeTextSize);

    [AvaloniaFact]
    public void Prefs_AnUnknownStoredStyle_ReadsAsCompact()
    {
        var file = new JsonObject { ["starsScopeTextStyle"] = "Fancy", ["eramScopeTextStyle"] = "Detailed" };
        File.WriteAllText(UserPreferences.FilePath, file.ToJsonString());

        var preferences = new UserPreferences();

        Assert.Equal(ScopeTextStyle.Compact, preferences.StarsScopeTextStyle);
        Assert.Equal(ScopeTextStyle.Compact, preferences.EramScopeTextStyle);
    }

    [AvaloniaFact]
    public void Prefs_AnOutOfRangeStoredSize_IsClampedOnLoad()
    {
        var file = new JsonObject { ["starsScopeTextSize"] = 9, ["eramScopeTextSize"] = -3 };
        File.WriteAllText(UserPreferences.FilePath, file.ToJsonString());

        var preferences = new UserPreferences();

        Assert.Equal(UserPreferences.MaxStarsScopeTextSize, preferences.StarsScopeTextSize);
        Assert.Equal(0, preferences.EramScopeTextSize);
    }

    [AvaloniaFact]
    public void SetScopeText_ClampsEachSizeToItsLadder()
    {
        var preferences = new UserPreferences();

        preferences.SetScopeText(ScopeTextStyle.Realistic, 9, ScopeTextStyle.Realistic, -1);

        Assert.Equal(UserPreferences.MaxStarsScopeTextSize, preferences.StarsScopeTextSize);
        Assert.Equal(0, preferences.EramScopeTextSize);
        Assert.Equal((ScopeTextStyle.Realistic, 6, ScopeTextStyle.Realistic, 0), ScopeTextOf(new UserPreferences()));
    }

    [AvaloniaFact]
    public void SetScopeText_TellsListenersOnlyWhenAValueChanged()
    {
        var preferences = new UserPreferences();
        int raised = 0;
        preferences.ScopeTextChanged += () => raised++;

        preferences.SetScopeText(ScopeTextStyle.Compact, 2, ScopeTextStyle.Compact, 1);
        Assert.Equal(0, raised);

        preferences.SetScopeText(ScopeTextStyle.Compact, 2, ScopeTextStyle.Realistic, 1);
        Assert.Equal(1, raised);
    }

    [AvaloniaFact]
    public void SettingsViewModel_StagesWithoutWriting_AndApplyTellsListenersOnce()
    {
        var preferences = new UserPreferences();
        int raised = 0;
        preferences.ScopeTextChanged += () => raised++;
        var settings = new SettingsViewModel(preferences)
        {
            SelectedStarsScopeTextStyleIndex = (int)ScopeTextStyle.Realistic,
            StarsScopeTextSize = 4,
            SelectedEramScopeTextStyleIndex = (int)ScopeTextStyle.Realistic,
            EramScopeTextSize = 3,
        };
        Assert.Equal((ScopeTextStyle.Compact, 2, ScopeTextStyle.Compact, 1), ScopeTextOf(preferences));
        Assert.Equal(0, raised);

        settings.ApplyCommand.Execute(null);

        Assert.Equal((ScopeTextStyle.Realistic, 4, ScopeTextStyle.Realistic, 3), ScopeTextOf(preferences));
        Assert.Equal(1, raised);
        Assert.Equal((ScopeTextStyle.Realistic, 4, ScopeTextStyle.Realistic, 3), ScopeTextOf(new UserPreferences()));
    }

    [AvaloniaFact]
    public void SettingsViewModel_EachSizeBoxIsEnabledOnlyWhileItsStyleIsRealistic()
    {
        var settings = new SettingsViewModel(new UserPreferences());
        Assert.False(settings.IsStarsScopeTextSizeEnabled);
        Assert.False(settings.IsEramScopeTextSizeEnabled);

        settings.SelectedStarsScopeTextStyleIndex = (int)ScopeTextStyle.Realistic;
        Assert.True(settings.IsStarsScopeTextSizeEnabled);
        Assert.False(settings.IsEramScopeTextSizeEnabled);

        settings.SelectedEramScopeTextStyleIndex = (int)ScopeTextStyle.Realistic;
        settings.SelectedStarsScopeTextStyleIndex = (int)ScopeTextStyle.Compact;
        Assert.False(settings.IsStarsScopeTextSizeEnabled);
        Assert.True(settings.IsEramScopeTextSizeEnabled);
    }

    [AvaloniaFact]
    public void SettingsViewModel_ResettingAppearance_RestoresTheDefaults()
    {
        var preferences = new UserPreferences();
        preferences.SetScopeText(ScopeTextStyle.Realistic, 5, ScopeTextStyle.Realistic, 4);
        var settings = new SettingsViewModel(preferences) { SelectedSection = SettingsSectionId.Appearance };

        settings.ResetSectionCommand.Execute(null);

        Assert.Equal((int)ScopeTextStyle.Compact, settings.SelectedStarsScopeTextStyleIndex);
        Assert.Equal(UserPreferences.DefaultStarsScopeTextSize, settings.StarsScopeTextSize);
        Assert.Equal((int)ScopeTextStyle.Compact, settings.SelectedEramScopeTextStyleIndex);
        Assert.Equal(UserPreferences.DefaultEramScopeTextSize, settings.EramScopeTextSize);
    }

    [AvaloniaFact]
    public void SettingsViewModel_StagesAndExportsTheFourKeys()
    {
        var settings = new SettingsViewModel(new UserPreferences());

        settings.StagePreferences(
            new JsonObject
            {
                ["starsScopeTextStyle"] = "Realistic",
                ["starsScopeTextSize"] = 5,
                ["eramScopeTextStyle"] = "Realistic",
                ["eramScopeTextSize"] = 2,
            }
        );

        Assert.Equal((int)ScopeTextStyle.Realistic, settings.SelectedStarsScopeTextStyleIndex);
        Assert.Equal(5, settings.StarsScopeTextSize);
        Assert.Equal((int)ScopeTextStyle.Realistic, settings.SelectedEramScopeTextStyleIndex);
        Assert.Equal(2, settings.EramScopeTextSize);
        JsonObject staged = JsonNode.Parse(settings.ExportStagedPreferences().Content)!.AsObject();
        Assert.Equal("Realistic", staged["starsScopeTextStyle"]?.GetValue<string>());
        Assert.Equal(5, staged["starsScopeTextSize"]?.GetValue<int>());
        Assert.Equal("Realistic", staged["eramScopeTextStyle"]?.GetValue<string>());
        Assert.Equal(2, staged["eramScopeTextSize"]?.GetValue<int>());
    }

    [AvaloniaTheory]
    [InlineData("SelectedStarsScopeTextStyleIndex", "STARS text")]
    [InlineData("StarsScopeTextSize", "STARS size")]
    [InlineData("SelectedEramScopeTextStyleIndex", "ERAM text")]
    [InlineData("EramScopeTextSize", "ERAM size")]
    public void TheSettingsRows_AreInTheAppearanceSection_AndFoundByTheirLabels(string key, string label)
    {
        SettingsSearchEntry entry = SettingsSearchCatalog.Entries.Single(e => (e.Section == SettingsSectionId.Appearance) && (e.Key == key));

        Assert.Equal(label, entry.Label);
        Assert.Contains(entry, SettingsSearch.Run(label.ToLowerInvariant(), SettingsSearchCatalog.Entries).Matches);
    }
}
