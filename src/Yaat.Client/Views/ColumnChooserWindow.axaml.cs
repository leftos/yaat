using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Interactivity;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Sim.Commands;

namespace Yaat.Client.Views;

/// <summary>
/// Chooses the Aircraft List's columns. Its Import / Export opens the hub with the column layout ticked: an imported column
/// layout is staged in the chooser's rows (OK applies it, Cancel drops it), and any other item ticked there applies at once.
/// </summary>
public partial class ColumnChooserWindow : Window
{
    private static readonly ILogger Log = AppLog.CreateLogger<ColumnChooserWindow>();

    private readonly ColumnChooserState _state;
    private readonly UserPreferences _preferences;

    // Null only in the designer window, which wires no Import / Export button.
    private readonly FavoriteStore? _favorites;
    private readonly HashSet<SettingsItemType> _liveImported = [];

    public ObservableCollection<ColumnEntry> Entries { get; } = [];
    public bool Confirmed { get; private set; }
    public bool ShowOnlyActive { get; private set; }
    public bool AlternatingRowColor { get; private set; }
    public SavedGridLayout? ImportedLayout { get; private set; }

    /// <summary>The item types an import from this window applied straight to the preferences (every one but the column layout).</summary>
    public IReadOnlySet<SettingsItemType> LiveImported => _liveImported;

    // Parameterless ctor required for the Avalonia designer / XamlLoader. Not used at runtime.
    public ColumnChooserWindow()
    {
        InitializeComponent();
        AutomationGate.ApplyShowActivated(this);
        _state = new ColumnChooserState
        {
            Columns = [],
            ShowOnlyActive = false,
            AlternatingRowColor = false,
            ColumnWidths = null,
            SortColumn = null,
            SortDirection = null,
            DefaultOrder = [],
        };
        _preferences = new UserPreferences();
    }

    /// <summary>Builds the chooser over the grid's columns and settings as they are now.</summary>
    /// <param name="state">The grid's columns, settings, widths and sort the chooser opens on.</param>
    /// <param name="preferences">The preferences an import from the chooser's Import / Export writes every item but the column layout to.</param>
    /// <param name="favorites">The favorites store that Import / Export imports and exports.</param>
    public ColumnChooserWindow(ColumnChooserState state, UserPreferences preferences, FavoriteStore favorites)
    {
        InitializeComponent();
        AutomationGate.ApplyShowActivated(this);

        _state = state;
        _preferences = preferences;
        _favorites = favorites;

        foreach (ColumnEntry col in state.Columns)
        {
            Entries.Add(col);
        }

        ColumnList.ItemsSource = Entries;
        ShowOnlyActiveCheckBox.IsChecked = state.ShowOnlyActive;
        AlternatingRowColorCheckBox.IsChecked = state.AlternatingRowColor;

        MoveTopButton.Click += OnMoveTop;
        MoveUpButton.Click += OnMoveUp;
        MoveDownButton.Click += OnMoveDown;
        MoveLastButton.Click += OnMoveLast;
        ToggleButton.Click += OnToggle;
        ExportButton.Click += (_, _) => OpenImportExport(ImportExportTab.Export);
        ImportButton.Click += (_, _) => OpenImportExport(ImportExportTab.Import);
        ResetButton.Click += OnReset;
        OkButton.Click += OnOk;
        CancelButton.Click += OnCancel;
    }

    private List<ColumnEntry> GetSelectedEntriesInOrder()
    {
        if (ColumnList.SelectedItems is not { Count: > 0 } selectedItems)
        {
            return [];
        }

        var selected = new HashSet<ColumnEntry>(selectedItems.Cast<ColumnEntry>());
        return [.. Entries.Where(e => selected.Contains(e))];
    }

    private void ReselectItems(List<ColumnEntry> items)
    {
        if (ColumnList.SelectedItems is null)
        {
            return;
        }

        ColumnList.SelectedItems.Clear();
        foreach (ColumnEntry item in items)
        {
            ColumnList.SelectedItems.Add(item);
        }
    }

    private void OnMoveTop(object? sender, RoutedEventArgs e)
    {
        List<ColumnEntry> selected = GetSelectedEntriesInOrder();
        if (selected.Count == 0)
        {
            return;
        }

        foreach (ColumnEntry item in selected)
        {
            Entries.Remove(item);
        }

        for (int i = 0; i < selected.Count; i++)
        {
            Entries.Insert(i, selected[i]);
        }

        ReselectItems(selected);
    }

    private void OnMoveUp(object? sender, RoutedEventArgs e)
    {
        List<ColumnEntry> selected = GetSelectedEntriesInOrder();
        if (selected.Count == 0)
        {
            return;
        }

        var indices = selected.Select(item => Entries.IndexOf(item)).ToList();
        if (indices[0] == 0)
        {
            return;
        }

        foreach (ColumnEntry item in selected)
        {
            int idx = Entries.IndexOf(item);
            Entries.RemoveAt(idx);
            Entries.Insert(idx - 1, item);
        }

        ReselectItems(selected);
    }

    private void OnMoveDown(object? sender, RoutedEventArgs e)
    {
        List<ColumnEntry> selected = GetSelectedEntriesInOrder();
        if (selected.Count == 0)
        {
            return;
        }

        var indices = selected.Select(item => Entries.IndexOf(item)).ToList();
        if (indices[^1] == Entries.Count - 1)
        {
            return;
        }

        for (int i = selected.Count - 1; i >= 0; i--)
        {
            int idx = Entries.IndexOf(selected[i]);
            Entries.RemoveAt(idx);
            Entries.Insert(idx + 1, selected[i]);
        }

        ReselectItems(selected);
    }

    private void OnMoveLast(object? sender, RoutedEventArgs e)
    {
        List<ColumnEntry> selected = GetSelectedEntriesInOrder();
        if (selected.Count == 0)
        {
            return;
        }

        foreach (ColumnEntry item in selected)
        {
            Entries.Remove(item);
        }

        foreach (ColumnEntry item in selected)
        {
            Entries.Add(item);
        }

        ReselectItems(selected);
    }

    private void OnToggle(object? sender, RoutedEventArgs e)
    {
        List<ColumnEntry> selected = GetSelectedEntriesInOrder();
        if (selected.Count == 0)
        {
            return;
        }

        bool allVisible = selected.All(item => item.IsVisible);
        foreach (ColumnEntry item in selected)
        {
            item.IsVisible = !allVisible;
        }
    }

    private async void OpenImportExport(ImportExportTab initialTab)
    {
        try
        {
            FavoriteStore favorites =
                _favorites ?? throw new InvalidOperationException("The column chooser's designer window has no favorites store to import or export");
            var hub = new ImportExportWindow(
                _preferences,
                new StagingImportTarget(this, new UserPreferencesImportTarget(_preferences, favorites)),
                new StagingExportSource(this, new UserPreferencesExportSource(_preferences, favorites)),
                new HashSet<SettingsItemType> { SettingsItemType.GridLayout },
                initialTab
            );
            IReadOnlySet<SettingsItemType> imported = await hub.ShowOverAsync(this);
            _liveImported.UnionWith(imported.Where(itemType => itemType != SettingsItemType.GridLayout));
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "The column chooser's Import / Export failed");
        }
    }

    /// <summary>
    /// The column layout OK would apply: the rows' order and visibility, over the grid's widths and sort with a staged
    /// imported layout's widths and sort in their place.
    /// </summary>
    private SavedGridLayout ShownLayout()
    {
        (string? Column, ListSortDirection? Direction) sort = SortOnOk();
        return new SavedGridLayout
        {
            ColumnOrder = [.. Entries.Select(entry => entry.Key)],
            HiddenColumns = Entries.Where(entry => !entry.IsVisible).Select(entry => entry.Key).ToList() is { Count: > 0 } hidden ? hidden : null,
            ColumnWidths = WidthsOnOk(),
            SortColumn = sort.Column,
            SortDirection = sort.Direction,
        };
    }

    // OK sorts the grid by a staged layout's sort when it names one of the grid's columns, and otherwise leaves the sort as it is.
    private (string? Column, ListSortDirection? Direction) SortOnOk() =>
        ((ImportedLayout is { SortColumn: { } column, SortDirection: { } direction }) && Entries.Any(entry => entry.Key == column))
            ? (column, direction)
            : (_state.SortColumn, _state.SortDirection);

    // OK sets each of the grid's columns that a staged layout gives a width to that width, and leaves the rest as they are.
    private Dictionary<string, double>? WidthsOnOk()
    {
        Dictionary<string, double>? current = (_state.ColumnWidths is { } widthsNow) ? new Dictionary<string, double>(widthsNow) : null;
        if (ImportedLayout?.ColumnWidths is not { Count: > 0 } imported)
        {
            return current;
        }

        Dictionary<string, double> widths = current ?? [];
        foreach (ColumnEntry entry in Entries)
        {
            if (imported.TryGetValue(entry.Key, out double width))
            {
                widths[entry.Key] = width;
            }
        }

        return widths;
    }

    /// <summary>Shows an imported column layout in the rows; OK hands it to the grid with its widths and sort.</summary>
    private void StageGridLayout(SavedGridLayout layout)
    {
        // Reorder entries to match imported column order
        if (layout.ColumnOrder is { Count: > 0 })
        {
            var keyToEntry = new Dictionary<string, ColumnEntry>();
            foreach (ColumnEntry entry in Entries)
            {
                keyToEntry[entry.Key] = entry;
            }

            var ordered = new List<ColumnEntry>();
            var used = new HashSet<string>();

            foreach (string key in layout.ColumnOrder)
            {
                if (keyToEntry.TryGetValue(key, out ColumnEntry? entry))
                {
                    ordered.Add(entry);
                    used.Add(key);
                }
            }

            // Append any columns not mentioned in the import (keep relative order)
            foreach (ColumnEntry entry in Entries)
            {
                if (!used.Contains(entry.Key))
                {
                    ordered.Add(entry);
                }
            }

            Entries.Clear();
            foreach (ColumnEntry entry in ordered)
            {
                Entries.Add(entry);
            }
        }

        // Update visibility
        HashSet<string>? hiddenSet = layout.HiddenColumns is { Count: > 0 } ? [.. layout.HiddenColumns] : null;
        foreach (ColumnEntry entry in Entries)
        {
            entry.IsVisible = hiddenSet is null || !hiddenSet.Contains(entry.Key);
        }

        // Store for MainWindow to apply widths/sort after OK
        ImportedLayout = layout;
    }

    private void OnReset(object? sender, RoutedEventArgs e)
    {
        var keyToEntry = new Dictionary<string, ColumnEntry>();
        foreach (ColumnEntry entry in Entries)
        {
            keyToEntry[entry.Key] = entry;
        }

        var ordered = new List<ColumnEntry>();
        var used = new HashSet<string>();

        foreach (string key in _state.DefaultOrder)
        {
            if (keyToEntry.TryGetValue(key, out ColumnEntry? entry))
            {
                entry.IsVisible = true;
                ordered.Add(entry);
                used.Add(key);
            }
        }

        foreach (ColumnEntry entry in Entries)
        {
            if (!used.Contains(entry.Key))
            {
                entry.IsVisible = true;
                ordered.Add(entry);
            }
        }

        Entries.Clear();
        foreach (ColumnEntry entry in ordered)
        {
            Entries.Add(entry);
        }

        ImportedLayout = null;
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        Confirmed = true;
        ShowOnlyActive = ShowOnlyActiveCheckBox.IsChecked == true;
        AlternatingRowColor = AlternatingRowColorCheckBox.IsChecked == true;
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    /// <summary>Stages an imported column layout in the chooser; every other item imports straight into the preferences.</summary>
    private sealed class StagingImportTarget(ColumnChooserWindow chooser, ISettingsImportTarget live) : ISettingsImportTarget
    {
        public IReadOnlyList<SavedMacro> Macros => live.Macros;

        public IReadOnlyList<SavedLayout> Layouts => live.Layouts;

        public FavoriteStore Favorites => live.Favorites;

        public FavoriteImportResult? ImportFavoritesFile(string fileName, byte[] content, FavoriteImportMode mode) =>
            live.ImportFavoritesFile(fileName, content, mode);

        public void ReplaceMacros(IReadOnlyList<SavedMacro> macros) => live.ReplaceMacros(macros);

        public void ReplaceLayouts(IReadOnlyList<SavedLayout> layouts) => live.ReplaceLayouts(layouts);

        public int ApplyVerbs(CommandSchemeImport verbs) => live.ApplyVerbs(verbs);

        public void ReplaceGridLayout(SavedGridLayout layout) => chooser.StageGridLayout(layout);

        public PreferencesImportResult ReplacePreferences(JsonObject preferences) => live.ReplacePreferences(preferences);

        public void LoadImportedFavoriteSets(IReadOnlyList<string> setIds, bool replace) => live.LoadImportedFavoriteSets(setIds, replace);
    }

    /// <summary>Exports the column layout the chooser shows; every other item as the preferences hold it.</summary>
    private sealed class StagingExportSource(ColumnChooserWindow chooser, ISettingsExportSource live) : ISettingsExportSource
    {
        public SettingsBundleEntry Export(SettingsItemType itemType) =>
            (itemType == SettingsItemType.GridLayout) ? SettingsBundleItems.GridLayout(chooser.ShownLayout()) : live.Export(itemType);

        public IReadOnlyList<FavoriteSet> FavoriteSets => live.FavoriteSets;

        public SettingsBundleEntry ExportFavoriteSet(string setId) => live.ExportFavoriteSet(setId);

        public IReadOnlyList<SavedMacro>? SelectedMacros => live.SelectedMacros;
    }
}

/// <summary>What the column chooser opens on: the grid's columns in display order and the settings, widths and sort it has now.</summary>
public sealed record ColumnChooserState
{
    /// <summary>The grid's columns in display order, with their visibility.</summary>
    public required IReadOnlyList<ColumnEntry> Columns { get; init; }

    public required bool ShowOnlyActive { get; init; }

    public required bool AlternatingRowColor { get; init; }

    /// <summary>The widths of the columns that are not auto-sized; null when every column is.</summary>
    public required IReadOnlyDictionary<string, double>? ColumnWidths { get; init; }

    public required string? SortColumn { get; init; }

    public required ListSortDirection? SortDirection { get; init; }

    /// <summary>The grid's column keys in the order the grid declares them, which Reset puts back.</summary>
    public required IReadOnlyList<string> DefaultOrder { get; init; }
}

public partial class ColumnEntry : ObservableObject
{
    public string Key { get; init; } = "";
    public string Name { get; init; } = "";

    [ObservableProperty]
    private bool _isVisible = true;
}
