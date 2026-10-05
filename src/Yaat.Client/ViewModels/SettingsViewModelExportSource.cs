using Yaat.Client.Services;

namespace Yaat.Client.ViewModels;

/// <summary>
/// The export source of the Import / Export hub opened from Settings: every item is read as the open window shows it,
/// staged edits and imports included. Favorites are read from the staged copy of the store once a favorites import has
/// made one, and from the user's store before that; the loaded favorite sets are the preferences' with every staged
/// import's sets to load applied.
/// </summary>
/// <param name="settings">The open Settings window's view model.</param>
/// <param name="preferences">The preferences the window edits; the favorites export records their loaded favorite set ids.</param>
/// <param name="importTarget">The window's import target, which holds the user's favorites store and any staged copy of it.</param>
/// <param name="selectedMacros">The macros a macros export holds when only the selected rows are exported; null for every macro.</param>
public sealed class SettingsViewModelExportSource(
    SettingsViewModel settings,
    UserPreferences preferences,
    SettingsViewModelImportTarget importTarget,
    IReadOnlyList<SavedMacro>? selectedMacros
) : ISettingsExportSource
{
    /// <summary>The entry for the item; a grid layout never saved or staged exports as an empty layout (every field unset).</summary>
    public SettingsBundleEntry Export(SettingsItemType itemType) =>
        itemType switch
        {
            SettingsItemType.Preferences => settings.ExportStagedPreferences(),
            SettingsItemType.Macros => SettingsBundleItems.Macros(settings.StagedMacros),
            SettingsItemType.Verbs => SettingsBundleItems.Verbs(settings.ExportVerbs()),
            SettingsItemType.Favorites => SettingsBundleItems.Favorites(
                importTarget.FavoritesToExport,
                importTarget.LoadedFavoriteSetIdsToExport(preferences.LoadedFavoriteSetIds)
            ),
            SettingsItemType.GridLayout => SettingsBundleItems.GridLayout(settings.StagedGridLayout ?? new SavedGridLayout()),
            SettingsItemType.Layouts => SettingsBundleItems.Layouts(settings.StagedLayouts),
            _ => throw new ArgumentOutOfRangeException(nameof(itemType), itemType, "Unknown settings item type"),
        };

    public IReadOnlyList<FavoriteSet> FavoriteSets => importTarget.FavoritesToExport.OrderedSets;

    public SettingsBundleEntry ExportFavoriteSet(string setId) => SettingsBundleItems.FavoriteSet(importTarget.FavoritesToExport, setId);

    public IReadOnlyList<SavedMacro>? SelectedMacros => selectedMacros;
}
