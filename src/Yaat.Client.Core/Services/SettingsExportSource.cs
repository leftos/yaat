namespace Yaat.Client.Services;

/// <summary>
/// Where an export reads each item from: the user's preferences directly (<see cref="UserPreferencesExportSource"/>), or a
/// host that exports what it has staged.
/// </summary>
public interface ISettingsExportSource
{
    /// <summary>The bundle entry holding the item as it stands now; favorites as the whole library.</summary>
    SettingsBundleEntry Export(SettingsItemType itemType);

    /// <summary>The favorite sets an export can hold one of, in the order the favorites store lists them.</summary>
    IReadOnlyList<FavoriteSet> FavoriteSets { get; }

    /// <summary>The bundle entry holding one favorite set and the favorites it lists, as a single-set file.</summary>
    /// <exception cref="ArgumentException">No set has the id.</exception>
    SettingsBundleEntry ExportFavoriteSet(string setId);

    /// <summary>
    /// The macros an export from the Export tab holds when only the selected ones are exported; null when it holds every
    /// macro. <see cref="Export"/> always holds every macro, as a backup before a Replace must.
    /// </summary>
    IReadOnlyList<SavedMacro>? SelectedMacros { get; }
}

/// <summary>An export source that reads the user's preferences and favorites store as they are at each export.</summary>
/// <param name="preferences">
/// The preferences every item but favorites is read from, plus the loaded favorite set ids recorded in the favorites library.
/// </param>
/// <param name="favorites">The favorites store exported as the whole library.</param>
public sealed class UserPreferencesExportSource(UserPreferences preferences, FavoriteStore favorites) : ISettingsExportSource
{
    /// <summary>The entry for the item; a grid layout never saved exports as an empty layout (every field unset).</summary>
    public SettingsBundleEntry Export(SettingsItemType itemType) =>
        itemType switch
        {
            SettingsItemType.Preferences => SettingsBundleItems.Preferences(preferences),
            SettingsItemType.Macros => SettingsBundleItems.Macros([
                .. preferences.Macros.Select(m => new SavedMacro { Name = m.Name, Expansion = m.Expansion }),
            ]),
            SettingsItemType.Verbs => SettingsBundleItems.Verbs(preferences.CommandScheme),
            SettingsItemType.Favorites => SettingsBundleItems.Favorites(favorites, preferences.LoadedFavoriteSetIds),
            SettingsItemType.GridLayout => SettingsBundleItems.GridLayout(preferences.GridLayout ?? new SavedGridLayout()),
            SettingsItemType.Layouts => SettingsBundleItems.Layouts(preferences.Layouts),
            _ => throw new ArgumentOutOfRangeException(nameof(itemType), itemType, "Unknown settings item type"),
        };

    public IReadOnlyList<FavoriteSet> FavoriteSets => favorites.OrderedSets;

    public SettingsBundleEntry ExportFavoriteSet(string setId) => SettingsBundleItems.FavoriteSet(favorites, setId);

    public IReadOnlyList<SavedMacro>? SelectedMacros => null;
}
