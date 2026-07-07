using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;

namespace Yaat.Client.UI.Tests.ViewModels;

/// <summary>
/// Regression test for the macOS UI-thread freeze (issue #280). The server broadcasts one
/// <c>AircraftUpdated</c> message per aircraft, and <see cref="MainViewModel.OnAircraftUpdated"/>
/// used to rebuild the global shown-route/path sets on every single update. A busy tick that updated
/// N aircraft therefore ran the O(n) <see cref="GroundViewModel.RefreshShownTaxiRoutes"/> N times —
/// an O(n²) allocation storm that pinned the UI thread. The refresh is now coalesced to run once
/// after a burst of updates drains.
/// </summary>
public class ShownRouteRefreshCoalesceTests
{
    private static AircraftDto MakeAircraft(string callsign) =>
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
            Destination: "LAX",
            Route: "",
            FlightRules: "IFR",
            Status: "Active"
        );

    [AvaloniaFact]
    public void UpdateBurst_CoalescesTaxiRouteRefresh_ToOne()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        Dispatcher.UIThread.RunJobs();
        int baseline = vm.Ground.RefreshShownTaxiRoutesCallCount;

        // One AircraftUpdated per aircraft, as the server broadcasts them.
        for (int i = 0; i < 20; i++)
        {
            vm.OnAircraftUpdated(MakeAircraft($"AAL{i:000}"));
        }
        Dispatcher.UIThread.RunJobs();

        // Per-update rebuilds would be +20; coalesced is exactly +1.
        Assert.Equal(baseline + 1, vm.Ground.RefreshShownTaxiRoutesCallCount);
    }
}
