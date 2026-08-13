using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// GitHub #351: selecting an aircraft (e.g. by clicking it on the radar) must auto-scroll the
/// aircraft list so the selected row is visible, instead of leaving the RPO to hunt for it in a
/// long list. These tests host the real <see cref="DataGridView"/> headlessly with enough rows to
/// overflow the viewport and assert the selected row is realized after a VM-driven selection.
/// </summary>
public class AircraftListAutoScrollTests
{
    private static AircraftDto MakeAircraft(string callsign, string status = "Active") =>
        new(
            Callsign: callsign,
            AircraftType: "B738",
            Latitude: 37.62,
            Longitude: -122.22,
            Heading: 90,
            Altitude: 0,
            GroundSpeed: 0,
            BeaconCode: 1200,
            TransponderMode: "Standby",
            IsIdenting: false,
            VerticalSpeed: 0,
            AssignedHeading: null,
            AssignedAltitude: null,
            AssignedSpeed: null,
            Departure: "OAK",
            Destination: "LAX",
            Route: "",
            FlightRules: "IFR",
            Status: status
        );

    private static (Window window, MainViewModel vm, DataGrid grid) HostGrid(IEnumerable<AircraftDto> aircraft)
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        vm.ApplyScenarioBootstrap(new ScenarioBootstrap("scenario-351", "Auto Scroll", "OAK", null, null, [.. aircraft]));

        var view = new DataGridView { DataContext = vm };
        var window = new Window
        {
            Width = 500,
            Height = 400,
            Content = view,
        };
        window.ShowAndRunLayout();

        var grid = view.GetDataGrid()!;
        Assert.NotNull(grid);
        return (window, vm, grid);
    }

    private static void PumpLayout(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static bool RowIsRealized(DataGrid grid, AircraftModel aircraft) =>
        grid.GetVisualDescendants().OfType<DataGridRow>().Any(r => ReferenceEquals(r.DataContext, aircraft));

    // Selecting an aircraft far below the viewport from outside the grid (radar click, command
    // input, context menu — anything that sets MainViewModel.SelectedAircraft) must scroll the
    // list so the row is visible. Row virtualization means an off-screen row is never realized,
    // so "realized" is the observable proxy for "scrolled into view".
    [AvaloniaFact]
    public void SelectingAircraftBelowViewport_ScrollsRowIntoView()
    {
        var (window, vm, grid) = HostGrid(Enumerable.Range(1, 60).Select(i => MakeAircraft($"UAL{i:D3}")));
        PumpLayout(window);

        var last = vm.Aircraft.First(a => a.Callsign == "UAL060");
        Assert.False(RowIsRealized(grid, last));

        vm.SelectedAircraft = last;
        PumpLayout(window);

        Assert.Equal(last, grid.SelectedItem);
        Assert.True(RowIsRealized(grid, last));
    }

    // A selection pointing at an aircraft the current filter hides (e.g. "show only active" is on
    // and a delayed aircraft is selected from the radar) has no row to scroll to — it must no-op
    // without throwing and without moving the list.
    [AvaloniaFact]
    public void SelectingFilteredOutAircraft_IsSafeNoOp()
    {
        var aircraft = Enumerable.Range(1, 60).Select(i => MakeAircraft($"UAL{i:D3}")).ToList();
        aircraft.Add(MakeAircraft("DEL999", "Delayed (60s)"));
        var (window, vm, grid) = HostGrid(aircraft);

        vm.ShowOnlyActiveAircraft = true;
        vm.AircraftView.Refresh();
        PumpLayout(window);

        var hidden = vm.Aircraft.First(a => a.Callsign == "DEL999");
        Assert.DoesNotContain(hidden, vm.AircraftView.Cast<AircraftModel>());

        var firstVisible = vm.AircraftView.Cast<AircraftModel>().First();
        vm.SelectedAircraft = hidden;
        PumpLayout(window);

        Assert.True(RowIsRealized(grid, firstVisible));
        Assert.False(RowIsRealized(grid, hidden));
    }
}
