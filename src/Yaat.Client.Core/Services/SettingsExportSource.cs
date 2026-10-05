namespace Yaat.Client.Services;

/// <summary>
/// Where an export reads each item from: the user's preferences directly (<see cref="UserPreferencesExportSource"/>), or a
/// host that exports what it has staged.
/// </summary>
public interface ISettingsExportSource
{
    /// <summary>The bundle entry holding the item as it stands now.</summary>
    SettingsBundleEntry Export(SettingsItemType itemType);
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
}
