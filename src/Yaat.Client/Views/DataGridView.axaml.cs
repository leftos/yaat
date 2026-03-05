using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
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
    }

    private void OnGridDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not DataGrid grid || grid.SelectedItem is not AircraftModel ac || DataContext is not MainViewModel vm)
        {
            return;
        }

        OpenFlightPlanEditor(ac, vm, TopLevel.GetTopLevel(this) as Window);
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
