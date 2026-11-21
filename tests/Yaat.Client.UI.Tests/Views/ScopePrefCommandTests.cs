using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.Logging;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Prefs;
using Yaat.Client.Views.Scope.Toolbar;
using Yaat.Sim.Data;
using static Yaat.Client.UI.Tests.Helpers.ScopeTestFixture;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The <c>.scope pref</c> verbs: a Scope captures its display state as a pref set and applies one back, and save, saveas,
/// load, rename and delete work per facility through the pref-set store, each echoing once.
/// </summary>
public class ScopePrefCommandTests : IDisposable
{
    private const string BadName = "Bad pref set name: x/y (1-16 of A-Z 0-9 - _)";

    private const string SaveUsage = "No pref set is active: use .scope pref saveas <name>";

    private const string PrefUsage =
        "Usage: .scope pref save | .scope pref saveas <name> | .scope pref load <name>"
        + " | .scope pref rename <old> <new> | .scope pref delete <name>";

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

    private readonly string _root = Path.Combine(Path.GetTempPath(), "yaat-prefcmd-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task ADecimalRange_RoundTripsThroughAPrefSet()
    {
        (MainViewModel main, ScopeViewModel scope) = await SeededScopeAsync();
        try
        {
            main.RunScopeCommand(".scope range 0.5");
            main.RunScopeCommand(".scope pref saveas DEC");
            main.RunScopeCommand(".scope range 100");

            main.RunScopeCommand(".scope pref load dec");

            Assert.Equal(0.5, scope.RangeNm);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task SaveAsThenLoad_RoundTripsEveryValue()
    {
        (MainViewModel main, ScopeViewModel scope) = await SeededScopeAsync();
        try
        {
            main.RunScopeCommand(".scope range 25");
            main.RunScopeCommand(".scope brite fdb 80");
            main.RunScopeCommand(".scope db char 16");
            main.RunScopeCommand(".scope template eram");
            main.RunScopeCommand(".scope map 2 on");
            main.RunViewCommand(".view ca tear br 10 20");
            main.RunScopeCommand(".scope tearoff range tl 30 40");

            main.RunScopeCommand(".scope pref saveas SR-DAY");
            Assert.Equal(".scope pref saveas SR-DAY", main.StatusText);
            Assert.Equal("SR-DAY", scope.ActivePrefSet);

            main.RunScopeCommand(".scope range 100");
            main.RunScopeCommand(".scope brite fdb 30");
            main.RunScopeCommand(".scope db char 10");
            main.RunScopeCommand(".scope template stars");
            main.RunScopeCommand(".scope map 2 off");
            main.RunViewCommand(".view ca dock");
            main.RunScopeCommand(".scope tearoff range br 1 1");

            main.RunScopeCommand(".scope pref load sr-day");

            Assert.Equal(".scope pref load SR-DAY", main.StatusText);
            Assert.Equal("SR-DAY", scope.ActivePrefSet);
            Assert.Equal(25, scope.RangeNm);
            Assert.Equal(80, scope.Brightness.Fdb);
            Assert.Equal(16, scope.DbSettings.CharSize);
            Assert.Equal(ScopeTemplates.EramId, scope.TemplateId);
            Assert.True(scope.MapToggles.Single(t => t.StarsId == 2).IsOn);
            Assert.Equal(new ScopePlacement(ScopeCorner.BottomRight, 10, 20), scope.FloatingViews.Single(v => v.Name == "ca").Place);
            Assert.Equal(new ScopePlacement(ScopeCorner.TopLeft, 30, 40), scope.Tearoffs.Single(t => t.Key.Id == ScopeButtonId.Range).Place);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task Load_ClosesUnnamedViews_AndReplacesTearoffs()
    {
        (MainViewModel main, ScopeViewModel scope) = await SeededScopeAsync();
        try
        {
            main.RunScopeCommand(".scope tearoff range tl 10 10");
            main.RunScopeCommand(".scope pref saveas BASE");

            main.RunViewCommand(".view ca on");
            main.RunScopeCommand(".scope tearoff brite br 5 5");
            Assert.True(scope.IsViewOpen("ca"));
            Assert.Equal([ScopeButtonId.Range, ScopeButtonId.Brite], scope.Tearoffs.Select(t => t.Key.Id));

            main.RunScopeCommand(".scope pref load BASE");

            Assert.False(scope.IsViewOpen("ca"));
            Assert.Equal([ScopeButtonId.Range], scope.Tearoffs.Select(t => t.Key.Id));
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task Load_SetNamingAMissingMap_LoadsTheRest_AndSaysHowMany()
    {
        (MainViewModel main, ScopeViewModel scope) = await SeededScopeAsync();
        try
        {
            main.RunScopeCommand(".scope pref saveas TWO");
            ScopePrefSetStore store = Store();
            store.SaveSet("ZOA", store.FindSet("ZOA", "TWO")! with { Maps = [2, 999] });
            main.RunScopeCommand(".scope map 2 off");

            main.RunScopeCommand(".scope pref load TWO");

            Assert.Equal(".scope pref load TWO (1 map not on this Radar)", main.StatusText);
            Assert.True(scope.MapToggles.Single(t => t.StarsId == 2).IsOn);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task Load_WhileWaitingForSeed_HoldsTheMaps_AndTheyWinOverTheSeed()
    {
        (MainViewModel saver, ScopeViewModel saverScope) = await SeededScopeAsync();
        try
        {
            saver.RunScopeCommand(".scope range 25");
            saver.RunScopeCommand(".scope map 2 off");
            saver.RunScopeCommand(".scope map 31 on");
            saver.RunScopeCommand(".scope pref saveas HOLD");
        }
        finally
        {
            saverScope.Dispose();
        }

        MainViewModel main = await MainAsync();
        main.SetActivePositionFromServer(EramConfig());
        using var scope = new ScopeViewModel(main, Store(), profileStore: null, ScopePrefSetStore.PrimarySlot);
        main.OpenScope = scope;
        Assert.True(scope.IsWaitingForMapSource);

        main.RunScopeCommand(".scope pref load HOLD");

        Assert.Equal(".scope pref load HOLD (its maps apply once the Radar's maps load)", main.StatusText);

        SeedSource(main, 40.0, -100.0, "m2");

        Assert.False(scope.IsWaitingForMapSource);
        Assert.Equal(25, scope.RangeNm);
        Assert.Equal((37.62, -122.38), (scope.CenterLat, scope.CenterLon));
        Assert.True(scope.MapToggles.Single(t => t.StarsId == 31).IsOn);
        Assert.False(scope.MapToggles.Single(t => t.StarsId == 2).IsOn);
    }

    [AvaloniaFact]
    public async Task NoStore_SaysPrefSetsAreUnavailable()
    {
        MainViewModel main = await MainAsync();
        using var scope = new ScopeViewModel(main, prefStore: null, profileStore: null, ScopePrefSetStore.PrimarySlot);
        main.OpenScope = scope;

        main.RunScopeCommand(".scope pref saveas SR");

        Assert.Equal("Pref sets are not available", main.StatusText);
        Assert.Null(scope.ActivePrefSet);
    }

    [AvaloniaFact]
    public async Task NoFacility_SaysPrefSetsNeedAnActivePosition_AndLeavesTheState()
    {
        MainViewModel main = await MainAsync();
        using var scope = new ScopeViewModel(main, Store(), profileStore: null, ScopePrefSetStore.PrimarySlot);
        main.OpenScope = scope;
        Assert.Null(scope.Viewer.EramFacilityId);

        main.RunScopeCommand(".scope pref load SR");

        Assert.Equal("No facility: pref sets need an active position", main.StatusText);
        Assert.Null(scope.ActivePrefSet);
        Assert.Equal(ScopeViewModel.DefaultRangeNm, scope.RangeNm);
    }

    [AvaloniaFact]
    public async Task SaveAs_ATakenName_SaysItExists()
    {
        (MainViewModel main, ScopeViewModel scope) = await SeededScopeAsync();
        try
        {
            main.RunScopeCommand(".scope range 25");
            main.RunScopeCommand(".scope pref saveas SR-DAY");
            main.RunScopeCommand(".scope range 100");
            main.RunScopeCommand(".scope pref saveas sr-day");

            Assert.Equal("Pref set exists: SR-DAY", main.StatusText);
            Assert.Equal(25, Store().FindSet("ZOA", "SR-DAY")!.RangeNm);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task SaveAs_WithTooManyParts_GivesTheUsage()
    {
        (MainViewModel main, ScopeViewModel scope) = await SeededScopeAsync();
        try
        {
            main.RunScopeCommand(".scope pref saveas a b c");

            Assert.Equal(PrefUsage, main.StatusText);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task SaveAs_WithABadName_SaysWhatANameIs()
    {
        (MainViewModel main, ScopeViewModel scope) = await SeededScopeAsync();
        try
        {
            main.RunScopeCommand(".scope pref saveas x/y");

            Assert.Equal(BadName, main.StatusText);
            Assert.Null(scope.ActivePrefSet);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task Load_AnUnknownSet_SaysSo_AndKeepsTheState()
    {
        (MainViewModel main, ScopeViewModel scope) = await SeededScopeAsync();
        try
        {
            main.RunScopeCommand(".scope range 25");

            main.RunScopeCommand(".scope pref load nosuch");

            Assert.Equal("Unknown pref set: nosuch", main.StatusText);
            Assert.Equal(25, scope.RangeNm);
            Assert.Null(scope.ActivePrefSet);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task Rename_ToATakenName_SaysItExists()
    {
        (MainViewModel main, ScopeViewModel scope) = await SeededScopeAsync();
        try
        {
            main.RunScopeCommand(".scope pref saveas ONE");
            main.RunScopeCommand(".scope range 50");
            main.RunScopeCommand(".scope pref saveas TWO");

            main.RunScopeCommand(".scope pref rename ONE TWO");

            Assert.Equal("Pref set exists: TWO", main.StatusText);
            Assert.Equal("TWO", scope.ActivePrefSet);
            Assert.Equal(40, Store().FindSet("ZOA", "ONE")!.RangeNm);
            Assert.Equal(50, Store().FindSet("ZOA", "TWO")!.RangeNm);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task Delete_AnUnknownSet_SaysSo()
    {
        (MainViewModel main, ScopeViewModel scope) = await SeededScopeAsync();
        try
        {
            main.RunScopeCommand(".scope pref delete nosuch");

            Assert.Equal("Unknown pref set: nosuch", main.StatusText);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task Save_WithNoActiveSet_SaysHowToNameOne()
    {
        (MainViewModel main, ScopeViewModel scope) = await SeededScopeAsync();
        try
        {
            main.RunScopeCommand(".scope pref save");

            Assert.Equal(SaveUsage, main.StatusText);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task Rename_MovesTheActiveName()
    {
        (MainViewModel main, ScopeViewModel scope) = await SeededScopeAsync();
        try
        {
            main.RunScopeCommand(".scope pref saveas OLD");

            main.RunScopeCommand(".scope pref rename OLD NEW");

            Assert.Equal(".scope pref rename OLD NEW", main.StatusText);
            Assert.Equal("NEW", scope.ActivePrefSet);
            Assert.Null(Store().FindSet("ZOA", "OLD"));
            Assert.NotNull(Store().FindSet("ZOA", "NEW"));
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task Delete_ClearsTheActiveName()
    {
        (MainViewModel main, ScopeViewModel scope) = await SeededScopeAsync();
        try
        {
            main.RunScopeCommand(".scope pref saveas GONE");

            main.RunScopeCommand(".scope pref delete GONE");

            Assert.Equal(".scope pref delete GONE", main.StatusText);
            Assert.Null(scope.ActivePrefSet);
            Assert.Null(Store().FindSet("ZOA", "GONE"));
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task Load_NeverWritesThePreferences()
    {
        (MainViewModel main, ScopeViewModel scope) = await SeededScopeAsync();
        try
        {
            UserPreferences prefs = main.Preferences;
            prefs.SetShowAtpa(true);
            int charSize = prefs.RadarDatablockFontSize == 16 ? 20 : 16;
            main.RunScopeCommand($".scope template {ScopeTemplates.EuroScopeId}");
            main.RunScopeCommand($".scope db atpa {(prefs.ShowAtpa ? "off" : "on")}");
            main.RunScopeCommand($".scope db char {charSize}");
            main.RunScopeCommand(".scope pref saveas EURO-SET");

            main.RunScopeCommand($".scope template {ScopeTemplates.StarsId}");
            main.RunScopeCommand(".scope db atpa on");
            main.RunScopeCommand(".scope db char 10");
            bool euroScopeMode = prefs.EuroScopeMode;
            bool showAtpa = prefs.ShowAtpa;
            int fontSize = prefs.RadarDatablockFontSize;
            byte[] before = File.ReadAllBytes(UserPreferences.FilePath);

            main.RunScopeCommand(".scope pref load EURO-SET");

            Assert.Equal(ScopeTemplates.EuroScopeId, scope.TemplateId);
            Assert.False(scope.DbSettings.Atpa);
            Assert.Equal(charSize, scope.DbSettings.CharSize);
            Assert.Equal(euroScopeMode, prefs.EuroScopeMode);
            Assert.Equal(showAtpa, prefs.ShowAtpa);
            Assert.Equal(fontSize, prefs.RadarDatablockFontSize);
            Assert.Equal(before, File.ReadAllBytes(UserPreferences.FilePath));
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task Load_LeavesDwellLocksAlone_AndTheFileHoldsNone()
    {
        (MainViewModel main, ScopeViewModel scope) = await SeededScopeAsync();
        try
        {
            scope.ToggleDwellLock("UAL238");
            main.RunScopeCommand(".scope pref saveas FIRST");
            main.RunScopeCommand(".scope pref saveas SECOND");

            main.RunScopeCommand(".scope pref load SECOND");

            Assert.Contains("UAL238", scope.DataBlockState.DwellLockedCallsigns);
            Assert.DoesNotContain("UAL238", File.ReadAllText(Path.Combine(_root, "prefsets", "ZOA.json")));
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task Load_AnUnknownTemplate_KeepsTheCurrentTemplate()
    {
        (MainViewModel main, ScopeViewModel scope) = await SeededScopeAsync();
        try
        {
            SaveBroken(main, set => set with { TemplateId = "nope" });
            main.RunScopeCommand($".scope template {ScopeTemplates.EramId}");

            main.RunScopeCommand(".scope pref load BAD");

            Assert.Equal(ScopeTemplates.EramId, scope.TemplateId);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task Load_AnOutOfRangeCharSize_KeepsTheCurrentSize()
    {
        (MainViewModel main, ScopeViewModel scope) = await SeededScopeAsync();
        try
        {
            SaveBroken(main, set => set with { Db = set.Db with { CharSize = 99 } });
            main.RunScopeCommand(".scope db char 16");

            main.RunScopeCommand(".scope pref load BAD");

            Assert.Equal(16, scope.DbSettings.CharSize);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task Load_ARangeOfZero_KeepsTheCurrentRange()
    {
        (MainViewModel main, ScopeViewModel scope) = await SeededScopeAsync();
        try
        {
            SaveBroken(main, set => set with { RangeNm = 0 });
            main.RunScopeCommand(".scope range 30");

            main.RunScopeCommand(".scope pref load BAD");

            Assert.Equal(30, scope.RangeNm);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task Load_ARangeAboveTheMaximum_KeepsTheCurrentRange()
    {
        (MainViewModel main, ScopeViewModel scope) = await SeededScopeAsync();
        try
        {
            SaveBroken(main, set => set with { RangeNm = 300 });
            main.RunScopeCommand(".scope range 30");

            main.RunScopeCommand(".scope pref load BAD");

            Assert.Equal(30, scope.RangeNm);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task Load_ANoCentreOfZeros_KeepsTheCurrentCentre()
    {
        (MainViewModel main, ScopeViewModel scope) = await SeededScopeAsync();
        try
        {
            SaveBroken(main, set => set with { CenterLat = 0, CenterLon = 0 });

            main.RunScopeCommand(".scope pref load BAD");

            Assert.Equal((37.62, -122.38), (scope.CenterLat, scope.CenterLon));
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task Load_AnOutOfRangeBriteValue_KeepsTheCurrentValue()
    {
        (MainViewModel main, ScopeViewModel scope) = await SeededScopeAsync();
        try
        {
            SaveBroken(main, set => set with { Brightness = new Dictionary<string, int> { ["fdb"] = 101 } });
            main.RunScopeCommand(".scope brite fdb 80");

            main.RunScopeCommand(".scope pref load BAD");

            Assert.Equal(80, scope.Brightness.Fdb);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task Load_AnUnknownBriteGroup_LeavesTheBrightnessAlone()
    {
        (MainViewModel main, ScopeViewModel scope) = await SeededScopeAsync();
        try
        {
            SaveBroken(main, set => set with { Brightness = new Dictionary<string, int> { ["nope"] = 50 } });
            ScopeBrightness before = scope.Brightness;

            main.RunScopeCommand(".scope pref load BAD");

            Assert.Equal(before, scope.Brightness);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task Load_ASetHoldingANullTearoffAndViewPlace_AppliesTheRest()
    {
        (MainViewModel main, ScopeViewModel scope) = await SeededScopeAsync();
        try
        {
            main.RunScopeCommand(".scope pref saveas NULLS");
            ScopePrefSetStore store = Store();
            store.SaveSet("ZOA", store.FindSet("ZOA", "NULLS")! with { RangeNm = 25, Tearoffs = [null!], ViewPlaces = [null!] });
            main.RunScopeCommand(".scope range 100");

            main.RunScopeCommand(".scope pref load NULLS");

            Assert.Equal(".scope pref load NULLS", main.StatusText);
            Assert.Equal(25, scope.RangeNm);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task Load_ANegativeViewOffset_ClampsToZero()
    {
        (MainViewModel main, ScopeViewModel scope) = await SeededScopeAsync();
        try
        {
            main.RunScopeCommand(".scope pref saveas NEG");
            ScopePrefSetStore store = Store();
            store.SaveSet(
                "ZOA",
                store.FindSet("ZOA", "NEG")! with
                {
                    OpenViews = ["ssa", "ca"],
                    ViewPlaces = [new ScopePrefView("ssa", "tl", 8, 8), new ScopePrefView("ca", "br", -5, -5)],
                }
            );

            main.RunScopeCommand(".scope pref load NEG");

            Assert.Equal(new ScopePlacement(ScopeCorner.BottomRight, 0, 0), scope.FloatingViews.Single(v => v.Name == "ca").Place);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task Load_SetNamingTwoMissingMaps_SaysMaps()
    {
        (MainViewModel main, ScopeViewModel scope) = await SeededScopeAsync();
        try
        {
            main.RunScopeCommand(".scope pref saveas TWO");
            ScopePrefSetStore store = Store();
            store.SaveSet("ZOA", store.FindSet("ZOA", "TWO")! with { Maps = [998, 999] });

            main.RunScopeCommand(".scope pref load TWO");

            Assert.Equal(".scope pref load TWO (2 maps not on this Radar)", main.StatusText);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task Load_WhileWaiting_WithASourceOfNoMaps_AppliesTheRangeAndLeavesTheMapsEmpty()
    {
        (MainViewModel saver, ScopeViewModel saverScope) = await SeededScopeAsync();
        try
        {
            saver.RunScopeCommand(".scope range 25");
            saver.RunScopeCommand(".scope pref saveas HOLD");
        }
        finally
        {
            saverScope.Dispose();
        }

        MainViewModel main = await MainAsync();
        main.SetActivePositionFromServer(EramConfig());
        using var scope = new ScopeViewModel(main, Store(), profileStore: null, ScopePrefSetStore.PrimarySlot);
        main.OpenScope = scope;
        main.RunScopeCommand(".scope pref load HOLD");
        Assert.Equal(".scope pref load HOLD (its maps apply once the Radar's maps load)", main.StatusText);

        main.ScopeMaps.ApplyVideoMapsDtoForTesting(
            new FacilityVideoMapsDto("ZOA", "NCT", [new StarsAreaDto("A", "Area A", 40.0, -100.0, 60, [])], [], []),
            null,
            null
        );

        Assert.False(scope.IsWaitingForMapSource);
        Assert.Empty(scope.MapToggles);
        Assert.Empty(scope.VideoMaps);
        Assert.Equal(25, scope.RangeNm);
    }

    [AvaloniaFact]
    public async Task SaveAs_WhenTheStoreCannotWrite_SaysWhy()
    {
        (MainViewModel main, ScopeViewModel scope) = await SeededScopeAsync();
        try
        {
            main.RunScopeCommand(".scope pref saveas FIRST");

            using (new FileStream(Path.Combine(_root, "prefsets", "ZOA.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                main.RunScopeCommand(".scope pref saveas SECOND");
            }

            Assert.StartsWith("Could not save pref sets: ", main.StatusText);
            Assert.Equal("FIRST", scope.ActivePrefSet);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task BarePrefAndAnUnknownSubverb_GiveTheUsage()
    {
        (MainViewModel main, ScopeViewModel scope) = await SeededScopeAsync();
        try
        {
            main.RunScopeCommand(".scope pref");
            Assert.Equal(PrefUsage, main.StatusText);

            main.RunScopeCommand(".scope pref bogus");
            Assert.Equal(PrefUsage, main.StatusText);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task Rename_OfAMissingOldName_SaysUnknown()
    {
        (MainViewModel main, ScopeViewModel scope) = await SeededScopeAsync();
        try
        {
            main.RunScopeCommand(".scope pref rename NOSUCH NEW");

            Assert.Equal("Unknown pref set: NOSUCH", main.StatusText);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task Rename_ToABadNewName_SaysWhatANameIs()
    {
        (MainViewModel main, ScopeViewModel scope) = await SeededScopeAsync();
        try
        {
            main.RunScopeCommand(".scope pref saveas OLD");

            main.RunScopeCommand(".scope pref rename OLD x/y");

            Assert.Equal(BadName, main.StatusText);
            Assert.Equal("OLD", scope.ActivePrefSet);
            Assert.NotNull(Store().FindSet("ZOA", "OLD"));
        }
        finally
        {
            scope.Dispose();
        }
    }

    /// <summary>Saves the scope's state as BAD, then replaces that set in the store with <paramref name="change"/>'s result.</summary>
    private void SaveBroken(MainViewModel main, Func<ScopePrefSet, ScopePrefSet> change)
    {
        main.RunScopeCommand(".scope pref saveas BAD");
        ScopePrefSetStore store = Store();
        store.SaveSet("ZOA", change(store.FindSet("ZOA", "BAD")!));
    }

    private static PositionDisplayConfigDto EramConfig() => new([], [], [], "C44", Sector44, 3, NoStarsDisplay: true);

    private static VideoMapData Map(string id) => new() { MapId = id, Lines = [] };

    /// <summary>
    /// Loads the Scope's map source as a scenario load does: maps 31, 2 and 10 (in that list order), centred at
    /// <paramref name="lat"/>/<paramref name="lon"/>. <paramref name="cachedMaps"/> go in the video-map cache, so they draw
    /// once turned on; every map starts off.
    /// </summary>
    private static void SeedSource(MainViewModel main, double lat, double lon, params string[] cachedMaps)
    {
        foreach (string id in cachedMaps)
        {
            main.CacheVideoMapForTesting(Map(id));
        }

        main.ScopeMaps.ApplyVideoMapsDtoForTesting(Facility(lat, lon), null, null);
    }

    /// <summary>A facility with maps 31, 2 and 10 (in that list order) and one STARS area at <paramref name="lat"/>/<paramref name="lon"/>.</summary>
    private static FacilityVideoMapsDto Facility(double lat, double lon) =>
        new("ZOA", "NCT", [new StarsAreaDto("A", "Area A", lat, lon, 60, [])], [MapInfo(31), MapInfo(2), MapInfo(10)], []);

    private static VideoMapInfoDto MapInfo(int starsId) =>
        new($"m{starsId}", $"Map {starsId}", $"MAP{starsId}", [], "A", starsId, AlwaysVisible: false, TdmOnly: false);

    /// <summary>
    /// A seeded scope with set HOLD saved at 25 NM, 37.62/-122.38, with map 31 on, then moved off it (50 NM, 39/-110, map 2
    /// on), and a load of the map source begun; returns the load's token.
    /// </summary>
    private async Task<(MainViewModel Main, ScopeViewModel Scope, int Load)> MidLoadAfterLeavingHoldAsync()
    {
        (MainViewModel main, ScopeViewModel scope) = await SeededScopeAsync();
        main.RunScopeCommand(".scope range 25");
        main.RunScopeCommand(".scope map 2 off");
        main.RunScopeCommand(".scope map 31 on");
        main.RunScopeCommand(".scope pref saveas HOLD");
        main.RunScopeCommand(".scope range 50");
        main.RunScopeCommand(".scope map 31 off");
        main.RunScopeCommand(".scope map 2 on");
        scope.CenterLat = 39.0;
        scope.CenterLon = -110.0;
        return (main, scope, main.ScopeMaps.BeginLoadForTesting());
    }

    [AvaloniaFact]
    public async Task PrefSaveAs_MidLoad_SavesTheHeldMapsRangeAndCentre()
    {
        (MainViewModel main, ScopeViewModel scope, _) = await MidLoadAfterLeavingHoldAsync();
        try
        {
            main.RunScopeCommand(".scope pref load HOLD");
            Assert.Equal(50, scope.RangeNm);

            main.RunScopeCommand(".scope pref saveas X");

            ScopePrefSet saved = Store().FindSet("ZOA", "X")!;
            Assert.Equal(25, saved.RangeNm);
            Assert.Equal((37.62, -122.38), (saved.CenterLat, saved.CenterLon));
            Assert.Equal([31], saved.Maps);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task PrefLoad_MidLoad_AppliesAfterTheReseed()
    {
        (MainViewModel main, ScopeViewModel scope, int load) = await MidLoadAfterLeavingHoldAsync();
        try
        {
            main.RunScopeCommand(".scope pref load HOLD");
            Assert.Equal(".scope pref load HOLD (its maps apply once the Radar's maps load)", main.StatusText);
            Assert.Equal(50, scope.RangeNm);

            main.ScopeMaps.FinishLoadForTesting(load, Facility(38.0, -121.0), null);

            Assert.Equal(25, scope.RangeNm);
            Assert.Equal((37.62, -122.38), (scope.CenterLat, scope.CenterLon));
            Assert.Equal([31], scope.MapToggles.Where(t => t.IsOn).Select(t => t.StarsId));
            Assert.Equal("HOLD", scope.ActivePrefSet);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task PrefLoad_MidLoad_ThatEndsWithNoMaps_AppliesAgainstTheCurrentToggles()
    {
        (MainViewModel main, ScopeViewModel scope, int load) = await MidLoadAfterLeavingHoldAsync();
        try
        {
            main.RunScopeCommand(".scope pref load HOLD");
            Assert.Equal(50, scope.RangeNm);

            main.ScopeMaps.FinishLoadForTesting(load, null, null);

            Assert.False(scope.IsWaitingForMapSource);
            Assert.Equal(25, scope.RangeNm);
            Assert.Equal((37.62, -122.38), (scope.CenterLat, scope.CenterLon));
            Assert.Equal([31], scope.MapToggles.Where(t => t.IsOn).Select(t => t.StarsId));
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task PrefLoad_MidLoad_ThenClear_KeepsTheSetForTheNextSeed()
    {
        (MainViewModel main, ScopeViewModel scope, int load) = await MidLoadAfterLeavingHoldAsync();
        try
        {
            main.RunScopeCommand(".scope pref load HOLD");

            main.ScopeMaps.Clear();
            main.ScopeMaps.FinishLoadForTesting(load, Facility(38.0, -121.0), null);

            Assert.Equal(50, scope.RangeNm);
            Assert.Empty(scope.MapToggles);

            SeedSource(main, 38.0, -121.0);

            Assert.Equal(25, scope.RangeNm);
            Assert.Equal((37.62, -122.38), (scope.CenterLat, scope.CenterLon));
            Assert.Equal([31], scope.MapToggles.Where(t => t.IsOn).Select(t => t.StarsId));
        }
        finally
        {
            scope.Dispose();
        }
    }

    /// <summary>A scope at ZOA with maps 31, 2 and 10, map 2 on, at 37.62/-122.38 and 40 NM.</summary>
    private async Task<(MainViewModel Main, ScopeViewModel Scope)> SeededScopeAsync()
    {
        MainViewModel main = await MainAsync();
        main.SetActivePositionFromServer(EramConfig());
        SeedSource(main, 37.62, -122.38, "m2");
        var scope = new ScopeViewModel(main, Store(), profileStore: null, ScopePrefSetStore.PrimarySlot);
        main.OpenScope = scope;
        scope.SetMapOn(2, true);
        return (main, scope);
    }

    private ScopePrefSetStore Store() => new(_root, AppLog.CreateLogger<ScopePrefSetStore>());
}
