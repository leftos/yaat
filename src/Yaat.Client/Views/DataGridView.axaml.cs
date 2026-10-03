using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging;
using Yaat.Client.ContextMenus;
using Yaat.Client.Logging;
using Yaat.Client.Models;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views;

public partial class DataGridView : UserControl
{
    private static readonly ILogger MenuLog = AppLog.CreateLogger("DataGridView");
    private bool _suppressSelectionFeedback;
    private RightPress? _rightPress;

    public DataGridView()
    {
        InitializeComponent();
        KeyDown += OnDataGridViewKeyDown;
    }

    public DataGrid? GetDataGrid() => this.FindControl<DataGrid>("AircraftGrid");

    /// <summary>A right-click on a list row: the row's aircraft, and the selected row that sends the relative items, or null.</summary>
    internal readonly record struct RightPress(AircraftModel Clicked, AircraftModel? Previous);

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        DataGrid? grid = GetDataGrid();
        if (grid is null || DataContext is not MainViewModel vm)
        {
            return;
        }

        grid.SelectionChanged += OnGridSelectionChanged;
        grid.DoubleTapped += OnGridDoubleTapped;
        grid.ContextRequested += OnGridContextRequested;
        grid.AddHandler(PointerPressedEvent, OnGridPointerPressed, RoutingStrategies.Tunnel);
        vm.PropertyChanged += OnViewModelPropertyChanged;

        TextBox? searchBox = this.FindControl<TextBox>("SearchBox");
        searchBox?.KeyDown += OnSearchBoxKeyDown;
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        base.OnUnloaded(e);

        DataGrid? grid = GetDataGrid();
        if (grid is not null)
        {
            grid.SelectionChanged -= OnGridSelectionChanged;
            grid.DoubleTapped -= OnGridDoubleTapped;
            grid.ContextRequested -= OnGridContextRequested;
            grid.RemoveHandler(PointerPressedEvent, OnGridPointerPressed);
        }

        TextBox? searchBox = this.FindControl<TextBox>("SearchBox");
        searchBox?.KeyDown -= OnSearchBoxKeyDown;

        if (DataContext is MainViewModel vm)
        {
            vm.PropertyChanged -= OnViewModelPropertyChanged;
        }
    }

    private void OnSearchBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        if (sender is TextBox textBox)
        {
            textBox.Text = "";
        }

        GetDataGrid()?.Focus();
        e.Handled = true;
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SelectedAircraft))
        {
            _suppressSelectionFeedback = true;
            try
            {
                DataGrid? grid = GetDataGrid();
                if (grid is not null && sender is MainViewModel vm)
                {
                    grid.SelectedItem = vm.SelectedAircraft;

                    // #351: bring the selected row into view for selections made outside the grid
                    // (radar/ground click, command input, context menus). Deferred because the row
                    // may not be realized yet when the selection lands (Avalonia DataGrid quirk),
                    // and skipped when the active filter hides the aircraft from the view.
                    AircraftModel? selected = vm.SelectedAircraft;
                    if (selected is not null && vm.AircraftView.Contains(selected))
                    {
                        Dispatcher.UIThread.Post(() => grid.ScrollIntoView(selected, null), DispatcherPriority.Background);
                    }
                }
            }
            finally
            {
                _suppressSelectionFeedback = false;
            }
        }
    }

    private void OnGridSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionFeedback)
        {
            return;
        }

        if (sender is not DataGrid grid || DataContext is not MainViewModel vm)
        {
            return;
        }

        vm.SelectedAircraft = grid.SelectedItem as AircraftModel;
    }

    /// <summary>The data row <paramref name="source"/> sits in; null for a column header or the space outside the rows.</summary>
    private static DataGridRow? FindDataRow(object? source)
    {
        for (var visual = source as Control; visual is not null; visual = visual.GetVisualParent() as Control)
        {
            if (visual is DataGridRow row)
            {
                return row;
            }

            if (visual is DataGridColumnHeader)
            {
                return null;
            }
        }

        return null;
    }

    private void OnGridDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not DataGrid grid || grid.SelectedItem is not AircraftModel ac || DataContext is not MainViewModel vm)
        {
            return;
        }

        if (FindDataRow(e.Source) is null)
        {
            return;
        }

        FlightPlanEditorManager.Open(ac, vm);
    }

    /// <summary>
    /// Records a right-press on a data row before the grid handles it: the row's aircraft and, when another row is
    /// selected, that row as the sender of the relative items (<see cref="ResolveRightClick"/>). With another row
    /// selected the press is marked handled, so the grid keeps that selection; the context request still follows on
    /// release.
    /// </summary>
    private void OnGridPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _rightPress = null;
        if (sender is not DataGrid grid || DataContext is not MainViewModel vm)
        {
            return;
        }

        if (e.GetCurrentPoint(grid).Properties.PointerUpdateKind != PointerUpdateKind.RightButtonPressed)
        {
            return;
        }

        if (FindDataRow(e.Source)?.DataContext is not AircraftModel clicked)
        {
            return;
        }

        RightPress press = ResolveRightClick(clicked, vm.SelectedAircraft);
        _rightPress = press;
        if (press.Previous is not null)
        {
            e.Handled = true;
        }
    }

    /// <summary>
    /// Attaches the menu for the request, after detaching the last one so a request outside the rows opens none. A
    /// pointer request commands the row under the pointer, sent from the selection recorded on its press; a keyboard
    /// request has no pointer and commands the selected row, with no previous selection.
    /// </summary>
    private void OnGridContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        RightPress? press = _rightPress;
        _rightPress = null;
        if (sender is not DataGrid grid)
        {
            return;
        }

        grid.ContextMenu = null;
        if (DataContext is not MainViewModel vm)
        {
            MenuLog.LogWarning("Context request on the aircraft list: the list has no main view model, so no aircraft menu opens");
            return;
        }

        RightPress? click;
        if (e.TryGetPosition(grid, out _))
        {
            click = PointerClick(e.Source, press);
        }
        else
        {
            click = KeyboardClick(grid);
        }

        if (click is not { } resolved)
        {
            return;
        }

        grid.ContextMenu = BuildAircraftMenu(vm, grid, resolved.Clicked, resolved.Previous, [resolved.Clicked]);
    }

    /// <summary>
    /// The row under a pointer request: the press recorded on that row when there is one, otherwise the row with no
    /// previous selection; null outside the rows.
    /// </summary>
    private static RightPress? PointerClick(object? source, RightPress? press)
    {
        if (FindDataRow(source)?.DataContext is not AircraftModel clicked)
        {
            return null;
        }

        if ((press is { } recorded) && ReferenceEquals(recorded.Clicked, clicked))
        {
            return recorded;
        }

        return new RightPress(clicked, null);
    }

    /// <summary>The selected row for a keyboard request, with no previous selection; null when no row is selected.</summary>
    private static RightPress? KeyboardClick(DataGrid grid)
    {
        if (grid.SelectedItem is AircraftModel selected)
        {
            return new RightPress(selected, null);
        }

        return null;
    }

    /// <summary>
    /// Which aircraft a right-click on a list row commands and which sends its relative items, by the canvases' rule:
    /// the previous selection is <paramref name="selected"/> when it is another aircraft than
    /// <paramref name="clicked"/> (callsigns compared ignoring case), otherwise null.
    /// </summary>
    internal static RightPress ResolveRightClick(AircraftModel clicked, AircraftModel? selected)
    {
        bool another = (selected is not null) && !string.Equals(selected.Callsign, clicked.Callsign, StringComparison.OrdinalIgnoreCase);
        return new RightPress(clicked, another ? selected : null);
    }

    /// <summary>
    /// The whole aircraft-list context menu a right-click shows, built without assigning it, through
    /// <see cref="AircraftMenuBuilder"/>: the right-clicked <paramref name="ac"/>, the selected aircraft
    /// <paramref name="previousSelection"/> that relative actions target, and <paramref name="selection"/> supplying the
    /// RPO callsigns and the assumable shadows. The list draws no canvas, so its view section is empty. Popups and
    /// flyouts open at the pointer on <paramref name="flyoutTarget"/>.
    /// </summary>
    internal static ContextMenu BuildAircraftMenu(
        MainViewModel vm,
        Control flyoutTarget,
        AircraftModel ac,
        AircraftModel? previousSelection,
        IReadOnlyList<AircraftModel> selection
    )
    {
        var host = new ClientMenuHost(vm, ac, flyoutTarget);
        return AircraftMenuBuilder.Build(ac, new MenuClick(ac.Callsign, previousSelection, selection), host, _ => []);
    }

    private void OnDataGridViewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.Control || DataContext is not MainViewModel vm)
        {
            return;
        }

        int delta = e.Key switch
        {
            Key.OemPlus => 1,
            Key.Add => 1,
            Key.OemMinus => -1,
            Key.Subtract => -1,
            _ => 0,
        };
        bool reset = e.Key is Key.D0 or Key.NumPad0;

        if (delta != 0)
        {
            int current = vm.Preferences.DataGridFontSize;
            int next = Math.Clamp(current + delta, 8, 24);
            vm.Preferences.SetDataGridFontSize(next);
            vm.DataGridScale = next / 12.0;
            e.Handled = true;
        }
        else if (reset)
        {
            vm.Preferences.SetDataGridFontSize(12);
            vm.DataGridScale = 1.0;
            e.Handled = true;
        }
    }
}
