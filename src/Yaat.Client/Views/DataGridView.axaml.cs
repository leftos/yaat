using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views;

public partial class DataGridView : UserControl
{
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

        grid.DoubleTapped += OnGridDoubleTapped;
        grid.ContextRequested += OnGridContextRequested;
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        base.OnUnloaded(e);

        var grid = GetDataGrid();
        if (grid is not null)
        {
            grid.ContextRequested -= OnGridContextRequested;
        }
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

        OpenFlightPlanEditor(ac, vm, TopLevel.GetTopLevel(this) as Window);
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
        editItem.Click += (_, _) => OpenFlightPlanEditor(ac, vm, TopLevel.GetTopLevel(this) as Window);
        menu.Items.Add(editItem);

        var deleteItem = new MenuItem { Header = "Delete" };
        deleteItem.Click += async (_, _) => await vm.Connection.SendCommandAsync(callsign, "DEL", initials);
        menu.Items.Add(deleteItem);

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

    public static void OpenFlightPlanEditor(AircraftModel ac, MainViewModel vm, Window? owner)
    {
        var window = new FlightPlanEditorWindow(
            ac,
            async (callsign, amendment) =>
            {
                var dto = new FlightPlanAmendmentDto(
                    AircraftType: amendment.AircraftType,
                    EquipmentSuffix: amendment.EquipmentSuffix,
                    Departure: amendment.Departure,
                    Destination: amendment.Destination,
                    CruiseSpeed: amendment.CruiseSpeed,
                    CruiseAltitude: amendment.CruiseAltitude,
                    FlightRules: amendment.FlightRules,
                    Route: amendment.Route,
                    Remarks: amendment.Remarks,
                    Scratchpad1: amendment.Scratchpad1,
                    Scratchpad2: amendment.Scratchpad2,
                    BeaconCode: amendment.BeaconCode
                );

                await vm.Connection.AmendFlightPlanAsync(callsign, dto);
            }
        );

        new WindowGeometryHelper(window, vm.Preferences, "FlightPlanEditor", 640, 250).Restore();

        if (owner is not null)
        {
            window.Show(owner);
        }
        else
        {
            window.Show();
        }
    }
}
