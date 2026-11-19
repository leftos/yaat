using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Scope.Prefs;
using Yaat.Client.Views.Scope.Profiles;

namespace Yaat.Client.UI.Tests.ViewModels;

/// <summary>
/// A scenario load copies the old per-scenario Radar settings the preferences hold for it into the primary Scope's saved
/// state for the student position's facility, once, when that facility has none, and drops the entry.
/// </summary>
public class ScopeRadarSettingsMigrationTests
{
    private static readonly ActivePositionDto Sector44 = new(
        Callsign: "OAK_44_CTR",
        Name: "Sector 44",
        RadioName: null,
        Frequency: "135.100",
        FacilityId: "ZOA",
        FacilityName: "Oakland Center",
        PositionType: "Artcc",
        HandoffCode: "C44",
        EramSectorId: "44"
    );

    [AvaloniaFact]
    public async Task NoSavedState_CopiesTheMappedValues_AndRemovesTheEntry()
    {
        WritePreferences(
            (
                "S1",
                new JsonObject
                {
                    ["rangeNm"] = 55,
                    ["enabledStarsIds"] = new JsonArray(101, 102),
                    ["showRangeRings"] = false,
                    ["rangeRingSizeNm"] = 10,
                    ["showFixes"] = true,
                    ["pinnedMarkers"] = new JsonArray("sfo270010", "OAK"),
                    ["isPanZoomLocked"] = true,
                    ["showTopDown"] = true,
                    ["historyCount"] = 4,
                    ["brightnessValues"] = new JsonObject
                    {
                        ["MapA"] = 40,
                        ["Fdb"] = 70,
                        ["Dcb"] = 10,
                    },
                }
            )
        );
        string root = ScopeStoreFixture.NewStoreRoot();
        using IDisposable store = ScopeStoreFixture.UseStore(root);
        try
        {
            MainViewModel vm = await ScopeTestFixture.MainAsync();
            ScopePrefSet before = vm.PrimaryScope.CapturePrefSet(null);
            vm.ApplyScenarioBootstrap(Bootstrap("S1", Config(Sector44)));

            ScopePrefSet state = SavedState(root)!;
            Assert.Equal(55, state.RangeNm);
            Assert.Equal<int>([101, 102], state.Maps);
            Assert.Equal(new ScopePrefRangeRing(false, 10, null, null), state.MapTools.RangeRing);
            Assert.True(state.MapTools.FixesAndMarkers!.ShowFixes);
            Assert.Equal<string>(["SFO270010", "OAK"], state.MapTools.FixesAndMarkers.Markers);
            Assert.True(state.MapTools.Lock);
            Assert.True(state.Tdm);
            Assert.Equal<int?>(4, state.PinHistory);
            Assert.Equal(40, state.Brightness["mapa"]);
            Assert.Equal(70, state.Brightness["fdb"]);
            Assert.Equal<string>([.. before.Brightness.Keys.Order()], [.. state.Brightness.Keys.Order()]);
            Assert.Null(RadarSettings()["S1"]);
            Assert.Equal(55, vm.PrimaryScope.CapturePrefSet(null).RangeNm);

            vm.ScopeMaps.ApplyVideoMapsDtoForTesting(MapsDto(), null, null);
            vm.PrimaryScope.SaveState();

            ScopePrefSet reseeded = SavedState(root)!;
            Assert.Equal(55, reseeded.RangeNm);
            Assert.Contains(101, reseeded.Maps);
            Assert.Contains(102, reseeded.Maps);
        }
        finally
        {
            ScopeStoreFixture.DeleteStoreRoot(root);
        }
    }

    [AvaloniaFact]
    public async Task SavedState_IsKept_AndTheEntryIsRemoved()
    {
        WritePreferences(("S1", new JsonObject { ["rangeNm"] = 55 }));
        string root = ScopeStoreFixture.NewStoreRoot();
        using IDisposable store = ScopeStoreFixture.UseStore(root);
        try
        {
            MainViewModel seeded = await LoadAsync("S9");
            seeded.PrimaryScope.RangeNm = 25;
            seeded.PrimaryScope.SaveState();

            await LoadAsync("S1");

            Assert.Equal(25, SavedState(root)!.RangeNm);
            Assert.Null(RadarSettings()["S1"]);
        }
        finally
        {
            ScopeStoreFixture.DeleteStoreRoot(root);
        }
    }

    [AvaloniaFact]
    public async Task SecondScenarioAtTheSameFacility_DoesNotOverwriteTheFirst()
    {
        WritePreferences(("S1", new JsonObject { ["rangeNm"] = 55 }), ("S2", new JsonObject { ["rangeNm"] = 70 }));
        string root = ScopeStoreFixture.NewStoreRoot();
        using IDisposable store = ScopeStoreFixture.UseStore(root);
        try
        {
            MainViewModel vm = await ScopeTestFixture.MainAsync();
            vm.ApplyScenarioBootstrap(Bootstrap("S1", Config(Sector44)));
            vm.ApplyScenarioBootstrap(Bootstrap("S2", Config(Sector44)));

            Assert.Equal(55, vm.PrimaryScope.CapturePrefSet(null).RangeNm);
            Assert.Equal(55, SavedState(root)!.RangeNm);
            Assert.Null(RadarSettings()["S1"]);
            Assert.Null(RadarSettings()["S2"]);
        }
        finally
        {
            ScopeStoreFixture.DeleteStoreRoot(root);
        }
    }

    [AvaloniaFact]
    public async Task ExtraWindowEntries_AreDroppedWithThePrimary()
    {
        WritePreferences(
            ("S1", new JsonObject { ["rangeNm"] = 55 }),
            ("S1#2", new JsonObject { ["rangeNm"] = 60 }),
            ("S1#3", new JsonObject { ["rangeNm"] = 65 }),
            ("S1#", new JsonObject { ["rangeNm"] = 55 }),
            ("S1#a", new JsonObject { ["rangeNm"] = 55 }),
            ("S10", new JsonObject { ["rangeNm"] = 70 })
        );
        string root = ScopeStoreFixture.NewStoreRoot();
        using IDisposable store = ScopeStoreFixture.UseStore(root);
        try
        {
            await LoadAsync("S1");

            JsonObject radar = RadarSettings();
            Assert.Null(radar["S1"]);
            Assert.Null(radar["S1#2"]);
            Assert.Null(radar["S1#3"]);
            Assert.NotNull(radar["S1#"]);
            Assert.NotNull(radar["S1#a"]);
            Assert.NotNull(radar["S10"]);
        }
        finally
        {
            ScopeStoreFixture.DeleteStoreRoot(root);
        }
    }

    [AvaloniaFact]
    public async Task NoStudentPosition_WritesNothing_AndKeepsTheEntry()
    {
        WritePreferences(("S1", new JsonObject { ["rangeNm"] = 55 }));
        string root = ScopeStoreFixture.NewStoreRoot();
        using IDisposable store = ScopeStoreFixture.UseStore(root);
        try
        {
            MainViewModel vm = await ScopeTestFixture.MainAsync();
            vm.ApplyScenarioBootstrap(Bootstrap("S1", config: null));

            Assert.False(File.Exists(Path.Combine(root, "current", "ZOA.json")));
            Assert.NotNull(RadarSettings()["S1"]);
        }
        finally
        {
            ScopeStoreFixture.DeleteStoreRoot(root);
        }
    }

    [AvaloniaFact]
    public async Task UnsafeFacility_WritesNothing_AndKeepsTheEntry()
    {
        WritePreferences(("S1", new JsonObject { ["rangeNm"] = 55 }));
        string root = ScopeStoreFixture.NewStoreRoot();
        using IDisposable store = ScopeStoreFixture.UseStore(root);
        try
        {
            await LoadAsync("S1", Position(facilityId: "BAD/ID"));

            Assert.False(Directory.Exists(Path.Combine(root, "current")));
            Assert.NotNull(RadarSettings()["S1"]);
        }
        finally
        {
            ScopeStoreFixture.DeleteStoreRoot(root);
        }
    }

    [AvaloniaFact]
    public async Task TrackFacility_IsTheKey_NotTheListingFacility()
    {
        WritePreferences(("S1", new JsonObject { ["rangeNm"] = 55 }));
        string root = ScopeStoreFixture.NewStoreRoot();
        using IDisposable store = ScopeStoreFixture.UseStore(root);
        try
        {
            await LoadAsync("S1", Position(facilityId: "OAK", trackFacilityId: "ZOA"));

            Assert.Equal(55, SavedState(root)!.RangeNm);
            Assert.False(File.Exists(Path.Combine(root, "current", "OAK.json")));
        }
        finally
        {
            ScopeStoreFixture.DeleteStoreRoot(root);
        }
    }

    [AvaloniaFact]
    public async Task NoEntry_WritesNothing()
    {
        WritePreferences(("S1", new JsonObject { ["rangeNm"] = 55 }));
        string root = ScopeStoreFixture.NewStoreRoot();
        using IDisposable store = ScopeStoreFixture.UseStore(root);
        try
        {
            await LoadAsync("S9");

            Assert.False(File.Exists(Path.Combine(root, "current", "ZOA.json")));
        }
        finally
        {
            ScopeStoreFixture.DeleteStoreRoot(root);
        }
    }

    [AvaloniaFact]
    public async Task OutOfGateValues_FallBackToTheSeedAndDefaults()
    {
        WritePreferences(
            (
                "S1",
                new JsonObject
                {
                    ["rangeNm"] = 0,
                    ["centerLat"] = 0,
                    ["centerLon"] = 0,
                    ["rangeRingSizeNm"] = 7,
                    ["rangeRingCenterLat"] = 0,
                    ["rangeRingCenterLon"] = 0,
                    ["historyCount"] = 0,
                    ["brightnessValues"] = new JsonObject { ["MapA"] = 130 },
                }
            )
        );
        string root = ScopeStoreFixture.NewStoreRoot();
        using IDisposable store = ScopeStoreFixture.UseStore(root);
        try
        {
            MainViewModel vm = await ScopeTestFixture.MainAsync();
            ScopePrefSet before = vm.PrimaryScope.CapturePrefSet(null);
            vm.ApplyScenarioBootstrap(Bootstrap("S1", Config(Sector44)));

            ScopePrefSet state = SavedState(root)!;
            Assert.Equal(ScopeViewModel.DefaultRangeNm, state.RangeNm);
            Assert.Equal(0.0, state.CenterLat);
            Assert.Equal(0.0, state.CenterLon);
            Assert.Null(state.MapTools.RangeRing);
            Assert.Equal(before.PinHistory, state.PinHistory);
            Assert.Equal(before.Brightness["mapa"], state.Brightness["mapa"]);
        }
        finally
        {
            ScopeStoreFixture.DeleteStoreRoot(root);
        }
    }

    [AvaloniaFact]
    public async Task WriteFailure_KeepsTheEntry_AndTheLoadContinues()
    {
        WritePreferences(("S1", new JsonObject { ["rangeNm"] = 55 }));
        string root = ScopeStoreFixture.NewStoreRoot();
        using IDisposable store = ScopeStoreFixture.UseStore(root);
        try
        {
            // A directory where the state file goes: LoadCurrent reads it as no state, and SaveCurrent cannot replace it.
            Directory.CreateDirectory(Path.Combine(root, "current", "ZOA.json"));

            MainViewModel vm = await ScopeTestFixture.MainAsync();
            vm.ApplyScenarioBootstrap(Bootstrap("S1", Config(Sector44)));

            Assert.Equal(55, vm.PrimaryScope.CapturePrefSet(null).RangeNm);
            Assert.NotNull(RadarSettings()["S1"]);
        }
        finally
        {
            ScopeStoreFixture.DeleteStoreRoot(root);
        }
    }

    [AvaloniaFact]
    public async Task NullStore_DoesNothing()
    {
        WritePreferences(("S1", new JsonObject { ["rangeNm"] = 55 }));
        Func<(ScopePrefSetStore?, ScopeProfileStore?)> previous = MainViewModel.ScopeStoreFactory;
        MainViewModel.ScopeStoreFactory = () => (null, null);
        try
        {
            MainViewModel vm = await ScopeTestFixture.MainAsync();
            vm.ApplyScenarioBootstrap(Bootstrap("S1", Config(Sector44)));

            Assert.NotNull(RadarSettings()["S1"]);
        }
        finally
        {
            MainViewModel.ScopeStoreFactory = previous;
        }
    }

    private static ActivePositionDto Position(string facilityId = "ZOA", string? trackFacilityId = null) =>
        Sector44 with
        {
            FacilityId = facilityId,
            TrackFacilityId = trackFacilityId,
        };

    private static PositionDisplayConfigDto Config(ActivePositionDto position) => new([], [], [], "C44", position, 3, NoStarsDisplay: true);

    private static FacilityVideoMapsDto MapsDto() =>
        new(
            "ZOA",
            "ZOA",
            [new StarsAreaDto("A", "Area A", 38.07, -121.0, 60, [])],
            [MapInfo("map-a", "AAA", "A", 101), MapInfo("map-b", "BBB", "B", 102)],
            [new MapGroupDto([101, 102], [])]
        );

    private static VideoMapInfoDto MapInfo(string id, string shortName, string brightness, int starsId) =>
        new(id, $"{shortName} full name", shortName, [], brightness, starsId, false, false);

    private static ScenarioBootstrap Bootstrap(string scenarioId, PositionDisplayConfigDto? config) =>
        new()
        {
            ScenarioId = scenarioId,
            ScenarioName = null,
            PrimaryAirportId = "OAK",
            PositionDisplayConfig = config,
            FlightStripsConfig = null,
            Aircraft = [],
            ElapsedSeconds = 0,
            SessionStartUtc = null,
        };

    private static async Task<MainViewModel> LoadAsync(string scenarioId, ActivePositionDto? position = null)
    {
        MainViewModel vm = await ScopeTestFixture.MainAsync();
        vm.ApplyScenarioBootstrap(Bootstrap(scenarioId, Config(position ?? Sector44)));
        return vm;
    }

    private static void WritePreferences(params (string ScenarioId, JsonObject Settings)[] entries)
    {
        var radar = new JsonObject();
        foreach ((string id, JsonObject settings) in entries)
        {
            radar[id] = settings;
        }

        File.WriteAllText(UserPreferences.FilePath, new JsonObject { ["preferencesVersion"] = 1, ["radarSettings"] = radar }.ToJsonString());
    }

    private static JsonObject RadarSettings() =>
        JsonNode.Parse(File.ReadAllText(UserPreferences.FilePath))!.AsObject()["radarSettings"]?.AsObject() ?? [];

    private static ScopePrefSet? SavedState(string root) => ScopeStoreFixture.Store(root).LoadCurrent("ZOA", ScopePrefSetStore.PrimarySlot);
}
