using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Yaat.Sim.Commands;

namespace Yaat.Client.Services;

/// <summary>
/// Builds the bundle entry for each item type from the data to export, and parses an entry back. Each entry's bytes are
/// what that item's single-item export has always written: the macros and grid layout as <see cref="UserPreferences.JsonOptions"/>
/// JSON, the verbs as <see cref="CommandSchemeFile.Serialize"/> in UTF-8, the favorites as the
/// <see cref="FavoriteExport.ExportLibrary"/> zip.
/// </summary>
public static class SettingsBundleItems
{
    /// <summary>The allowlisted preferences (<see cref="UserPreferences.ExportBundlePreferences"/>) as <c>preferences.json</c>.</summary>
    public static SettingsBundleEntry Preferences(UserPreferences preferences) =>
        new(
            SettingsItemType.Preferences,
            SettingsBundleFormats.PreferencesJson,
            "preferences.json",
            JsonSerializer.SerializeToUtf8Bytes(preferences.ExportBundlePreferences(), UserPreferences.JsonOptions)
        );

    /// <summary>The macros, as a <c>*.yaat-macros.json</c> file.</summary>
    public static SettingsBundleEntry Macros(IReadOnlyList<SavedMacro> macros) =>
        new(
            SettingsItemType.Macros,
            SettingsBundleFormats.MacrosJson,
            "macros" + SettingsBundleFormats.MacrosExtension,
            JsonSerializer.SerializeToUtf8Bytes<List<SavedMacro>>([.. macros], UserPreferences.JsonOptions)
        );

    /// <summary>The full command scheme, as a <c>*.yaat-verbs.json</c> file.</summary>
    public static SettingsBundleEntry Verbs(CommandScheme scheme) =>
        new(
            SettingsItemType.Verbs,
            SettingsBundleFormats.VerbsJson,
            "command-verbs" + CommandSchemeFile.Extension,
            Encoding.UTF8.GetBytes(CommandSchemeFile.Serialize(scheme))
        );

    /// <summary>Every favorite set and favorite plus the loaded-set ids, as a <c>*.yaat-favlibrary.zip</c> file.</summary>
    public static SettingsBundleEntry Favorites(FavoriteStore store, IReadOnlyList<string> loadedSetIds)
    {
        using var buffer = new MemoryStream();
        FavoriteExport.ExportLibrary(store, loadedSetIds, buffer);
        return new SettingsBundleEntry(
            SettingsItemType.Favorites,
            SettingsBundleFormats.FavoritesLibraryZip,
            "favorites" + FavoriteExport.LibraryExportExtension,
            buffer.ToArray()
        );
    }

    /// <summary>
    /// One favorite set and the favorites it lists, as the <c>[Name].yaat-favset.zip</c> file a single-set export has always
    /// written (<see cref="FavoriteExport.ExportSet"/>).
    /// </summary>
    /// <exception cref="ArgumentException">No set has the id.</exception>
    public static SettingsBundleEntry FavoriteSet(FavoriteStore store, string setId)
    {
        FavoriteSet set = store.GetSet(setId) ?? throw new ArgumentException($"Unknown favorite set id '{setId}'", nameof(setId));
        using var buffer = new MemoryStream();
        FavoriteExport.ExportSet(store, setId, buffer);
        return new SettingsBundleEntry(
            SettingsItemType.Favorites,
            SettingsBundleFormats.FavoritesSetZip,
            FavoriteStore.SanitizeFileName(set.DisplayName, "set") + FavoriteExport.SetExportExtension,
            buffer.ToArray()
        );
    }

    /// <summary>The Aircraft List column layout, as a <c>*.yaat-grid-layout.json</c> file.</summary>
    public static SettingsBundleEntry GridLayout(SavedGridLayout layout) =>
        new(
            SettingsItemType.GridLayout,
            SettingsBundleFormats.GridLayoutJson,
            "aircraft-list" + SettingsBundleFormats.GridLayoutExtension,
            JsonSerializer.SerializeToUtf8Bytes(layout, UserPreferences.JsonOptions)
        );

    /// <summary>The saved window layouts, each with its geometry as saved, as <c>layouts.json</c>.</summary>
    public static SettingsBundleEntry Layouts(IReadOnlyList<SavedLayout> layouts) =>
        new(
            SettingsItemType.Layouts,
            SettingsBundleFormats.LayoutsJson,
            "layouts.json",
            JsonSerializer.SerializeToUtf8Bytes<List<SavedLayout>>([.. layouts], UserPreferences.JsonOptions)
        );

    /// <summary>Parses the entry as written by its exporter above.</summary>
    /// <exception cref="InvalidDataException">The bytes do not parse; the message names the entry's file.</exception>
    public static List<SavedMacro> ReadMacros(SettingsBundleEntry entry)
    {
        List<SavedMacro?> macros = Deserialize<List<SavedMacro?>>(entry);
        var checkedMacros = new List<SavedMacro>(macros.Count);
        for (int i = 0; i < macros.Count; i++)
        {
            if ((macros[i] is not { } macro) || (macro.Name is null) || (macro.Expansion is null))
            {
                throw Unreadable(entry, $"macro {i + 1} is empty or has no name or expansion", null);
            }

            checkedMacros.Add(macro);
        }

        return checkedMacros;
    }

    /// <summary>Parses the entry as written by its exporter above.</summary>
    /// <exception cref="InvalidDataException">The bytes do not parse; the message names the entry's file.</exception>
    public static List<SavedLayout> ReadLayouts(SettingsBundleEntry entry)
    {
        List<SavedLayout?> layouts = Deserialize<List<SavedLayout?>>(entry);
        var checkedLayouts = new List<SavedLayout>(layouts.Count);
        for (int i = 0; i < layouts.Count; i++)
        {
            if ((layouts[i] is not { } layout) || (layout.Name is null) || (layout.WindowGeometries is null))
            {
                throw Unreadable(entry, $"layout {i + 1} is empty or has no name or window geometries", null);
            }

            if ((layout.ExtraRadarViews is null) || (layout.ExtraGroundViews is null))
            {
                throw Unreadable(entry, $"layout '{layout.Name}' has an empty list of extra views", null);
            }

            // A null id would break the favorite-set id remapping after favorites were already imported.
            if (layout.LoadedFavoriteSetIds?.Any(id => id is null) == true)
            {
                throw Unreadable(entry, $"layout '{layout.Name}' lists an empty loaded favorite set id", null);
            }

            checkedLayouts.Add(layout);
        }

        return checkedLayouts;
    }

    /// <summary>Parses the entry as written by its exporter above.</summary>
    /// <exception cref="InvalidDataException">The bytes do not parse; the message names the entry's file.</exception>
    public static SavedGridLayout ReadGridLayout(SettingsBundleEntry entry) => Deserialize<SavedGridLayout>(entry);

    /// <summary>Parses the entry as written by its exporter above.</summary>
    /// <exception cref="InvalidDataException">The bytes do not parse; the message names the entry's file.</exception>
    public static CommandSchemeImport ReadVerbs(SettingsBundleEntry entry)
    {
        try
        {
            return CommandSchemeFile.Deserialize(ReadText(entry));
        }
        catch (JsonException ex)
        {
            throw Unreadable(entry, ex.Message, ex);
        }
    }

    /// <summary>Parses the entry as written by its exporter above.</summary>
    /// <exception cref="InvalidDataException">The bytes do not parse; the message names the entry's file.</exception>
    public static JsonObject ReadPreferences(SettingsBundleEntry entry)
    {
        try
        {
            using var stream = new MemoryStream(entry.Content);
            return JsonNode.Parse(stream) as JsonObject ?? throw Unreadable(entry, "it is not a JSON object", null);
        }
        catch (JsonException ex)
        {
            throw Unreadable(entry, ex.Message, ex);
        }
    }

    /// <summary>The error for an entry whose bytes do not parse as its item type; the message names the entry's file.</summary>
    internal static InvalidDataException Unreadable(SettingsBundleEntry entry, string reason, Exception? inner) =>
        new($"'{entry.FileName}' could not be read as {SettingsBundleFormats.ItemTypeName(entry.ItemType)}: {reason}", inner);

    // Streams rather than spans, so a byte-order mark from a hand-edited file is skipped as the per-feature importers did.
    private static T Deserialize<T>(SettingsBundleEntry entry)
        where T : class
    {
        try
        {
            using var stream = new MemoryStream(entry.Content);
            return JsonSerializer.Deserialize<T>(stream, UserPreferences.JsonOptions) ?? throw Unreadable(entry, "it is empty", null);
        }
        catch (JsonException ex)
        {
            throw Unreadable(entry, ex.Message, ex);
        }
    }

    private static string ReadText(SettingsBundleEntry entry)
    {
        using var reader = new StreamReader(new MemoryStream(entry.Content), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
