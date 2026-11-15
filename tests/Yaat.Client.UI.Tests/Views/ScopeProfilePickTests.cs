using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Logging;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.UI.Tests.ViewModels;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Scope.Prefs;
using Yaat.Client.Views.Scope.Profiles;
using Yaat.Client.Views.Scope.Toolbar;

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
    private static MainViewModel NewMain()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        foreach (string type in PositionTypes)
        {
            main.Preferences.ClearScopeProfileOverride(type);
        }

        return main;
    }

    private static ScopeViewModel OpenScope(MainViewModel main, ScopePrefSetStore? store)
    {
        var scope = new ScopeViewModel(main, store, profileStore: null);
        main.OpenScope = scope;
        return scope;
    }

    private ScopePrefSetStore Store() => new(_root, AppLog.CreateLogger<ScopePrefSetStore>());

    [AvaloniaFact]
    public void FirstPosition_TraconWithTypeApp_PicksStars()
    {
        MainViewModel main = NewMain();
        main.SetStudentPositionType("APP");

        main.SetActivePositionFromServer(Config(Tracon));

        Assert.Equal(ScopeProfileKind.Stars, main.ScopeProfile);
    }

    [AvaloniaFact]
    public void LaterPositionChange_ToAnEramSector_KeepsStars()
    {
        MainViewModel main = NewMain();
        main.SetStudentPositionType("APP");
        main.SetActivePositionFromServer(Config(Tracon));

        main.SetActivePositionFromServer(Config(Sector44));

        Assert.Equal(ScopeProfileKind.Stars, main.ScopeProfile);
    }

    [AvaloniaFact]
    public void Override_BeatsTheKindTest()
    {
        MainViewModel main = NewMain();
        main.Preferences.SetScopeProfileOverride("APP", "eram");
        main.SetStudentPositionType("APP");

        main.SetActivePositionFromServer(Config(Tracon));

        Assert.Equal(ScopeProfileKind.Eram, main.ScopeProfile);
    }

    [AvaloniaFact]
    public void PositionBeforeType_PicksByKind_ThenThePositionTypesOverride()
    {
        MainViewModel main = NewMain();
        main.Preferences.SetScopeProfileOverride("APP", "eram");

        main.SetActivePositionFromServer(Config(Tracon));
        Assert.Equal(ScopeProfileKind.Stars, main.ScopeProfile);

        main.SetStudentPositionType("APP");
        Assert.Equal(ScopeProfileKind.Eram, main.ScopeProfile);
    }

    [AvaloniaFact]
    public void PositionBeforeType_WithAPickByHandBetween_KeepsTheHandPick()
    {
        MainViewModel main = NewMain();
        main.Preferences.SetScopeProfileOverride("APP", "eram");
        using ScopeViewModel scope = OpenScope(main, store: null);
        main.SetActivePositionFromServer(Config(Tracon));

        main.RunScopeCommand(".scope profile mixed");
        main.SetStudentPositionType("APP");

        Assert.Equal(ScopeProfileKind.Mixed, main.ScopeProfile);
        Assert.Equal(ScopeProfileKind.Mixed, scope.ProfileKind);
    }

    [AvaloniaFact]
    public void PickByHand_WithTypeTwr_SavesTheOverride_AndEchoes()
    {
        MainViewModel main = NewMain();
        using ScopeViewModel scope = OpenScope(main, store: null);
        main.SetStudentPositionType("TWR");

        main.RunScopeCommand(".scope profile mixed");

        Assert.Equal("mixed", main.Preferences.GetScopeProfileOverride("TWR"));
        Assert.Equal("Scope profile MIXED", main.StatusText);
        Assert.Equal(ScopeProfileKind.Mixed, main.ScopeProfile);
    }

    [AvaloniaFact]
    public void PickByHand_WithNoType_SavesNoOverride()
    {
        MainViewModel main = NewMain();
        using ScopeViewModel scope = OpenScope(main, store: null);

        main.RunScopeCommand(".scope profile eram");

        Assert.Equal(ScopeProfileKind.Eram, main.ScopeProfile);
        Assert.All(PositionTypes, type => Assert.Null(main.Preferences.GetScopeProfileOverride(type)));
    }

    [AvaloniaFact]
    public void Unload_ThenANewLoad_PicksAgain()
    {
        MainViewModel main = NewMain();
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
    public void TwoScopes_ShareTheProfile()
    {
        MainViewModel main = NewMain();
        using var first = new ScopeViewModel(main, prefStore: null, profileStore: null);
        using var second = new ScopeViewModel(main, prefStore: null, profileStore: null);
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
    public void CapturedPrefSet_RecordsTheProfileName_AndNoBands()
    {
        MainViewModel main = NewMain();
        using ScopeViewModel scope = OpenScope(main, store: null);
        main.SetStudentPositionType("APP");
        main.SetActivePositionFromServer(Config(Tracon));

        ScopePrefSet set = scope.CapturePrefSet("DAY");

        Assert.Equal("stars", set.Kind);
        Assert.Null(set.Bands);
    }

    [AvaloniaFact]
    public void PrefLoad_OfASetWithKindEram_SwitchesTheProfile_AndSavesNoOverride()
    {
        MainViewModel main = NewMain();
        using ScopeViewModel scope = OpenScope(main, Store());
        main.SetStudentPositionType("APP");
        main.SetActivePositionFromServer(Config(Tracon));
        Store().SaveSet("NCT", scope.CapturePrefSet("ENROUTE") with { Kind = "eram", TemplateId = "stars" });

        main.RunScopeCommand(".scope pref load ENROUTE");

        Assert.Equal(ScopeProfileKind.Eram, main.ScopeProfile);
        Assert.Equal(ScopeProfileKind.Eram, scope.ProfileKind);
        Assert.Equal("stars", scope.TemplateId);
        // The scope has no maps yet, so the load's map note comes before the profile's.
        Assert.Equal(".scope pref load ENROUTE (its maps apply once the scope's maps load); Scope profile ERAM", main.StatusText);
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

    private static RewindResultDto RecordingResult(string? studentPositionType) =>
        new(true, null, [], [], [], "scenario-3", "OAK Ground", "OAK", StudentPositionType: studentPositionType);

    [AvaloniaFact]
    public void RoomJoinOrder_PositionThenANullType_KeepsTheProfile_AndALaterAsKeepsIt()
    {
        MainViewModel main = NewMain();
        main.SetActivePositionFromServer(Config(Tracon));

        main.SetStudentPositionType(null);
        Assert.Equal(ScopeProfileKind.Stars, main.ScopeProfile);

        main.SetActivePositionFromServer(Config(Sector44));
        Assert.Equal(ScopeProfileKind.Stars, main.ScopeProfile);
    }

    [AvaloniaFact]
    public void RecordingWithANullType_ThenAPosition_PicksAndKeepsTheProfile()
    {
        MainViewModel main = NewMain();
        main.ApplyRecordingResult(RecordingResult(null));
        main.SetActivePositionFromServer(Config(Tracon));

        main.SetStudentPositionType(null);

        Assert.Equal(ScopeProfileKind.Stars, main.ScopeProfile);
    }

    [AvaloniaFact]
    public void RecordingLoad_KeepsTheRoomsProfile()
    {
        MainViewModel main = NewMain();
        main.SetStudentPositionType("APP");
        main.SetActivePositionFromServer(Config(Tracon));

        main.ApplyRecordingResult(RecordingResult("CTR"));

        Assert.Equal(ScopeProfileKind.Stars, main.ScopeProfile);
    }

    [AvaloniaFact]
    public void ScenarioLoadResult_ClearsTheProfile_SoItsPositionPicksAgain()
    {
        MainViewModel main = NewMain();
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
    public void ScenarioLoadedByAnotherClient_ClearsTheProfile_SoItsPositionPicksAgain()
    {
        MainViewModel main = NewMain();
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
                StudentPositionType: "CTR"
            )
        );
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(ScopeProfileKind.Eram, main.ScopeProfile);
    }

    [AvaloniaFact]
    public void PickByHand_BeforeAnyPosition_IsKeptWhenThePositionArrives()
    {
        MainViewModel main = NewMain();
        using ScopeViewModel scope = OpenScope(main, store: null);
        main.RunScopeCommand(".scope profile eram");

        main.SetStudentPositionType("APP");
        main.SetActivePositionFromServer(Config(Tracon));

        Assert.Equal(ScopeProfileKind.Eram, main.ScopeProfile);
    }

    [AvaloniaFact]
    public void Unload_BringsBackMixed_AndKeepsTheTemplate()
    {
        MainViewModel main = NewMain();
        using ScopeViewModel scope = OpenScope(main, store: null);
        main.SetStudentPositionType("CTR");
        main.SetActivePositionFromServer(Config(Sector44));
        Assert.Equal("ERAM", scope.SpinnerValue(ScopeToolbarButtons.Find(ScopeButtonId.Profile)!));

        main.SetStudentPositionType(null);
        main.SetActivePositionFromServer(null);

        Assert.Equal(ScopeProfileKind.Mixed, scope.ProfileKind);
        Assert.Equal("MIXED", scope.SpinnerValue(ScopeToolbarButtons.Find(ScopeButtonId.Profile)!));
        Assert.Equal("eram", scope.TemplateId);
    }

    [AvaloniaFact]
    public void CapturedPrefSet_RecordsTheKindWord_NotTheFilesName()
    {
        MainViewModel main = NewMain();
        using var scope = new ScopeViewModel(main, prefStore: null, profileStore: ProfileStoreWith("stars", p => p["name"] = "My STARS"));
        main.OpenScope = scope;
        main.SetStudentPositionType("APP");
        main.SetActivePositionFromServer(Config(Tracon));

        Assert.Equal("stars", scope.CapturePrefSet("DAY").Kind);
    }

    [AvaloniaFact]
    public void PrefLoad_OfKindEram_IntoAStarsRoom_SwitchesToEramAndEchoesIt()
    {
        MainViewModel main = NewMain();
        using ScopeViewModel scope = OpenScope(main, Store());
        main.SetActivePositionFromServer(Config(Tracon));
        Assert.Equal(ScopeProfileKind.Stars, main.ScopeProfile);
        Store().SaveSet("NCT", scope.CapturePrefSet("ENR") with { Kind = "eram" });

        main.RunScopeCommand(".scope pref load ENR");

        Assert.Equal(ScopeProfileKind.Eram, main.ScopeProfile);
        Assert.EndsWith("; Scope profile ERAM", main.StatusText);
    }

    [AvaloniaFact]
    public void PrefLoad_OfTheCurrentProfile_EchoesNoProfileNote()
    {
        MainViewModel main = NewMain();
        using ScopeViewModel scope = OpenScope(main, Store());
        main.SetStudentPositionType("APP");
        main.SetActivePositionFromServer(Config(Tracon));
        Store().SaveSet("NCT", scope.CapturePrefSet("DAY"));

        main.RunScopeCommand(".scope pref load DAY");

        Assert.Equal(ScopeProfileKind.Stars, main.ScopeProfile);
        Assert.StartsWith(".scope pref load DAY", main.StatusText);
        Assert.DoesNotContain("Scope profile", main.StatusText);
    }

    [AvaloniaFact]
    public void ProfileViewsOrder_OrdersTheOpenViews()
    {
        MainViewModel main = NewMain();
        using var scope = new ScopeViewModel(
            main,
            prefStore: null,
            profileStore: ProfileStoreWith("mixed", p => p["views"] = new JsonArray("ca", "ssa"))
        );

        scope.SetViewOpen("ca", true);

        Assert.Equal(["ca", "ssa"], scope.OpenViews);
    }

    [AvaloniaFact]
    public void SavedStateRestore_KeepsTheProfile_AndItsTemplateStands()
    {
        MainViewModel main = NewMain();
        Store()
            .SaveCurrent(
                "NCT",
                new ScopePrefSet
                {
                    TemplateId = "euroscope",
                    Db = new ScopePrefDb(false, true, false, 12),
                    Kind = "eram",
                }
            );
        using ScopeViewModel scope = OpenScope(main, Store());
        main.SetStudentPositionType("APP");

        main.SetActivePositionFromServer(Config(Tracon));

        Assert.Equal(ScopeProfileKind.Stars, main.ScopeProfile);
        Assert.Equal(ScopeProfileKind.Stars, scope.ProfileKind);
        Assert.Equal("euroscope", scope.TemplateId);
    }
}
