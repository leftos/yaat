using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views;

public partial class DataGridView : UserControl
{
    private bool _suppressSelectionFeedback;

    public DataGridView()
    {
        InitializeComponent();
        KeyDown += OnDataGridViewKeyDown;
    }

    public DataGrid? GetDataGrid() => this.FindControl<DataGrid>("AircraftGrid");

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

    private static bool IsInDataRow(object? source)
    {
        for (var visual = source as Control; visual is not null; visual = visual.GetVisualParent() as Control)
        {
            if (visual is DataGridRow)
            {
                return true;
            }

            if (visual is DataGridColumnHeader)
            {
                return false;
            }
        }

        return false;
    }

    private void OnGridDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not DataGrid grid || grid.SelectedItem is not AircraftModel ac || DataContext is not MainViewModel vm)
        {
            return;
        }

        if (!IsInDataRow(e.Source))
        {
            return;
        }

        FlightPlanEditorManager.Open(ac, vm);
    }

    private void OnGridContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not DataGrid grid || grid.SelectedItem is not AircraftModel ac || DataContext is not MainViewModel vm)
        {
            return;
        }

        if (!IsInDataRow(e.Source))
        {
            return;
        }

        List<AircraftModel> selection = [.. grid.SelectedItems.OfType<AircraftModel>()];
        grid.ContextMenu = BuildAircraftMenu(vm, grid, ac, selection, vm.Preferences.UserInitials);
    }

    /// <summary>
    /// The whole aircraft-list context menu a right-click shows, built without assigning it: the header, the
    /// favorites block, the phase-aware command groups and the multi-selection RPO items, for
    /// <paramref name="ac"/> with <paramref name="selection"/> supplying the selected callsigns and shadows.
    /// <paramref name="flyoutTarget"/> is the control the command and note flyouts anchor to.
    /// </summary>
    internal static ContextMenu BuildAircraftMenu(
        MainViewModel vm,
        Control flyoutTarget,
        AircraftModel ac,
        IReadOnlyList<AircraftModel> selection,
        string initials
    )
    {
        string callsign = ac.Callsign;
        var context = new MenuContext(callsign, initials, null, vm.SessionSoloTrainingMode, vm.VfrCommandsForIfr, MenuView.List);
        var host = new ListMenuHost(vm, ac, flyoutTarget);
        var menu = new ContextMenu();
        SharedMenuGroups.AddHeader(menu.Items, ac, context, host, []);
        menu.Items.Add(SharedMenuGroups.Favorites(ac, context, host));
        menu.Items.Add(new Separator());

        if (ac.IsDelayed)
        {
            SharedMenuGroups.AddDelayedSpawn(menu, ac, context, host);
            return menu;
        }

        AddCommandGroups(menu, ac, context, host);
        SharedMenuGroups.AddFoot(menu.Items, ac, context, host);

        // RPO control
        List<string> selectedCallsigns = [.. selection.Select(a => a.Callsign)];
        if (selectedCallsigns.Count == 0)
        {
            selectedCallsigns = [callsign];
        }

        List<string> selectedShadows = [.. selection.Where(AircraftCommandApplicability.CanAssume).Select(a => a.Callsign)];
        SharedMenuGroups.AddAssumeSelected(menu, selectedShadows, context, host);

        vm.BuildRpoMenuItems(menu, selectedCallsigns);

        return menu;
    }

    /// <summary>
    /// The command groups between the favorites block and the foot (<see cref="SharedMenuGroups.AddFoot"/>). An
    /// assumable live-traffic shadow takes the two assume items and then the same phase-aware groups a simulated
    /// aircraft gets: a command sent to an airborne shadow auto-assumes it server-side, so the groups apply as they
    /// are, minus the two the server refuses for a shadow — the ask-pilot queries
    /// (<see cref="AircraftCommandApplicability.CanAskPilot"/>) and the flight-plan editor
    /// (<see cref="AircraftCommandApplicability.CanEditFlightPlan"/>). A surface shadow is not assumable and keeps its
    /// read-only track / coordination menu.
    /// </summary>
    private static void AddCommandGroups(ContextMenu menu, AircraftModel ac, MenuContext context, ListMenuHost host)
    {
        if (AircraftCommandApplicability.CanAssume(ac))
        {
            SharedMenuGroups.AddLiveTrafficAssume(menu.Items, ac, context, host);
            menu.Items.Add(new Separator());
        }
        else if (ac.IsLiveTraffic)
        {
            menu.Items.Add(SharedMenuGroups.Track(ac, context, host, MenuView.List));
            menu.Items.Add(SharedMenuGroups.Coordination(ac, context, host));
            return;
        }

        SharedMenuGroups.AddListAircraftCommands(menu.Items, ac, context, host);

        menu.Items.Add(new Separator());
        menu.Items.Add(SharedMenuGroups.Track(ac, context, host, MenuView.List));
        menu.Items.Add(SharedMenuGroups.Squawk(ac, context, host, MenuView.List));
        if (AircraftCommandApplicability.CanAskPilot(ac))
        {
            menu.Items.Add(SharedMenuGroups.AskPilot(ac, context, host, MenuView.List));
        }

        menu.Items.Add(SharedMenuGroups.Coordination(ac, context, host));

        menu.Items.Add(new Separator());
        if (SharedMenuGroups.EditFlightPlan(ac, context, host) is { } editItem)
        {
            menu.Items.Add(editItem);
        }
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
