using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;

namespace Yaat.Client.UI.Tests.ViewModels;

public class MainViewModelSessionSettingsTests
{
    [AvaloniaFact]
    public void ApplySessionSettings_UsesAutoDeleteOverrideForDropdown()
    {
        var vm = new MainViewModel(new FakeFilePickerService());

        vm.ApplySessionSettings(
            new SessionSettingsDto(
                AutoDeleteOverride: null,
                EffectiveAutoDeleteMode: "Parked",
                DepartureAutoDeleteDistanceNm: null,
                AutoAcceptDelaySeconds: 5,
                AutoClearedToLand: true,
                AutoCrossRunway: true,
                AutoPullUpToParallel: true,
                AutoGoAroundOnOccupiedRunway: true,
                AutoRejectTakeoffOnOccupiedRunway: true,
                AutoArrivalSpacingOnOccupiedRunway: true,
                ValidateDctFixes: true,
                SoloTrainingMode: true,
                SoloParkingInitialCallupRatePercent: 50,
                SoloArrivalGeneratorRatePercent: 75,
                SoloGoAroundProbabilityPercent: 0,
                HasSoloParkingInitialCallupSource: true,
                HasSoloArrivalGeneratorSource: true,
                RpoShowPilotSpeech: true,
                LiveTrafficEnabled: true,
                LiveTrafficCeilingFt: 7_000
            )
        );

        Assert.Equal(0, vm.SessionAutoDeleteIndex);
        Assert.Equal("Parked", vm.ActiveAutoDeleteMode);
        Assert.Equal(5, vm.SessionAutoAcceptDelaySeconds);
        Assert.True(vm.SessionAutoClearedToLand);
        Assert.True(vm.SessionAutoCrossRunway);
        Assert.True(vm.SessionAutoGoAroundOnOccupiedRunway);
        Assert.True(vm.SessionAutoArrivalSpacingOnOccupiedRunway);
        Assert.True(vm.SessionLiveTrafficEnabled);
        Assert.Equal(7_000, vm.SessionLiveTrafficCeilingFt);
        Assert.True(vm.SessionValidateDctFixes);
        Assert.True(vm.SessionSoloTrainingMode);
        Assert.Equal(40, vm.SessionSoloParkingInitialCallupIntervalSeconds);
        Assert.Equal(75, vm.SessionSoloArrivalGeneratorRatePercent);
        Assert.True(vm.ShowSessionSoloParkingInitialCallupRate);
        Assert.True(vm.ShowSessionSoloArrivalGeneratorRate);
        Assert.True(vm.SessionRpoShowPilotSpeech);
    }

    [AvaloniaFact]
    public void ApplySessionSettings_ExplicitAutoDeleteOverrideSelectsOverrideOption()
    {
        var vm = new MainViewModel(new FakeFilePickerService());

        vm.ApplySessionSettings(
            new SessionSettingsDto(
                AutoDeleteOverride: "Parked",
                EffectiveAutoDeleteMode: "Parked",
                DepartureAutoDeleteDistanceNm: null,
                AutoAcceptDelaySeconds: 5,
                AutoClearedToLand: false,
                AutoCrossRunway: false,
                AutoPullUpToParallel: false,
                AutoGoAroundOnOccupiedRunway: false,
                AutoRejectTakeoffOnOccupiedRunway: false,
                AutoArrivalSpacingOnOccupiedRunway: false,
                ValidateDctFixes: true,
                SoloTrainingMode: false,
                SoloParkingInitialCallupRatePercent: 100,
                SoloArrivalGeneratorRatePercent: 100,
                SoloGoAroundProbabilityPercent: 0,
                HasSoloParkingInitialCallupSource: false,
                HasSoloArrivalGeneratorSource: false,
                RpoShowPilotSpeech: false
            )
        );

        Assert.Equal(3, vm.SessionAutoDeleteIndex);
        Assert.Equal("Parked", vm.ActiveAutoDeleteMode);
    }

    [AvaloniaFact]
    public void ApplySessionSettingsFromLoadScenarioResult_PopulatesFullFlyoutStateWithoutBroadcast()
    {
        var vm = new MainViewModel(new FakeFilePickerService());

        vm.ApplySessionSettings(
            new SessionSettingsDto(
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
                SoloTrainingMode: false,
                SoloParkingInitialCallupRatePercent: 100,
                SoloArrivalGeneratorRatePercent: 100,
                SoloGoAroundProbabilityPercent: 0,
                HasSoloParkingInitialCallupSource: false,
                HasSoloArrivalGeneratorSource: false,
                RpoShowPilotSpeech: false
            )
        );

        vm.ApplySessionSettingsFromLoadScenarioResult(
            new LoadScenarioResultDto(
                Success: true,
                Name: "Test",
                ScenarioId: "scenario-1",
                AircraftCount: 0,
                DelayedCount: 0,
                IsPaused: true,
                SimRate: 1,
                PrimaryAirportId: "SFO",
                Warnings: [],
                AllAircraft: [],
                ActiveRunways: [],
                ActiveRunwaysPrefill: [],
                ActiveRunwaysPromptNeeded: false,
                ActiveRunwaysAssigned: [],
                AutoDeleteOverride: null,
                EffectiveAutoDeleteMode: "Parked",
                AutoAcceptDelaySeconds: 5,
                AutoClearedToLand: true,
                AutoCrossRunway: true,
                ValidateDctFixes: true,
                SoloTrainingMode: true,
                SoloParkingInitialCallupRatePercent: 100,
                SoloArrivalGeneratorRatePercent: 65,
                HasSoloParkingInitialCallupSource: true,
                HasSoloArrivalGeneratorSource: true,
                RpoShowPilotSpeech: true
            )
        );

        Assert.Equal(0, vm.SessionAutoDeleteIndex);
        Assert.Equal("Parked", vm.ActiveAutoDeleteMode);
        Assert.Equal(5, vm.SessionAutoAcceptDelaySeconds);
        Assert.True(vm.SessionAutoClearedToLand);
        Assert.True(vm.SessionAutoCrossRunway);
        Assert.True(vm.SessionValidateDctFixes);
        Assert.True(vm.SessionSoloTrainingMode);
        Assert.Equal(20, vm.SessionSoloParkingInitialCallupIntervalSeconds);
        Assert.Equal(65, vm.SessionSoloArrivalGeneratorRatePercent);
        Assert.True(vm.SessionRpoShowPilotSpeech);
    }

    [AvaloniaFact]
    public void ApplySessionSettings_UsesDepartureAutoDeleteDistanceForTheBox_AndNullBlanksIt()
    {
        var vm = new MainViewModel(new FakeFilePickerService());

        vm.ApplySessionSettings(SessionSettingsWithDepartureDistance(30));
        Assert.Equal(30m, vm.SessionDepartureAutoDeleteDistanceNm);

        vm.ApplySessionSettings(SessionSettingsWithDepartureDistance(null));
        Assert.Null(vm.SessionDepartureAutoDeleteDistanceNm);
    }

    [AvaloniaFact]
    public void ApplySessionSettingsFromLoadScenarioResult_CarriesDepartureAutoDeleteDistance()
    {
        var vm = new MainViewModel(new FakeFilePickerService());

        vm.ApplySessionSettingsFromLoadScenarioResult(
            new LoadScenarioResultDto(
                Success: true,
                Name: "Test",
                ScenarioId: "scenario-1",
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
                DepartureAutoDeleteDistanceNm: 45
            )
        );

        Assert.Equal(45m, vm.SessionDepartureAutoDeleteDistanceNm);
    }

    private static SessionSettingsDto SessionSettingsWithDepartureDistance(double? distanceNm) =>
        new(
            AutoDeleteOverride: null,
            EffectiveAutoDeleteMode: null,
            DepartureAutoDeleteDistanceNm: distanceNm,
            AutoAcceptDelaySeconds: -1,
            AutoClearedToLand: false,
            AutoCrossRunway: false,
            AutoPullUpToParallel: false,
            AutoGoAroundOnOccupiedRunway: false,
            AutoRejectTakeoffOnOccupiedRunway: false,
            AutoArrivalSpacingOnOccupiedRunway: false,
            ValidateDctFixes: false,
            SoloTrainingMode: false,
            SoloParkingInitialCallupRatePercent: 100,
            SoloArrivalGeneratorRatePercent: 100,
            SoloGoAroundProbabilityPercent: 0,
            HasSoloParkingInitialCallupSource: false,
            HasSoloArrivalGeneratorSource: false,
            RpoShowPilotSpeech: false
        );

    /// <summary>
    /// The flyout's auto arrival spacing toggle is greyed out for a student who is themselves the approach
    /// controller: the sim has nobody to do the spacing then. Ground (and every other position, including none at
    /// all once the scenario is unloaded) leaves it live.
    /// </summary>
    [AvaloniaFact]
    public void AutoArrivalSpacingApplies_IsFalseForAnApproachStudent_AndTrueForGround()
    {
        var vm = new MainViewModel(new FakeFilePickerService());

        vm.SetStudentPositionType("APP");
        Assert.False(vm.SessionAutoArrivalSpacingApplies);

        vm.SetStudentPositionType("CTR");
        Assert.False(vm.SessionAutoArrivalSpacingApplies);

        vm.SetStudentPositionType("GND");
        Assert.True(vm.SessionAutoArrivalSpacingApplies);

        vm.SetStudentPositionType("APP");
        vm.ClearScenarioState();
        Assert.True(vm.SessionAutoArrivalSpacingApplies);
    }

    private const string ScenarioWithParkingAndArrivalGenerator = """
        {
          "aircraftGenerators": [
            { "id": "G1", "runway": "30", "intervalTime": 300 }
          ],
          "aircraft": [
            { "callsign": "A1", "startingConditions": { "type": "Parking", "parking": "A1" } }
          ]
        }
        """;

    /// <summary>
    /// The setup dialog seeds its pacing sliders from the Settings defaults, but its choices are for this load only:
    /// only Settings changes the stored defaults.
    /// </summary>
    [AvaloniaFact(Timeout = 60_000)]
    public async Task ConfirmingTheSetupDialog_LeavesTheStoredPacingDefaultsAlone()
    {
        using var scope = new PreferencesFileScope();
        var stored = new UserPreferences();
        stored.SetSoloTrainingMode(true);
        stored.SetSoloPacingRates(50, 35);
        var vm = new MainViewModel(new FakeFilePickerService());

        await vm.LoadScenarioFromJsonAsync(ScenarioWithParkingAndArrivalGenerator, "Pacing test");

        Assert.True(vm.ShowScenarioSetup);
        Assert.Equal(40, vm.ScenarioSetupParkingInitialCallupIntervalSeconds);
        Assert.Equal(35, vm.ScenarioSetupArrivalGeneratorRatePercent);

        vm.ScenarioSetupParkingInitialCallupIntervalSeconds = 100;
        vm.ScenarioSetupArrivalGeneratorRatePercent = 80;
        await vm.ConfirmScenarioSetupCommand.ExecuteAsync(null);

        var reread = new UserPreferences();
        Assert.Equal(50, reread.SoloParkingInitialCallupRatePercent);
        Assert.Equal(35, reread.SoloArrivalGeneratorRatePercent);
    }

    [AvaloniaFact]
    public void ApplySessionSettings_NegativeAutoAcceptDelay_TurnsAutoAcceptOff_AndKeepsThePreviousDelay()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        vm.ApplySessionSettings(SessionSettingsWithAutoAcceptDelay(12));

        vm.ApplySessionSettings(SessionSettingsWithAutoAcceptDelay(-1));

        Assert.False(vm.SessionAutoAcceptEnabled);
        Assert.Equal(12, vm.SessionAutoAcceptDelaySeconds);
    }

    [AvaloniaFact]
    public void ApplySessionSettings_AutoAcceptDelay_TurnsAutoAcceptOn_WithThatDelay()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        vm.ApplySessionSettings(SessionSettingsWithAutoAcceptDelay(-1));

        vm.ApplySessionSettings(SessionSettingsWithAutoAcceptDelay(12));

        Assert.True(vm.SessionAutoAcceptEnabled);
        Assert.Equal(12, vm.SessionAutoAcceptDelaySeconds);
    }

    [AvaloniaTheory]
    [InlineData("TWR", "Auto cleared-to-land (TWR)", "Auto arrival spacing (TWR)")]
    [InlineData("GND", "Auto cleared-to-land (GND)", "Auto arrival spacing (GND)")]
    [InlineData("DEL", "Auto cleared-to-land", "Auto arrival spacing")]
    [InlineData(null, "Auto cleared-to-land", "Auto arrival spacing")]
    public void FlyoutLabels_NameTheStudentPositionType(string? positionType, string clearedToLand, string arrivalSpacing)
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        vm.SetStudentPositionType("APP");
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.SetStudentPositionType(positionType);

        Assert.Equal(clearedToLand, vm.SessionAutoClearedToLandLabel);
        Assert.Equal(arrivalSpacing, vm.SessionAutoArrivalSpacingLabel);
        Assert.Contains(nameof(MainViewModel.SessionAutoClearedToLandLabel), raised);
        Assert.Contains(nameof(MainViewModel.SessionAutoArrivalSpacingLabel), raised);
    }

    [AvaloniaFact]
    public void FlyoutLabels_ForAnApproachStudent_NameApp_AndGreyArrivalSpacing()
    {
        var vm = new MainViewModel(new FakeFilePickerService());

        vm.SetStudentPositionType("APP");

        Assert.Equal("Auto cleared-to-land (APP)", vm.SessionAutoClearedToLandLabel);
        Assert.False(vm.SessionAutoArrivalSpacingApplies);
    }

    private static SessionSettingsDto SessionSettingsWithAutoAcceptDelay(int delaySeconds) =>
        SessionSettingsWithDepartureDistance(null) with
        {
            AutoAcceptDelaySeconds = delaySeconds,
        };

    [AvaloniaFact]
    public void LiveTrafficAvailable_IsFalseUntilTheServerReportsTheGateOn()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        Assert.False(vm.LiveTrafficAvailable);

        vm.LiveTrafficStatus = new LiveTrafficStatusDto(
            FeedConfigured: false,
            Connected: false,
            LastMessageAgeSeconds: null,
            TracksInScope: 0,
            FeedTimeUtc: null,
            BehindSeconds: null,
            Preparing: false
        );
        Assert.False(vm.LiveTrafficAvailable);

        vm.LiveTrafficStatus = new LiveTrafficStatusDto(
            FeedConfigured: true,
            Connected: false,
            LastMessageAgeSeconds: null,
            TracksInScope: 0,
            FeedTimeUtc: null,
            BehindSeconds: null,
            Preparing: false
        );
        Assert.True(vm.LiveTrafficAvailable);
    }

    [AvaloniaFact]
    public void LiveTrafficAvailable_RaisesChangeNotification_WhenTheStatusArrives()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.LiveTrafficStatus = new LiveTrafficStatusDto(
            FeedConfigured: true,
            Connected: true,
            LastMessageAgeSeconds: 1,
            TracksInScope: 3,
            FeedTimeUtc: null,
            BehindSeconds: null,
            Preparing: false
        );

        Assert.Contains(nameof(MainViewModel.LiveTrafficAvailable), raised);
        Assert.True(vm.LiveTrafficAvailable);
    }
}
