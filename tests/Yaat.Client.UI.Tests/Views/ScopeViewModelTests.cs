using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.UI.Tests.Fakes;
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
        var scope = new ScopeViewModel(main);
        Assert.True(scope.IsWaitingForRadarSeed);
        Assert.Empty(scope.VideoMaps);

        VideoMapData map = Map("101");
        CentreRadar(main, 37.62, -122.38, map);

        Assert.Equal((37.62, -122.38), (scope.CenterLat, scope.CenterLon));
        Assert.Equal(60, scope.RangeNm);
        Assert.Same(map, Assert.Single(scope.VideoMaps));
        Assert.Equal("B", scope.BrightnessLookup["101"]);
        Assert.False(scope.IsWaitingForRadarSeed);
        scope.Dispose();
    }

    [AvaloniaFact]
    public void Scope_AfterSeed_DoesNotFollowRadar()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        var scope = new ScopeViewModel(main);
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
        scope.Dispose();
    }

    [AvaloniaFact]
    public void Scope_SeededFromRadar_TakesItsMvaTintFlag()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        main.Radar.ShowMvaHints = true;
        CentreRadar(main, 37.62, -122.38, Map("101"));

        var scope = new ScopeViewModel(main);

        Assert.False(scope.IsWaitingForRadarSeed);
        Assert.True(scope.ShowMvaHints);
        scope.Dispose();
    }

    [AvaloniaFact]
    public void Scope_OpenedBeforeRadarCentred_TakesRadarsMvaTintFlagAtTheSeed()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        var scope = new ScopeViewModel(main);
        Assert.True(scope.IsWaitingForRadarSeed);

        main.Radar.ShowMvaHints = true;
        CentreRadar(main, 37.62, -122.38, Map("101"));

        Assert.False(scope.IsWaitingForRadarSeed);
        Assert.True(scope.ShowMvaHints);
        scope.Dispose();
    }

    [AvaloniaFact]
    public void Scope_AfterSeed_DoesNotFollowRadarsMvaTintFlag()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        var scope = new ScopeViewModel(main);
        main.Radar.ShowMvaHints = true;
        CentreRadar(main, 37.62, -122.38, Map("101"));

        main.Radar.ShowMvaHints = false;

        Assert.True(scope.ShowMvaHints);
        scope.Dispose();
    }

    [AvaloniaFact]
    public void MainSelection_FollowsIntoTheScope()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        var scope = new ScopeViewModel(main);
        AircraftModel ac = Aircraft("UAL238");
        main.Aircraft.Add(ac);

        main.SelectedAircraft = ac;
        Assert.Same(ac, scope.SelectedAircraft);

        main.SelectedAircraft = null;
        Assert.Null(scope.SelectedAircraft);
        scope.Dispose();
    }

    [AvaloniaFact]
    public void ScopeWindow_Closed_StopsFollowingMainSelection()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        var scope = new ScopeViewModel(main);
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
