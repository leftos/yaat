using Avalonia.Collections;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;

namespace Yaat.Client.UI.Tests.ViewModels;

/// <summary>
/// Regression tests for the Aircraft List sort failing to refresh when a new aircraft becomes
/// active while the "only active" filter is on and a column sort (by Callsign) is applied.
///
/// Root cause: a brand-new aircraft arrives through <see cref="MainViewModel.OnAircraftUpdated"/>'s
/// "unknown callsign" branch, which does <c>Aircraft.Add(model)</c>. With the filter on, the
/// <see cref="DataGridCollectionView"/>'s internal (filtered) list is smaller than the source
/// collection, so its incremental sorted-insert is handed an out-of-range source index, the
/// comparer is fed <c>null</c>, and the item is appended at the bottom instead of sort-inserted.
/// The fix forces a full <c>RefreshAircraftView()</c> after the add, which re-filters and re-sorts
/// correctly.
/// </summary>
public class AircraftListSortRefreshTests
{
    private static MainViewModel NewVm() => new(new FakeFilePickerService());

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

    private static void SortBy(MainViewModel vm, string propertyName)
    {
        // Mirror what the DataGrid header does on a sort: every column gets a CustomSortComparer of
        // GroupStableSortComparer wrapping the column's property comparer (SetupDataGrid), which the
        // grid pushes into the view's SortDescriptions.
        vm.AircraftView.SortDescriptions.Add(
            DataGridSortDescription.FromComparer(new GroupStableSortComparer(new PropertySortComparer(propertyName)))
        );
        vm.AircraftView.Refresh();
    }

    private static List<string> VisibleCallsigns(MainViewModel vm) => [.. vm.AircraftView.Cast<AircraftModel>().Select(a => a.Callsign)];

    [AvaloniaFact]
    public void NewActiveAircraft_SortsIntoPosition_WhenOnlyActiveFilterOn()
    {
        var vm = NewVm();

        // Active aircraft plus delayed placeholders so the "only active" filter actually shrinks
        // the view relative to the source collection — that size gap is what triggers the bug.
        vm.ApplyScenarioBootstrap(
            new ScenarioBootstrap(
                "scenario-1",
                "Sort Test",
                "OAK",
                null,
                null,
                [
                    MakeAircraft("BBB1"),
                    MakeAircraft("DDD1"),
                    MakeAircraft("FFF1"),
                    MakeAircraft("DEL1", "Delayed (60s)"),
                    MakeAircraft("DEL2", "Delayed (90s)"),
                ]
            )
        );

        vm.ShowOnlyActiveAircraft = true;
        SortBy(vm, "Callsign");

        Assert.Equal(["BBB1", "DDD1", "FFF1"], VisibleCallsigns(vm));

        // A brand-new active aircraft arrives — its callsign sorts between BBB1 and DDD1.
        vm.OnAircraftUpdated(MakeAircraft("CCC1"));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(["BBB1", "CCC1", "DDD1", "FFF1"], VisibleCallsigns(vm));
    }

    [AvaloniaFact]
    public void NewActiveAircraft_SortsIntoPosition_ForNonCallsignColumn_WhenOnlyActiveFilterOn()
    {
        // The bug and the fix are column-agnostic: every column wraps GroupStableSortComparer.
        // Sort by Destination instead of Callsign and confirm a fresh row still slots in by that key.
        var vm = NewVm();

        vm.ApplyScenarioBootstrap(
            new ScenarioBootstrap(
                "scenario-3",
                "Sort Test",
                "OAK",
                null,
                null,
                [
                    MakeAircraft("Q1", destination: "AAA"),
                    MakeAircraft("Q2", destination: "CCC"),
                    MakeAircraft("Q3", destination: "EEE"),
                    MakeAircraft("DEL1", "Delayed (60s)", "ZZZ"),
                    MakeAircraft("DEL2", "Delayed (90s)", "ZZZ"),
                ]
            )
        );

        vm.ShowOnlyActiveAircraft = true;
        SortBy(vm, "Destination");

        Assert.Equal(["Q1", "Q2", "Q3"], VisibleCallsigns(vm));

        // New active aircraft destined BBB sorts between AAA (Q1) and CCC (Q2).
        vm.OnAircraftUpdated(MakeAircraft("Q4", destination: "BBB"));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(["Q1", "Q4", "Q2", "Q3"], VisibleCallsigns(vm));
    }

    [AvaloniaFact]
    public void DelayedAircraftBecomingActive_SortsIntoPosition_WhenOnlyActiveFilterOn()
    {
        // Guard for the pre-existing path: a delayed placeholder (already in the collection, hidden
        // by the filter) flipping to active goes through the known-callsign branch, which already
        // calls RefreshAircraftView(). This must stay correctly sorted.
        var vm = NewVm();

        vm.ApplyScenarioBootstrap(
            new ScenarioBootstrap(
                "scenario-2",
                "Sort Test",
                "OAK",
                null,
                null,
                [MakeAircraft("BBB1"), MakeAircraft("DDD1"), MakeAircraft("FFF1"), MakeAircraft("CCC1", "Delayed (60s)")]
            )
        );

        vm.ShowOnlyActiveAircraft = true;
        SortBy(vm, "Callsign");

        Assert.Equal(["BBB1", "DDD1", "FFF1"], VisibleCallsigns(vm));

        vm.OnAircraftUpdated(MakeAircraft("CCC1"));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(["BBB1", "CCC1", "DDD1", "FFF1"], VisibleCallsigns(vm));
    }
}
