using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Scope.Prefs;

namespace Yaat.Client.UI.Tests.ViewModels;

/// <summary>
/// The Scope keeps its view per scenario: a scenario load restores the scenario's own saved state, else the facility's
/// last state, else the map source's seed at the primary airport; the outgoing scenario's state is saved under its own id.
/// </summary>
public class ScopeScenarioStateTests
{
    // KOAK in the committed navigation data (about 37.7213, -122.2208); the centre checks allow 0.01°.
    private const double OakLat = 37.7213;
    private const double OakLon = -122.2208;

    private static readonly ActivePositionDto Nct = new(
        Callsign: "NCT_44_APP",
        Name: "Sector 44",
        RadioName: null,
        Frequency: "135.100",
        FacilityId: "NCT",
        FacilityName: "NorCal TRACON",
        PositionType: "Artcc",
        HandoffCode: "C44",
        EramSectorId: "44"
    );

    private static readonly ActivePositionDto Zoa = Nct with { Callsign = "OAK_44_CTR", FacilityId = "ZOA", FacilityName = "Oakland Center" };

    [AvaloniaFact]
    public async Task ScenarioReload_RestoresItsOwnView()
    {
        string root = ScopeStoreFixture.NewStoreRoot();
        using IDisposable store = ScopeStoreFixture.UseStore(root);
        try
        {
            MainViewModel vm = await ScopeTestFixture.MainAsync();
            Load(vm, "SA", Nct);
            Assert.Equal("NCT", vm.PrimaryScope.Viewer.EramFacilityId);
            vm.PrimaryScope.RangeNm = 25;
            vm.PrimaryScope.CenterLat = 37.1;
            vm.PrimaryScope.CenterLon = -121.9;

            Load(vm, "SB", Nct);
            vm.PrimaryScope.RangeNm = 60;

            Load(vm, "SA", Nct);

            ScopePrefSet shown = vm.PrimaryScope.CapturePrefSet(null);
            Assert.Equal(25, shown.RangeNm);
            Assert.Equal(37.1, shown.CenterLat);
            Assert.Equal(-121.9, shown.CenterLon);
        }
        finally
        {
            ScopeStoreFixture.DeleteStoreRoot(root);
        }
    }

    [AvaloniaFact]
    public async Task NewScenarioOnAKnownFacility_UsesTheFacilitysLastView()
    {
        string root = ScopeStoreFixture.NewStoreRoot();
        using IDisposable store = ScopeStoreFixture.UseStore(root);
        try
        {
            MainViewModel vm = await ScopeTestFixture.MainAsync();
            SaveFacilityState(vm, root, "NCT", 30);

            Load(vm, "SNEW", Nct);

            Assert.Equal(30, vm.PrimaryScope.CapturePrefSet(null).RangeNm);
        }
        finally
        {
            ScopeStoreFixture.DeleteStoreRoot(root);
        }
    }

    [AvaloniaFact]
    public async Task NoSavedState_SeedsThePrimaryAirport()
    {
        string root = ScopeStoreFixture.NewStoreRoot();
        using IDisposable store = ScopeStoreFixture.UseStore(root);
        try
        {
            MainViewModel vm = await ScopeTestFixture.MainAsync();
            Load(vm, "SA", Nct);
            vm.ScopeMaps.ApplyVideoMapsDtoForTesting(
                new FacilityVideoMapsDto("ZOA", "NCT", [new StarsAreaDto("A", "Area A", 38.07, -121.0, 60, [])], [], []),
                null,
                null
            );

            Assert.Equal(ScopeViewModel.DefaultRangeNm, vm.PrimaryScope.RangeNm);
            Assert.Equal(OakLat, vm.PrimaryScope.CenterLat, 0.01);
            Assert.Equal(OakLon, vm.PrimaryScope.CenterLon, 0.01);
        }
        finally
        {
            ScopeStoreFixture.DeleteStoreRoot(root);
        }
    }

    [AvaloniaFact]
    public async Task OldPerScenarioSettings_MigrateEvenWhenTheFacilityHasState()
    {
        File.WriteAllText(
            UserPreferences.FilePath,
            new JsonObject
            {
                ["preferencesVersion"] = 1,
                ["radarSettings"] = new JsonObject { ["SA"] = new JsonObject { ["rangeNm"] = 20 } },
            }.ToJsonString()
        );
        string root = ScopeStoreFixture.NewStoreRoot();
        using IDisposable store = ScopeStoreFixture.UseStore(root);
        try
        {
            MainViewModel vm = await ScopeTestFixture.MainAsync();
            SaveFacilityState(vm, root, "NCT", 30);

            Load(vm, "SA", Nct);

            Assert.Equal(20, vm.PrimaryScope.CapturePrefSet(null).RangeNm);
            JsonObject? radar = JsonNode.Parse(File.ReadAllText(UserPreferences.FilePath))!.AsObject()["radarSettings"]?.AsObject();
            Assert.Null(radar?["SA"]);
            Assert.Equal(20, ScenarioState(root, "SA")!.RangeNm);
            Assert.Equal(30, ScopeStoreFixture.Store(root).LoadCurrent("NCT", ScopePrefSetStore.PrimarySlot)!.RangeNm);
        }
        finally
        {
            ScopeStoreFixture.DeleteStoreRoot(root);
        }
    }

    [AvaloniaFact]
    public async Task ScenarioSwitch_SavesTheOutgoingScenarioUnderItsOwnId()
    {
        string root = ScopeStoreFixture.NewStoreRoot();
        using IDisposable store = ScopeStoreFixture.UseStore(root);
        try
        {
            MainViewModel vm = await ScopeTestFixture.MainAsync();
            Load(vm, "SA", Nct);
            vm.PrimaryScope.RangeNm = 25;

            Load(vm, "SB", Zoa);

            Assert.Equal(25, ScenarioState(root, "SA")!.RangeNm);
            Assert.Null(ScenarioState(root, "SB"));
            Assert.Equal(25, ScopeStoreFixture.Store(root).LoadCurrent("NCT", ScopePrefSetStore.PrimarySlot)!.RangeNm);
            Assert.Null(ScopeStoreFixture.Store(root).LoadCurrent("ZOA", ScopePrefSetStore.PrimarySlot));

            vm.PrimaryScope.RangeNm = 60;
            vm.PrimaryScope.SaveState();

            Assert.Equal(60, ScenarioState(root, "SB")!.RangeNm);
            Assert.Equal(25, ScenarioState(root, "SA")!.RangeNm);
        }
        finally
        {
            ScopeStoreFixture.DeleteStoreRoot(root);
        }
    }

    [AvaloniaFact]
    public async Task UnsafeScenarioId_FallsBackToFacilityState()
    {
        string root = ScopeStoreFixture.NewStoreRoot();
        using IDisposable store = ScopeStoreFixture.UseStore(root);
        try
        {
            MainViewModel vm = await ScopeTestFixture.MainAsync();
            SaveFacilityState(vm, root, "NCT", 30);

            Load(vm, "BAD/ID", Nct);
            vm.PrimaryScope.SaveState();

            Assert.Equal(30, vm.PrimaryScope.CapturePrefSet(null).RangeNm);
            Assert.False(Directory.Exists(Path.Combine(root, "current-scenario")));
        }
        finally
        {
            ScopeStoreFixture.DeleteStoreRoot(root);
        }
    }

    [AvaloniaFact]
    public async Task Unload_SavesTheScenarioView_AndTheClearedViewWritesNoKey()
    {
        string root = ScopeStoreFixture.NewStoreRoot();
        using IDisposable store = ScopeStoreFixture.UseStore(root);
        try
        {
            MainViewModel vm = await ScopeTestFixture.MainAsync();
            Load(vm, "SA", Nct);
            vm.ScopeMaps.ApplyVideoMapsDtoForTesting(MapsDto(), null, null);
            ScopeMapToggle toggle = vm.PrimaryScope.MapToggles.First(t => t.StarsId == 102);
            toggle.IsOn = !toggle.IsOn;
            vm.PrimaryScope.RangeNm = 25;
            vm.PrimaryScope.CenterLat = 37.1;
            vm.PrimaryScope.CenterLon = -121.9;
            ScopePrefSet before = vm.PrimaryScope.CapturePrefSet(null);

            vm.ClearScenarioState();
            vm.PrimaryScope.RangeNm = 70;
            Load(vm, "SB", Nct);

            ScopePrefSet facility = ScopeStoreFixture.Store(root).LoadCurrent("NCT", ScopePrefSetStore.PrimarySlot)!;
            Assert.Equal(25, facility.RangeNm);
            Assert.Equal<int>([.. before.Maps.Order()], [.. facility.Maps.Order()]);
            Assert.Null(ScenarioState(root, "SB"));

            Load(vm, "SA", Nct);

            ScopePrefSet shown = vm.PrimaryScope.CapturePrefSet(null);
            Assert.Equal(25, shown.RangeNm);
            Assert.Equal(37.1, shown.CenterLat);
            Assert.Equal(-121.9, shown.CenterLon);
            Assert.Equal<int>([.. before.Maps.Order()], [.. shown.Maps.Order()]);
        }
        finally
        {
            ScopeStoreFixture.DeleteStoreRoot(root);
        }
    }

    [AvaloniaFact]
    public async Task NewScenarioAtTheSameFacility_KeepsTheFacilitysLastViewThroughTheSeed()
    {
        string root = ScopeStoreFixture.NewStoreRoot();
        using IDisposable store = ScopeStoreFixture.UseStore(root);
        try
        {
            MainViewModel vm = await ScopeTestFixture.MainAsync();
            Load(vm, "SA", Nct);
            vm.ScopeMaps.ApplyVideoMapsDtoForTesting(MapsDto(), null, null);
            ScopeMapToggle toggle = vm.PrimaryScope.MapToggles.First(t => t.StarsId == 102);
            toggle.IsOn = !toggle.IsOn;
            vm.PrimaryScope.RangeNm = 30;
            ScopePrefSet before = vm.PrimaryScope.CapturePrefSet(null);

            int token = vm.ScopeMaps.BeginLoadForTesting();
            Load(vm, "SB", Nct);
            vm.ScopeMaps.FinishLoadForTesting(token, MapsDto(), null);

            ScopePrefSet shown = vm.PrimaryScope.CapturePrefSet(null);
            Assert.Equal(30, vm.PrimaryScope.RangeNm);
            Assert.Equal<int>([.. before.Maps.Order()], [.. shown.Maps.Order()]);
        }
        finally
        {
            ScopeStoreFixture.DeleteStoreRoot(root);
        }
    }

    [AvaloniaFact]
    public async Task RecordingLoad_RestoresItsScenariosView_AndLeavesThePreviousScenariosKey()
    {
        string root = ScopeStoreFixture.NewStoreRoot();
        using IDisposable store = ScopeStoreFixture.UseStore(root);
        try
        {
            MainViewModel vm = await ScopeTestFixture.MainAsync();
            Load(vm, "SA", Nct);
            vm.PrimaryScope.RangeNm = 25;
            SaveScenarioState(vm, root, "SR", ScopePrefSetStore.PrimarySlot, 15);

            vm.ApplyRecordingResult(new RewindResultDto(true, null, [], [], null, [], "SR", "Recorded", "OAK"));

            Assert.Equal(15, vm.PrimaryScope.CapturePrefSet(null).RangeNm);
            Assert.Equal(25, ScenarioState(root, "SA")!.RangeNm);

            // The restored view is held for the seed: the maps arriving keep the recording's range, not the seed's.
            vm.ScopeMaps.ApplyVideoMapsDtoForTesting(MapsDto(), null, null);
            Assert.Equal(15, vm.PrimaryScope.RangeNm);
            vm.PrimaryScope.RangeNm = 80;
            vm.PrimaryScope.SaveState();

            Assert.Equal(80, ScenarioState(root, "SR")!.RangeNm);
            Assert.Equal(25, ScenarioState(root, "SA")!.RangeNm);
        }
        finally
        {
            ScopeStoreFixture.DeleteStoreRoot(root);
        }
    }

    [AvaloniaFact]
    public async Task ExtraScope_OpenedDuringAScenario_RestoresItsSlotsScenarioState_AndSavesUnderItOnClose()
    {
        string root = ScopeStoreFixture.NewStoreRoot();
        using IDisposable store = ScopeStoreFixture.UseStore(root);
        try
        {
            MainViewModel vm = await ScopeTestFixture.MainAsync();
            ScopeStoreFixture.Store(root).SaveCurrent("NCT", 2, vm.PrimaryScope.CapturePrefSet(null) with { RangeNm = 50 });
            SaveScenarioState(vm, root, "SA", 2, 33);
            Load(vm, "SA", Nct);

            ScopeViewInstance extra = vm.OpenExtraScopeView();

            Assert.Equal(2, extra.Ordinal);
            Assert.Equal(33, extra.Vm.CapturePrefSet(null).RangeNm);

            // The restored range is held until the maps seed the scope; a zoom comes after that.
            vm.ScopeMaps.ApplyVideoMapsDtoForTesting(MapsDto(), null, null);
            extra.Vm.RangeNm = 44;
            vm.CloseExtraScopeView(extra);

            Assert.Equal(44, ScopeStoreFixture.Store(root).LoadScenarioCurrent("SA", 2)!.RangeNm);
        }
        finally
        {
            ScopeStoreFixture.DeleteStoreRoot(root);
        }
    }

    [AvaloniaFact]
    public async Task ReloadOnAnotherFacility_TheScenariosOwnViewWins()
    {
        string root = ScopeStoreFixture.NewStoreRoot();
        using IDisposable store = ScopeStoreFixture.UseStore(root);
        try
        {
            MainViewModel vm = await ScopeTestFixture.MainAsync();
            Load(vm, "SA", Nct);
            vm.PrimaryScope.RangeNm = 25;
            SaveFacilityState(vm, root, "ZOA", 50);

            Load(vm, "SA", Zoa);

            Assert.Equal("ZOA", vm.PrimaryScope.Viewer.EramFacilityId);
            Assert.Equal(25, vm.PrimaryScope.CapturePrefSet(null).RangeNm);
        }
        finally
        {
            ScopeStoreFixture.DeleteStoreRoot(root);
        }
    }

    [AvaloniaFact]
    public async Task OldPerScenarioSettings_WithAnUnsafeFacility_MigrateIntoTheScenarioState()
    {
        File.WriteAllText(
            UserPreferences.FilePath,
            new JsonObject
            {
                ["preferencesVersion"] = 1,
                ["radarSettings"] = new JsonObject { ["SA"] = new JsonObject { ["rangeNm"] = 20 } },
            }.ToJsonString()
        );
        string root = ScopeStoreFixture.NewStoreRoot();
        using IDisposable store = ScopeStoreFixture.UseStore(root);
        try
        {
            MainViewModel vm = await ScopeTestFixture.MainAsync();

            Load(vm, "SA", Nct with { FacilityId = "BAD/ID" });

            Assert.Equal(20, ScenarioState(root, "SA")!.RangeNm);
            JsonObject? radar = JsonNode.Parse(File.ReadAllText(UserPreferences.FilePath))!.AsObject()["radarSettings"]?.AsObject();
            Assert.Null(radar?["SA"]);
            Assert.False(Directory.Exists(Path.Combine(root, "current")));
        }
        finally
        {
            ScopeStoreFixture.DeleteStoreRoot(root);
        }
    }

    private static FacilityVideoMapsDto MapsDto() =>
        new(
            "ZOA",
            "NCT",
            [new StarsAreaDto("A", "Area A", 38.07, -121.0, 60, [])],
            [MapInfo("map-a", "AAA", "A", 101), MapInfo("map-b", "BBB", "B", 102)],
            [new MapGroupDto([101, 102], [])]
        );

    private static VideoMapInfoDto MapInfo(string id, string shortName, string brightness, int starsId) =>
        new(id, $"{shortName} full name", shortName, [], brightness, starsId, false, false);

    private static void SaveScenarioState(MainViewModel vm, string root, string scenarioId, int slot, double rangeNm) =>
        ScopeStoreFixture.Store(root).SaveScenarioCurrent(scenarioId, slot, vm.PrimaryScope.CapturePrefSet(null) with { RangeNm = rangeNm });

    private static void Load(MainViewModel vm, string scenarioId, ActivePositionDto position) =>
        vm.ApplyScenarioBootstrap(
            new ScenarioBootstrap
            {
                ScenarioId = scenarioId,
                ScenarioName = null,
                PrimaryAirportId = "OAK",
                PositionDisplayConfig = new PositionDisplayConfigDto([], [], [], "C44", position, 3, NoStarsDisplay: true),
                FlightStripsConfig = null,
                Aircraft = [],
                ElapsedSeconds = 0,
                SessionStartUtc = null,
            }
        );

    private static void SaveFacilityState(MainViewModel vm, string root, string facility, double rangeNm) =>
        ScopeStoreFixture
            .Store(root)
            .SaveCurrent(facility, ScopePrefSetStore.PrimarySlot, vm.PrimaryScope.CapturePrefSet(null) with { RangeNm = rangeNm });

    private static ScopePrefSet? ScenarioState(string root, string scenarioId) =>
        ScopeStoreFixture.Store(root).LoadScenarioCurrent(scenarioId, ScopePrefSetStore.PrimarySlot);
}
