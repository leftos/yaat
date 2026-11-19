using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;
using Yaat.Sim.Data;

namespace Yaat.Client.UI.Tests.ViewModels;

/// <summary>
/// The extra Ground View instances <see cref="MainViewModel"/> owns (issue #434). Ordinal
/// allocation and persistence, the one app-wide selected aircraft every instance mirrors, the mirrored
/// ground layout, profile reconciliation, and the restore-on-construction path.
/// </summary>
public class MainViewModelViewInstancesTests
{
    private static AircraftDto MakeAircraft(string callsign) =>
        new()
        {
            Callsign = callsign,
            AircraftType = "B738",
            Latitude = 37.62,
            Longitude = -122.22,
            Heading = 90,
            Altitude = 0,
            GroundSpeed = 0,
            BeaconCode = 1200,
            TransponderMode = "Standby",
            IsIdenting = false,
            VerticalSpeed = 0,
            AssignedHeading = null,
            AssignedAltitude = null,
            AssignedSpeed = null,
            Departure = "OAK",
            Destination = "LAX",
            Route = "",
            FlightRules = "IFR",
            Status = "Active",
        };

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
            null,
            IsRunwaysOnly: false
        );
    }

    private static void ClearPersistedViews(UserPreferences preferences) => preferences.SetExtraGroundViews([]);

    [AvaloniaFact]
    public void OpenExtraGroundView_AllocatesLowestFreeOrdinalAndPersists()
    {
        using IDisposable navScope = SeedNavDb();
        var vm = new MainViewModel(new FakeFilePickerService());
        vm.MarkNavDbReady();
        try
        {
            vm.OpenExtraGroundView("KOAK");
            vm.OpenExtraGroundView("KOAK");

            // The docked view is #1, so the first two extra windows are #2 and #3.
            Assert.Equal([2, 3], vm.ExtraGroundViews.Select(i => i.Ordinal).ToList());
            Assert.Equal([new SavedExtraView(2, "OAK"), new SavedExtraView(3, "OAK")], vm.Preferences.ExtraGroundViews);

            vm.CloseExtraGroundView(vm.ExtraGroundViews.Single(i => i.Ordinal == 2));
            Assert.Equal([new SavedExtraView(3, "OAK")], vm.Preferences.ExtraGroundViews);

            // The gap the closed window left is reused before a new number is taken.
            vm.OpenExtraGroundView("KOAK");
            Assert.Equal([2, 3], vm.ExtraGroundViews.Select(i => i.Ordinal).Order().ToList());
            Assert.Equal([new SavedExtraView(2, "OAK"), new SavedExtraView(3, "OAK")], vm.Preferences.ExtraGroundViews);
        }
        finally
        {
            ClearPersistedViews(vm.Preferences);
        }
    }

    [AvaloniaFact]
    public void SelectedAircraft_FansOutToExtras_AndChildSelectionWritesBack()
    {
        using IDisposable navScope = SeedNavDb();
        var vm = new MainViewModel(new FakeFilePickerService());
        vm.MarkNavDbReady();
        try
        {
            vm.OnAircraftUpdated(MakeAircraft("AAL1"));
            vm.OnAircraftUpdated(MakeAircraft("UAL2"));
            Dispatcher.UIThread.RunJobs();
            AircraftModel first = vm.Aircraft.Single(a => a.Callsign == "AAL1");
            AircraftModel second = vm.Aircraft.Single(a => a.Callsign == "UAL2");

            vm.OpenExtraGroundView("KOAK");
            GroundViewInstance extra = vm.ExtraGroundViews.Single();

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
            ClearPersistedViews(vm.Preferences);
        }
    }

    [AvaloniaFact]
    public void ExtraGroundView_MirrorsPrimaryLayoutOnOpen()
    {
        using IDisposable navScope = SeedNavDb();
        var vm = new MainViewModel(new FakeFilePickerService());
        vm.MarkNavDbReady();
        try
        {
            vm.Ground.SetLayoutForTesting(Layout("TST"));

            vm.OpenExtraGroundView("TST");
            GroundViewInstance extra = vm.ExtraGroundViews.Single();

            // The window opened after the layout loaded still shows it — mirrored, not re-fetched.
            Assert.Same(vm.Ground.Layout, extra.Vm.Layout);
            Assert.Same(vm.Ground.DomainLayout, extra.Vm.DomainLayout);
        }
        finally
        {
            ClearPersistedViews(vm.Preferences);
        }
    }

    [AvaloniaFact]
    public void ReconcileExtraViews_KeepsSurvivorsClosesMissingOpensNew()
    {
        using IDisposable navScope = SeedNavDb();
        var vm = new MainViewModel(new FakeFilePickerService());
        vm.MarkNavDbReady();
        try
        {
            vm.OpenExtraGroundView("KOAK");
            vm.OpenExtraGroundView("KOAK");
            GroundViewInstance survivor = vm.ExtraGroundViews.Single(i => i.Ordinal == 3);

            vm.ReconcileExtraViews([new SavedExtraView(3, "OAK"), new SavedExtraView(4, "OAK")]);

            Assert.Equal([3, 4], vm.ExtraGroundViews.Select(i => i.Ordinal).ToList());
            // A survivor keeps its view-model, so applying a profile doesn't reset that window's view state.
            Assert.Same(survivor, vm.ExtraGroundViews.Single(i => i.Ordinal == 3));
            Assert.Same(survivor.Vm, vm.ExtraGroundViews.Single(i => i.Ordinal == 3).Vm);
            Assert.Equal([new SavedExtraView(3, "OAK"), new SavedExtraView(4, "OAK")], vm.Preferences.ExtraGroundViews);
        }
        finally
        {
            ClearPersistedViews(vm.Preferences);
        }
    }

    [AvaloniaFact]
    public void ExtraViews_RestoreFromPreferencesOnConstruction()
    {
        using IDisposable navScope = SeedNavDb();
        var preferences = new UserPreferences();
        MainViewModel? vm = null;
        try
        {
            preferences.SetExtraGroundViews([new SavedExtraView(2, "KOAK"), new SavedExtraView(5, "KSFO")]);

            vm = new MainViewModel(new FakeFilePickerService());

            Assert.Equal([2, 5], vm.ExtraGroundViews.Select(i => i.Ordinal).ToList());
            Assert.Equal("#5", vm.ExtraGroundViews[1].Vm.SettingsKeySuffix);
            Assert.False(vm.ExtraGroundViews[1].Vm.IsPrimary);
            // The airport is restored with the ordinal — a window reopens on the airport it was on.
            Assert.Equal("KSFO", vm.ExtraGroundViews[1].AirportId);
        }
        finally
        {
            if (vm is not null)
            {
                ClearPersistedViews(vm.Preferences);
            }

            ClearPersistedViews(preferences);
        }
    }

    // The picker lists the ARTCC config's airports, which are FAA ids ("OAK"), while scenarios and
    // layouts carry ICAO ("KOAK"). Both forms must name the same airport, so the nav db is seeded with
    // the canonical mapping every test that exercises normalization needs.
    private static IDisposable SeedNavDb() =>
        NavigationDatabase.ScopedOverride(
            NavigationDatabase.ForTesting(
                fixes: new Dictionary<string, (double Lat, double Lon)>(StringComparer.OrdinalIgnoreCase)
                {
                    ["KOAK"] = (37.72, -122.22),
                    ["KSFO"] = (37.62, -122.38),
                },
                airports: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["KOAK"] = "KOAK",
                    ["OAK"] = "KOAK",
                    ["KSFO"] = "KSFO",
                    ["SFO"] = "KSFO",
                }
            )
        );

    [AvaloniaFact]
    public void OpenExtraView_TakesFaaOrIcaoFormIdentically()
    {
        using IDisposable navScope = SeedNavDb();
        var vm = new MainViewModel(new FakeFilePickerService());
        try
        {
            vm.MarkNavDbReady();

            GroundViewInstance faaForm = vm.OpenExtraGroundView("OAK");
            GroundViewInstance icaoForm = vm.OpenExtraGroundView("KOAK");

            // "OAK" (as the ARTCC config lists it) and "KOAK" are the same airport and the same window.
            Assert.Equal("OAK", faaForm.AirportId);
            Assert.Equal("OAK", icaoForm.AirportId);
            Assert.Equal("Ground View #2 — OAK", faaForm.Title);
            Assert.Equal("Ground View #3 — OAK", icaoForm.Title);
        }
        finally
        {
            ClearPersistedViews(vm.Preferences);
        }
    }

    [AvaloniaFact]
    public void OpenExtraGroundView_SameAirportMirrors()
    {
        using IDisposable navScope = SeedNavDb();
        var vm = new MainViewModel(new FakeFilePickerService());
        try
        {
            vm.MarkNavDbReady();
            vm.Ground.SetLayoutForTesting(Layout("KOAK"));

            // The FAA form the picker lists names the primary's ICAO-filed airport, so it mirrors.
            GroundViewInstance instance = vm.OpenExtraGroundView("oak");

            // Same airport as the primary: mirror it rather than fetch and reconstruct a second copy.
            Assert.True(instance.Vm.IsMirroring);
            Assert.Same(vm.Ground.Layout, instance.Vm.Layout);
        }
        finally
        {
            ClearPersistedViews(vm.Preferences);
        }
    }

    [AvaloniaFact]
    public void OpenExtraGroundView_DifferentAirportDoesNotMirror()
    {
        using IDisposable navScope = SeedNavDb();
        var vm = new MainViewModel(new FakeFilePickerService());
        try
        {
            vm.MarkNavDbReady();
            vm.Ground.SetLayoutForTesting(Layout("KOAK"));

            GroundViewInstance instance = vm.OpenExtraGroundView("KSFO");

            // A different airport loads its own layout, so the window must not follow the primary's.
            Assert.False(instance.Vm.IsMirroring);
            Assert.NotSame(vm.Ground.Layout, instance.Vm.Layout);
        }
        finally
        {
            ClearPersistedViews(vm.Preferences);
        }
    }

    [AvaloniaFact]
    public void ScenarioBootstrap_ReseedsExtraGroundFromTheNewPrimaryAirport()
    {
        using IDisposable navScope = SeedNavDb();
        var vm = new MainViewModel(new FakeFilePickerService());
        try
        {
            vm.MarkNavDbReady();
            vm.Ground.SetLayoutForTesting(Layout("KOAK"));
            GroundViewInstance onOak = vm.OpenExtraGroundView("OAK");
            GroundViewInstance onSfo = vm.OpenExtraGroundView("SFO");
            Assert.True(onOak.Vm.IsMirroring);
            Assert.False(onSfo.Vm.IsMirroring);

            // The docked view's layout is still the old scenario's while the bootstrap runs (the new one
            // arrives from the server later), so the mirror decision must follow the bootstrap's airport.
            vm.ApplyScenarioBootstrap(
                new ScenarioBootstrap
                {
                    ScenarioId = "scenario-sfo",
                    ScenarioName = "SFO scenario",
                    PrimaryAirportId = "KSFO",
                    PositionDisplayConfig = null,
                    FlightStripsConfig = null,
                    Aircraft = [],
                    ElapsedSeconds = 0,
                    SessionStartUtc = null,
                }
            );

            Assert.False(onOak.Vm.IsMirroring);
            Assert.True(onSfo.Vm.IsMirroring);
        }
        finally
        {
            ClearPersistedViews(vm.Preferences);
        }
    }

    [AvaloniaFact]
    public void ClearScenarioState_DropsEveryExtraGroundViewsScenarioId()
    {
        using IDisposable navScope = SeedNavDb();
        var vm = new MainViewModel(new FakeFilePickerService());
        try
        {
            vm.MarkNavDbReady();
            vm.ApplyScenarioBootstrap(
                new ScenarioBootstrap
                {
                    ScenarioId = "scenario-oak",
                    ScenarioName = "OAK scenario",
                    PrimaryAirportId = "KOAK",
                    PositionDisplayConfig = null,
                    FlightStripsConfig = null,
                    Aircraft = [],
                    ElapsedSeconds = 0,
                    SessionStartUtc = null,
                }
            );
            GroundViewInstance mirroring = vm.OpenExtraGroundView("OAK");
            GroundViewInstance own = vm.OpenExtraGroundView("SFO");
            Assert.True(mirroring.Vm.IsMirroring);
            Assert.Equal("scenario-oak", mirroring.Vm.ActiveScenarioId);
            Assert.Equal("scenario-oak", own.Vm.ActiveScenarioId);

            vm.ClearScenarioState();

            // A pan in either window after the unload must not write a saved view under the old scenario.
            Assert.Null(vm.Ground.ActiveScenarioId);
            Assert.Null(mirroring.Vm.ActiveScenarioId);
            Assert.Null(own.Vm.ActiveScenarioId);
        }
        finally
        {
            ClearPersistedViews(vm.Preferences);
        }
    }

    [AvaloniaFact]
    public void ReconcileExtraViews_ReopensInstanceWhoseAirportChanged()
    {
        using IDisposable navScope = SeedNavDb();
        var vm = new MainViewModel(new FakeFilePickerService());
        vm.MarkNavDbReady();
        try
        {
            GroundViewInstance original = vm.OpenExtraGroundView("KOAK");

            vm.ReconcileExtraViews([new SavedExtraView(2, "KSFO")]);

            // Same ordinal, different airport: the window is rebuilt on the new airport rather than kept,
            // because the airport decides what the instance shows.
            GroundViewInstance reopened = Assert.Single(vm.ExtraGroundViews);
            Assert.NotSame(original, reopened);
            Assert.Equal(2, reopened.Ordinal);
            Assert.Equal("SFO", reopened.AirportId);
        }
        finally
        {
            ClearPersistedViews(vm.Preferences);
        }
    }
}
