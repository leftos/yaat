using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Scope;
using Yaat.Sim;
using Yaat.Sim.Data;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The scope's view model: it takes the radar's centre and maps once, the first time the radar has both, and is
/// independent of the radar after that; it shares the app-wide selection both ways while open and lets go of it on close.
/// </summary>
public class ScopeViewModelTests
{
    private static AircraftModel Aircraft(string callsign) =>
        new()
        {
            Callsign = callsign,
            AircraftType = "B738",
            FlightRules = "IFR",
            Position = new LatLon(37.62, -122.38),
        };

    private static VideoMapData Map(string id) => new() { MapId = id, Lines = [] };

    /// <summary>Gives the radar what a loaded scenario gives it: a centre, a range and its video maps.</summary>
    private static void CentreRadar(MainViewModel main, double lat, double lon, VideoMapData map)
    {
        main.Radar.CenterLat = lat;
        main.Radar.CenterLon = lon;
        main.Radar.RangeNm = 60;
        main.Radar.BrightnessLookup[map.MapId] = "B";
        main.Radar.ActiveVideoMaps = [map];
    }

    [AvaloniaFact]
    public void Scope_OpenedBeforeRadarCentred_TakesRadarsFirstCentreAndMaps()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        Assert.Equal((0d, 0d), (main.Radar.CenterLat, main.Radar.CenterLon));
        using var scope = new ScopeViewModel(main);
        Assert.True(scope.IsWaitingForRadarSeed);
        Assert.Empty(scope.VideoMaps);

        VideoMapData map = Map("101");
        CentreRadar(main, 37.62, -122.38, map);

        Assert.Equal((37.62, -122.38), (scope.CenterLat, scope.CenterLon));
        Assert.Equal(60, scope.RangeNm);
        Assert.Same(map, Assert.Single(scope.VideoMaps));
        Assert.Equal("B", scope.BrightnessLookup["101"]);
        Assert.False(scope.IsWaitingForRadarSeed);
    }

    [AvaloniaFact]
    public void Scope_AfterSeed_DoesNotFollowRadar()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        using var scope = new ScopeViewModel(main);
        VideoMapData map = Map("101");
        CentreRadar(main, 37.62, -122.38, map);

        // A later radar pan, zoom or map change leaves the scope where it is.
        main.Radar.CenterLat = 40.0;
        main.Radar.CenterLon = -100.0;
        main.Radar.RangeNm = 20;
        main.Radar.ActiveVideoMaps = [Map("202")];

        Assert.Equal((37.62, -122.38), (scope.CenterLat, scope.CenterLon));
        Assert.Equal(60, scope.RangeNm);
        Assert.Same(map, Assert.Single(scope.VideoMaps));
    }

    [AvaloniaFact]
    public void Scope_SeededFromRadar_TakesItsMvaTintFlag()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        main.Radar.ShowMvaHints = true;
        CentreRadar(main, 37.62, -122.38, Map("101"));

        using var scope = new ScopeViewModel(main);

        Assert.False(scope.IsWaitingForRadarSeed);
        Assert.True(scope.ShowMvaHints);
    }

    [AvaloniaFact]
    public void Scope_OpenedBeforeRadarCentred_TakesRadarsMvaTintFlagAtTheSeed()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        using var scope = new ScopeViewModel(main);
        Assert.True(scope.IsWaitingForRadarSeed);

        main.Radar.ShowMvaHints = true;
        CentreRadar(main, 37.62, -122.38, Map("101"));

        Assert.False(scope.IsWaitingForRadarSeed);
        Assert.True(scope.ShowMvaHints);
    }

    [AvaloniaFact]
    public void Scope_AfterSeed_DoesNotFollowRadarsMvaTintFlag()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        using var scope = new ScopeViewModel(main);
        main.Radar.ShowMvaHints = true;
        CentreRadar(main, 37.62, -122.38, Map("101"));

        main.Radar.ShowMvaHints = false;

        Assert.True(scope.ShowMvaHints);
    }

    /// <summary>A centred radar with toggles 31, 2 and 10 (in that list order); <paramref name="active"/> are its maps that are on.</summary>
    private static void CentreRadarWithToggles(MainViewModel main, params VideoMapData[] active)
    {
        RadarMapToggles.Add(main.Radar, 31, 2, 10);
        foreach (VideoMapToggleItem toggle in main.Radar.MapToggles)
        {
            toggle.IsEnabled = active.Any(m => m.MapId == toggle.MapId);
        }

        // The centre comes last: a waiting scope seeds the moment the radar has a centre and toggles.
        main.Radar.RangeNm = 60;
        main.Radar.ActiveVideoMaps = active;
        main.Radar.CenterLat = 37.62;
        main.Radar.CenterLon = -122.38;
    }

    [AvaloniaFact]
    public void Scope_SeedsItsMapToggles_FromTheRadarsTogglesAndActiveMaps_InStarsIdOrder()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        CentreRadarWithToggles(main, Map("m10"));

        using var scope = new ScopeViewModel(main);

        Assert.False(scope.IsWaitingForRadarSeed);
        Assert.Equal([2, 10, 31], scope.MapToggles.Select(t => t.StarsId));
        Assert.Equal([false, true, false], scope.MapToggles.Select(t => t.IsOn));
        ScopeMapToggle ten = scope.MapToggles[1];
        Assert.Equal(("m10", "Map 10", "A"), (ten.MapId, ten.Name, ten.BrightnessCategory));
    }

    [AvaloniaFact]
    public void Scope_SeedsFromACentredRadarWithEveryMapOff()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        using var scope = new ScopeViewModel(main);
        Assert.True(scope.IsWaitingForRadarSeed);

        CentreRadarWithToggles(main);

        Assert.False(scope.IsWaitingForRadarSeed);
        Assert.Equal((37.62, -122.38), (scope.CenterLat, scope.CenterLon));
        Assert.Equal(60, scope.RangeNm);
        Assert.Equal(3, scope.MapToggles.Count);
        Assert.All(scope.MapToggles, t => Assert.False(t.IsOn));
        Assert.Empty(scope.VideoMaps);
    }

    [AvaloniaFact]
    public void ScopeMapToggle_LeavesTheRadarsTogglesAndMapsAlone()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        CentreRadarWithToggles(main, Map("m10"));
        IReadOnlyList<VideoMapData>? radarMaps = main.Radar.ActiveVideoMaps;
        using var scope = new ScopeViewModel(main);

        scope.MapToggles[0].IsOn = true;
        scope.MapToggles[1].IsOn = false;

        Assert.Equal([false, false, true], main.Radar.MapToggles.Select(t => t.IsEnabled));
        Assert.Same(radarMaps, main.Radar.ActiveVideoMaps);
    }

    [AvaloniaFact]
    public void ScopeVideoMaps_FollowTheOnToggles_InToggleOrder_AndClearAllEmptiesThem()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        VideoMapData m31 = Map("m31");
        VideoMapData m2 = Map("m2");
        CentreRadarWithToggles(main, m31, m2);
        using var scope = new ScopeViewModel(main);
        Assert.Equal([m31, m2], scope.VideoMaps);

        // Map 10 has no data in the cache and was not seeded, so it draws nothing; the seeded two follow toggle order.
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
    public void ScopeMapTurnedOn_ThatWasNotSeeded_DrawsFromTheVideoMapCache()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        VideoMapData m31 = Map("m31");
        CentreRadarWithToggles(main, m31);
        VideoMapData m10 = Map("m10");
        main.CacheVideoMapForTesting(m10);
        using var scope = new ScopeViewModel(main);

        scope.SetMapOn(10, true);

        Assert.Equal([m10, m31], scope.VideoMaps);
    }

    [AvaloniaFact]
    public void ScopeMap_CachedCopy_WinsOverTheSeededOne()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        VideoMapData seeded = Map("m2");
        CentreRadarWithToggles(main, seeded);
        VideoMapData cached = Map("m2");
        main.CacheVideoMapForTesting(cached);
        using var scope = new ScopeViewModel(main);
        Assert.Same(seeded, Assert.Single(scope.VideoMaps));

        scope.SetMapOn(10, true);

        Assert.Same(cached, Assert.Single(scope.VideoMaps));
    }

    [AvaloniaFact]
    public void MainSelection_FollowsIntoTheScope()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        using var scope = new ScopeViewModel(main);
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
        using var scope = new ScopeViewModel(main);
        var window = new ScopeWindow(main.Preferences, scope);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        window.Close();
        Dispatcher.UIThread.RunJobs();

        AircraftModel ac = Aircraft("UAL238");
        main.Aircraft.Add(ac);
        main.SelectedAircraft = ac;
        Assert.Null(scope.SelectedAircraft);
        // Closing also drops the wait for the radar's first centre, so the radar no longer roots the closed scope.
        Assert.False(scope.IsWaitingForRadarSeed);
    }
}
