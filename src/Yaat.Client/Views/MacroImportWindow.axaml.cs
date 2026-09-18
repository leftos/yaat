using System.Collections.ObjectModel;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using Yaat.Client.Services;

namespace Yaat.Client.Views;

public enum ConflictResolution
{
    Overwrite,
    Skip,
    Rename,
}

public sealed class MacroImportResult
{
    public required List<SavedMacro> NewMacros { get; init; }
    public required List<MacroConflictResolution> Conflicts { get; init; }
}

public sealed class MacroConflictResolution
{
    public required SavedMacro Macro { get; init; }
    public required ConflictResolution Resolution { get; init; }
    public string? RenamedName { get; init; }
}

public partial class MacroImportItem : ObservableObject
{
    public required SavedMacro Macro { get; init; }
    public required string ExistingExpansion { get; init; }

    [ObservableProperty]
    private ConflictResolution _resolution = ConflictResolution.Overwrite;

    [ObservableProperty]
    private string _renamedName = "";

    [ObservableProperty]
    private bool _isRenaming;

    [ObservableProperty]
    private string? _renameError;

    public string IncomingDisplay => $"#{Macro.Name}  →  {Macro.Expansion}";
    public string ExistingDisplay => $"Existing:  {ExistingExpansion}";

    public static IReadOnlyList<ConflictResolution> ResolutionOptions { get; } =
    [ConflictResolution.Overwrite, ConflictResolution.Skip, ConflictResolution.Rename];

    partial void OnResolutionChanged(ConflictResolution value)
    {
        IsRenaming = value == ConflictResolution.Rename;
    }
}

public partial class MacroImportWindow : Window
{
    private readonly ObservableCollection<MacroImportItem> _items;
    private readonly List<SavedMacro> _newMacros;
    private readonly HashSet<string> _allExistingBaseNames;

    public MacroImportWindow()
    {
        _items = [];
        _newMacros = [];
        _allExistingBaseNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        InitializeComponent();
    }

    public MacroImportWindow(List<MacroImportItem> conflicts, List<SavedMacro> newMacros, HashSet<string> allExistingBaseNames)
    {
        _items = new ObservableCollection<MacroImportItem>(conflicts);
        _newMacros = newMacros;
        _allExistingBaseNames = allExistingBaseNames;

        InitializeComponent();

        ItemsControl? list = this.FindControl<ItemsControl>("ConflictList");
        list?.ItemsSource = _items;

        Button? applyBtn = this.FindControl<Button>("ApplyButton");
        applyBtn?.Click += OnApplyClick;

        Button? overwriteAllBtn = this.FindControl<Button>("OverwriteAllButton");
        overwriteAllBtn?.Click += OnOverwriteAllClick;

        Button? skipAllBtn = this.FindControl<Button>("SkipAllButton");
        skipAllBtn?.Click += OnSkipAllClick;

        Button? cancelBtn = this.FindControl<Button>("CancelButton");
        cancelBtn?.Click += OnCancelClick;

        foreach (MacroImportItem item in _items)
        {
            item.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName is nameof(MacroImportItem.RenamedName) or nameof(MacroImportItem.Resolution))
                {
                    ValidateRenames();
                }
            };
        }
    }

    private void OnApplyClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ValidateRenames();
        if (_items.Any(i => i.Resolution == ConflictResolution.Rename && i.RenameError is not null))
        {
            return;
        }

        Close(BuildResult());
    }

    private void OnOverwriteAllClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        foreach (MacroImportItem item in _items)
        {
            item.Resolution = ConflictResolution.Overwrite;
        }

        Close(BuildResult());
    }

    private void OnSkipAllClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        foreach (MacroImportItem item in _items)
        {
            item.Resolution = ConflictResolution.Skip;
        }

        Close(BuildResult());
    }

    private void OnCancelClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close(null);

    private MacroImportResult BuildResult()
    {
        var conflicts = _items
            .Select(i => new MacroConflictResolution
            {
                Macro = i.Macro,
                Resolution = i.Resolution,
                RenamedName = i.Resolution == ConflictResolution.Rename ? i.RenamedName.Trim() : null,
            })
            .ToList();

        return new MacroImportResult { NewMacros = _newMacros, Conflicts = conflicts };
    }

    private void ValidateRenames()
    {
        // Collect all renamed base names to detect duplicates among rename items themselves
        var renamedBaseNames = new List<(MacroImportItem Item, string BaseName)>();
        foreach (MacroImportItem item in _items)
        {
            if (item.Resolution == ConflictResolution.Rename)
            {
                string name = item.RenamedName.Trim();
                string baseName = name.Length > 0 ? MacroDefinition.ExtractBaseName(name) : "";
                renamedBaseNames.Add((item, baseName));
            }
        }

        foreach (MacroImportItem item in _items)
        {
            if (item.Resolution != ConflictResolution.Rename)
            {
                item.RenameError = null;
                continue;
            }

            string name = item.RenamedName.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                item.RenameError = "Name is required";
                continue;
            }

            if (!MacroDefinition.IsValidName(name))
            {
                item.RenameError = "Invalid macro name";
                continue;
            }

            string baseName = MacroDefinition.ExtractBaseName(name);

            // Check against existing macros (including non-conflicting imports)
            IEnumerable<string> newMacroBaseNames = _newMacros.Select(m => MacroDefinition.ExtractBaseName(m.Name));
            IEnumerable<string> allTaken = _allExistingBaseNames.Union(newMacroBaseNames, StringComparer.OrdinalIgnoreCase);
            if (allTaken.Contains(baseName, StringComparer.OrdinalIgnoreCase))
            {
                item.RenameError = "Name already exists";
                continue;
            }

            // Check for duplicates among other rename items
            int duplicateCount = renamedBaseNames.Count(r =>
                r.Item != item && string.Equals(r.BaseName, baseName, StringComparison.OrdinalIgnoreCase)
            );
            if (duplicateCount > 0)
            {
                item.RenameError = "Duplicate rename";
                continue;
            }

            item.RenameError = null;
        }
    }
}
