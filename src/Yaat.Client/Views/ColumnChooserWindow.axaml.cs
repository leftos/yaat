using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using Yaat.Client.Services;

namespace Yaat.Client.Views;

public partial class ColumnChooserWindow : Window
{
    private static readonly FilePickerFileType GridLayoutFileType = new("YAAT Grid Layout") { Patterns = ["*.yaat-grid-layout.json"] };

    private readonly Dictionary<string, double>? _columnWidths;
    private readonly string? _sortColumn;
    private readonly ListSortDirection? _sortDirection;
    private readonly List<string> _defaultOrder;

    public ObservableCollection<ColumnEntry> Entries { get; } = [];
    public bool Confirmed { get; private set; }
    public bool ShowOnlyActive { get; private set; }
    public SavedGridLayout? ImportedLayout { get; private set; }

    public ColumnChooserWindow()
    {
        InitializeComponent();
        _defaultOrder = [];
    }

    public ColumnChooserWindow(
        List<ColumnEntry> columns,
        bool showOnlyActive,
        Dictionary<string, double>? columnWidths,
        string? sortColumn,
        ListSortDirection? sortDirection,
        List<string> defaultOrder
    )
    {
        InitializeComponent();

        _columnWidths = columnWidths;
        _sortColumn = sortColumn;
        _sortDirection = sortDirection;
        _defaultOrder = defaultOrder;

        foreach (var col in columns)
        {
            Entries.Add(col);
        }

        ColumnList.ItemsSource = Entries;
        ShowOnlyActiveCheckBox.IsChecked = showOnlyActive;

        MoveTopButton.Click += OnMoveTop;
        MoveUpButton.Click += OnMoveUp;
        MoveDownButton.Click += OnMoveDown;
        MoveLastButton.Click += OnMoveLast;
        ExportButton.Click += OnExport;
        ImportButton.Click += OnImport;
        ResetButton.Click += OnReset;
        OkButton.Click += OnOk;
        CancelButton.Click += OnCancel;
    }

    private void OnMoveTop(object? sender, RoutedEventArgs e)
    {
        var idx = ColumnList.SelectedIndex;
        if (idx <= 0)
        {
            return;
        }

        var item = Entries[idx];
        Entries.RemoveAt(idx);
        Entries.Insert(0, item);
        ColumnList.SelectedIndex = 0;
    }

    private void OnMoveUp(object? sender, RoutedEventArgs e)
    {
        var idx = ColumnList.SelectedIndex;
        if (idx <= 0)
        {
            return;
        }

        var item = Entries[idx];
        Entries.RemoveAt(idx);
        Entries.Insert(idx - 1, item);
        ColumnList.SelectedIndex = idx - 1;
    }

    private void OnMoveDown(object? sender, RoutedEventArgs e)
    {
        var idx = ColumnList.SelectedIndex;
        if (idx < 0 || idx >= Entries.Count - 1)
        {
            return;
        }

        var item = Entries[idx];
        Entries.RemoveAt(idx);
        Entries.Insert(idx + 1, item);
        ColumnList.SelectedIndex = idx + 1;
    }

    private void OnMoveLast(object? sender, RoutedEventArgs e)
    {
        var idx = ColumnList.SelectedIndex;
        if (idx < 0 || idx >= Entries.Count - 1)
        {
            return;
        }

        var item = Entries[idx];
        Entries.RemoveAt(idx);
        Entries.Add(item);
        ColumnList.SelectedIndex = Entries.Count - 1;
    }

    private async void OnExport(object? sender, RoutedEventArgs e)
    {
        var layout = new SavedGridLayout
        {
            ColumnOrder = Entries.Select(entry => entry.Key).ToList(),
            HiddenColumns = Entries.Where(entry => !entry.IsVisible).Select(entry => entry.Key).ToList() is { Count: > 0 } hidden ? hidden : null,
            ColumnWidths = _columnWidths,
            SortColumn = _sortColumn,
            SortDirection = _sortDirection,
        };

        var file = await StorageProvider.SaveFilePickerAsync(
            new FilePickerSaveOptions
            {
                Title = "Export Grid Layout",
                SuggestedFileName = "layout.yaat-grid-layout.json",
                FileTypeChoices = [GridLayoutFileType],
            }
        );

        if (file is null)
        {
            return;
        }

        await using var stream = await file.OpenWriteAsync();
        await JsonSerializer.SerializeAsync(stream, layout, UserPreferences.JsonOptions);
    }

    private async void OnImport(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = "Import Grid Layout",
                AllowMultiple = false,
                FileTypeFilter = [GridLayoutFileType],
            }
        );

        if (files.Count == 0)
        {
            return;
        }

        SavedGridLayout? layout;
        try
        {
            await using var stream = await files[0].OpenReadAsync();
            layout = await JsonSerializer.DeserializeAsync<SavedGridLayout>(stream, UserPreferences.JsonOptions);
        }
        catch (JsonException)
        {
            return;
        }

        if (layout is null)
        {
            return;
        }

        // Reorder entries to match imported column order
        if (layout.ColumnOrder is { Count: > 0 })
        {
            var keyToEntry = new Dictionary<string, ColumnEntry>();
            foreach (var entry in Entries)
            {
                keyToEntry[entry.Key] = entry;
            }

            var ordered = new List<ColumnEntry>();
            var used = new HashSet<string>();

            foreach (var key in layout.ColumnOrder)
            {
                if (keyToEntry.TryGetValue(key, out var entry))
                {
                    ordered.Add(entry);
                    used.Add(key);
                }
            }

            // Append any columns not mentioned in the import (keep relative order)
            foreach (var entry in Entries)
            {
                if (!used.Contains(entry.Key))
                {
                    ordered.Add(entry);
                }
            }

            Entries.Clear();
            foreach (var entry in ordered)
            {
                Entries.Add(entry);
            }
        }

        // Update visibility
        var hiddenSet = layout.HiddenColumns is { Count: > 0 } ? new HashSet<string>(layout.HiddenColumns) : null;
        foreach (var entry in Entries)
        {
            entry.IsVisible = hiddenSet is null || !hiddenSet.Contains(entry.Key);
        }

        // Store for MainWindow to apply widths/sort after OK
        ImportedLayout = layout;
    }

    private void OnReset(object? sender, RoutedEventArgs e)
    {
        var keyToEntry = new Dictionary<string, ColumnEntry>();
        foreach (var entry in Entries)
        {
            keyToEntry[entry.Key] = entry;
        }

        var ordered = new List<ColumnEntry>();
        var used = new HashSet<string>();

        foreach (var key in _defaultOrder)
        {
            if (keyToEntry.TryGetValue(key, out var entry))
            {
                entry.IsVisible = true;
                ordered.Add(entry);
                used.Add(key);
            }
        }

        foreach (var entry in Entries)
        {
            if (!used.Contains(entry.Key))
            {
                entry.IsVisible = true;
                ordered.Add(entry);
            }
        }

        Entries.Clear();
        foreach (var entry in ordered)
        {
            Entries.Add(entry);
        }

        ImportedLayout = null;
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        Confirmed = true;
        ShowOnlyActive = ShowOnlyActiveCheckBox.IsChecked == true;
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}

public partial class ColumnEntry : ObservableObject
{
    public string Key { get; init; } = "";
    public string Name { get; init; } = "";

    [ObservableProperty]
    private bool _isVisible = true;
}
