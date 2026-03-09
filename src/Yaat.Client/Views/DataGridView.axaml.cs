using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Yaat.Client.Models;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views;

public partial class DataGridView : UserControl
{
    private bool _suppressSelectionFeedback;

    public DataGridView()
    {
        InitializeComponent();
    }

    public DataGrid? GetDataGrid()
    {
        return this.FindControl<DataGrid>("AircraftGrid");
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        var grid = GetDataGrid();
        if (grid is null || DataContext is not MainViewModel vm)
        {
            return;
        }

        grid.SelectionChanged += OnGridSelectionChanged;
        grid.DoubleTapped += OnGridDoubleTapped;
        grid.ContextRequested += OnGridContextRequested;
        vm.PropertyChanged += OnViewModelPropertyChanged;

        var searchBox = this.FindControl<TextBox>("SearchBox");
        if (searchBox is not null)
        {
            searchBox.KeyDown += OnSearchBoxKeyDown;
        }
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        base.OnUnloaded(e);

        var grid = GetDataGrid();
        if (grid is not null)
        {
            grid.SelectionChanged -= OnGridSelectionChanged;
            grid.DoubleTapped -= OnGridDoubleTapped;
            grid.ContextRequested -= OnGridContextRequested;
        }

        var searchBox = this.FindControl<TextBox>("SearchBox");
        if (searchBox is not null)
        {
            searchBox.KeyDown -= OnSearchBoxKeyDown;
        }

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
                var grid = GetDataGrid();
                if (grid is not null && sender is MainViewModel vm)
                {
                    grid.SelectedItem = vm.SelectedAircraft;
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

        FlightPlanEditorManager.Open(ac, vm, TopLevel.GetTopLevel(this) as Window);
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

        var callsign = ac.Callsign;
        var initials = vm.Preferences.UserInitials;
        var menu = new ContextMenu();

        menu.Items.Add(
            new MenuItem
            {
                Header = $"{callsign} — {ac.AircraftType}",
                IsEnabled = false,
                FontWeight = Avalonia.Media.FontWeight.Bold,
            }
        );
        menu.Items.Add(new Separator());

        AddCommandTextBox(menu, cmd => vm.Connection.SendCommandAsync(callsign, cmd, initials));

        menu.Items.Add(new Separator());
        var editItem = new MenuItem { Header = "Edit flight plan" };
        editItem.Click += (_, _) => FlightPlanEditorManager.Open(ac, vm, TopLevel.GetTopLevel(this) as Window);
        menu.Items.Add(editItem);

        var deleteItem = new MenuItem { Header = "Delete" };
        deleteItem.Click += async (_, _) => await vm.Connection.SendCommandAsync(callsign, "DEL", initials);
        menu.Items.Add(deleteItem);

        // Aircraft assignment
        if (vm.AssignableMembers.Count > 0)
        {
            menu.Items.Add(new Separator());

            var selectedCallsigns = grid.SelectedItems.OfType<AircraftModel>().Select(a => a.Callsign).ToList();
            if (selectedCallsigns.Count == 0)
            {
                selectedCallsigns = [callsign];
            }

            var assignSubmenu = new MenuItem { Header = $"Assign ({selectedCallsigns.Count})" };
            foreach (var member in vm.AssignableMembers)
            {
                var memberItem = new MenuItem { Header = member.Initials };
                var connId = member.ConnectionId;
                memberItem.Click += async (_, _) => await vm.AssignAircraftAsync(selectedCallsigns, connId);
                assignSubmenu.Items.Add(memberItem);
            }
            menu.Items.Add(assignSubmenu);

            var unassignItem = new MenuItem { Header = $"Unassign ({selectedCallsigns.Count})" };
            unassignItem.Click += async (_, _) => await vm.UnassignAircraftAsync(selectedCallsigns);
            menu.Items.Add(unassignItem);
        }

        grid.ContextMenu = menu;
    }

    private static void AddCommandTextBox(ContextMenu menu, Func<string, Task> onSubmit)
    {
        var textBox = new TextBox
        {
            Watermark = "Command",
            FontSize = 12,
            MinWidth = 160,
        };
        textBox.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                var text = textBox.Text?.Trim();
                if (!string.IsNullOrEmpty(text))
                {
                    menu.Close();
                    await onSubmit(text);
                }
            }
            else if (e.Key != Key.Escape)
            {
                e.Handled = true;
            }
        };
        menu.Items.Add(textBox);
    }
}
