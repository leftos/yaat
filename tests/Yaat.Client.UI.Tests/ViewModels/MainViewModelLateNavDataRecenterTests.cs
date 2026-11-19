using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;

namespace Yaat.Client.UI.Tests.ViewModels;

/// <summary>
/// The Scope map source's centre when the navigation data finishes loading after a scenario bootstrap set its primary
/// airport: the maps end on the airport, as they would had the data loaded first.
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
                SessionStartUtc = null,
            }
        );

    private static void ApplyNctVideoMaps(MainViewModel vm) =>
        vm.ScopeMaps.ApplyVideoMapsDtoForTesting(
            new FacilityVideoMapsDto("ZOA", "NCT", [new StarsAreaDto("A", "Area A", AreaLat, AreaLon, 60, [])], [], []),
            null,
            null
        );

    private static void BootstrapOakWithAreaFallback(MainViewModel vm)
    {
        BootstrapOak(vm);
        ApplyNctVideoMaps(vm);

        // The airport was not known yet, so the maps took the area's centre.
        Assert.Equal(AreaLat, vm.ScopeMaps.CenterLat);
        Assert.Equal(AreaLon, vm.ScopeMaps.CenterLon);
    }

    [AvaloniaFact]
    public async Task NavDataAfterBootstrap_RecentresTheScopeMapsOnPrimaryAirport()
    {
        (MainViewModel vm, GatedStartupLoads loads) = GatedStartupLoads.Create();

        BootstrapOakWithAreaFallback(vm);

        loads.Gate.SetResult();
        await vm.NavDataInitialization;

        Assert.Equal("Navigation data loaded", vm.StatusText);
        Assert.Equal(OakLat, vm.ScopeMaps.CenterLat, 0.01);
        Assert.Equal(OakLon, vm.ScopeMaps.CenterLon, 0.01);
    }

    [AvaloniaFact]
    public async Task NavDataBetweenBootstrapAndVideoMaps_CentresTheMapsOnPrimaryAirport()
    {
        (MainViewModel vm, GatedStartupLoads loads) = GatedStartupLoads.Create();

        BootstrapOak(vm);
        loads.Gate.SetResult();
        await vm.NavDataInitialization;
        ApplyNctVideoMaps(vm);

        Assert.Equal(OakLat, vm.ScopeMaps.CenterLat, 0.01);
        Assert.Equal(OakLon, vm.ScopeMaps.CenterLon, 0.01);
    }
}
