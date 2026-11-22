using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Yaat.Client.Services;
using Yaat.Client.ViewModels.Authoring;

namespace Yaat.Client.Views.Authoring;

/// <summary>
/// The Scenario Editor's Aircraft tab over a <see cref="ScenarioAircraftViewModel"/>: Add, Duplicate and Remove above a
/// read-only grid of the scenario's aircraft. The grid sorts its own view, never the document. Its column order, widths
/// and sort (as the grid's view holds it) are saved to the aircraft-grid preference when they change, and the saved ones
/// are applied once a copy's rows arrive and the grid is shown, so a tab not yet shown never drops them.
/// </summary>
public partial class ScenarioAircraftView : UserControl
{
    private bool _restoring;
    private bool _layoutPending;
    private string? _lastSaved;
    private DataGridSortDescriptionCollection? _sorts;

    /// <summary>Initializes a new instance of the <see cref="ScenarioAircraftView"/> class.</summary>
    public ScenarioAircraftView()
    {
        InitializeComponent();
        AircraftGrid.ColumnReordered += (_, _) => SaveLayout();
        AircraftGrid.PointerCaptureLost += (_, _) => SaveLayout();
        AircraftGrid.PropertyChanged += OnGridPropertyChanged;
        AircraftGrid.Loaded += OnGridLoaded;
    }

    // A column's key in the saved layout: its header text.
    private static string ColumnKey(DataGridColumn column) => (column.Header as string) ?? column.SortMemberPath ?? "";

    // A copy's rows arrived (an open, or a reload after an import): the saved layout waits for the grid to be shown.
    private void OnGridPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if ((e.Property == DataGrid.ItemsSourceProperty) && (e.NewValue is not null))
        {
            _layoutPending = true;
            HookSorts();
            RestorePending();
        }
    }

    private void OnGridLoaded(object? sender, RoutedEventArgs e)
    {
        HookSorts();
        RestorePending();
    }

    // Follows the sort descriptions of the grid's current view, the one the grid made over the rows it shows.
    private void HookSorts()
    {
        DataGridSortDescriptionCollection? sorts = AircraftGrid.CollectionView?.SortDescriptions;
        if (ReferenceEquals(sorts, _sorts))
        {
            return;
        }

        _sorts?.CollectionChanged -= OnSortsChanged;
        _sorts = sorts;
        _sorts?.CollectionChanged += OnSortsChanged;
    }

    private void OnSortsChanged(object? sender, NotifyCollectionChangedEventArgs e) => SaveLayout();

    // Applies the saved layout once rows arrived and the grid is in the window with its headers.
    private void RestorePending()
    {
        if (!_layoutPending || !AircraftGrid.IsLoaded || (DataContext is not ScenarioAircraftViewModel vm))
        {
            return;
        }

        _layoutPending = false;
        ApplyLayout(vm.GridLayout);
    }

    // Applies the layout and takes it as the one last saved: the sort the grid applies later, and a click that changes
    // nothing, write nothing.
    private void ApplyLayout(SavedGridLayout? layout)
    {
        _restoring = true;
        try
        {
            ApplyOrder(layout?.ColumnOrder ?? []);
            ApplyWidths(layout?.ColumnWidths ?? []);
            SavedGridLayout applied = Capture();
            if (ApplySort(layout?.SortColumn, layout?.SortDirection))
            {
                applied.SortColumn = layout!.SortColumn;
                applied.SortDirection = layout.SortDirection;
            }

            _lastSaved = Signature(applied);
        }
        finally
        {
            _restoring = false;
        }
    }

    private void ApplyOrder(List<string> order)
    {
        var byKey = AircraftGrid.Columns.ToDictionary(ColumnKey, StringComparer.Ordinal);
        int displayIndex = 0;
        foreach (string key in order)
        {
            if (byKey.Remove(key, out DataGridColumn? column))
            {
                column.DisplayIndex = displayIndex++;
            }
        }
    }

    private void ApplyWidths(Dictionary<string, double> widths)
    {
        foreach (DataGridColumn column in AircraftGrid.Columns)
        {
            if (widths.TryGetValue(ColumnKey(column), out double width) && (width > 0))
            {
                column.Width = new DataGridLength(width);
            }
        }
    }

    // Asks the column to sort as a header click does (the grid applies it on its next dispatcher turn); false when the
    // layout has no sort or no column has its key.
    private bool ApplySort(string? sortColumn, ListSortDirection? direction)
    {
        if ((sortColumn is null) || (direction is not { } dir))
        {
            return false;
        }

        DataGridColumn? column = AircraftGrid.Columns.FirstOrDefault(c => string.Equals(ColumnKey(c), sortColumn, StringComparison.Ordinal));
        column?.Sort(dir);
        return column is not null;
    }

    // The grid's column order, the widths of the columns given one, and its view's first sort mapped back to its column.
    private SavedGridLayout Capture()
    {
        DataGridSortDescription? sort = AircraftGrid.CollectionView?.SortDescriptions.FirstOrDefault();
        DataGridColumn? sorted =
            (sort is { HasPropertyPath: true })
                ? AircraftGrid.Columns.FirstOrDefault(c => string.Equals(c.SortMemberPath, sort.PropertyPath, StringComparison.Ordinal))
                : null;
        return new SavedGridLayout
        {
            ColumnOrder = [.. AircraftGrid.Columns.OrderBy(c => c.DisplayIndex).Select(ColumnKey)],
            ColumnWidths = AircraftGrid.Columns.Where(c => !c.Width.IsAuto).ToDictionary(ColumnKey, c => c.ActualWidth, StringComparer.Ordinal),
            SortColumn = (sorted is null) ? null : ColumnKey(sorted),
            SortDirection = (sorted is null) ? null : sort!.Direction,
        };
    }

    // Saves the grid's column order, widths and sort when they differ from what was last saved or applied.
    private void SaveLayout()
    {
        if (_restoring || _layoutPending || (DataContext is not ScenarioAircraftViewModel vm))
        {
            return;
        }

        SavedGridLayout layout = Capture();
        string signature = Signature(layout);
        if (string.Equals(signature, _lastSaved, StringComparison.Ordinal))
        {
            return;
        }

        _lastSaved = signature;
        vm.SaveGridLayout(layout);
    }

    private static string Signature(SavedGridLayout layout) =>
        string.Join(
            "|",
            string.Join(",", layout.ColumnOrder ?? []),
            string.Join(
                ",",
                (layout.ColumnWidths ?? []).OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => FormattableString.Invariant($"{p.Key}={p.Value}"))
            ),
            layout.SortColumn ?? "",
            layout.SortDirection?.ToString() ?? ""
        );
}
