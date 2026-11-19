using System.Collections.Specialized;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Prefs;
using Yaat.Sim;
using Yaat.Sim.Data;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The scope's view model: it takes its maps, centre, range and MVA-tint default from the Scope's own map source
/// (<see cref="ScopeMapSource"/>) at every facility load; it shares the app-wide selection both
/// ways while open and lets go of it on close.
/// </summary>
public class ScopeViewModelTests
{
    // The first STARS area's centre: where a load centres while the primary airport's position is not known.
    private const double AreaLat = 37.62;
    private const double AreaLon = -122.38;

    // KOAK, the late primary-airport position a source recentres on.
    private const double OakLat = 37.7213;
    private const double OakLon = -122.2208;

    private static AircraftModel Aircraft(string callsign) =>
        new()
        {
            Callsign = callsign,
            AircraftType = "B738",
            FlightRules = "IFR",
            Position = new LatLon(37.62, -122.38),
        };

    private static VideoMapData Map(string id) => new() { MapId = id, Lines = [] };

    private static VideoMapInfoDto MapInfo(int starsId, bool tdmOnly = false) =>
        new($"m{starsId}", $"Map {starsId}", $"MAP{starsId}", [], "A", starsId, AlwaysVisible: false, TdmOnly: tdmOnly);

    /// <summary>A facility with one STARS area at <paramref name="lat"/>/<paramref name="lon"/> and <paramref name="maps"/> in that order.</summary>
    private static FacilityVideoMapsDto Facility(double lat, double lon, params VideoMapInfoDto[] maps) =>
        new("ZOA", "NCT", [new StarsAreaDto("A", "Area A", lat, lon, 60, [])], [.. maps], []);

    /// <summary>Loads maps 31, 2 and 10 (in that list order) into the source, centred on the area.</summary>
    private static void LoadSource(MainViewModel main) =>
        main.ScopeMaps.ApplyVideoMapsDtoForTesting(Facility(AreaLat, AreaLon, MapInfo(31), MapInfo(2), MapInfo(10)), null, null);

    [AvaloniaFact]
    public void Scope_OpenedBeforeTheSourceLoads_SeedsOnItsFirstLoad()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        using var scope = new ScopeViewModel(main, prefStore: null, profileStore: null, ScopePrefSetStore.PrimarySlot);
        Assert.True(scope.IsWaitingForMapSource);
        Assert.Empty(scope.MapToggles);

        LoadSource(main);

        Assert.False(scope.IsWaitingForMapSource);
        Assert.Equal((AreaLat, AreaLon), (scope.CenterLat, scope.CenterLon));
        Assert.Equal(40, scope.RangeNm);
        Assert.Equal(3, scope.MapToggles.Count);
        Assert.Equal([2, 10, 31], scope.MapToggles.Select(t => t.StarsId));
        Assert.All(scope.MapToggles, t => Assert.False(t.IsOn));
        Assert.Empty(scope.VideoMaps);
        Assert.Equal("A", scope.BrightnessLookup["m2"]);
    }

    [AvaloniaFact]
    public void Scope_TakesTheSourcesMvaTintDefault_AtEachSeed()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        main.ScopeMaps.ShowMvaHintsDefault = true;
        LoadSource(main);
        using var scope = new ScopeViewModel(main, prefStore: null, profileStore: null, ScopePrefSetStore.PrimarySlot);
        Assert.True(scope.ShowMvaHints);

        scope.ShowMvaHints = false;
        LoadSource(main);
        Assert.True(scope.ShowMvaHints);

        // The default reaches the scope at the next seed, not before.
        main.ScopeMaps.ShowMvaHintsDefault = false;
        Assert.True(scope.ShowMvaHints);
        LoadSource(main);
        Assert.False(scope.ShowMvaHints);
    }

    [AvaloniaFact]
    public void Scope_SeedsItsMapToggles_FromTheSourceMaps_InStarsIdOrder()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        LoadSource(main);

        using var scope = new ScopeViewModel(main, prefStore: null, profileStore: null, ScopePrefSetStore.PrimarySlot);

        Assert.False(scope.IsWaitingForMapSource);
        Assert.Equal([2, 10, 31], scope.MapToggles.Select(t => t.StarsId));
        Assert.All(scope.MapToggles, t => Assert.False(t.IsOn));
        ScopeMapToggle ten = scope.MapToggles[1];
        Assert.Equal(("m10", "Map 10", "A"), (ten.MapId, ten.Name, ten.BrightnessCategory));
    }

    [AvaloniaFact]
    public void Scope_SeedsTdmOnly_FromTheVideoMapDto()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        main.ScopeMaps.ApplyVideoMapsDtoForTesting(Facility(AreaLat, AreaLon, MapInfo(6), MapInfo(5, tdmOnly: true)), null, null);

        using var scope = new ScopeViewModel(main, prefStore: null, profileStore: null, ScopePrefSetStore.PrimarySlot);

        Assert.Equal([("m5", true), ("m6", false)], scope.MapToggles.Select(t => (t.MapId, t.TdmOnly)));
        Assert.Equal(["m5"], scope.TdmOnlyMapIds);
    }

    [AvaloniaFact]
    public void ScopeVideoMaps_FollowTheOnToggles_InToggleOrder_AndClearAllEmptiesThem()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        VideoMapData m31 = Map("m31");
        VideoMapData m2 = Map("m2");
        main.CacheVideoMapForTesting(m31);
        main.CacheVideoMapForTesting(m2);
        LoadSource(main);
        using var scope = new ScopeViewModel(main, prefStore: null, profileStore: null, ScopePrefSetStore.PrimarySlot);
        scope.SetMapOn(31, true);
        scope.SetMapOn(2, true);
        Assert.Equal([m2, m31], scope.VideoMaps);

        // Map 10 has no data in the cache, so it draws nothing.
        scope.MapToggles[1].IsOn = true;
        Assert.Equal([m2, m31], scope.VideoMaps);

        scope.MapToggles[0].IsOn = false;
        Assert.Equal([m31], scope.VideoMaps);

        scope.ClearMaps();
        Assert.Empty(scope.VideoMaps);
        Assert.All(scope.MapToggles, t => Assert.False(t.IsOn));

        scope.MapToggles[0].IsOn = true;
        Assert.Equal([m2], scope.VideoMaps);
    }

    [AvaloniaFact]
    public void ScopeMapTurnedOn_DrawsFromTheVideoMapCache()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        LoadSource(main);
        VideoMapData m10 = Map("m10");
        main.CacheVideoMapForTesting(m10);
        using var scope = new ScopeViewModel(main, prefStore: null, profileStore: null, ScopePrefSetStore.PrimarySlot);

        scope.SetMapOn(10, true);

        Assert.Same(m10, Assert.Single(scope.VideoMaps));
    }

    [AvaloniaFact]
    public void Scope_Reseeds_WhenTheSourceLoadsANewFacility_KeepingOnMapsByMapId()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        LoadSource(main);
        using var scope = new ScopeViewModel(main, prefStore: null, profileStore: null, ScopePrefSetStore.PrimarySlot);
        scope.SetMapOn(2, true);
        scope.SetMapOn(31, true);
        scope.RangeNm = 25;

        // Map 31 was on; the new facility's STARS id 31 is another map, so it is not kept on.
        var other31 = new VideoMapInfoDto("x31", "Other 31", "OTH31", [], "A", 31, AlwaysVisible: false, TdmOnly: false);
        main.ScopeMaps.ApplyVideoMapsDtoForTesting(Facility(38.0, -121.0, MapInfo(40), MapInfo(2), MapInfo(5), other31), null, null);

        Assert.Equal([2, 5, 31, 40], scope.MapToggles.Select(t => t.StarsId));
        Assert.Equal([true, false, false, false], scope.MapToggles.Select(t => t.IsOn));
        Assert.Equal("x31", scope.MapToggles[2].MapId);
        Assert.Equal((38.0, -121.0), (scope.CenterLat, scope.CenterLon));
        Assert.Equal(40, scope.RangeNm);
    }

    [AvaloniaFact]
    public void Scope_Cleared_EmptiesTheScopesMaps_AndKeepsItsCentre()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        main.ScopeMaps.ApplyVideoMapsDtoForTesting(Facility(AreaLat, AreaLon, MapInfo(2), MapInfo(5, tdmOnly: true)), null, null);
        main.CacheVideoMapForTesting(Map("m2"));
        using var scope = new ScopeViewModel(main, prefStore: null, profileStore: null, ScopePrefSetStore.PrimarySlot);
        scope.SetMapOn(2, true);
        scope.CenterLat = 40.0;
        scope.CenterLon = -100.0;
        scope.RangeNm = 25;

        main.ScopeMaps.Clear();

        Assert.Empty(scope.MapToggles);
        Assert.Empty(scope.VideoMaps);
        Assert.Empty(scope.TdmOnlyMapIds);
        Assert.Equal((40.0, -100.0), (scope.CenterLat, scope.CenterLon));
        Assert.Equal(25, scope.RangeNm);
        Assert.True(scope.IsWaitingForMapSource);
    }

    [AvaloniaTheory]
    [InlineData("none")]
    [InlineData("pan")]
    [InlineData("zoom")]
    public void Scope_FollowsTheSourcesLateRecentre_OnlyWhileUnmoved(string userChange)
    {
        var main = new MainViewModel(new FakeFilePickerService());
        LoadSource(main);
        using var scope = new ScopeViewModel(main, prefStore: null, profileStore: null, ScopePrefSetStore.PrimarySlot);
        if (userChange == "pan")
        {
            scope.CenterLat = 38.5;
            scope.CenterLon = -121.5;
        }
        else if (userChange == "zoom")
        {
            scope.RangeNm = 25;
        }

        (double Lat, double Lon) before = (scope.CenterLat, scope.CenterLon);
        main.ScopeMaps.RecenterOnPrimaryAirport(OakLat, OakLon);

        Assert.Equal((OakLat, OakLon), (main.ScopeMaps.CenterLat, main.ScopeMaps.CenterLon));
        Assert.Equal(userChange == "none" ? (OakLat, OakLon) : before, (scope.CenterLat, scope.CenterLon));
    }

    [AvaloniaFact]
    public void Scope_SeedsOnce_WhenOpenedMidLoad()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        main.ScopeMaps.ApplyVideoMapsDtoForTesting(Facility(AreaLat, AreaLon, MapInfo(7)), null, null);

        // A newer load has begun: the source still lists the old facility's maps, which are not the ones coming.
        int load = main.ScopeMaps.BeginLoadForTesting();
        using var scope = new ScopeViewModel(main, prefStore: null, profileStore: null, ScopePrefSetStore.PrimarySlot);
        Assert.True(scope.IsWaitingForMapSource);
        Assert.Empty(scope.MapToggles);
        int rebuilds = 0;
        scope.MapToggles.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                rebuilds++;
            }
        };

        main.ScopeMaps.FinishLoadForTesting(load, Facility(AreaLat, AreaLon, MapInfo(31), MapInfo(2), MapInfo(10)), null);

        Assert.Equal(1, rebuilds);
        Assert.False(scope.IsWaitingForMapSource);
        Assert.Equal([2, 10, 31], scope.MapToggles.Select(t => t.StarsId));
    }

    [AvaloniaFact]
    public void Scope_OpenedMidLoad_ThatEndsWithNoNewCatalogue_SeedsFromTheStandingOne()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        LoadSource(main);
        int load = main.ScopeMaps.BeginLoadForTesting();
        using var scope = new ScopeViewModel(main, prefStore: null, profileStore: null, ScopePrefSetStore.PrimarySlot);
        Assert.True(scope.IsWaitingForMapSource);

        main.ScopeMaps.FinishLoadForTesting(load, null, null);

        Assert.False(scope.IsWaitingForMapSource);
        Assert.Equal([2, 10, 31], scope.MapToggles.Select(t => t.StarsId));
        Assert.Equal((AreaLat, AreaLon), (scope.CenterLat, scope.CenterLon));
    }

    [AvaloniaFact]
    public void Scope_OpenedAfterALoadWithNoCatalogue_KeepsWaiting()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        int load = main.ScopeMaps.BeginLoadForTesting();
        main.ScopeMaps.FinishLoadForTesting(load, null, null);
        Assert.True(main.ScopeMaps.IsReady);

        using var scope = new ScopeViewModel(main, prefStore: null, profileStore: null, ScopePrefSetStore.PrimarySlot);

        Assert.True(scope.IsWaitingForMapSource);
        Assert.Empty(scope.MapToggles);
        Assert.Equal((0d, 0d), (scope.CenterLat, scope.CenterLon));

        LoadSource(main);

        Assert.False(scope.IsWaitingForMapSource);
        Assert.Equal((AreaLat, AreaLon), (scope.CenterLat, scope.CenterLon));
    }

    [AvaloniaFact]
    public void MainSelection_FollowsIntoTheScope()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        using var scope = new ScopeViewModel(main, prefStore: null, profileStore: null, ScopePrefSetStore.PrimarySlot);
        AircraftModel ac = Aircraft("UAL238");
        main.Aircraft.Add(ac);

        main.SelectedAircraft = ac;
        Assert.Same(ac, scope.SelectedAircraft);

        main.SelectedAircraft = null;
        Assert.Null(scope.SelectedAircraft);
    }

    [AvaloniaFact]
    public void ScopeWindow_Closed_StopsFollowingMainSelection()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        using var scope = new ScopeViewModel(main, prefStore: null, profileStore: null, ScopePrefSetStore.PrimarySlot);
        var window = new ScopeWindow(main.Preferences, scope);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        window.Close();
        Dispatcher.UIThread.RunJobs();

        AircraftModel ac = Aircraft("UAL238");
        main.Aircraft.Add(ac);
        main.SelectedAircraft = ac;
        Assert.Null(scope.SelectedAircraft);

        // Closing also stops listening to the map source, so the source no longer roots the closed scope.
        LoadSource(main);
        Assert.Empty(scope.MapToggles);
    }
}
