using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;

namespace Yaat.Client.UI.Tests.ViewModels;

/// <summary>
/// The two solo advisory settings, "Pre-fill traffic advisories" and "Live advisory check": their defaults, the saved
/// preferences, the settings bundle, the Settings window's staging, and the session copy the session flyout edits.
/// Each test runs on its own empty preferences file (<c>[PreferencesFilePerTest]</c>).
/// </summary>
public class SoloAdvisorySettingsTests
{
    [AvaloniaFact]
    public void Defaults_LiveCheckOff_PrefillTrueValues()
    {
        var preferences = new UserPreferences();

        Assert.Equal(SoloAdvisoryPrefill.TrueValues, preferences.SoloAdvisoryPrefill);
        Assert.False(preferences.SoloAdvisoryLiveCheck);
        Assert.Equal(SoloAdvisoryPrefill.TrueValues, UserPreferences.CreateDefaults().SoloAdvisoryPrefill);
        Assert.False(UserPreferences.CreateDefaults().SoloAdvisoryLiveCheck);
    }

    [AvaloniaFact]
    public void Prefs_RoundTripBothSettings()
    {
        var preferences = new UserPreferences();

        preferences.SetSoloAdvisorySettings(SoloAdvisoryPrefill.LeftForStudent, liveCheck: true);

        var reloaded = new UserPreferences();
        Assert.Equal(SoloAdvisoryPrefill.LeftForStudent, reloaded.SoloAdvisoryPrefill);
        Assert.True(reloaded.SoloAdvisoryLiveCheck);
    }

    [AvaloniaFact]
    public void Bundle_ExportsAndImportsBothSettings()
    {
        var preferences = new UserPreferences();
        preferences.SetSoloAdvisorySettings(SoloAdvisoryPrefill.LeftForStudent, liveCheck: true);

        JsonObject exported = preferences.ExportBundlePreferences();
        Assert.Equal("LeftForStudent", exported["soloAdvisoryPrefill"]?.GetValue<string>());
        Assert.True(exported["soloAdvisoryLiveCheck"]?.GetValue<bool>());

        preferences.SetSoloAdvisorySettings(SoloAdvisoryPrefill.TrueValues, liveCheck: false);
        PreferencesImportResult result = preferences.ImportBundlePreferences(
            new JsonObject { ["soloAdvisoryPrefill"] = "LeftForStudent", ["soloAdvisoryLiveCheck"] = true }
        );

        Assert.Equal(["soloAdvisoryPrefill", "soloAdvisoryLiveCheck"], result.AppliedKeys);
        Assert.Empty(result.IgnoredKeys);
        Assert.Equal(SoloAdvisoryPrefill.LeftForStudent, preferences.SoloAdvisoryPrefill);
        Assert.True(preferences.SoloAdvisoryLiveCheck);

        // The Settings window stages an import into its fields and exports what they show.
        var settings = new SettingsViewModel(new UserPreferences());
        settings.StagePreferences(new JsonObject { ["soloAdvisoryPrefill"] = "LeftForStudent", ["soloAdvisoryLiveCheck"] = true });
        Assert.Equal((int)SoloAdvisoryPrefill.LeftForStudent, settings.SelectedSoloAdvisoryPrefillIndex);
        Assert.True(settings.SoloAdvisoryLiveCheck);

        JsonObject staged = JsonNode.Parse(settings.ExportStagedPreferences().Content)!.AsObject();
        Assert.Equal("LeftForStudent", staged["soloAdvisoryPrefill"]?.GetValue<string>());
        Assert.True(staged["soloAdvisoryLiveCheck"]?.GetValue<bool>());
    }

    [AvaloniaTheory]
    [InlineData("Blank")]
    [InlineData("leftforstudent")]
    [InlineData("1")]
    public void Bundle_RejectsUnknownPrefillValue(string value)
    {
        var preferences = new UserPreferences();

        PreferencesImportResult result = preferences.ImportBundlePreferences(new JsonObject { ["soloAdvisoryPrefill"] = value });

        Assert.Empty(result.AppliedKeys);
        Assert.Equal(["soloAdvisoryPrefill"], result.IgnoredKeys);
        Assert.Equal(SoloAdvisoryPrefill.TrueValues, preferences.SoloAdvisoryPrefill);
    }

    [AvaloniaFact]
    public void SettingsViewModel_StagesAndAppliesBothSettings()
    {
        var preferences = new UserPreferences();
        var settings = new SettingsViewModel(preferences);
        Assert.Equal(["True values", "Left for the student"], SoloAdvisoryPrefillLabels.Options);
        Assert.Same(SoloAdvisoryPrefillLabels.Options, SettingsViewModel.SoloAdvisoryPrefillOptions);
        Assert.Same(SoloAdvisoryPrefillLabels.Options, MainViewModel.SessionSoloAdvisoryPrefillOptions);
        Assert.Equal((int)SoloAdvisoryPrefill.TrueValues, settings.SelectedSoloAdvisoryPrefillIndex);
        Assert.False(settings.SoloAdvisoryLiveCheck);

        settings.SelectedSoloAdvisoryPrefillIndex = (int)SoloAdvisoryPrefill.LeftForStudent;
        settings.SoloAdvisoryLiveCheck = true;

        Assert.Equal(SoloAdvisoryPrefill.TrueValues, preferences.SoloAdvisoryPrefill);
        Assert.False(preferences.SoloAdvisoryLiveCheck);

        settings.ApplyCommand.Execute(null);

        Assert.Equal(SoloAdvisoryPrefill.LeftForStudent, preferences.SoloAdvisoryPrefill);
        Assert.True(preferences.SoloAdvisoryLiveCheck);
        Assert.Equal(SoloAdvisoryPrefill.LeftForStudent, new UserPreferences().SoloAdvisoryPrefill);
    }

    [AvaloniaFact]
    public void SessionSetting_SeededFromPreferences()
    {
        new UserPreferences().SetSoloAdvisorySettings(SoloAdvisoryPrefill.LeftForStudent, liveCheck: true);

        var vm = new MainViewModel(new FakeFilePickerService());

        Assert.Equal(SoloAdvisoryPrefill.LeftForStudent, vm.SessionSoloAdvisoryPrefill);
        Assert.Equal((int)SoloAdvisoryPrefill.LeftForStudent, vm.SessionSoloAdvisoryPrefillIndex);
        Assert.True(vm.SessionSoloAdvisoryLiveCheck);
    }

    [AvaloniaFact]
    public void SessionSetting_ChangeDoesNotWritePreferences()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        Assert.Equal(SoloAdvisoryPrefill.TrueValues, vm.SessionSoloAdvisoryPrefill);
        Assert.False(vm.SessionSoloAdvisoryLiveCheck);

        vm.SessionSoloAdvisoryPrefillIndex = (int)SoloAdvisoryPrefill.LeftForStudent;
        vm.SessionSoloAdvisoryLiveCheck = true;

        Assert.Equal(SoloAdvisoryPrefill.LeftForStudent, vm.SessionSoloAdvisoryPrefill);
        Assert.Equal(SoloAdvisoryPrefill.TrueValues, vm.Preferences.SoloAdvisoryPrefill);
        Assert.False(vm.Preferences.SoloAdvisoryLiveCheck);
        var reloaded = new UserPreferences();
        Assert.Equal(SoloAdvisoryPrefill.TrueValues, reloaded.SoloAdvisoryPrefill);
        Assert.False(reloaded.SoloAdvisoryLiveCheck);
    }

    [AvaloniaFact]
    public void SessionSetting_ReseededOnRoomJoin()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        vm.ApplyRoomState(RoomState("ROOM-A"));
        vm.Preferences.SetSoloAdvisorySettings(SoloAdvisoryPrefill.LeftForStudent, liveCheck: true);
        Assert.Equal(SoloAdvisoryPrefill.TrueValues, vm.SessionSoloAdvisoryPrefill);

        vm.ApplyRoomState(RoomState("ROOM-B"));

        Assert.Equal(SoloAdvisoryPrefill.LeftForStudent, vm.SessionSoloAdvisoryPrefill);
        Assert.True(vm.SessionSoloAdvisoryLiveCheck);
    }

    [AvaloniaFact]
    public void SessionSetting_KeptAcrossARejoinOfTheSameRoom()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        vm.ApplyRoomState(RoomState("ROOM-A"));
        vm.SessionSoloAdvisoryPrefillIndex = (int)SoloAdvisoryPrefill.LeftForStudent;
        vm.SessionSoloAdvisoryLiveCheck = true;

        vm.ApplyRoomState(RoomState("ROOM-A"));

        Assert.Equal(SoloAdvisoryPrefill.LeftForStudent, vm.SessionSoloAdvisoryPrefill);
        Assert.True(vm.SessionSoloAdvisoryLiveCheck);
    }

    [AvaloniaFact]
    public void SessionSetting_ReseededOnRecordingLoad()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        vm.Preferences.SetSoloAdvisorySettings(SoloAdvisoryPrefill.LeftForStudent, liveCheck: true);

        vm.ApplyRecordingResult(new RewindResultDto(true, null, [], [], [], "scenario-3", "OAK Ground", "OAK"));

        Assert.Equal(SoloAdvisoryPrefill.LeftForStudent, vm.SessionSoloAdvisoryPrefill);
        Assert.True(vm.SessionSoloAdvisoryLiveCheck);
    }

    [AvaloniaFact]
    public void SessionPrefillIndex_IgnoresAnUndefinedValue()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        vm.ApplySessionSettings(SessionSettings(soloTrainingMode: true));
        vm.SessionSoloAdvisoryPrefillIndex = (int)SoloAdvisoryPrefill.LeftForStudent;

        // A combo box with no selection reports -1.
        vm.SessionSoloAdvisoryPrefillIndex = -1;

        Assert.Equal(SoloAdvisoryPrefill.LeftForStudent, vm.SessionSoloAdvisoryPrefill);
        Assert.Equal((int)SoloAdvisoryPrefill.LeftForStudent, vm.SessionSoloAdvisoryPrefillIndex);
    }

    [AvaloniaFact]
    public void SessionSetting_ReseededOnScenarioLoad()
    {
        var vm = new MainViewModel(new FakeFilePickerService());

        // Another member loads a scenario into the room.
        vm.Preferences.SetSoloAdvisorySettings(SoloAdvisoryPrefill.LeftForStudent, liveCheck: true);
        vm.OnScenarioLoaded(new ScenarioLoadedDto("scenario-1", "OAK Ground", "OAK", true, 1, [], [], []));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(SoloAdvisoryPrefill.LeftForStudent, vm.SessionSoloAdvisoryPrefill);
        Assert.True(vm.SessionSoloAdvisoryLiveCheck);

        // This client loads one.
        vm.Preferences.SetSoloAdvisorySettings(SoloAdvisoryPrefill.TrueValues, liveCheck: false);
        vm.ApplySessionSettingsFromLoadScenarioResult(
            new LoadScenarioResultDto(
                Success: true,
                Name: "Test",
                ScenarioId: "scenario-2",
                AircraftCount: 0,
                DelayedCount: 0,
                IsPaused: true,
                SimRate: 1,
                PrimaryAirportId: "OAK",
                Warnings: [],
                AllAircraft: [],
                ActiveRunways: [],
                ActiveRunwaysPrefill: [],
                ActiveRunwaysPromptNeeded: false,
                ActiveRunwaysAssigned: [],
                ToweredAirports: []
            )
        );

        Assert.Equal(SoloAdvisoryPrefill.TrueValues, vm.SessionSoloAdvisoryPrefill);
        Assert.False(vm.SessionSoloAdvisoryLiveCheck);
    }

    [AvaloniaFact]
    public void SessionSetting_MidSessionChangeSurvivesASessionSettingsUpdate()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        vm.ApplyRoomState(RoomState("ROOM-A"));
        vm.SessionSoloAdvisoryPrefillIndex = (int)SoloAdvisoryPrefill.LeftForStudent;
        vm.SessionSoloAdvisoryLiveCheck = true;

        vm.ApplySessionSettings(SessionSettings(soloTrainingMode: true));

        Assert.Equal(SoloAdvisoryPrefill.LeftForStudent, vm.SessionSoloAdvisoryPrefill);
        Assert.True(vm.SessionSoloAdvisoryLiveCheck);
    }

    [AvaloniaFact]
    public void SessionFlyout_ShowsSettingsOnlyInSolo()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.ApplySessionSettings(SessionSettings(soloTrainingMode: false));
        Assert.False(vm.ShowSessionSoloAdvisorySettings);

        vm.ApplySessionSettings(SessionSettings(soloTrainingMode: true));
        Assert.True(vm.ShowSessionSoloAdvisorySettings);
        Assert.Contains(nameof(MainViewModel.ShowSessionSoloAdvisorySettings), changed);

        vm.ApplySessionSettings(SessionSettings(soloTrainingMode: false));
        Assert.False(vm.ShowSessionSoloAdvisorySettings);

        string flyout = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yaat.Client", "Views", "CommandInputView.axaml"));
        int panel = flyout.IndexOf("IsVisible=\"{Binding ShowSessionSoloAdvisorySettings}\"", StringComparison.Ordinal);
        Assert.True(panel >= 0, "the session flyout has no panel shown by ShowSessionSoloAdvisorySettings");
        int panelEnd = flyout.IndexOf("</StackPanel>", panel, StringComparison.Ordinal);
        Assert.True(panelEnd > panel, "the session flyout's solo advisory panel is not closed");
        string rest = flyout[panel..panelEnd];
        Assert.Contains("Text=\"Pre-fill traffic advisories\"", rest);
        Assert.Contains("ItemsSource=\"{Binding SessionSoloAdvisoryPrefillOptions}\"", rest);
        Assert.Contains("SelectedIndex=\"{Binding SessionSoloAdvisoryPrefillIndex}\"", rest);
        Assert.Contains("Content=\"Live advisory check\"", rest);
        Assert.Contains("IsChecked=\"{Binding SessionSoloAdvisoryLiveCheck}\"", rest);
    }

    private static RoomStateDto RoomState(string roomId) =>
        new(
            RoomId: roomId,
            CreatorInitials: "CX",
            CreatorArtccId: "ZOA",
            Members: [],
            ScenarioName: null,
            ScenarioId: null,
            IsPaused: true,
            SimRate: 1.0,
            PrimaryAirportId: null,
            AllAircraft: [],
            AircraftGenerators: [],
            VfrArrivalGenerators: [],
            OverflightGenerators: [],
            Positions: [],
            ActiveRunways: [],
            ToweredAirports: []
        );

    private static SessionSettingsDto SessionSettings(bool soloTrainingMode) =>
        new(
            AutoDeleteOverride: null,
            EffectiveAutoDeleteMode: null,
            DepartureAutoDeleteDistanceNm: null,
            AutoAcceptDelaySeconds: -1,
            AutoClearedToLand: false,
            AutoCrossRunway: false,
            AutoPullUpToParallel: false,
            AutoGoAroundOnOccupiedRunway: false,
            AutoRejectTakeoffOnOccupiedRunway: false,
            AutoArrivalSpacingOnOccupiedRunway: false,
            ValidateDctFixes: false,
            SoloTrainingMode: soloTrainingMode,
            SoloParkingInitialCallupRatePercent: 100,
            SoloArrivalGeneratorRatePercent: 100,
            SoloGoAroundProbabilityPercent: 0,
            HasSoloParkingInitialCallupSource: false,
            HasSoloArrivalGeneratorSource: false,
            RpoShowPilotSpeech: false
        );

    private static string FindRepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while ((directory is not null) && !File.Exists(Path.Combine(directory.FullName, "yaat.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException($"yaat.slnx not found above {AppContext.BaseDirectory}");
    }
}
