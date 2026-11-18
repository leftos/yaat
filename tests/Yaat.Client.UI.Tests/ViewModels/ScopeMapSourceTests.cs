using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;
using Yaat.Sim.Data;

namespace Yaat.Client.UI.Tests.ViewModels;

/// <summary>
/// The Scope's own map and display source (<see cref="ScopeMapSource"/>): the facility's video-map catalogue, centre, position
/// display config, fixes and field elevation, fed by <see cref="MainViewModel"/> on the same paths as the main radar.
/// </summary>
public class ScopeMapSourceTests
{
    // KOAK in the committed navigation data (about 37.7213, -122.2208); the centre checks allow 0.01°.
    private const double OakLat = 37.7213;
    private const double OakLon = -122.2208;

    // The ARTCC area's centre, far east of OAK: where the source centres when the airport position is not known yet.
    private const double AreaLat = 38.07458;
    private const double AreaLon = -121.00386;

    private static ScenarioBootstrap OakBootstrap(PositionDisplayConfigDto? config = null) =>
        new()
        {
            ScenarioId = "scenario-oak",
            ScenarioName = "OAK scenario",
            PrimaryAirportId = "OAK",
            PositionDisplayConfig = config,
            FlightStripsConfig = null,
            Aircraft = [],
            ElapsedSeconds = 0,
            SessionStartUtc = null,
        };

    // A scenario whose primary airport id the navigation data cannot place, so its load takes its first STARS area's centre.
    private static ScenarioBootstrap UnplaceableAirportBootstrap() =>
        new()
        {
            ScenarioId = "scenario-zzzz",
            ScenarioName = "ZZZZ scenario",
            PrimaryAirportId = "ZZZZ",
            PositionDisplayConfig = null,
            FlightStripsConfig = null,
            Aircraft = [],
            ElapsedSeconds = 0,
            SessionStartUtc = null,
        };

    private static VideoMapInfoDto Map(string id, string shortName, string brightness, int starsId, bool tdmOnly = false) =>
        new(id, $"{shortName} full name", shortName, [], brightness, starsId, false, tdmOnly);

    private static FacilityVideoMapsDto NctDto(string artccId = "ZOA", List<MapGroupDto>? groups = null) =>
        new(
            artccId,
            "NCT",
            [new StarsAreaDto("A", "Area A", AreaLat, AreaLon, 60, [])],
            [Map("map-c", "CCC", "B", 30), Map("map-a", "AAA", "A", 10, tdmOnly: true), Map("map-b", "BBB", "B", 20)],
            groups ?? [new MapGroupDto([10, 20, null], [])]
        );

    private static PositionDisplayConfigDto PositionConfig(List<int?> mapIds, List<string> airports) => new(mapIds, [], airports, "2B", null, 7);

    private static async Task<MainViewModel> MainWithNavDataAsync()
    {
        (MainViewModel vm, GatedStartupLoads loads) = GatedStartupLoads.Create();
        loads.Gate.SetResult();
        await vm.NavDataInitialization;
        return vm;
    }

    [AvaloniaFact]
    public void AppliedDto_ListsItsMapsInDtoOrder_WithBrightnessAndMvaFacility()
    {
        (MainViewModel vm, _) = GatedStartupLoads.Create();

        vm.ScopeMaps.ApplyVideoMapsDtoForTesting(NctDto(), null, null);

        Assert.Equal(["map-c", "map-a", "map-b"], vm.ScopeMaps.Maps.Select(m => m.MapId));
        ScopeMapInfo a = vm.ScopeMaps.Maps[1];
        Assert.Equal("AAA", a.ShortName);
        Assert.Equal("AAA full name", a.Name);
        Assert.Equal("A", a.BrightnessCategory);
        Assert.Equal(10, a.StarsId);
        Assert.True(a.TdmOnly);
        Assert.Equal("B", vm.ScopeMaps.BrightnessLookup["map-c"]);
        Assert.Equal("A", vm.ScopeMaps.BrightnessLookup["map-a"]);
        Assert.Equal("NCT", vm.ScopeMaps.MvaFacilityId);
        Assert.Equal([10, 20, null], vm.ScopeMaps.MapGroupMapIds);
    }

    [AvaloniaFact]
    public async Task AppliedDto_CentresOnThePrimaryAirport_WhenItsPositionIsKnown()
    {
        MainViewModel vm = await MainWithNavDataAsync();
        vm.ApplyScenarioBootstrap(OakBootstrap());

        vm.ScopeMaps.ApplyVideoMapsDtoForTesting(NctDto(), null, null);

        Assert.Equal(OakLat, vm.ScopeMaps.CenterLat, 0.01);
        Assert.Equal(OakLon, vm.ScopeMaps.CenterLon, 0.01);
    }

    [AvaloniaFact]
    public void AppliedDto_CentresOnTheFirstArea_WhenNoPrimaryPosition()
    {
        (MainViewModel vm, _) = GatedStartupLoads.Create();
        vm.ApplyScenarioBootstrap(OakBootstrap());

        vm.ScopeMaps.ApplyVideoMapsDtoForTesting(NctDto(), null, null);

        Assert.Equal(AreaLat, vm.ScopeMaps.CenterLat);
        Assert.Equal(AreaLon, vm.ScopeMaps.CenterLon);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecenterOnPrimaryAirport_MovesTheCentre_OnlyWhileOnTheAreaFallback(bool movedSinceFallback)
    {
        (MainViewModel vm, _) = GatedStartupLoads.Create();
        vm.ScopeMaps.ApplyVideoMapsDtoForTesting(NctDto(), null, null);
        if (movedSinceFallback)
        {
            // A first recentre leaves the area fallback, so the centre is no longer the one a load put there.
            vm.ScopeMaps.RecenterOnPrimaryAirport(38.5, -121.5);
        }

        vm.ScopeMaps.RecenterOnPrimaryAirport(OakLat, OakLon);

        Assert.Equal(movedSinceFallback ? 38.5 : OakLat, vm.ScopeMaps.CenterLat);
        Assert.Equal(movedSinceFallback ? -121.5 : OakLon, vm.ScopeMaps.CenterLon);
    }

    [AvaloniaFact]
    public void Loaded_RaisedOncePerAppliedDto_AndIsReadyTrue()
    {
        (MainViewModel vm, _) = GatedStartupLoads.Create();
        int loaded = 0;
        bool readyWhenRaised = false;
        vm.ScopeMaps.Loaded += (_, _) =>
        {
            loaded++;
            readyWhenRaised = vm.ScopeMaps.IsReady;
        };

        vm.ScopeMaps.ApplyVideoMapsDtoForTesting(NctDto(), null, null);

        Assert.Equal(1, loaded);
        Assert.True(readyWhenRaised);
        Assert.True(vm.ScopeMaps.IsReady);

        vm.ScopeMaps.ApplyVideoMapsDtoForTesting(NctDto(), null, null);

        Assert.Equal(2, loaded);
    }

    [AvaloniaFact]
    public void Load_SupersededByANewerLoad_RaisesNoLoaded()
    {
        (MainViewModel vm, _) = GatedStartupLoads.Create();
        ScopeMapSource source = vm.ScopeMaps;
        int loaded = 0;
        source.Loaded += (_, _) => loaded++;
        int first = source.BeginLoadForTesting();
        int second = source.BeginLoadForTesting();

        source.FinishLoadForTesting(first, NctDto(), null);

        Assert.Equal(0, loaded);
        Assert.Empty(source.Maps);
        Assert.False(source.HasCatalogue);
        Assert.True(source.IsLoading);

        source.FinishLoadForTesting(second, NctDto(), null);

        Assert.Equal(1, loaded);
        Assert.Equal(3, source.Maps.Count);
        Assert.False(source.IsLoading);
    }

    [AvaloniaFact]
    public void Clear_MidLoad_RaisesNoLoaded()
    {
        (MainViewModel vm, _) = GatedStartupLoads.Create();
        ScopeMapSource source = vm.ScopeMaps;
        int loaded = 0;
        source.Loaded += (_, _) => loaded++;
        int load = source.BeginLoadForTesting();

        source.Clear();
        Assert.False(source.IsLoading);
        source.FinishLoadForTesting(load, NctDto(), null);

        Assert.Equal(0, loaded);
        Assert.Empty(source.Maps);
        Assert.False(source.HasCatalogue);
        Assert.False(source.IsReady);
        Assert.False(source.IsLoading);
    }

    [AvaloniaFact]
    public void DownloadFailure_AfterTheDtoApplied_StillRaisesLoaded()
    {
        (MainViewModel vm, _) = GatedStartupLoads.Create();
        ScopeMapSource source = vm.ScopeMaps;
        int loaded = 0;
        bool loadingWhenRaised = false;
        source.Loaded += (_, _) =>
        {
            loaded++;
            loadingWhenRaised = source.IsLoading;
        };
        int load = source.BeginLoadForTesting();

        source.FinishLoadForTesting(load, NctDto(), new HttpRequestException("download failed"));

        Assert.Equal(1, loaded);
        Assert.True(loadingWhenRaised);
        Assert.Equal(3, source.Maps.Count);
        Assert.True(source.HasCatalogue);
        Assert.True(source.IsReady);
        Assert.False(source.IsLoading);
    }

    [AvaloniaFact]
    public void Load_WithNoMaps_RaisesNoLoaded_AndEnds()
    {
        (MainViewModel vm, _) = GatedStartupLoads.Create();
        ScopeMapSource source = vm.ScopeMaps;
        int loaded = 0;
        source.Loaded += (_, _) => loaded++;
        int load = source.BeginLoadForTesting();

        source.FinishLoadForTesting(load, null, null);

        Assert.Equal(0, loaded);
        Assert.False(source.HasCatalogue);
        Assert.True(source.IsReady);
        Assert.False(source.IsLoading);
    }

    [AvaloniaFact]
    public void Clear_EmptiesTheCatalogue_ResetsTheDefaults_AndRaisesCleared()
    {
        (MainViewModel vm, _) = GatedStartupLoads.Create();
        ScopeMapSource source = vm.ScopeMaps;
        source.ApplyVideoMapsDtoForTesting(NctDto(), null, null);
        source.ApplyPositionDisplayConfig(PositionConfig([30], ["OAK"]));
        source.SetPrimaryAirportId("OAK");
        source.ShowMvaHintsDefault = true;
        int cleared = 0;
        source.Cleared += (_, _) => cleared++;
        int generation = source.Generation;

        source.Clear();

        Assert.Empty(source.Maps);
        Assert.Empty(source.BrightnessLookup);
        Assert.Empty(source.MapGroupMapIds);
        Assert.Empty(source.WeatherAirports);
        Assert.Null(source.MvaFacilityId);
        Assert.Null(source.PrimaryAirportId);
        Assert.False(source.ShowMvaHintsDefault);
        Assert.False(source.IsReady);
        Assert.True(source.Generation > generation);
        Assert.Equal(1, cleared);
    }

    [AvaloniaFact]
    public void Clear_ThenALoad_CentresOnTheNewFirstArea_NotThePreviousScenariosAirport()
    {
        (MainViewModel vm, _) = GatedStartupLoads.Create();
        ScopeMapSource source = vm.ScopeMaps;
        source.SetPrimaryAirportPosition(OakLat, OakLon);
        source.ApplyVideoMapsDtoForTesting(NctDto(), null, null);
        Assert.Equal(OakLat, source.CenterLat, 0.01);

        // The next scenario's primary airport has no position of its own, so it centres on its first area.
        source.Clear();
        source.ApplyVideoMapsDtoForTesting(NctDto(), null, null);

        Assert.Equal(AreaLat, source.CenterLat);
        Assert.Equal(AreaLon, source.CenterLon);
    }

    [AvaloniaFact]
    public async Task ScenarioBootstrap_OverAnother_WithNoClear_CentresOnTheNewFacilitysFirstArea()
    {
        MainViewModel vm = await MainWithNavDataAsync();
        vm.ApplyScenarioBootstrap(OakBootstrap());
        vm.ScopeMaps.ApplyVideoMapsDtoForTesting(NctDto(), null, null);
        Assert.Equal(OakLat, vm.ScopeMaps.CenterLat, 0.01);

        // Loaded straight over the first, with no Clear: the airport id that resolves to nothing leaves no position.
        vm.ApplyScenarioBootstrap(UnplaceableAirportBootstrap());
        vm.ScopeMaps.ApplyVideoMapsDtoForTesting(NctDto(), null, null);

        Assert.Equal(AreaLat, vm.ScopeMaps.CenterLat);
        Assert.Equal(AreaLon, vm.ScopeMaps.CenterLon);
    }

    [AvaloniaFact]
    public void PositionDisplayConfig_GroupWinsOverTheDtoGroup()
    {
        (MainViewModel vm, _) = GatedStartupLoads.Create();

        vm.ScopeMaps.ApplyPositionDisplayConfig(PositionConfig([30, null, 10], ["OAK"]));
        vm.ScopeMaps.ApplyVideoMapsDtoForTesting(NctDto(), null, null);

        Assert.Equal([30, null, 10], vm.ScopeMaps.MapGroupMapIds);
    }

    [AvaloniaFact]
    public void PositionDisplayConfig_SetsWeatherAirports()
    {
        (MainViewModel vm, _) = GatedStartupLoads.Create();

        vm.ScopeMaps.ApplyPositionDisplayConfig(PositionConfig([10], ["OAK", "SFO"]));

        Assert.Equal(["OAK", "SFO"], vm.ScopeMaps.WeatherAirports);
    }

    [AvaloniaFact]
    public void ScenarioBootstrap_ThroughMainViewModel_ReachesTheSource()
    {
        (MainViewModel vm, _) = GatedStartupLoads.Create();

        vm.ApplyScenarioBootstrap(OakBootstrap(PositionConfig([20], ["OAK", "SJC"])));

        Assert.Equal("OAK", vm.ScopeMaps.PrimaryAirportId);
        Assert.Equal(["OAK", "SJC"], vm.ScopeMaps.WeatherAirports);
        Assert.Equal([20], vm.ScopeMaps.MapGroupMapIds);
    }

    [AvaloniaFact]
    public async Task NavDbReady_ThroughMainViewModel_GivesTheSourceItsFixes_AndDescribePointNamesAnFrd()
    {
        (MainViewModel vm, GatedStartupLoads loads) = GatedStartupLoads.Create();
        Assert.Null(vm.ScopeMaps.DescribePoint(37.80, -122.30));

        loads.Gate.SetResult();
        await vm.NavDataInitialization;

        Assert.NotNull(vm.ScopeMaps.Fixes);
        Assert.NotEmpty(vm.ScopeMaps.Fixes);
        string? expected = RadarViewModel.TryToFrd(vm.Radar.Fixes, 37.80, -122.30);
        Assert.NotNull(expected);
        Assert.Equal(expected, vm.ScopeMaps.DescribePoint(37.80, -122.30));
    }

    [AvaloniaFact]
    public async Task GetFieldElevation_FallsBackFromDestinationToPrimaryToZero()
    {
        MainViewModel vm = await MainWithNavDataAsync();
        double? sfo = NavigationDatabase.Instance.GetAirportElevation("SFO");
        double? oak = NavigationDatabase.Instance.GetAirportElevation("OAK");
        Assert.NotNull(sfo);
        Assert.NotNull(oak);

        Assert.Equal(0, vm.ScopeMaps.GetFieldElevation(null));
        Assert.Equal(0, vm.ScopeMaps.GetFieldElevation("ZZZZ"));
        vm.ScopeMaps.SetPrimaryAirportId("OAK");

        Assert.Equal(sfo.Value, vm.ScopeMaps.GetFieldElevation("SFO"));
        Assert.Equal(oak.Value, vm.ScopeMaps.GetFieldElevation("ZZZZ"));
        Assert.Equal(oak.Value, vm.ScopeMaps.GetFieldElevation(null));
    }

    [AvaloniaFact]
    public void AppliedDto_MarksTheMapsFavouritedForItsArtcc()
    {
        const string artcc = "TESTSCOPEMAPSFAV";
        (MainViewModel vm, _) = GatedStartupLoads.Create();
        vm.Preferences.SetFavoriteVideoMap(FavoriteMapScope.Artcc, artcc, "map-b", true);

        vm.ScopeMaps.ApplyVideoMapsDtoForTesting(NctDto(artcc), null, null);

        Assert.Equal(artcc, vm.ScopeMaps.ActiveArtccId);
        Assert.Equal(["map-b"], vm.ScopeMaps.Maps.Where(m => m.IsFavoriteArtcc).Select(m => m.MapId));
        Assert.DoesNotContain(vm.ScopeMaps.Maps, m => m.IsFavoriteAirport || m.IsFavoriteScenario);
    }
}
