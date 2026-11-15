using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.Logging;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Prefs;
using Yaat.Client.Views.Scope.Toolbar;
using Yaat.Sim.Data;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The Scope's own saved state: it saves its display state per facility when it is disposed and when the facility
/// changes, and restores a facility's saved state when the Scope opens and whenever the facility changes.
/// </summary>
public class ScopeSavedStateTests : IDisposable
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

    private static readonly ActivePositionDto NctSector1 = new(
        Callsign: "NCT_1_CTR",
        Name: "Sector 1",
        RadioName: null,
        Frequency: "120.300",
        FacilityId: "NCT",
        FacilityName: "Northern California Approach",
        PositionType: "Tracon",
        HandoffCode: "N1",
        EramSectorId: "1"
    );

    private readonly string _root = Path.Combine(Path.GetTempPath(), "yaat-scope-state-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task Dispose_ThenReopen_RestoresTheStateAndTheViewsPlaces()
    {
        (MainViewModel main, ScopeViewModel first) = await SeededScopeAsync();
        main.RunScopeCommand(".scope range 25");
        main.RunScopeCommand(".scope map 2 off");
        main.RunScopeCommand(".scope map 31 on");
        main.RunViewCommand(".view ca tear br 10 20");
        main.RunViewCommand(".view ssa tear br 5 6");
        main.RunScopeCommand(".scope tearoff range tl 30 40");
        first.Dispose();

        ScopeViewModel reopened = Reopen(main);
        try
        {
            Assert.Equal(25, reopened.RangeNm);
            Assert.Equal(new ScopePlacement(ScopeCorner.BottomRight, 10, 20), reopened.FloatingViews.Single(v => v.Name == "ca").Place);
            Assert.Equal(new ScopePlacement(ScopeCorner.BottomRight, 5, 6), reopened.FloatingViews.Single(v => v.Name == "ssa").Place);
            Assert.Equal(new ScopePlacement(ScopeCorner.TopLeft, 30, 40), reopened.Tearoffs.Single(t => t.Key.Id == ScopeButtonId.Range).Place);
            Assert.True(reopened.MapToggles.Single(t => t.StarsId == 31).IsOn);
            Assert.False(reopened.MapToggles.Single(t => t.StarsId == 2).IsOn);
        }
        finally
        {
            reopened.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task Reopen_ShowsTheActiveSetName_AndClearsItOnceTheSetIsDeleted()
    {
        (MainViewModel main, ScopeViewModel first) = await SeededScopeAsync();
        main.RunScopeCommand(".scope pref saveas SR-DAY");
        first.Dispose();

        ScopeViewModel second = Reopen(main);
        try
        {
            Assert.Equal("SR-DAY", second.ActivePrefSet);
            main.RunScopeCommand(".scope pref delete SR-DAY");
        }
        finally
        {
            second.Dispose();
        }

        ScopeViewModel third = Reopen(main);
        try
        {
            Assert.Null(third.ActivePrefSet);
        }
        finally
        {
            third.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task FacilityChange_SavesTheOldFacilitysStateAndRestoresTheNewOnes()
    {
        MainViewModel nctMain = await MainAsync();
        nctMain.SetActivePositionFromServer(Config(NctSector1));
        SeedRadar(nctMain, 40.0, -100.0, "m2");
        var nctScope = new ScopeViewModel(nctMain, Store());
        nctMain.OpenScope = nctScope;
        nctMain.RunScopeCommand(".scope range 80");
        nctScope.Dispose();

        (MainViewModel main, ScopeViewModel scope) = await SeededScopeAsync();
        try
        {
            main.RunScopeCommand(".scope range 25");

            main.SetActivePositionFromServer(Config(NctSector1));

            Assert.Equal("NCT", scope.Viewer.EramFacilityId);
            Assert.Equal(25, Store().LoadCurrent("ZOA")!.RangeNm);
            Assert.Equal(80, scope.RangeNm);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task FacilityChange_ToAFacilityWithNoState_KeepsTheScreenAndClearsTheActiveSet()
    {
        (MainViewModel main, ScopeViewModel scope) = await SeededScopeAsync();
        try
        {
            main.RunScopeCommand(".scope range 25");
            main.RunScopeCommand(".scope pref saveas SR-DAY");

            main.SetActivePositionFromServer(Config(NctSector1));

            Assert.Equal("NCT", scope.Viewer.EramFacilityId);
            Assert.Equal(25, scope.RangeNm);
            Assert.Null(scope.ActivePrefSet);
            Assert.Equal(25, Store().LoadCurrent("ZOA")!.RangeNm);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task FacilityChange_ToTheSameFacility_SavesAndRestoresNothing()
    {
        (MainViewModel main, ScopeViewModel first) = await SeededScopeAsync();
        main.RunScopeCommand(".scope range 25");
        first.Dispose();

        ScopeViewModel scope = Reopen(main);
        try
        {
            Assert.Equal(25, scope.RangeNm);
            main.RunScopeCommand(".scope range 50");

            main.SetActivePositionFromServer(Config(Sector44 with { Callsign = "OAK_14_CTR", HandoffCode = "C14" }));

            Assert.Equal("ZOA", scope.Viewer.EramFacilityId);
            Assert.Equal(50, scope.RangeNm);
            Assert.Equal(25, Store().LoadCurrent("ZOA")!.RangeNm);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task FacilityChange_ToNoFacility_SavesTheOldOneAndKeepsTheScreen()
    {
        (MainViewModel main, ScopeViewModel first) = await SeededScopeAsync();
        main.RunScopeCommand(".scope range 25");
        first.Dispose();

        ScopeViewModel scope = Reopen(main);
        try
        {
            main.RunScopeCommand(".scope range 50");

            main.SetActivePositionFromServer(null);

            Assert.Null(scope.Viewer.EramFacilityId);
            Assert.Equal(50, scope.RangeNm);
            Assert.Equal(50, Store().LoadCurrent("ZOA")!.RangeNm);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task NoFacilityThenAFacility_RestoresTheNewFacilitysState()
    {
        (MainViewModel saver, ScopeViewModel saverScope) = await SeededScopeAsync();
        saver.RunScopeCommand(".scope range 25");
        saverScope.Dispose();

        MainViewModel main = await MainAsync();
        SeedRadar(main, 30.0, -90.0, "m2");
        var scope = new ScopeViewModel(main, Store());
        main.OpenScope = scope;
        try
        {
            Assert.Null(scope.Viewer.EramFacilityId);
            Assert.Equal(60, scope.RangeNm);

            main.SetActivePositionFromServer(Config(Sector44));

            Assert.Equal("ZOA", scope.Viewer.EramFacilityId);
            Assert.Equal(25, scope.RangeNm);
            Assert.Equal((40.0, -100.0), (scope.CenterLat, scope.CenterLon));
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task NoStore_SavesAndRestoresNothing()
    {
        MainViewModel main = await MainAsync();
        main.SetActivePositionFromServer(Config(Sector44));
        SeedRadar(main, 40.0, -100.0, "m2");
        var scope = new ScopeViewModel(main, prefStore: null);
        main.OpenScope = scope;
        try
        {
            main.RunScopeCommand(".scope range 25");
        }
        finally
        {
            scope.Dispose();
        }

        Assert.False(Directory.Exists(_root));
    }

    [AvaloniaFact]
    public async Task NoFacility_SavesAndRestoresNothing()
    {
        MainViewModel main = await MainAsync();
        var scope = new ScopeViewModel(main, Store());
        main.OpenScope = scope;
        try
        {
            main.RunScopeCommand(".scope range 25");
        }
        finally
        {
            scope.Dispose();
        }

        Assert.False(Directory.Exists(_root));
    }

    [AvaloniaFact]
    public async Task Dispose_WhenTheStateCannotBeSaved_LogsAWarningAndDoesNotThrow()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "current"), "");
        MainViewModel main = await MainAsync();
        main.SetActivePositionFromServer(Config(Sector44));
        SeedRadar(main, 40.0, -100.0, "m2");
        ScopeViewModel scope = Reopen(main);
        long mark = new FileInfo(AppLog.LogPath).Length;

        scope.Dispose();

        string logged = ReadLogSince(mark);
        Assert.Contains("[warn]", logged);
        Assert.Contains("Could not save the scope's state for ZOA", logged);
    }

    [AvaloniaFact]
    public async Task Restore_WhileWaitingForTheRadar_AppliesTheSavedStateAfterTheSeed()
    {
        (MainViewModel saver, ScopeViewModel saverScope) = await SeededScopeAsync();
        saver.RunScopeCommand(".scope range 25");
        saver.RunScopeCommand(".scope map 2 off");
        saver.RunScopeCommand(".scope map 31 on");
        saverScope.Dispose();

        MainViewModel main = await MainAsync();
        main.SetActivePositionFromServer(Config(Sector44));
        var scope = new ScopeViewModel(main, Store());
        main.OpenScope = scope;
        try
        {
            Assert.True(scope.IsWaitingForRadarSeed);

            SeedRadar(main, 30.0, -90.0, "m2");

            Assert.False(scope.IsWaitingForRadarSeed);
            Assert.Equal(25, scope.RangeNm);
            Assert.Equal((40.0, -100.0), (scope.CenterLat, scope.CenterLon));
            Assert.True(scope.MapToggles.Single(t => t.StarsId == 31).IsOn);
            Assert.False(scope.MapToggles.Single(t => t.StarsId == 2).IsOn);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task Dispose_ThenReopen_DoesNotRestoreDwellLocks()
    {
        (MainViewModel main, ScopeViewModel first) = await SeededScopeAsync();
        first.ToggleDwellLock("UAL238");
        Assert.Contains("UAL238", first.DataBlockState.DwellLockedCallsigns);
        first.Dispose();

        ScopeViewModel reopened = Reopen(main);
        try
        {
            Assert.DoesNotContain("UAL238", reopened.DataBlockState.DwellLockedCallsigns);
            Assert.DoesNotContain("UAL238", File.ReadAllText(Path.Combine(_root, "current", "ZOA.json")));
        }
        finally
        {
            reopened.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task Reopen_WhenTheNamedSetIsGone_ClearsTheActiveSet_AndRestoresTheRest()
    {
        (MainViewModel main, ScopeViewModel first) = await SeededScopeAsync();
        main.RunScopeCommand(".scope range 25");
        main.RunScopeCommand(".scope pref saveas SR-DAY");
        first.Dispose();

        Assert.True(Store().DeleteSet("ZOA", "SR-DAY"));

        ScopeViewModel reopened = Reopen(main);
        try
        {
            Assert.Null(reopened.ActivePrefSet);
            Assert.Equal(25, reopened.RangeNm);
        }
        finally
        {
            reopened.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task Dispose_AfterAFacilityChange_SavesUnderTheNewFacility()
    {
        (MainViewModel main, ScopeViewModel scope) = await SeededScopeAsync();
        main.RunScopeCommand(".scope range 25");
        main.SetActivePositionFromServer(Config(NctSector1));
        main.RunScopeCommand(".scope range 80");

        scope.Dispose();

        Assert.Equal(25, Store().LoadCurrent("ZOA")!.RangeNm);
        Assert.Equal(80, Store().LoadCurrent("NCT")!.RangeNm);
    }

    [AvaloniaFact]
    public async Task FacilityChange_ThroughNoFacility_ReturnsToTheStateShapedInTheFirstOne()
    {
        (MainViewModel main, ScopeViewModel scope) = await SeededScopeAsync();
        try
        {
            main.RunScopeCommand(".scope range 25");
            main.RunViewCommand(".view ca tear br 10 20");

            main.SetActivePositionFromServer(null);
            Assert.Null(scope.Viewer.EramFacilityId);

            main.SetActivePositionFromServer(Config(Sector44));

            Assert.Equal("ZOA", scope.Viewer.EramFacilityId);
            Assert.Equal(25, scope.RangeNm);
            Assert.Equal(new ScopePlacement(ScopeCorner.BottomRight, 10, 20), scope.FloatingViews.Single(v => v.Name == "ca").Place);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task DisposeTwice_SavesOnlyTheFirstState()
    {
        (MainViewModel main, ScopeViewModel scope) = await SeededScopeAsync();
        main.RunScopeCommand(".scope range 25");
        scope.Dispose();
        main.RunScopeCommand(".scope range 50");

        scope.Dispose();

        Assert.Equal(25, Store().LoadCurrent("ZOA")!.RangeNm);
    }

    /// <summary>Opens a scope on <paramref name="main"/> for its facility and this test's store, as the window's constructor does.</summary>
    private ScopeViewModel Reopen(MainViewModel main)
    {
        var scope = new ScopeViewModel(main, Store());
        main.OpenScope = scope;
        return scope;
    }

    /// <summary>The log lines written since byte <paramref name="offset"/> of the run's log file.</summary>
    private static string ReadLogSince(long offset)
    {
        using var stream = new FileStream(AppLog.LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        stream.Position = offset;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static PositionDisplayConfigDto Config(ActivePositionDto position) =>
        new([], [], [], position.HandoffCode, position, 3, NoStarsDisplay: true);

    private static async Task<MainViewModel> MainAsync()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        await main.NavDataInitialization.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        return main;
    }

    private static VideoMapData Map(string id) => new() { MapId = id, Lines = [] };

    /// <summary>Gives the radar what a scenario load gives it, its centre last so a waiting scope seeds at once.</summary>
    private static void SeedRadar(MainViewModel main, double lat, double lon, params string[] activeMaps)
    {
        RadarMapToggles.Add(main.Radar, 31, 2, 10);
        foreach (VideoMapToggleItem toggle in main.Radar.MapToggles)
        {
            toggle.IsEnabled = activeMaps.Contains(toggle.MapId);
        }

        main.Radar.RangeNm = 60;
        main.Radar.ActiveVideoMaps = [.. activeMaps.Select(Map)];
        main.Radar.CenterLat = lat;
        main.Radar.CenterLon = lon;
    }

    /// <summary>A scope at ZOA with maps 31, 2 and 10, map 2 on, at 40/-100 and 60 NM.</summary>
    private async Task<(MainViewModel Main, ScopeViewModel Scope)> SeededScopeAsync()
    {
        MainViewModel main = await MainAsync();
        main.SetActivePositionFromServer(Config(Sector44));
        SeedRadar(main, 40.0, -100.0, "m2");
        var scope = new ScopeViewModel(main, Store());
        main.OpenScope = scope;
        return (main, scope);
    }

    private ScopePrefSetStore Store() => new(_root, AppLog.CreateLogger<ScopePrefSetStore>());
}
