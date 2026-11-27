using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using Yaat.Client.Logging;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.ViewModels;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Prefs;
using Yaat.Client.Views.Scope.Profiles;
using Yaat.Client.Views.Scope.Toolbar;
using Yaat.Client.Views.Scope.Toolbar.Dcb;
using static Yaat.Client.UI.Tests.Helpers.ScopeTestFixture;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The room's Scope profile: picked once, when the first student position arrives, from the per-type override or else the
/// kind test; kept through a later <c>AS</c>; changed by hand with <c>.scope profile</c>, which saves the override; picked
/// again after an unload; shared by every Scope window; recorded in a pref set and switched by a pref-set load, but never
/// by the automatic saved-state restore.
/// </summary>
public class ScopeProfilePickTests : IDisposable
{
    private static readonly string[] PositionTypes = ["GND", "TWR", "APP", "CTR"];

    private static readonly ActivePositionDto Tracon = new(
        Callsign: "NCT_1_APP",
        Name: "Sector 1",
        RadioName: null,
        Frequency: "120.300",
        FacilityId: "NCT",
        FacilityName: "Northern California Approach",
        PositionType: "Tracon",
        HandoffCode: "1"
    );

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

    private readonly string _root = Path.Combine(Path.GetTempPath(), "yaat-scope-profile-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        // NewMain clears the overrides before each test; this clears the ones the last test set, so none outlives the class.
        var preferences = new UserPreferences();
        foreach (string type in PositionTypes)
        {
            preferences.ClearScopeProfileOverride(type);
        }

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static PositionDisplayConfigDto Config(ActivePositionDto position) =>
        new([], [], [], position.HandoffCode, position, 3, NoStarsDisplay: true);

    /// <summary>A main view model whose preferences hold no profile override, whatever an earlier test saved.</summary>
    private static async Task<MainViewModel> NewMain()
    {
        MainViewModel main = await MainAsync();
        foreach (string type in PositionTypes)
        {
            main.Preferences.ClearScopeProfileOverride(type);
        }

        return main;
    }

    private static ScopeViewModel OpenScope(MainViewModel main, ScopePrefSetStore? store)
    {
        var scope = new ScopeViewModel(main, store, profileStore: null, ScopePrefSetStore.PrimarySlot);
        main.OpenScope = scope;
        return scope;
    }

    private ScopePrefSetStore Store() => new(_root, AppLog.CreateLogger<ScopePrefSetStore>());

    [AvaloniaFact]
    public async Task FirstPosition_TraconWithTypeApp_PicksStars()
    {
        MainViewModel main = await NewMain();
        main.SetStudentPositionType("APP");

        main.SetActivePositionFromServer(Config(Tracon));

        Assert.Equal(ScopeProfileKind.Stars, main.ScopeProfile);
    }

    [AvaloniaFact]
    public async Task LaterPositionChange_ToAnEramSector_KeepsStars()
    {
        MainViewModel main = await NewMain();
        main.SetStudentPositionType("APP");
        main.SetActivePositionFromServer(Config(Tracon));

        main.SetActivePositionFromServer(Config(Sector44));

        Assert.Equal(ScopeProfileKind.Stars, main.ScopeProfile);
    }

    [AvaloniaFact]
    public async Task Override_BeatsTheKindTest()
    {
        MainViewModel main = await NewMain();
        main.Preferences.SetScopeProfileOverride("APP", "eram");
        main.SetStudentPositionType("APP");

        main.SetActivePositionFromServer(Config(Tracon));

        Assert.Equal(ScopeProfileKind.Eram, main.ScopeProfile);
    }

    [AvaloniaFact]
    public async Task PositionBeforeType_PicksByKind_ThenThePositionTypesOverride()
    {
        MainViewModel main = await NewMain();
        main.Preferences.SetScopeProfileOverride("APP", "eram");

        main.SetActivePositionFromServer(Config(Tracon));
        Assert.Equal(ScopeProfileKind.Stars, main.ScopeProfile);

        main.SetStudentPositionType("APP");
        Assert.Equal(ScopeProfileKind.Eram, main.ScopeProfile);
    }

    [AvaloniaFact]
    public async Task PositionBeforeType_WithAPickByHandBetween_KeepsTheHandPick()
    {
        MainViewModel main = await NewMain();
        main.Preferences.SetScopeProfileOverride("APP", "eram");
        using ScopeViewModel scope = OpenScope(main, store: null);
        main.SetActivePositionFromServer(Config(Tracon));

        main.RunScopeCommand(".scope profile mixed");
        main.SetStudentPositionType("APP");

        Assert.Equal(ScopeProfileKind.Mixed, main.ScopeProfile);
        Assert.Equal(ScopeProfileKind.Mixed, scope.ProfileKind);
    }

    [AvaloniaFact]
    public async Task PickByHand_WithTypeTwr_SavesTheOverride_AndEchoes()
    {
        MainViewModel main = await NewMain();
        using ScopeViewModel scope = OpenScope(main, store: null);
        main.SetStudentPositionType("TWR");

        main.RunScopeCommand(".scope profile mixed");

        Assert.Equal("mixed", main.Preferences.GetScopeProfileOverride("TWR"));
        Assert.Equal("Radar profile MIXED", main.StatusText);
        Assert.Equal(ScopeProfileKind.Mixed, main.ScopeProfile);
    }

    [AvaloniaFact]
    public async Task PickByHand_WithNoType_SavesNoOverride()
    {
        MainViewModel main = await NewMain();
        using ScopeViewModel scope = OpenScope(main, store: null);

        main.RunScopeCommand(".scope profile eram");

        Assert.Equal(ScopeProfileKind.Eram, main.ScopeProfile);
        Assert.All(PositionTypes, type => Assert.Null(main.Preferences.GetScopeProfileOverride(type)));
    }

    [AvaloniaFact]
    public async Task Unload_ThenANewLoad_PicksAgain()
    {
        MainViewModel main = await NewMain();
        main.SetStudentPositionType("APP");
        main.SetActivePositionFromServer(Config(Tracon));

        main.SetStudentPositionType(null);
        main.SetActivePositionFromServer(null);
        Assert.Null(main.ScopeProfile);

        main.SetStudentPositionType("CTR");
        main.SetActivePositionFromServer(Config(Sector44));
        Assert.Equal(ScopeProfileKind.Eram, main.ScopeProfile);
    }

    [AvaloniaFact]
    public async Task TwoScopes_ShareTheProfile()
    {
        MainViewModel main = await NewMain();
        using var first = new ScopeViewModel(main, prefStore: null, profileStore: null, ScopePrefSetStore.PrimarySlot);
        using var second = new ScopeViewModel(main, prefStore: null, profileStore: null, ScopePrefSetStore.PrimarySlot);
        main.OpenScope = first;
        main.SetStudentPositionType("APP");

        main.SetActivePositionFromServer(Config(Tracon));
        Assert.Equal((ScopeProfileKind.Stars, ScopeProfileKind.Stars), (first.ProfileKind, second.ProfileKind));
        Assert.Equal(("stars", "stars"), (first.TemplateId, second.TemplateId));

        main.RunScopeCommand(".scope profile eram");
        Assert.Equal((ScopeProfileKind.Eram, ScopeProfileKind.Eram), (first.ProfileKind, second.ProfileKind));
        Assert.Equal(("eram", "eram"), (first.TemplateId, second.TemplateId));
    }

    [AvaloniaFact]
    public async Task CapturedPrefSet_RecordsTheProfileName()
    {
        MainViewModel main = await NewMain();
        using ScopeViewModel scope = OpenScope(main, store: null);
        main.SetStudentPositionType("APP");
        main.SetActivePositionFromServer(Config(Tracon));

        ScopePrefSet set = scope.CapturePrefSet("DAY");

        Assert.Equal("stars", set.Kind);
    }

    [AvaloniaFact]
    public async Task PrefLoad_OfASetWithKindEram_SwitchesTheProfile_AndSavesNoOverride()
    {
        MainViewModel main = await NewMain();
        using ScopeViewModel scope = OpenScope(main, Store());
        main.SetStudentPositionType("APP");
        main.SetActivePositionFromServer(Config(Tracon));
        Store().SaveSet("NCT", scope.CapturePrefSet("ENROUTE") with { Kind = "eram" });

        main.RunScopeCommand(".scope pref load ENROUTE");

        Assert.Equal(ScopeProfileKind.Eram, main.ScopeProfile);
        Assert.Equal(ScopeProfileKind.Eram, scope.ProfileKind);
        Assert.Equal("eram", scope.TemplateId);
        // The scope has no maps yet, so the load's map note comes before the profile's.
        Assert.Equal(".scope pref load ENROUTE (its maps apply once the Radar's maps load); Radar profile ERAM", main.StatusText);
        Assert.Null(main.Preferences.GetScopeProfileOverride("APP"));
    }

    /// <summary>A profile store under the test's root whose shipped file <paramref name="name"/> <paramref name="edit"/> has changed.</summary>
    private ScopeProfileStore ProfileStoreWith(string name, Action<JsonObject> edit)
    {
        string root = Path.Combine(_root, "profiles");
        var store = new ScopeProfileStore(root, AppLog.CreateLogger<ScopeProfileStore>());
        store.Get(ScopeProfileKind.Mixed);
        string file = Path.Combine(root, $"{name}.json");
        JsonObject profile = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        edit(profile);
        File.WriteAllText(file, profile.ToJsonString());
        return store;
    }

    [AvaloniaFact]
    public async Task OldProfileFile_ListingTemplateInItsToolbar_AppliesWithNoWarningForIt()
    {
        string token = "nobutton" + Guid.NewGuid().ToString("N");
        ScopeProfileStore profiles = ProfileStoreWith("mixed", p => p["toolbar"] = new JsonArray("maps", "template", "TEMPLATE", token));
        MainViewModel main = await NewMain();
        long mark = new FileInfo(AppLog.LogPath).Length;
        using var scope = new ScopeViewModel(main, prefStore: null, profiles, ScopePrefSetStore.PrimarySlot);
        main.OpenScope = scope;

        main.PickScopeProfileByHand(ScopeProfileKind.Mixed);

        Assert.Equal(ScopeButtonId.Maps, scope.ToolbarButtons[0].Id);
        // The token's warning proves the log is read; the retired name is never among the names it lists.
        string[] warnings = [.. LogLinesSince(mark).Where(line => line.Contains(token, StringComparison.Ordinal))];
        Assert.NotEmpty(warnings);
        Assert.All(warnings, line => Assert.DoesNotContain("template", line, StringComparison.OrdinalIgnoreCase));
    }

    [AvaloniaFact]
    public async Task ProfileFile_NamingTemplateAmongItsViews_WarnsOfIt()
    {
        string token = "noview" + Guid.NewGuid().ToString("N");
        ScopeProfileStore profiles = ProfileStoreWith("mixed", p => p["views"] = new JsonArray("ssa", "template", token));
        MainViewModel main = await NewMain();
        long mark = new FileInfo(AppLog.LogPath).Length;
        using var scope = new ScopeViewModel(main, prefStore: null, profiles, ScopePrefSetStore.PrimarySlot);
        main.OpenScope = scope;

        main.PickScopeProfileByHand(ScopeProfileKind.Mixed);

        // Only the toolbar's retired TEMPLATE name is skipped silently; a view named template is warned of like any other.
        string warning = Assert.Single(LogLinesSince(mark), line => line.Contains(token, StringComparison.Ordinal));
        Assert.Contains("template", warning, StringComparison.Ordinal);
    }

    private static string[] LogLinesSince(long mark)
    {
        using var stream = new FileStream(AppLog.LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        stream.Seek(mark, SeekOrigin.Begin);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split('\n');
    }

    private static RewindResultDto RecordingResult(string? studentPositionType) =>
        new(true, null, [], [], null, [], "scenario-3", "OAK Ground", "OAK", StudentPositionType: studentPositionType);

    [AvaloniaFact]
    public async Task RoomJoinOrder_PositionThenANullType_KeepsTheProfile_AndALaterAsKeepsIt()
    {
        MainViewModel main = await NewMain();
        main.SetActivePositionFromServer(Config(Tracon));

        main.SetStudentPositionType(null);
        Assert.Equal(ScopeProfileKind.Stars, main.ScopeProfile);

        main.SetActivePositionFromServer(Config(Sector44));
        Assert.Equal(ScopeProfileKind.Stars, main.ScopeProfile);
    }

    [AvaloniaFact]
    public async Task RecordingWithANullType_ThenAPosition_PicksAndKeepsTheProfile()
    {
        MainViewModel main = await NewMain();
        main.ApplyRecordingResult(RecordingResult(null));
        main.SetActivePositionFromServer(Config(Tracon));

        main.SetStudentPositionType(null);

        Assert.Equal(ScopeProfileKind.Stars, main.ScopeProfile);
    }

    [AvaloniaFact]
    public async Task RecordingLoad_KeepsTheRoomsProfile()
    {
        MainViewModel main = await NewMain();
        main.SetStudentPositionType("APP");
        main.SetActivePositionFromServer(Config(Tracon));

        main.ApplyRecordingResult(RecordingResult("CTR"));

        Assert.Equal(ScopeProfileKind.Stars, main.ScopeProfile);
    }

    [AvaloniaFact]
    public async Task ScenarioLoadResult_ClearsTheProfile_SoItsPositionPicksAgain()
    {
        MainViewModel main = await NewMain();
        main.SetStudentPositionType("APP");
        main.SetActivePositionFromServer(Config(Tracon));

        main.ApplyScenarioResult(
            MainViewModelActiveRunwaysTests.LoadResult([], [], promptNeeded: false) with
            {
                PositionDisplayConfig = Config(Sector44),
            }
        );

        Assert.Equal(ScopeProfileKind.Eram, main.ScopeProfile);
    }

    [AvaloniaFact]
    public async Task ScenarioLoadedByAnotherClient_ClearsTheProfile_SoItsPositionPicksAgain()
    {
        MainViewModel main = await NewMain();
        main.SetStudentPositionType("APP");
        main.SetActivePositionFromServer(Config(Tracon));

        main.OnScenarioLoaded(
            new ScenarioLoadedDto(
                "scenario-1",
                "OAK Ground",
                "OAK",
                true,
                1,
                [],
                [],
                [],
                PositionDisplayConfig: Config(Sector44),
                StudentPositionType: "CTR",
                SessionStartUtc: null
            )
        );
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(ScopeProfileKind.Eram, main.ScopeProfile);
    }

    [AvaloniaFact]
    public async Task PickByHand_BeforeAnyPosition_IsKeptWhenThePositionArrives()
    {
        MainViewModel main = await NewMain();
        using ScopeViewModel scope = OpenScope(main, store: null);
        main.RunScopeCommand(".scope profile eram");

        main.SetStudentPositionType("APP");
        main.SetActivePositionFromServer(Config(Tracon));

        Assert.Equal(ScopeProfileKind.Eram, main.ScopeProfile);
    }

    [AvaloniaFact]
    public async Task Unload_BringsBackTheStarsProfile()
    {
        MainViewModel main = await NewMain();
        using ScopeViewModel scope = OpenScope(main, store: null);
        main.SetStudentPositionType("CTR");
        main.SetActivePositionFromServer(Config(Sector44));
        Assert.Equal("ERAM", scope.SpinnerValue(ScopeToolbarButtons.Find(ScopeButtonId.Profile)!));

        main.SetStudentPositionType(null);
        main.SetActivePositionFromServer(null);

        Assert.Equal(ScopeProfileKind.Stars, scope.ProfileKind);
        Assert.Equal(ScopeBarKind.Stars, scope.BarKind);
        Assert.Equal("STARS", scope.SpinnerValue(ScopeToolbarButtons.Find(ScopeButtonId.Profile)!));
        Assert.Equal("stars", scope.TemplateId);
    }

    [AvaloniaFact]
    public async Task CapturedPrefSet_RecordsTheKindWord_NotTheFilesName()
    {
        MainViewModel main = await NewMain();
        using var scope = new ScopeViewModel(
            main,
            prefStore: null,
            profileStore: ProfileStoreWith("stars", p => p["name"] = "My STARS"),
            ScopePrefSetStore.PrimarySlot
        );
        main.OpenScope = scope;
        main.SetStudentPositionType("APP");
        main.SetActivePositionFromServer(Config(Tracon));

        Assert.Equal("stars", scope.CapturePrefSet("DAY").Kind);
    }

    [AvaloniaFact]
    public async Task PrefLoad_OfKindEram_IntoAStarsRoom_SwitchesToEramAndEchoesIt()
    {
        MainViewModel main = await NewMain();
        using ScopeViewModel scope = OpenScope(main, Store());
        main.SetActivePositionFromServer(Config(Tracon));
        Assert.Equal(ScopeProfileKind.Stars, main.ScopeProfile);
        Store().SaveSet("NCT", scope.CapturePrefSet("ENR") with { Kind = "eram" });

        main.RunScopeCommand(".scope pref load ENR");

        Assert.Equal(ScopeProfileKind.Eram, main.ScopeProfile);
        Assert.EndsWith("; Radar profile ERAM", main.StatusText);
    }

    [AvaloniaFact]
    public async Task PrefLoad_OfTheCurrentProfile_EchoesNoProfileNote()
    {
        MainViewModel main = await NewMain();
        using ScopeViewModel scope = OpenScope(main, Store());
        main.SetStudentPositionType("APP");
        main.SetActivePositionFromServer(Config(Tracon));
        Store().SaveSet("NCT", scope.CapturePrefSet("DAY"));

        main.RunScopeCommand(".scope pref load DAY");

        Assert.Equal(ScopeProfileKind.Stars, main.ScopeProfile);
        Assert.StartsWith(".scope pref load DAY", main.StatusText);
        Assert.DoesNotContain("Radar profile", main.StatusText);
    }

    [AvaloniaFact]
    public async Task ProfileViewsOrder_OrdersTheOpenViews()
    {
        MainViewModel main = await NewMain();
        using var scope = new ScopeViewModel(
            main,
            prefStore: null,
            profileStore: ProfileStoreWith("mixed", p => p["views"] = new JsonArray("ca", "ssa")),
            ScopePrefSetStore.PrimarySlot
        );
        main.PickScopeProfileByHand(ScopeProfileKind.Mixed);

        scope.SetViewOpen("ca", true);

        Assert.Equal(["ca", "ssa"], scope.OpenViews);
    }

    [AvaloniaFact]
    public async Task SavedStateRestore_KeepsTheProfile_AndTheLookFollowsIt()
    {
        MainViewModel main = await NewMain();
        Store()
            .SaveCurrent(
                "NCT",
                ScopePrefSetStore.PrimarySlot,
                new ScopePrefSet
                {
                    Extra = new Dictionary<string, JsonElement> { ["templateId"] = JsonSerializer.SerializeToElement("euroscope") },
                    Db = new ScopePrefDb(false, true, false, true, true, true, 12),
                    Kind = "eram",
                    Lens = new ScopePrefLens(false, false, false, false),
                    MapTools = new ScopePrefMapTools(false, false, null, null, null),
                }
            );
        using ScopeViewModel scope = OpenScope(main, Store());
        main.SetStudentPositionType("APP");

        main.SetActivePositionFromServer(Config(Tracon));

        Assert.Equal(ScopeProfileKind.Stars, main.ScopeProfile);
        Assert.Equal(ScopeProfileKind.Stars, scope.ProfileKind);
        Assert.Equal("stars", scope.TemplateId);
    }

    [AvaloniaFact]
    public async Task ClassicToolsOpen_ThenProfileStars_ClosesTools_AndDisarmsTheRingTool()
    {
        MainViewModel main = await NewMain();
        ScopeViewModel scope = OpenScope(main, store: null);
        try
        {
            main.RunScopeCommand(".scope profile mixed");
            scope.Press(ScopeToolbarButtons.Find(ScopeButtonId.Tools)!);
            Assert.Equal(ScopeButtonId.Tools, scope.OpenMenu);
            Assert.NotEqual(ScopeRingTool.None, scope.ArmedRingTool);

            main.RunScopeCommand(".scope profile stars");

            Assert.Null(scope.OpenMenu);
            Assert.Equal(ScopeRingTool.None, scope.ArmedRingTool);
        }
        finally
        {
            scope.Dispose();
        }
    }

    private static void Settle(Window window)
    {
        for (int i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }

        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Clicks the tile on the STARS bar's shown page whose entry matches <paramref name="match"/>.</summary>
    private static void PressTile(Window window, ScopeHost host, Func<StarsDcbEntry, bool> match)
    {
        StarsDcbBar bar = Assert.Single(host.GetVisualDescendants().OfType<StarsDcbBar>());
        int index = bar.Entries.ToList().FindIndex(e => match(e));
        Assert.True(index >= 0);
        DcbTile tile = bar.Tiles[index];
        Point point = tile.TranslatePoint(new Point(tile.Bounds.Width / 2, tile.Bounds.Height / 2), window)!.Value;
        window.MouseMove(point);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Settle(window);
    }

    [AvaloniaFact]
    public async Task YaatProfileMixed_OnTheStarsBar_SwapsTheBarToTheClassicToolbar()
    {
        MainViewModel main = await NewMain();
        ScopeViewModel scope = OpenScope(main, store: null);
        var host = new ScopeHost { DataContext = scope };
        var window = new Window
        {
            Width = 1200,
            Height = 800,
            Content = host,
        };
        window.Closed += (_, _) => scope.Dispose();
        window.Show();
        main.RunScopeCommand(".scope profile stars");
        Settle(window);
        try
        {
            PressTile(window, host, e => e.Opens == StarsDcbPage.Yaat);
            PressTile(window, host, e => e.Opens == StarsDcbPage.YaatProfile);
            PressTile(window, host, e => e.Button == ScopeButtonId.ProfileMixed);

            Assert.Equal(ScopeProfileKind.Mixed, scope.ProfileKind);
            Assert.Single(host.GetVisualDescendants().OfType<ScopeToolbar>());
            Assert.Empty(host.GetVisualDescendants().OfType<StarsDcbBar>());
            Assert.False(scope.IsBarSubmenuOpen);
            Assert.Null(scope.OpenMenu);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public async Task YaatProfileEuroScope_OnTheStarsBar_ShowsTheEuroScopeLookOnTheClassicToolbar()
    {
        MainViewModel main = await NewMain();
        ScopeViewModel scope = OpenScope(main, store: null);
        var host = new ScopeHost { DataContext = scope };
        var window = new Window
        {
            Width = 1200,
            Height = 800,
            Content = host,
        };
        window.Closed += (_, _) => scope.Dispose();
        window.Show();
        main.RunScopeCommand(".scope profile stars");
        Settle(window);
        try
        {
            PressTile(window, host, e => e.Opens == StarsDcbPage.Yaat);
            PressTile(window, host, e => e.Opens == StarsDcbPage.YaatProfile);
            PressTile(window, host, e => e.Button == ScopeButtonId.ProfileEuroScope);

            Assert.Equal(ScopeProfileKind.EuroScope, scope.ProfileKind);
            Assert.Equal(ScopeTemplates.EuroScopeId, scope.TemplateId);
            Assert.Equal(ScopeBarKind.Classic, scope.BarKind);
            Assert.Single(host.GetVisualDescendants().OfType<ScopeToolbar>());
            Assert.Empty(host.GetVisualDescendants().OfType<StarsDcbBar>());
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
