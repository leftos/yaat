using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Client.Services;

namespace Yaat.Client.ViewModels;

/// <summary>The Import / Export hub's two tabs, in their order in the window.</summary>
public enum ImportExportTab
{
    Export,
    Import,
}

/// <summary>The answers to the backup question an import with a Replace ticked asks first, in the order its buttons show.</summary>
public enum ReplaceBackupChoice
{
    /// <summary>Save the current values of the Replace-ticked items, then import.</summary>
    BackUp,

    /// <summary>Import at once.</summary>
    ReplaceWithoutBackup,

    /// <summary>Import nothing.</summary>
    Cancel,
}

/// <summary>An export ready to write: the entries, the extension the file carries, and the name the save picker suggests.</summary>
/// <param name="Entries">One entry per ticked item type, in item type order.</param>
/// <param name="Extension">The file's extension, dot included (<see cref="SettingsBundleFile.ExportExtension"/>).</param>
/// <param name="SuggestedFileName">The file name offered in the save picker.</param>
public sealed record SettingsExportPlan(IReadOnlyList<SettingsBundleEntry> Entries, string Extension, string SuggestedFileName);

/// <summary>One line of the result after an import.</summary>
/// <param name="Result">What the item's import changed.</param>
/// <param name="Text">The line shown, e.g. "Macros: 1 added, 0 overwritten, 1 renamed, 0 skipped".</param>
public sealed record ImportSummaryRow(SettingsImportResult Result, string Text);

/// <summary>
/// The Import / Export hub. Export ticks item types and writes them through <see cref="SettingsBundleFile.WriteExport"/>:
/// one item as its own single-item file, two or more as a <c>.yaat-settings.zip</c> bundle. Import reads a file, lists
/// each entry with Merge or Replace (Merge only where <see cref="SettingsBundleFormats.SupportsMerge"/>), lists the Merge
/// clashes to resolve, and applies the ticked entries to the import target in one go. File pickers stay in the window,
/// so tests drive this with paths.
/// </summary>
public partial class ImportExportViewModel : ObservableObject
{
    /// <summary>The name the save picker suggests for the backup taken before a Replace.</summary>
    public const string BackupFileName = "settings-backup" + SettingsBundleFile.Extension;

    private static readonly ILogger Log = AppLog.CreateLogger<ImportExportViewModel>();

    private readonly ISettingsImportTarget _target;
    private readonly ISettingsExportSource _source;
    private readonly IReadOnlySet<SettingsItemType> _preselected;
    private readonly string _writtenBy;
    private readonly Func<string, Stream> _createFile;
    private readonly HashSet<SettingsItemType> _appliedItemTypes = [];
    private bool _imported;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedTab))]
    private int _selectedTabIndex;

    [ObservableProperty]
    private bool _canExport;

    [ObservableProperty]
    private string _exportSummary = "";

    [ObservableProperty]
    private string? _exportStatus;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImportFile))]
    [NotifyPropertyChangedFor(nameof(ChooseFileLabel))]
    private string? _importFileName;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImportError))]
    private string? _importError;

    [ObservableProperty]
    private bool _canImport;

    [ObservableProperty]
    private bool _hasImportResults;

    /// <summary>Builds the hub.</summary>
    /// <param name="target">Where an import reads the current state from and writes to.</param>
    /// <param name="source">Where an export reads each item from.</param>
    /// <param name="preselected">The item types ticked on the Export tab, and in an import preview when not empty.</param>
    /// <param name="initialTab">The tab shown first.</param>
    /// <param name="writtenBy">The client version recorded in a bundle's manifest.</param>
    /// <param name="createFile">
    /// Creates the temporary file an export or backup is written to before it is moved into place; it must fail when the
    /// file already exists (<c>File.Open(path, FileMode.CreateNew)</c>).
    /// </param>
    public ImportExportViewModel(
        ISettingsImportTarget target,
        ISettingsExportSource source,
        IReadOnlySet<SettingsItemType> preselected,
        ImportExportTab initialTab,
        string writtenBy,
        Func<string, Stream> createFile
    )
    {
        _target = target;
        _source = source;
        _preselected = preselected;
        _writtenBy = writtenBy;
        _createFile = createFile;
        _selectedTabIndex = (int)initialTab;

        foreach (SettingsItemType itemType in Enum.GetValues<SettingsItemType>())
        {
            var row = new ExportItemRow(itemType, preselected.Contains(itemType), DescribeExport(itemType, source));
            row.PropertyChanged += (_, _) => RefreshExport();
            ExportItems.Add(row);
        }

        RefreshFavoritesChoices();
        RefreshExport();
    }

    public ImportExportTab SelectedTab => (ImportExportTab)SelectedTabIndex;

    public ObservableCollection<ExportItemRow> ExportItems { get; } = [];

    public ObservableCollection<ImportItemRow> ImportItems { get; } = [];

    public ObservableCollection<ImportSummaryRow> ImportResults { get; } = [];

    /// <summary>
    /// Every item type an import in this hub applied, from every file opened in it; opening another file keeps them, so the
    /// host can bring its live views up to all of them when the hub closes.
    /// </summary>
    public IReadOnlySet<SettingsItemType> AppliedItemTypes => _appliedItemTypes;

    public bool HasImportFile => ImportFileName is not null;

    public bool HasImportError => !string.IsNullOrEmpty(ImportError);

    public string ChooseFileLabel => HasImportFile ? "Choose another file…" : "Choose a file…";

    public bool HasImportItems => ImportItems.Count > 0;

    /// <summary>
    /// Builds the entries of the ticked items; null, with the reason in <see cref="ExportStatus"/>, when nothing is ticked
    /// or one fails.
    /// </summary>
    public SettingsExportPlan? PrepareExport()
    {
        ExportStatus = null;
        List<ExportItemRow> ticked = [.. ExportItems.Where(r => r.IsSelected)];
        if (ticked.Count == 0)
        {
            ExportStatus = "Tick at least one item to export.";
            return null;
        }

        try
        {
            List<SettingsBundleEntry> entries = [.. ticked.Select(ExportEntry)];
            string extension = SettingsBundleFile.ExportExtension(entries);
            string baseName =
                ((entries.Count == 1) && (extension != SettingsBundleFile.Extension)) ? Path.GetFileName(entries[0].FileName) : "settings";
            string suggested = baseName.EndsWith(extension, StringComparison.OrdinalIgnoreCase) ? baseName : baseName + extension;
            return new SettingsExportPlan(entries, extension, suggested);
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Building the settings export failed");
            ExportStatus = $"Export failed: {ex.Message}";
            return null;
        }
    }

    /// <summary>Writes the export to the path; false, with the reason in <see cref="ExportStatus"/>, when the file cannot be written.</summary>
    public bool WriteExport(SettingsExportPlan plan, string path)
    {
        try
        {
            WriteAtomically(path, stream => SettingsBundleFile.WriteExport(plan.Entries, _writtenBy, stream));
            ExportStatus = $"Exported {ItemCount(plan.Entries.Count)} to {Path.GetFileName(path)}.";
            Log.LogInformation("Exported settings ({Items}) to {Path}", string.Join(", ", plan.Entries.Select(e => e.ItemType)), path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.LogError(ex, "Writing the settings export to {Path} failed", path);
            ExportStatus = $"Could not write '{Path.GetFileName(path)}': {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Reads the file and lists its entries for import. A file that cannot be read (not a zip, no readable manifest, a
    /// JSON file of no known kind) shows the reader's error naming the file and lists nothing.
    /// </summary>
    public void OpenImportFile(string path)
    {
        foreach (ImportItemRow old in ImportItems)
        {
            old.Changed -= OnImportRowChanged;
        }

        ImportItems.Clear();
        ImportResults.Clear();
        HasImportResults = false;
        ImportError = null;
        _imported = false;
        ImportFileName = Path.GetFileName(path);

        SettingsBundle? bundle = ReadBundle(path);
        if (bundle is not null)
        {
            foreach (SettingsBundleEntry entry in bundle.Entries)
            {
                AddImportRow(ImportItemRow.ForEntry(entry, _target, (_preselected.Count == 0) || _preselected.Contains(entry.ItemType)));
            }

            foreach (SkippedBundleEntry skipped in bundle.Skipped)
            {
                AddImportRow(ImportItemRow.ForSkipped(skipped));
            }
        }

        OnPropertyChanged(nameof(HasImportItems));
        RefreshImport();
    }

    /// <summary>
    /// Applies the ticked entries to the import target and lists what each changed; does nothing while
    /// <see cref="CanImport"/> is false. A failure stops the import; the items applied before it stay applied and listed.
    /// </summary>
    public void Import()
    {
        if (!CanImport)
        {
            return;
        }

        List<SettingsImportPlan> plans = [.. ImportItems.Where(r => r.IsSelected && (r.Plan is not null)).Select(r => r.Plan!)];
        try
        {
            // Each finished item is listed as it lands, so a later failure still shows what was applied.
            SettingsImportPlanner.ApplyAll(
                plans,
                _target,
                result =>
                {
                    _appliedItemTypes.Add(result.ItemType);
                    ImportResults.Add(new ImportSummaryRow(result, Summarize(result, plans.Single(p => p.ItemType == result.ItemType).Mode)));
                }
            );
            Log.LogInformation("Imported settings from {File}: {Items}", ImportFileName, string.Join("; ", ImportResults.Select(r => r.Text)));
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Importing settings from {File} failed after {Count} item(s)", ImportFileName, ImportResults.Count);
            ImportError = $"Import failed: {ex.Message}. Items applied before the failure stay applied.";
        }

        _imported = true;
        HasImportResults = ImportResults.Count > 0;
        RefreshImport();

        // An import can add favorite sets, or make the staged copy an export from Settings reads.
        RefreshFavoritesChoices();
    }

    /// <summary>
    /// Imports as <see cref="Import"/> does, first offering a backup when a ticked item is set to Replace: asks
    /// <paramref name="askBackup"/> with <see cref="BackupMessage"/>; on <see cref="ReplaceBackupChoice.BackUp"/> asks
    /// <paramref name="chooseBackupPath"/> where to save a <c>.yaat-settings.zip</c> of the current values of the
    /// Replace-ticked items (<see cref="PrepareBackup"/>) and imports once it is written. Cancel, a cancelled save and a
    /// backup that cannot be written import nothing. An import with no Replace ticked asks nothing.
    /// </summary>
    /// <param name="askBackup">Shows the question and returns the user's choice.</param>
    /// <param name="chooseBackupPath">Shows the save picker for the backup and returns the path, or null when cancelled.</param>
    public async Task ImportAsync(Func<string, Task<ReplaceBackupChoice>> askBackup, Func<SettingsExportPlan, Task<string?>> chooseBackupPath)
    {
        if (!CanImport)
        {
            return;
        }

        ImportError = null;
        IReadOnlyList<SettingsItemType> replacing = ReplaceTickedItemTypes;
        if (replacing.Count > 0)
        {
            ReplaceBackupChoice choice = await askBackup(BackupMessage(replacing));
            if (choice == ReplaceBackupChoice.Cancel)
            {
                return;
            }

            if ((choice == ReplaceBackupChoice.BackUp) && !await BackUpAsync(replacing, chooseBackupPath))
            {
                return;
            }
        }

        Import();
    }

    // True once the backup is written; false when the save is cancelled or the backup cannot be built or written.
    private async Task<bool> BackUpAsync(IReadOnlyList<SettingsItemType> itemTypes, Func<SettingsExportPlan, Task<string?>> chooseBackupPath)
    {
        if (PrepareBackup(itemTypes) is not { } plan)
        {
            return false;
        }

        string? path = await chooseBackupPath(plan);
        if (path is null)
        {
            Log.LogInformation("Backup before replacing {Items} cancelled; nothing imported", string.Join(", ", itemTypes));
            return false;
        }

        try
        {
            WriteAtomically(path, stream => SettingsBundleFile.Write(plan.Entries, _writtenBy, stream));
            Log.LogInformation("Backed up settings ({Items}) to {Path} before replacing them", string.Join(", ", itemTypes), path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.LogError(ex, "Writing the settings backup to {Path} failed", path);
            ImportError = $"Could not write the backup '{Path.GetFileName(path)}': {ex.Message}. Nothing was imported.";
            return false;
        }
    }

    // Writes to a temporary file in the path's folder and moves it over the path only once it is complete, so a failure
    // partway leaves a file already at the path (an older backup or export) as it was. The temporary file is deleted on
    // failure and the exception rethrown.
    private void WriteAtomically(string path, Action<Stream> write)
    {
        string fullPath = Path.GetFullPath(path);
        string folder = Path.GetDirectoryName(fullPath) ?? throw new IOException($"'{path}' names no folder to write in");
        string temp = Path.Combine(folder, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (Stream stream = _createFile(temp))
            {
                write(stream);
            }

            File.Move(temp, fullPath, overwrite: true);
        }
        catch
        {
            DeleteTemporaryFile(temp);
            throw;
        }
    }

    private static void DeleteTemporaryFile(string temp)
    {
        try
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
                Log.LogDebug("Deleted the temporary file {Path} after a failed write", temp);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.LogWarning(ex, "Could not delete the temporary file {Path} after a failed write", temp);
        }
    }

    /// <summary>The ticked item types set to Replace, in the order the file lists them.</summary>
    public IReadOnlyList<SettingsItemType> ReplaceTickedItemTypes =>
        [
            .. ImportItems
                .Where(r => r.IsSelected && (r.Plan is not null) && (r.Mode == SettingsImportMode.Replace))
                .Select(r => r.ItemType!.Value)
                .Distinct(),
        ];

    /// <summary>The backup question for the item types, e.g. "Replace removes your current Macros and Layouts. Save a copy of them first?".</summary>
    public static string BackupMessage(IReadOnlyList<SettingsItemType> itemTypes)
    {
        List<string> names = [.. itemTypes.Select(ImportExportItemText.Title)];
        string items = (names.Count <= 1) ? string.Join("", names) : $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}";
        return $"Replace removes your current {items}. Save a copy of them first?";
    }

    /// <summary>
    /// The backup of the item types as the export source holds them now, always as a <c>.yaat-settings.zip</c> bundle;
    /// null, with the reason in <see cref="ImportError"/>, when an item cannot be read.
    /// </summary>
    public SettingsExportPlan? PrepareBackup(IReadOnlyList<SettingsItemType> itemTypes)
    {
        try
        {
            List<SettingsBundleEntry> entries = [.. itemTypes.Select(_source.Export)];
            return new SettingsExportPlan(entries, SettingsBundleFile.Extension, BackupFileName);
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Building the settings backup failed");
            ImportError = $"Backup failed: {ex.Message}. Nothing was imported.";
            return null;
        }
    }

    /// <summary>The summary line for one item's import under the mode it was imported by.</summary>
    public static string Summarize(SettingsImportResult result, SettingsImportMode mode)
    {
        string title = ImportExportItemText.Title(result.ItemType);
        return result.ItemType switch
        {
            SettingsItemType.Preferences => $"{title}: {result.Overwritten} applied, {result.Skipped} ignored",
            SettingsItemType.Verbs => (result.UnknownCommands.Count == 0)
                ? $"{title}: {result.Overwritten} changed"
                : $"{title}: {result.Overwritten} changed; unknown commands skipped: {string.Join(", ", result.UnknownCommands)}",
            SettingsItemType.GridLayout => $"{title}: replaced",
            _ when mode == SettingsImportMode.Replace => $"{title}: replaced with {result.Added}",
            _ => $"{title}: {result.Added} added, {result.Overwritten} overwritten, {result.Renamed} renamed, {result.Skipped} skipped",
        };
    }

    private SettingsBundle? ReadBundle(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            return SettingsBundleFile.Read(path, stream);
        }
        catch (InvalidDataException ex)
        {
            Log.LogWarning(ex, "Settings file {Path} could not be read", path);
            ImportError = ex.Message;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.LogWarning(ex, "Settings file {Path} could not be opened", path);
            ImportError = $"'{Path.GetFileName(path)}' could not be opened: {ex.Message}";
        }

        return null;
    }

    private void AddImportRow(ImportItemRow row)
    {
        row.Changed += OnImportRowChanged;
        ImportItems.Add(row);
    }

    private void OnImportRowChanged(object? sender, EventArgs e) => RefreshImport();

    private void RefreshImport()
    {
        List<ImportItemRow> ticked = [.. ImportItems.Where(r => r.IsSelected)];
        CanImport = !_imported && (ticked.Count > 0) && ticked.All(r => (r.Plan is not null) && !r.HasRenameErrors);
    }

    // The favorites row exports one set when one is chosen, the whole library otherwise; the macros row the selected
    // macros when the hub was opened on a selection, every macro otherwise.
    private SettingsBundleEntry ExportEntry(ExportItemRow row) =>
        row.ItemType switch
        {
            SettingsItemType.Favorites when row.FavoritesChoice?.SetId is { } setId => _source.ExportFavoriteSet(setId),
            SettingsItemType.Macros when _source.SelectedMacros is { } selected => SettingsBundleItems.Macros(selected),
            _ => _source.Export(row.ItemType),
        };

    private static string DescribeExport(SettingsItemType itemType, ISettingsExportSource source) =>
        ((itemType == SettingsItemType.Macros) && (source.SelectedMacros is { } selected))
            ? ((selected.Count == 1) ? "The 1 selected macro" : $"The {selected.Count} selected macros")
            : ImportExportItemText.Description(itemType);

    // Lists every set the export source holds now, keeping the chosen set while it still exists.
    private void RefreshFavoritesChoices()
    {
        ExportItemRow row = ExportItems.Single(r => r.ItemType == SettingsItemType.Favorites);
        string? chosen = row.FavoritesChoice?.SetId;
        row.FavoritesChoices.Clear();
        row.FavoritesChoices.Add(FavoritesExportChoice.AllSets);
        foreach (FavoriteSet set in _source.FavoriteSets)
        {
            row.FavoritesChoices.Add(new FavoritesExportChoice(set.Id, set.DisplayName));
        }

        row.FavoritesChoice =
            row.FavoritesChoices.FirstOrDefault(c => string.Equals(c.SetId, chosen, StringComparison.Ordinal)) ?? FavoritesExportChoice.AllSets;
    }

    private void RefreshExport()
    {
        int ticked = ExportItems.Count(r => r.IsSelected);
        CanExport = ticked > 0;
        ExportSummary = ItemCount(ticked);
    }

    private static string ItemCount(int count) => (count == 1) ? "1 item" : $"{count} items";
}

/// <summary>The words the hub shows for each item type.</summary>
public static class ImportExportItemText
{
    /// <summary>The item type's name in the hub, e.g. "Command verbs".</summary>
    public static string Title(SettingsItemType itemType) =>
        itemType switch
        {
            SettingsItemType.Preferences => "Preferences",
            SettingsItemType.Macros => "Macros",
            SettingsItemType.Verbs => "Command verbs",
            SettingsItemType.Favorites => "Favorites",
            SettingsItemType.GridLayout => "Aircraft list columns",
            SettingsItemType.Layouts => "Layouts",
            _ => throw new ArgumentOutOfRangeException(nameof(itemType), itemType, "Unknown settings item type"),
        };

    /// <summary>What an export of the item holds.</summary>
    public static string Description(SettingsItemType itemType) =>
        itemType switch
        {
            SettingsItemType.Preferences => "Every Settings section",
            SettingsItemType.Macros => "Every macro",
            SettingsItemType.Verbs => "The aliases of every command",
            SettingsItemType.Favorites => "Every favorite set and favorite",
            SettingsItemType.GridLayout => "Order, widths, sort, hidden columns",
            SettingsItemType.Layouts => "Every saved layout",
            _ => throw new ArgumentOutOfRangeException(nameof(itemType), itemType, "Unknown settings item type"),
        };

    /// <summary>The file type the item is written as when exported alone; preferences and layouts always travel in a bundle.</summary>
    public static string ExportedAs(SettingsItemType itemType) =>
        itemType switch
        {
            SettingsItemType.Preferences => "in " + SettingsBundleFile.Extension,
            SettingsItemType.Macros => SettingsBundleFormats.MacrosExtension,
            SettingsItemType.Verbs => CommandSchemeFile.Extension,
            SettingsItemType.Favorites => FavoriteExport.LibraryExportExtension,
            SettingsItemType.GridLayout => SettingsBundleFormats.GridLayoutExtension,
            SettingsItemType.Layouts => "in " + SettingsBundleFile.Extension,
            _ => throw new ArgumentOutOfRangeException(nameof(itemType), itemType, "Unknown settings item type"),
        };

    /// <summary>The heading over the item's clash list, e.g. "Macros that clash".</summary>
    public static string ClashHeading(SettingsItemType itemType) =>
        itemType switch
        {
            SettingsItemType.Favorites => "Favorite sets that clash",
            _ => $"{Title(itemType)} that clash",
        };
}

/// <summary>One choice of what a favorites export holds: every set (<see cref="SetId"/> null) or one set.</summary>
/// <param name="SetId">The set's id; null for every set.</param>
/// <param name="Name">The choice as listed: "All sets", or the set's name.</param>
public sealed record FavoritesExportChoice(string? SetId, string Name)
{
    /// <summary>The first choice, and the default: the whole library.</summary>
    public static FavoritesExportChoice AllSets { get; } = new(null, "All sets");

    public override string ToString() => Name;
}

/// <summary>One item type on the Export tab.</summary>
/// <param name="itemType">The item type.</param>
/// <param name="isSelected">Whether it is ticked.</param>
/// <param name="description">What an export of the item holds, as the row shows it while no single favorite set is chosen.</param>
public partial class ExportItemRow(SettingsItemType itemType, bool isSelected, string description) : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected = isSelected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Description))]
    [NotifyPropertyChangedFor(nameof(ExportedAs))]
    private FavoritesExportChoice? _favoritesChoice;

    public SettingsItemType ItemType { get; } = itemType;

    public string Title => ImportExportItemText.Title(ItemType);

    /// <summary>What the export holds; for favorites, what the chosen set or library holds.</summary>
    public string Description => (FavoritesChoice?.SetId is not null) ? "One favorite set and its favorites" : description;

    /// <summary>The file type the item is written as alone; a single favorite set is written as a set file.</summary>
    public string ExportedAs => (FavoritesChoice?.SetId is not null) ? FavoriteExport.SetExportExtension : ImportExportItemText.ExportedAs(ItemType);

    /// <summary>True for the favorites row, which offers the whole library or one set.</summary>
    public bool HasFavoritesChoices => ItemType == SettingsItemType.Favorites;

    /// <summary>For the favorites row, <see cref="FavoritesExportChoice.AllSets"/> then every set by name; empty for every other row.</summary>
    public ObservableCollection<FavoritesExportChoice> FavoritesChoices { get; } = [];
}

/// <summary>
/// One entry of the file being imported: an entry this version reads, with its mode, plan and clashes, or a manifest
/// entry it skips, shown greyed and never tickable.
/// </summary>
public partial class ImportItemRow : ObservableObject
{
    private static readonly ILogger Log = AppLog.CreateLogger<ImportItemRow>();

    private readonly SettingsBundleEntry? _entry;
    private readonly ISettingsImportTarget? _target;

    [ObservableProperty]
    private SettingsImportMode _mode;

    private bool _isSelected;

    private string? _error;
    private string _title;
    private string _clashSummary;
    private SettingsImportPlan? _plan;

    private ImportItemRow(SettingsBundleEntry? entry, ISettingsImportTarget? target, string skippedTitle, string skippedReason)
    {
        _entry = entry;
        _target = target;
        ItemType = entry?.ItemType;
        Modes =
            entry is null ? []
            : SettingsBundleFormats.SupportsMerge(entry.ItemType) ? [SettingsImportMode.Merge, SettingsImportMode.Replace]
            : [SettingsImportMode.Replace];
        _mode = (Modes.Count > 0) ? Modes[0] : SettingsImportMode.Replace;
        _title = skippedTitle;
        _clashSummary = skippedReason;
    }

    /// <summary>Raised when anything that decides whether the import can run changes: the tick, the mode, a clash choice or rename.</summary>
    public event EventHandler? Changed;

    /// <summary>The entry's item type; null for a skipped entry of a type this version does not know.</summary>
    public SettingsItemType? ItemType { get; }

    /// <summary>The entry planned under <see cref="Mode"/>; null for a skipped entry or one whose content could not be read.</summary>
    public SettingsImportPlan? Plan
    {
        get => _plan;
        private set => SetProperty(ref _plan, value);
    }

    /// <summary>Why the entry's content could not be read, naming its file; null when it planned.</summary>
    public string? Error
    {
        get => _error;
        private set
        {
            if (SetProperty(ref _error, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    /// <summary>The row's name, with the count of named entries, e.g. "Macros (18)" or "Favorites (2 sets)".</summary>
    public string Title
    {
        get => _title;
        private set => SetProperty(ref _title, value);
    }

    /// <summary>The Clashes column: the clash count under Merge, "—" under Replace, the unknown commands, or why it was skipped.</summary>
    public string ClashSummary
    {
        get => _clashSummary;
        private set => SetProperty(ref _clashSummary, value);
    }

    /// <summary>The modes offered: Merge and Replace where the item supports Merge, Replace alone otherwise, none when skipped.</summary>
    public IReadOnlyList<SettingsImportMode> Modes { get; }

    /// <summary>False for a skipped entry, and for an entry whose content could not be read.</summary>
    public bool IsSelectable => Plan is not null;

    public bool CanChooseMode => IsSelectable && (Modes.Count > 1);

    public double RowOpacity => IsSelectable ? 1.0 : 0.5;

    public bool HasError => !string.IsNullOrEmpty(Error);

    public ObservableCollection<ImportClashRow> Clashes { get; } = [];

    public bool HasClashes => Clashes.Count > 0;

    /// <summary>The clash list shows only for a ticked row with clashes.</summary>
    public bool ShowClashes => IsSelected && HasClashes;

    public string ClashHeading => ItemType is { } itemType ? ImportExportItemText.ClashHeading(itemType) : "";

    public bool HasRenameErrors => Clashes.Any(c => c.RenameError is not null);

    internal static ImportItemRow ForEntry(SettingsBundleEntry entry, ISettingsImportTarget target, bool isSelected)
    {
        var row = new ImportItemRow(entry, target, ImportExportItemText.Title(entry.ItemType), "");
        row.Replan();
        row.IsSelected = isSelected && row.IsSelectable;
        return row;
    }

    internal static ImportItemRow ForSkipped(SkippedBundleEntry skipped) =>
        new(null, null, string.IsNullOrEmpty(skipped.ItemType) ? skipped.FileName : skipped.ItemType, $"Skipped: {skipped.Reason}");

    /// <summary>Whether the entry is imported; stays false for a row that is not <see cref="IsSelectable"/>.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value && IsSelectable))
            {
                OnPropertyChanged(nameof(ShowClashes));
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    partial void OnModeChanged(SettingsImportMode value)
    {
        if (_entry is null)
        {
            return;
        }

        Replan();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Replan()
    {
        if ((_entry is not { } entry) || (_target is not { } target))
        {
            return;
        }

        foreach (ImportClashRow old in Clashes)
        {
            old.PropertyChanged -= OnClashChanged;
        }

        Clashes.Clear();
        try
        {
            Plan = SettingsImportPlanner.Plan(entry, Mode, target);
            Error = null;
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "Settings entry {File} could not be planned", entry.FileName);
            Plan = null;
            Error = (ex is InvalidDataException) ? ex.Message : $"'{entry.FileName}' could not be read: {ex.Message}";
            IsSelected = false;
        }

        foreach (ImportClash clash in Plan?.Clashes ?? [])
        {
            var clashRow = new ImportClashRow(clash);
            clashRow.PropertyChanged += OnClashChanged;
            Clashes.Add(clashRow);
        }

        bool counted = entry.ItemType is not (SettingsItemType.GridLayout or SettingsItemType.Preferences);
        Title =
            (Plan is { } plan) && counted
                ? $"{ImportExportItemText.Title(entry.ItemType)} ({Count(plan)})"
                : ImportExportItemText.Title(entry.ItemType);
        ClashSummary = Describe(Plan);
        OnPropertyChanged(nameof(IsSelectable));
        OnPropertyChanged(nameof(CanChooseMode));
        OnPropertyChanged(nameof(RowOpacity));
        OnPropertyChanged(nameof(HasClashes));
        OnPropertyChanged(nameof(ShowClashes));
        Validate();
    }

    private static string Count(SettingsImportPlan plan) =>
        (plan.ItemType == SettingsItemType.Favorites)
            ? ((plan.IncomingNames.Count == 1) ? "1 set" : $"{plan.IncomingNames.Count} sets")
            : plan.IncomingNames.Count.ToString(CultureInfo.InvariantCulture);

    private string Describe(SettingsImportPlan? plan)
    {
        if (plan is null)
        {
            return "Could not be read";
        }

        if (plan.UnknownCommands.Count > 0)
        {
            return (plan.UnknownCommands.Count == 1) ? "1 unknown command, skipped" : $"{plan.UnknownCommands.Count} unknown commands, skipped";
        }

        if (Mode == SettingsImportMode.Replace)
        {
            return "—";
        }

        return plan.Clashes.Count switch
        {
            0 => "none",
            1 => "1 name clashes",
            _ => $"{plan.Clashes.Count} names clash",
        };
    }

    private void OnClashChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ImportClashRow.Choice) or nameof(ImportClashRow.RenameTo))
        {
            Validate();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Validate()
    {
        IReadOnlyList<RenameError> errors = (Plan is { } plan) ? SettingsImportPlanner.ValidateRenames(plan) : [];
        foreach (ImportClashRow clashRow in Clashes)
        {
            clashRow.RenameError = errors.FirstOrDefault(e => ReferenceEquals(e.Clash, clashRow.Clash))?.Message;
        }

        OnPropertyChanged(nameof(HasRenameErrors));
    }
}

/// <summary>One clash under Merge: Skip, Overwrite or Rename, written straight into the planner's <see cref="ImportClash"/>.</summary>
public partial class ImportClashRow(ImportClash clash) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRenaming))]
    private ClashChoice _choice = clash.Choice;

    [ObservableProperty]
    private string _renameTo = clash.RenameTo;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRenameError))]
    private string? _renameError;

    /// <summary>The choices in the order the hub lists them.</summary>
    public static IReadOnlyList<ClashChoice> ChoiceOptions { get; } = [ClashChoice.Skip, ClashChoice.Overwrite, ClashChoice.Rename];

    public ImportClash Clash { get; } = clash;

    public string IncomingName => Clash.IncomingName;

    public string ExistingName => Clash.ExistingName;

    public string ExistingDisplay => $"Existing: {Clash.ExistingName}";

    public bool IsRenaming => Choice == ClashChoice.Rename;

    public bool HasRenameError => !string.IsNullOrEmpty(RenameError);

    partial void OnChoiceChanging(ClashChoice value) => Clash.Choice = value;

    partial void OnRenameToChanging(string value) => Clash.RenameTo = value;
}
