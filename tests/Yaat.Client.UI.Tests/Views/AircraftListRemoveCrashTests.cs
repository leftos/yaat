using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// Regression for GitHub #237: deleting an aircraft from the list crashed the client with
/// <c>ArgumentOutOfRangeException</c> inside Avalonia's
/// <c>DataGridCollectionView.AdjustCurrencyForRemove</c>. The live DataGrid's selection/currency
/// bookkeeping drifts out of sync with the filtered+sorted view; when <c>Aircraft.Remove(ac)</c>
/// then fires the remove notification, <c>AdjustCurrencyForRemove</c> dereferences a stale
/// <c>CurrentPosition</c> via <c>GetItemAt</c> and throws — unwinding to the top of the UI
/// dispatcher, which is fatal.
///
/// These tests host the real <see cref="DataGridView"/> against a headless DataGrid so the
/// grid↔view currency interaction is exercised, then delete aircraft and assert the client does
/// not crash and the list stays consistent.
/// </summary>
public class AircraftListRemoveCrashTests
{
    private static AircraftDto MakeAircraft(string callsign, string status = "Active", string destination = "LAX") =>
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
            VerticalSpeed: 0,
            AssignedHeading: null,
            AssignedAltitude: null,
            AssignedSpeed: null,
            Departure: "OAK",
            Destination: destination,
            Route: "",
            FlightRules: "IFR",
            Status: status
        );

    private static (Window window, MainViewModel vm, DataGrid grid) HostGrid(IEnumerable<AircraftDto> aircraft)
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        vm.ApplyScenarioBootstrap(new ScenarioBootstrap("scenario-237", "Remove Crash", "OAK", null, null, [.. aircraft]));

        var view = new DataGridView { DataContext = vm };
        var window = new Window
        {
            Width = 500,
            Height = 600,
            Content = view,
        };
        window.ShowAndRunLayout();

        var grid = view.GetDataGrid()!;
        Assert.NotNull(grid);
        return (window, vm, grid);
    }

    private static void SortByCallsign(MainViewModel vm)
    {
        vm.AircraftView.SortDescriptions.Add(DataGridSortDescription.FromComparer(new GroupStableSortComparer(new PropertySortComparer("Callsign"))));
        vm.AircraftView.Refresh();
        Dispatcher.UIThread.RunJobs();
    }

    private static List<string> VisibleCallsigns(MainViewModel vm) => [.. vm.AircraftView.Cast<AircraftModel>().Select(a => a.Callsign)];

    // Deterministic reproduction of the #237 crash mechanism, driven through the real
    // OnAircraftDeleted path. In the live client the DataGrid mutates/re-asserts currency while the
    // DataGridCollectionView is mid-remove; that re-entrant structural change leaves CurrentPosition
    // stale, so AdjustCurrencyForRemove's IsCurrentInSync -> GetItemAt dereferences an out-of-range
    // index and throws ArgumentOutOfRangeException — the exact reported stack. We reproduce the
    // re-entrancy deterministically by removing a bystander aircraft the first time the view raises
    // CurrentChanging during the deletion. Without the fix this test throws (crashing the app);
    // the currency reset in RemoveAircraftFromList neutralizes it.
    [AvaloniaFact]
    public void DeletingAircraft_WhenCurrencyDesyncsMidRemoval_DoesNotCrash()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        vm.ApplyScenarioBootstrap(
            new ScenarioBootstrap(
                "scenario-237-reentrant",
                "Reentrant",
                "OAK",
                null,
                null,
                [MakeAircraft("AAA1"), MakeAircraft("BBB1"), MakeAircraft("CCC1")]
            )
        );

        // Currency sits on the last row, exactly as the grid places it when that row is selected.
        vm.AircraftView.MoveCurrentToPosition(vm.AircraftView.Count - 1);

        var bystander = vm.Aircraft.First(a => a.Callsign == "BBB1");
        var reentered = false;
        vm.AircraftView.CurrentChanging += (_, _) =>
        {
            if (reentered)
            {
                return;
            }

            reentered = true;
            vm.Aircraft.Remove(bystander);
        };

        vm.OnAircraftDeleted("AAA1");
        Dispatcher.UIThread.RunJobs();

        Assert.DoesNotContain("AAA1", VisibleCallsigns(vm));
        Assert.DoesNotContain("BBB1", VisibleCallsigns(vm));
        Assert.Equal(["CCC1"], VisibleCallsigns(vm));
    }

    // The reported crash: filter on + column sort + a selected row, then an aircraft is deleted.
    [AvaloniaFact]
    public void DeletingAircraft_WithFilterSortAndSelection_DoesNotCrash()
    {
        var (window, vm, grid) = HostGrid([
            MakeAircraft("AAA1"),
            MakeAircraft("BBB1"),
            MakeAircraft("CCC1"),
            MakeAircraft("DDD1"),
            MakeAircraft("EEE1"),
            MakeAircraft("DEL1", "Delayed (60s)"),
            MakeAircraft("DEL2", "Delayed (90s)"),
        ]);

        vm.ShowOnlyActiveAircraft = true;
        SortByCallsign(vm);
        Assert.Equal(["AAA1", "BBB1", "CCC1", "DDD1", "EEE1"], VisibleCallsigns(vm));

        // Select the last visible row, then push a fresh active aircraft through the incremental
        // Add path (the known-fragile sorted-insert regime), then delete an aircraft — the exact
        // shape that leaves the grid's currency out of sync with the view.
        grid.SelectedItem = vm.Aircraft.First(a => a.Callsign == "EEE1");
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        vm.OnAircraftUpdated(MakeAircraft("CCA1"));
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        vm.OnAircraftDeleted("AAA1");
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.DoesNotContain("AAA1", VisibleCallsigns(vm));
        Assert.Contains("CCA1", VisibleCallsigns(vm));
    }

    // Deleting the currently-selected aircraft must clear selection, not crash.
    [AvaloniaFact]
    public void DeletingSelectedAircraft_ClearsSelection_WithoutCrash()
    {
        var (window, vm, grid) = HostGrid([MakeAircraft("AAA1"), MakeAircraft("BBB1"), MakeAircraft("CCC1")]);

        grid.SelectedItem = vm.Aircraft.First(a => a.Callsign == "BBB1");
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("BBB1", vm.SelectedAircraft?.Callsign);

        vm.OnAircraftDeleted("BBB1");
        Dispatcher.UIThread.RunJobs();

        Assert.DoesNotContain("BBB1", VisibleCallsigns(vm));
        Assert.NotEqual("BBB1", vm.SelectedAircraft?.Callsign);
    }

    // Deleting an aircraft other than the selected one must preserve the selection.
    [AvaloniaFact]
    public void DeletingOtherAircraft_PreservesSelection_WithoutCrash()
    {
        var (window, vm, grid) = HostGrid([MakeAircraft("AAA1"), MakeAircraft("BBB1"), MakeAircraft("CCC1")]);

        grid.SelectedItem = vm.Aircraft.First(a => a.Callsign == "BBB1");
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        vm.OnAircraftDeleted("AAA1");
        Dispatcher.UIThread.RunJobs();

        Assert.DoesNotContain("AAA1", VisibleCallsigns(vm));
        Assert.Equal("BBB1", vm.SelectedAircraft?.Callsign);
    }
}
