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

    private static void ClearPersistedViews(UserPreferences preferences)
    {
        preferences.SetExtraRadarViews([]);
        preferences.SetExtraGroundViews([]);
    }

    [AvaloniaFact]
    public void OpenExtraRadarView_AllocatesLowestFreeOrdinalAndPersists()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        try
        {
            vm.OpenExtraRadarView("KOAK");
            vm.OpenExtraRadarView("KOAK");

            // The docked view is #1, so the first two extra windows are #2 and #3.
            Assert.Equal([2, 3], vm.ExtraRadarViews.Select(i => i.Ordinal).ToList());
            Assert.Equal([new SavedExtraView(2, "KOAK"), new SavedExtraView(3, "KOAK")], vm.Preferences.ExtraRadarViews);
            Assert.Equal("#3", vm.ExtraRadarViews[1].Vm.SettingsKeySuffix);
            Assert.False(vm.ExtraRadarViews[1].Vm.IsPrimary);

            vm.CloseExtraRadarView(vm.ExtraRadarViews.Single(i => i.Ordinal == 2));
            Assert.Equal([new SavedExtraView(3, "KOAK")], vm.Preferences.ExtraRadarViews);

            // The gap the closed window left is reused before a new number is taken.
            vm.OpenExtraRadarView("KOAK");
            Assert.Contains(vm.ExtraRadarViews, i => i.Ordinal == 2);
            Assert.Equal([new SavedExtraView(2, "KOAK"), new SavedExtraView(3, "KOAK")], vm.Preferences.ExtraRadarViews);
        }
        finally
        {
            ClearPersistedViews(vm.Preferences);
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
            AircraftModel first = vm.Aircraft.Single(a => a.Callsign == "AAL1");
            AircraftModel second = vm.Aircraft.Single(a => a.Callsign == "UAL2");

            vm.OpenExtraRadarView("KOAK");
            RadarViewInstance extra = vm.ExtraRadarViews.Single();

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
        var vm = new MainViewModel(new FakeFilePickerService());
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
        var vm = new MainViewModel(new FakeFilePickerService());
        try
        {
            vm.OpenExtraRadarView("KOAK");
            vm.OpenExtraRadarView("KOAK");
            RadarViewInstance survivor = vm.ExtraRadarViews.Single(i => i.Ordinal == 3);

            vm.ReconcileExtraViews([new SavedExtraView(3, "KOAK"), new SavedExtraView(4, "KOAK")], []);

            Assert.Equal([3, 4], vm.ExtraRadarViews.Select(i => i.Ordinal).ToList());
            // A survivor keeps its view-model, so applying a profile doesn't reset that window's view state.
            Assert.Same(survivor, vm.ExtraRadarViews.Single(i => i.Ordinal == 3));
            Assert.Same(survivor.Vm, vm.ExtraRadarViews.Single(i => i.Ordinal == 3).Vm);
            Assert.Equal([new SavedExtraView(3, "KOAK"), new SavedExtraView(4, "KOAK")], vm.Preferences.ExtraRadarViews);
        }
        finally
        {
            ClearPersistedViews(vm.Preferences);
        }
    }

    [AvaloniaFact]
    public void ExtraViews_RestoreFromPreferencesOnConstruction()
    {
        var preferences = new UserPreferences();
        MainViewModel? vm = null;
        try
        {
            preferences.SetExtraRadarViews([new SavedExtraView(2, "KOAK")]);
            preferences.SetExtraGroundViews([new SavedExtraView(2, "KOAK"), new SavedExtraView(5, "KSFO")]);

            vm = new MainViewModel(new FakeFilePickerService());

            Assert.Equal([2], vm.ExtraRadarViews.Select(i => i.Ordinal).ToList());
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
    private static void SeedNavDb()
    {
        NavigationDatabase.SetInstance(
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
    }

    [AvaloniaFact]
    public void OpenExtraRadarView_SeedsChosenAirportIdAndCentre()
    {
        SeedNavDb();
        var vm = new MainViewModel(new FakeFilePickerService());
        try
        {
            vm.MarkNavDbReady();

            RadarViewInstance instance = vm.OpenExtraRadarView("koak");

            // The picked airport is the instance's, canonicalized to the FAA form the ARTCC config and
            // the video-map facilities use — not the scenario's airport (there is no scenario here).
            Assert.Equal("OAK", instance.AirportId);
            Assert.Equal("OAK", instance.Vm.PrimaryAirportId);
            // The centre itself is not observable: SetPrimaryAirportPosition stores a private field that
            // only the video-map load (a server round-trip) copies into CenterLat/CenterLon.
        }
        finally
        {
            ClearPersistedViews(vm.Preferences);
        }
    }

    [AvaloniaFact]
    public void OpenExtraView_TakesFaaOrIcaoFormIdentically()
    {
        SeedNavDb();
        var vm = new MainViewModel(new FakeFilePickerService());
        try
        {
            vm.MarkNavDbReady();

            RadarViewInstance faaForm = vm.OpenExtraRadarView("OAK");
            RadarViewInstance icaoForm = vm.OpenExtraRadarView("KOAK");

            // "OAK" (as the ARTCC config lists it) and "KOAK" are the same airport and the same window.
            Assert.Equal("OAK", faaForm.AirportId);
            Assert.Equal("OAK", icaoForm.AirportId);
            Assert.Equal("Radar View #2 — OAK", faaForm.Title);
            Assert.Equal("Radar View #3 — OAK", icaoForm.Title);
        }
        finally
        {
            ClearPersistedViews(vm.Preferences);
        }
    }

    [AvaloniaFact]
    public void OpenExtraGroundView_SameAirportMirrors()
    {
        SeedNavDb();
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
        SeedNavDb();
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
        SeedNavDb();
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
            vm.ApplyScenarioBootstrap(new ScenarioBootstrap("scenario-sfo", "SFO scenario", "KSFO", null, null, [], ElapsedSeconds: 0));

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
        SeedNavDb();
        var vm = new MainViewModel(new FakeFilePickerService());
        try
        {
            vm.MarkNavDbReady();
            vm.ApplyScenarioBootstrap(new ScenarioBootstrap("scenario-oak", "OAK scenario", "KOAK", null, null, [], ElapsedSeconds: 0));
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
        var vm = new MainViewModel(new FakeFilePickerService());
        try
        {
            RadarViewInstance original = vm.OpenExtraRadarView("KOAK");

            vm.ReconcileExtraViews([new SavedExtraView(2, "KSFO")], []);

            // Same ordinal, different airport: the window is rebuilt on the new airport rather than kept,
            // because the airport decides what the instance shows.
            RadarViewInstance reopened = Assert.Single(vm.ExtraRadarViews);
            Assert.NotSame(original, reopened);
            Assert.Equal(2, reopened.Ordinal);
            Assert.Equal("KSFO", reopened.AirportId);
            Assert.Equal("KSFO", reopened.Vm.PrimaryAirportId);
        }
        finally
        {
            ClearPersistedViews(vm.Preferences);
        }
    }
}
