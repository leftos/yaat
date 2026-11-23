using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Input;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Yaat.Client.Services;
using Yaat.Client.ViewModels.Authoring;

namespace Yaat.Client.Views.Authoring;

/// <summary>
/// The Scenario Editor's Aircraft tab over a <see cref="ScenarioAircraftViewModel"/>: Add, Duplicate and Remove above a
/// read-only grid of the scenario's aircraft, and right of it, while an aircraft is selected, its detail panel, whose text
/// fields commit on focus loss or Enter through the commit command each names in its Tag, and whose type and airport
/// boxes also commit a pick from their drop-down. The grid sorts its own view, never the document. Its column order,
/// widths and sort (as the grid's view holds it) are saved to the aircraft-grid preference when they change, and the
/// saved ones are applied once a copy's rows arrive and the grid is shown, so a tab not yet shown never drops them.
/// </summary>
public partial class ScenarioAircraftView : UserControl
{
    private bool _restoring;
    private bool _layoutPending;
    private string? _lastSaved;
    private DataGridSortDescriptionCollection? _sorts;

    // The type or airport box whose drop-down just closed, until the next dispatcher turn.
    private Control? _justClosed;

    // The type or airport box whose text box lost the focus to its own open drop-down, until that drop-down closes.
    private Control? _listFocused;

    // The type or airport box a pick from the drop-down is being committed for, until that commit runs.
    private Control? _picking;

    // The type or airport box whose pick was committed before its drop-down's close was handled, until that close is.
    private Control? _picked;

    /// <summary>Initializes a new instance of the <see cref="ScenarioAircraftView"/> class.</summary>
    public ScenarioAircraftView()
    {
        InitializeComponent();
        AircraftGrid.ColumnReordered += (_, _) => SaveLayout();
        AircraftGrid.PointerCaptureLost += (_, _) => SaveLayout();
        AircraftGrid.PropertyChanged += OnGridPropertyChanged;
        AircraftGrid.Loaded += OnGridLoaded;
        DetailPanel.AddHandler(LostFocusEvent, OnFieldLostFocus, RoutingStrategies.Bubble, handledEventsToo: true);
        DetailPanel.AddHandler(KeyDownEvent, OnFieldKeyDown, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    /// <summary>
    /// Commits the detail panel's text field that has the keyboard focus, as losing the focus would, so Save and closing
    /// take the text still being typed in; does nothing when no field of the panel has it. A type or airport box commits
    /// its text whether the focus is in its text box or on an item of its drop-down.
    /// </summary>
    public void CommitFocusedField()
    {
        IInputElement? focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        if ((ListBoxOf(focused as Visual) is { } owner) && DetailPanel.IsVisualAncestorOf(owner))
        {
            Execute(owner.Tag as ICommand);
        }
        else if ((focused is Visual at) && DetailPanel.IsVisualAncestorOf(at))
        {
            Commit(at);
        }
    }

    // A field commits when it loses the focus. A type or airport box (its text box and its drop-down together) commits its
    // text when the focus leaves it entirely, and nothing when the focus moves between its text box and its open
    // drop-down; a move into the drop-down is remembered until it closes.
    private void OnFieldLostFocus(object? sender, FocusChangedEventArgs e)
    {
        if (ListBoxOf(e.Source as Visual) is not { } owner)
        {
            Commit(e.Source);
        }
        else if (!HasFocusIn(owner, e.NewFocusedElement))
        {
            _listFocused = null;
            Execute(owner.Tag as ICommand);
        }
        else if (!IsInBoxTree(owner, e.NewFocusedElement))
        {
            _listFocused = owner;
        }
    }

    // A type or airport box's drop-down closed: a selection it raises before the next dispatcher turn came from it.
    private void OnListClosed(object? sender, EventArgs e)
    {
        if (sender is Control control)
        {
            bool listHadFocus = ReferenceEquals(_listFocused, control);
            _listFocused = null;
            _justClosed = control;
            Dispatcher.UIThread.Post(() => AfterListClosed(control, listHadFocus));
        }
    }

    // The drop-down closed and the input that closed it is handled. The box's text is committed as its focus loss would
    // when the focus is no longer in the box (an item of a closed drop-down is not in it), or when the drop-down held the
    // focus and closed without a pick: a click elsewhere dismisses the drop-down without reaching what it clicked, and the
    // focus goes back to the box's text box. A commit of the text the document already holds records nothing. A pick
    // already committed (its selection came before the close) commits nothing more, and neither does a close after the
    // view left its window.
    private void AfterListClosed(Control control, bool listHadFocus)
    {
        _justClosed = null;
        bool committed = ReferenceEquals(_picked, control);
        _picked = null;
        if (committed || (TopLevel.GetTopLevel(this) is not { } top))
        {
            return;
        }

        IInputElement? focused = top.FocusManager?.GetFocusedElement();
        if (!HasFocusIn(control, focused) || (listHadFocus && !ReferenceEquals(_picking, control)))
        {
            Execute(control.Tag as ICommand);
        }
    }

    // Whether the focus is still in a type or airport box: on the box or in its own visual tree, or on an item of its
    // drop-down while the drop-down is open.
    private static bool HasFocusIn(Control box, IInputElement? focused) =>
        IsInBoxTree(box, focused) || (IsOpen(box) && ReferenceEquals(ListBoxOf(focused as Visual), box));

    // Whether an element is the box or in its own visual tree (its text box, its toggle), which its drop-down is not.
    private static bool IsInBoxTree(Control box, IInputElement? element) =>
        (element is Visual at) && (ReferenceEquals(at, box) || box.IsVisualAncestorOf(at));

    // A pick from the type box's suggestions or the airport box's list commits the picked value as one step, once the
    // drop-down has closed with it. Only a selection that comes from the drop-down (open when it changed, or closed by
    // it) is a pick: a typed text that matches an entry commits on focus loss or Enter like any text, and arrowing
    // through the open drop-down commits nothing until it closes on a pick, Enter or focus loss.
    private void OnListPicked(object? sender, SelectionChangedEventArgs e)
    {
        if (
            (sender is Control control)
            && (e.AddedItems.Count > 0)
            && (e.AddedItems[0] is string)
            && (IsOpen(control) || ReferenceEquals(_justClosed, control))
        )
        {
            _picking = control;
            Dispatcher.UIThread.Post(() => CommitPick(control));
        }
    }

    private static bool IsOpen(Control control) =>
        control switch
        {
            AutoCompleteBox box => box.IsDropDownOpen,
            ComboBox combo => combo.IsDropDownOpen,
            _ => false,
        };

    // Commits the pick once the drop-down has closed; when its close is still to be handled, it is told the pick is
    // committed. Nothing is committed after the view left its window.
    private void CommitPick(Control control)
    {
        if (ReferenceEquals(_picking, control))
        {
            _picking = null;
        }

        if (TopLevel.GetTopLevel(this) is null)
        {
            return;
        }

        (object? selected, string? text) = control switch
        {
            AutoCompleteBox box => (box.SelectedItem, box.Text),
            ComboBox combo => (combo.SelectedItem, combo.Text),
            _ => (null, null),
        };
        if (!IsOpen(control) && (selected is string picked) && string.Equals(picked, text, StringComparison.Ordinal))
        {
            if (ReferenceEquals(_justClosed, control))
            {
                _picked = control;
            }

            Execute(control.Tag as ICommand);
        }
    }

    // The type or airport box an element belongs to: the box itself, its text box, or an item of its drop-down, whose
    // visual or logical parents lead back to the box through its template; null for any other element.
    private static Control? ListBoxOf(Visual? element)
    {
        var seen = new HashSet<Visual>(ReferenceEqualityComparer.Instance);
        for (Visual? at = element; (at is not null) && seen.Add(at); at = at.GetVisualParent() ?? ((at as Control)?.Parent as Visual))
        {
            if (at is AutoCompleteBox or ComboBox { IsEditable: true })
            {
                return (Control)at;
            }

            if (at.TemplatedParent is AutoCompleteBox or ComboBox { IsEditable: true })
            {
                return (Control)at.TemplatedParent;
            }
        }

        return null;
    }

    private static void Execute(ICommand? command)
    {
        if ((command is not null) && command.CanExecute(null))
        {
            command.Execute(null);
        }
    }

    // Enter commits a field, except in a multi-line one, where it starts a new line.
    private void OnFieldKeyDown(object? sender, KeyEventArgs e)
    {
        if ((e.Key == Key.Enter) && (e.Source is TextBox { AcceptsReturn: false }))
        {
            Commit(e.Source);
        }
    }

    // Runs the commit command the field names in its Tag: a text box's own, or that of the type or airport box whose text
    // box it is.
    private void Commit(object? source)
    {
        if (source is not TextBox box)
        {
            return;
        }

        for (Visual? at = box; (at is not null) && !ReferenceEquals(at, DetailPanel); at = at.GetVisualParent())
        {
            if (at is Control { Tag: ICommand command })
            {
                Execute(command);
                return;
            }
        }
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
