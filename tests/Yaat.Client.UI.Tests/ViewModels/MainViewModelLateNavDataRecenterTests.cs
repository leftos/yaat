using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;

namespace Yaat.Client.UI.Tests.ViewModels;

/// <summary>
/// The main radar's centre when the navigation data finishes loading after a scenario bootstrap set its primary airport
/// (YAAT-1744): the scope ends on the airport, as it would had the data loaded first, unless the user moved it meanwhile.
/// </summary>
public class MainViewModelLateNavDataRecenterTests
{
    // KOAK in the committed navigation data (about 37.7213, -122.2208); the centre checks allow 0.01°.
    private const double OakLat = 37.7213;
    private const double OakLon = -122.2208;

    // The ARTCC area's centre, far east of OAK: where the scope lands when the airport position is not known yet.
    private const double AreaLat = 38.07458;
    private const double AreaLon = -121.00386;

    private static void BootstrapOak(MainViewModel vm) =>
        vm.ApplyScenarioBootstrap(
            new ScenarioBootstrap
            {
                ScenarioId = "scenario-oak",
                ScenarioName = "OAK scenario",
                PrimaryAirportId = "OAK",
                PositionDisplayConfig = null,
                FlightStripsConfig = null,
                Aircraft = [],
                ElapsedSeconds = 0,
            }
        );

    private static void ApplyNctVideoMaps(MainViewModel vm) =>
        vm.Radar.ApplyVideoMapsDtoForTesting(
            new FacilityVideoMapsDto("ZOA", "NCT", [new StarsAreaDto("A", "Area A", AreaLat, AreaLon, 60, [])], [], [])
        );

    private static void BootstrapOakWithAreaFallback(MainViewModel vm)
    {
        BootstrapOak(vm);
        ApplyNctVideoMaps(vm);

        // The airport was not known yet, so the scope took the area's centre.
        Assert.Equal(AreaLat, vm.Radar.CenterLat);
        Assert.Equal(AreaLon, vm.Radar.CenterLon);
    }

    [AvaloniaFact]
    public async Task NavDataAfterBootstrap_RecentresMainRadarOnPrimaryAirport()
    {
        (MainViewModel vm, GatedStartupLoads loads) = GatedStartupLoads.Create();

        BootstrapOakWithAreaFallback(vm);

        loads.Gate.SetResult();
        await vm.NavDataInitialization;

        Assert.Equal("Navigation data loaded", vm.StatusText);
        Assert.Equal(OakLat, vm.Radar.CenterLat, 0.01);
        Assert.Equal(OakLon, vm.Radar.CenterLon, 0.01);
    }

    [AvaloniaFact]
    public async Task NavDataAfterBootstrap_LeavesAPannedScopeAlone()
    {
        (MainViewModel vm, GatedStartupLoads loads) = GatedStartupLoads.Create();

        BootstrapOakWithAreaFallback(vm);
        vm.Radar.CenterLat = 38.5;
        vm.Radar.CenterLon = -121.5;

        loads.Gate.SetResult();
        await vm.NavDataInitialization;

        Assert.Equal(38.5, vm.Radar.CenterLat);
        Assert.Equal(-121.5, vm.Radar.CenterLon);
    }

    [AvaloniaFact]
    public async Task NavDataAfterBootstrap_LeavesAZoomedScopeOnTheFallback()
    {
        (MainViewModel vm, GatedStartupLoads loads) = GatedStartupLoads.Create();

        BootstrapOakWithAreaFallback(vm);
        vm.Radar.RangeNm = 20;

        loads.Gate.SetResult();
        await vm.NavDataInitialization;

        Assert.Equal(AreaLat, vm.Radar.CenterLat);
        Assert.Equal(AreaLon, vm.Radar.CenterLon);
    }

    [AvaloniaFact]
    public async Task NavDataBetweenBootstrapAndVideoMaps_CentresTheMapsOnPrimaryAirport()
    {
        (MainViewModel vm, GatedStartupLoads loads) = GatedStartupLoads.Create();

        BootstrapOak(vm);
        loads.Gate.SetResult();
        await vm.NavDataInitialization;
        ApplyNctVideoMaps(vm);

        Assert.Equal(OakLat, vm.Radar.CenterLat, 0.01);
        Assert.Equal(OakLon, vm.Radar.CenterLon, 0.01);
    }
}
