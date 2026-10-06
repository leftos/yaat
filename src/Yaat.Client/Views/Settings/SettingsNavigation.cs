using Yaat.Client.ViewModels;

namespace Yaat.Client.Views.Settings;

/// <summary>The Settings sidebar in display order: each group header followed by its sections.</summary>
public static class SettingsNavigation
{
    public static IReadOnlyList<SettingsNavItem> Items { get; } =
    [
        Header("General"),
        Section(SettingsSectionId.General, "General", "General"),
        Section(SettingsSectionId.Appearance, "General", "Appearance"),
        Header("Session"),
        Section(SettingsSectionId.ScenarioDefaults, "Session", "Scenario defaults"),
        Header("Views"),
        Section(SettingsSectionId.Radar, "Views", "Radar"),
        Section(SettingsSectionId.Ground, "Views", "Ground"),
        Section(SettingsSectionId.AircraftList, "Views", "Aircraft list"),
        Section(SettingsSectionId.StripsAndTdls, "Views", "Strips and vTDLS"),
        Section(SettingsSectionId.Terminal, "Views", "Terminal"),
        Header("Input"),
        Section(SettingsSectionId.CommandInput, "Input", "Command input"),
        Section(SettingsSectionId.CommandVerbs, "Input", "Command verbs"),
        Section(SettingsSectionId.Macros, "Input", "Macros"),
        Section(SettingsSectionId.Keys, "Input", "Keys"),
        Header("Voice"),
        Section(SettingsSectionId.Speech, "Voice", "Speech"),
        Section(SettingsSectionId.AudioDevices, "Voice", "Audio devices"),
        Header("Advanced"),
        Section(SettingsSectionId.ServerAdmin, "Advanced", "Server admin"),
    ];

    /// <summary>The sidebar row that opens <paramref name="id"/>.</summary>
    public static SettingsNavItem ItemFor(SettingsSectionId id) => Items.Single(item => item.Id == id);

    private static SettingsNavItem Header(string group) => new(null, group, group);

    private static SettingsNavItem Section(SettingsSectionId id, string group, string title) => new(id, group, title);
}
