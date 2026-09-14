using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;

namespace Yaat.Client.UI.Tests.ViewModels;

/// <summary>
/// The extra Radar/Ground View instances <see cref="MainViewModel"/> owns (issue #434). Ordinal
/// allocation and persistence, the one app-wide selected aircraft every instance mirrors, the mirrored
/// ground layout, profile reconciliation, and the restore-on-construction path.
/// The UI test project shares one preferences.json, so every test restores the persisted ordinals.
/// </summary>
public class MainViewModelViewInstancesTests
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
            IsIdenting: false,
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

    private static GroundLayoutDto Layout(string airportId)
    {
        const double lat = 37.62;
        const double lon = -122.38;
        return new GroundLayoutDto(
            airportId,
            [
                new GroundNodeDto(1, lat, lon, "TaxiwayIntersection", null, null, null),
                new GroundNodeDto(2, lat + 0.001, lon, "TaxiwayIntersection", null, null, null),
            ],
            [new GroundEdgeDto(1, 2, "C", DistanceNm: 0.06, IntermediatePoints: null)],
            null,
            null,
            null
        );
    }

    private static void ClearPersistedOrdinals(UserPreferences preferences)
    {
        preferences.SetExtraRadarViewOrdinals([]);
        preferences.SetExtraGroundViewOrdinals([]);
    }

    [AvaloniaFact]
    public void OpenExtraRadarView_AllocatesLowestFreeOrdinalAndPersists()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        try
        {
            vm.OpenExtraRadarViewCommand.Execute(null);
            vm.OpenExtraRadarViewCommand.Execute(null);

            // The docked view is #1, so the first two extra windows are #2 and #3.
            Assert.Equal([2, 3], vm.ExtraRadarViews.Select(i => i.Ordinal).ToList());
            Assert.Equal([2, 3], vm.Preferences.ExtraRadarViewOrdinals);
            Assert.Equal("#3", vm.ExtraRadarViews[1].Vm.SettingsKeySuffix);
            Assert.False(vm.ExtraRadarViews[1].Vm.IsPrimary);

            vm.CloseExtraRadarView(vm.ExtraRadarViews.Single(i => i.Ordinal == 2));
            Assert.Equal([3], vm.Preferences.ExtraRadarViewOrdinals);

            // The gap the closed window left is reused before a new number is taken.
            vm.OpenExtraRadarViewCommand.Execute(null);
            Assert.Contains(vm.ExtraRadarViews, i => i.Ordinal == 2);
            Assert.Equal([2, 3], vm.Preferences.ExtraRadarViewOrdinals);
        }
        finally
        {
            ClearPersistedOrdinals(vm.Preferences);
        }
    }

    [AvaloniaFact]
    public void SelectedAircraft_FansOutToExtras_AndChildSelectionWritesBack()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        try
        {
            vm.OnAircraftUpdated(MakeAircraft("AAL1"));
            vm.OnAircraftUpdated(MakeAircraft("UAL2"));
            Dispatcher.UIThread.RunJobs();
            var first = vm.Aircraft.Single(a => a.Callsign == "AAL1");
            var second = vm.Aircraft.Single(a => a.Callsign == "UAL2");

            vm.OpenExtraRadarViewCommand.Execute(null);
            var extra = vm.ExtraRadarViews.Single();

            // One app-wide selection: MainViewModel is the source of truth and every instance mirrors it.
            vm.SelectedAircraft = first;
            Assert.Same(first, extra.Vm.SelectedAircraft);
            Assert.Same(first, vm.Radar.SelectedAircraft);

            // Selecting in the extra window writes back through the same child-selection callback.
            extra.Vm.SelectedAircraft = second;
            Assert.Same(second, vm.SelectedAircraft);
            Assert.Same(second, vm.Radar.SelectedAircraft);
        }
        finally
        {
            ClearPersistedOrdinals(vm.Preferences);
        }
    }

    [AvaloniaFact]
    public void ExtraGroundView_MirrorsPrimaryLayoutOnOpen()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        try
        {
            vm.Ground.SetLayoutForTesting(Layout("TST"));

            vm.OpenExtraGroundViewCommand.Execute(null);
            var extra = vm.ExtraGroundViews.Single();

            // The window opened after the layout loaded still shows it — mirrored, not re-fetched.
            Assert.Same(vm.Ground.Layout, extra.Vm.Layout);
            Assert.Same(vm.Ground.DomainLayout, extra.Vm.DomainLayout);
        }
        finally
        {
            ClearPersistedOrdinals(vm.Preferences);
        }
    }

    [AvaloniaFact]
    public void ReconcileExtraViews_KeepsSurvivorsClosesMissingOpensNew()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        try
        {
            vm.OpenExtraRadarViewCommand.Execute(null);
            vm.OpenExtraRadarViewCommand.Execute(null);
            var survivor = vm.ExtraRadarViews.Single(i => i.Ordinal == 3);

            vm.ReconcileExtraViews([3, 4], []);

            Assert.Equal([3, 4], vm.ExtraRadarViews.Select(i => i.Ordinal).ToList());
            // A survivor keeps its view-model, so applying a profile doesn't reset that window's view state.
            Assert.Same(survivor, vm.ExtraRadarViews.Single(i => i.Ordinal == 3));
            Assert.Same(survivor.Vm, vm.ExtraRadarViews.Single(i => i.Ordinal == 3).Vm);
            Assert.Equal([3, 4], vm.Preferences.ExtraRadarViewOrdinals);
        }
        finally
        {
            ClearPersistedOrdinals(vm.Preferences);
        }
    }

    [AvaloniaFact]
    public void ExtraViews_RestoreFromPreferencesOnConstruction()
    {
        var preferences = new UserPreferences();
        MainViewModel? vm = null;
        try
        {
            preferences.SetExtraRadarViewOrdinals([2]);
            preferences.SetExtraGroundViewOrdinals([2, 5]);

            vm = new MainViewModel(new FakeFilePickerService());

            Assert.Equal([2], vm.ExtraRadarViews.Select(i => i.Ordinal).ToList());
            Assert.Equal([2, 5], vm.ExtraGroundViews.Select(i => i.Ordinal).ToList());
            Assert.Equal("#5", vm.ExtraGroundViews[1].Vm.SettingsKeySuffix);
            Assert.False(vm.ExtraGroundViews[1].Vm.IsPrimary);
        }
        finally
        {
            if (vm is not null)
            {
                ClearPersistedOrdinals(vm.Preferences);
            }

            ClearPersistedOrdinals(preferences);
        }
    }
}
