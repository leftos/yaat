namespace Yaat.Client.Services;

/// <summary>The kinds of user configuration a settings bundle carries, at most one entry of each.</summary>
public enum SettingsItemType
{
    /// <summary>The allowlisted fields of the Settings sections (<see cref="UserPreferences.BundledPreferenceKeys"/>).</summary>
    Preferences,

    /// <summary>The command macros.</summary>
    Macros,

    /// <summary>The command verbs (the full command scheme).</summary>
    Verbs,

    /// <summary>The favorites library: every set and favorite plus the loaded-set ids.</summary>
    Favorites,

    /// <summary>The Aircraft List column order, widths, sort and hidden columns.</summary>
    GridLayout,

    /// <summary>The saved window layouts.</summary>
    Layouts,
}

/// <summary>How an imported item lands on what the user has now.</summary>
public enum SettingsImportMode
{
    /// <summary>Keep what exists and add the file's entries; named entries that clash are resolved one by one.</summary>
    Merge,

    /// <summary>The file's entries take the place of the current ones.</summary>
    Replace,
}

/// <summary>
/// One item of a bundle: its type, the format its bytes are in, the file name it is stored under, and the bytes, which
/// are exactly the single-item file that type has always exported (or <c>preferences.json</c> / <c>layouts.json</c>).
/// </summary>
/// <param name="ItemType">What the entry holds.</param>
/// <param name="Format">One of the <see cref="SettingsBundleFormats"/> format names.</param>
/// <param name="FileName">The entry's file name inside the bundle, or the file's own name for a single-item file.</param>
/// <param name="Content">The entry's bytes.</param>
public sealed record SettingsBundleEntry(SettingsItemType ItemType, string Format, string FileName, byte[] Content);

/// <summary>A manifest entry the reader did not import, with the reason in words.</summary>
/// <param name="ItemType">The item type as the manifest names it.</param>
/// <param name="Format">The format as the manifest names it.</param>
/// <param name="FileName">The entry's file name as the manifest names it.</param>
/// <param name="Reason">Why it was skipped, e.g. "this version does not know the item type 'hotkeys'".</param>
public sealed record SkippedBundleEntry(string ItemType, string Format, string FileName, string Reason);

/// <summary>A bundle as read from a file: the entries this version can import and the ones it skipped.</summary>
/// <param name="FileName">The name of the file read, without its folder.</param>
/// <param name="BundleVersion">The manifest's bundle version; 0 for a single-item file, which has no manifest.</param>
/// <param name="WrittenBy">The client version that wrote the bundle; empty for a single-item file.</param>
/// <param name="Entries">The entries this version can import, in manifest order, at most one per item type.</param>
/// <param name="Skipped">The manifest entries it cannot import.</param>
public sealed record SettingsBundle(
    string FileName,
    int BundleVersion,
    string WrittenBy,
    IReadOnlyList<SettingsBundleEntry> Entries,
    IReadOnlyList<SkippedBundleEntry> Skipped
)
{
    /// <summary>True for a single-item file (one exported before bundles existed, or a lone in-bundle file), which has no manifest.</summary>
    public bool IsSingleItemFile => BundleVersion == 0;

    /// <summary>The entry of the given type, or null when the bundle has none.</summary>
    public SettingsBundleEntry? Find(SettingsItemType itemType) => Entries.FirstOrDefault(e => e.ItemType == itemType);
}

/// <summary>What a preferences import changed.</summary>
/// <param name="AppliedKeys">The preference keys written, in file order.</param>
/// <param name="IgnoredKeys">
/// The keys left alone, each logged with the reason: not allowlisted, unknown, a value of the wrong type or outside its
/// rule (<see cref="UserPreferences.BundledPreferenceKeys"/>), or a run-delay minimum above its maximum.
/// </param>
public sealed record PreferencesImportResult(IReadOnlyList<string> AppliedKeys, IReadOnlyList<string> IgnoredKeys);

/// <summary>
/// The names a settings bundle uses on disk: item type names and format names in the manifest, the entry file names, and
/// the single-item file extensions exported before bundles existed.
/// </summary>
public static class SettingsBundleFormats
{
    public const string PreferencesJson = "preferences-json";
    public const string MacrosJson = "macros-json";
    public const string VerbsJson = "verbs-json";
    public const string FavoritesLibraryZip = "favorites-library-zip";
    public const string FavoritesSetZip = "favorites-set-zip";
    public const string FavoritesJson = "favorites-json";
    public const string GridLayoutJson = "grid-layout-json";
    public const string LayoutsJson = "layouts-json";

    /// <summary>The extension of an exported macros file.</summary>
    public const string MacrosExtension = ".yaat-macros.json";

    /// <summary>The extension of an exported Aircraft List column layout.</summary>
    public const string GridLayoutExtension = ".yaat-grid-layout.json";

    /// <summary>The preferences entry's file name in a bundle; a lone file with this name reads as preferences.</summary>
    public const string PreferencesFileName = "preferences.json";

    /// <summary>The layouts entry's file name in a bundle; a lone file with this name reads as layouts.</summary>
    public const string LayoutsFileName = "layouts.json";

    private static readonly (string Extension, SettingsItemType ItemType, string Format)[] SingleItemExtensions =
    [
        (MacrosExtension, SettingsItemType.Macros, MacrosJson),
        (CommandSchemeFile.Extension, SettingsItemType.Verbs, VerbsJson),
        (GridLayoutExtension, SettingsItemType.GridLayout, GridLayoutJson),
        (FavoriteExport.SetExportExtension, SettingsItemType.Favorites, FavoritesSetZip),
        (FavoriteExport.LibraryExportExtension, SettingsItemType.Favorites, FavoritesLibraryZip),
    ];

    /// <summary>The item type's name in a bundle manifest.</summary>
    public static string ItemTypeName(SettingsItemType itemType) =>
        itemType switch
        {
            SettingsItemType.Preferences => "preferences",
            SettingsItemType.Macros => "macros",
            SettingsItemType.Verbs => "verbs",
            SettingsItemType.Favorites => "favorites",
            SettingsItemType.GridLayout => "gridLayout",
            SettingsItemType.Layouts => "layouts",
            _ => throw new ArgumentOutOfRangeException(nameof(itemType), itemType, "Unknown settings item type"),
        };

    /// <summary>The item type a manifest name stands for, or null for a name this version does not know.</summary>
    public static SettingsItemType? ParseItemType(string? name)
    {
        foreach (SettingsItemType itemType in Enum.GetValues<SettingsItemType>())
        {
            if (string.Equals(ItemTypeName(itemType), name, StringComparison.Ordinal))
            {
                return itemType;
            }
        }

        return null;
    }

    /// <summary>True when this version can read the format for the item type.</summary>
    public static bool IsKnownFormat(SettingsItemType itemType, string? format) =>
        itemType switch
        {
            SettingsItemType.Preferences => format == PreferencesJson,
            SettingsItemType.Macros => format == MacrosJson,
            SettingsItemType.Verbs => format == VerbsJson,
            SettingsItemType.Favorites => format is FavoritesLibraryZip or FavoritesSetZip or FavoritesJson,
            SettingsItemType.GridLayout => format == GridLayoutJson,
            SettingsItemType.Layouts => format == LayoutsJson,
            _ => false,
        };

    /// <summary>
    /// The extension a single-item export of this entry's type and format carries, or null when the type has no
    /// single-item file of its own (preferences, layouts, a lone favorite), so it always travels in a bundle.
    /// </summary>
    public static string? SingleItemExtension(SettingsItemType itemType, string format) =>
        SingleItemExtensions.Where(e => (e.ItemType == itemType) && (e.Format == format)).Select(e => e.Extension).FirstOrDefault();

    /// <summary>Merge is offered for macros, favorites and layouts; preferences, verbs and the grid layout import by Replace only.</summary>
    public static bool SupportsMerge(SettingsItemType itemType) =>
        itemType is SettingsItemType.Macros or SettingsItemType.Favorites or SettingsItemType.Layouts;

    /// <summary>The item type and format of a single-item file with a typed extension; null for anything else.</summary>
    internal static (SettingsItemType ItemType, string Format)? SingleItemFormat(string fileName)
    {
        foreach ((string extension, SettingsItemType itemType, string format) in SingleItemExtensions)
        {
            if (fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                return (itemType, format);
            }
        }

        return null;
    }
}
