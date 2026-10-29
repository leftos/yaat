using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Sim.Data.Airport;

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
                AutoCrossRunwayMode: AutoCrossRunwayMode.All,
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
        Assert.Equal(AutoCrossRunwayMode.All, vm.SessionAutoCrossRunwayMode);
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
                AutoCrossRunwayMode: AutoCrossRunwayMode.Off,
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
                AutoCrossRunwayMode: AutoCrossRunwayMode.Off,
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
                SessionSetup: null,
                ToweredAirports: [],
                AutoDeleteOverride: null,
                EffectiveAutoDeleteMode: "Parked",
                AutoAcceptDelaySeconds: 5,
                AutoClearedToLand: true,
                AutoCrossRunwayMode: AutoCrossRunwayMode.All,
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
        Assert.Equal(AutoCrossRunwayMode.All, vm.SessionAutoCrossRunwayMode);
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
                SessionSetup: null,
                ToweredAirports: [],
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
            AutoCrossRunwayMode: AutoCrossRunwayMode.Off,
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

    [AvaloniaFact]
    public void TypedDigits_WithinIdle_CommitOnlyLastValue()
    {
        (MainViewModel vm, ManualTimeProvider clock, List<SessionSettingKey> commits) = NewDebouncedVm();
        var committedValues = new List<int>();
        vm.SessionCommitter.Committed += _ => committedValues.Add(vm.SessionCommandRunDelayMinSeconds);

        vm.SessionCommandRunDelayMinSeconds = 1;
        AdvanceClock(clock, TimeSpan.FromMilliseconds(500));
        vm.SessionCommandRunDelayMinSeconds = 15;
        AdvanceClock(clock, TimeSpan.FromMilliseconds(999));

        Assert.Empty(commits);

        AdvanceClock(clock, TimeSpan.FromMilliseconds(1));

        Assert.Equal<SessionSettingKey>([SessionSettingKey.RunDelay], commits);
        Assert.Equal([15], committedValues);
    }

    [AvaloniaFact]
    public void DepartureDistance_NeverCommitsOnIdle()
    {
        (MainViewModel vm, ManualTimeProvider clock, List<SessionSettingKey> commits) = NewDebouncedVm();

        vm.SessionDepartureAutoDeleteDistanceNm = 1m;
        AdvanceClock(clock, TimeSpan.FromSeconds(5));
        Assert.Empty(commits);

        vm.SessionDepartureAutoDeleteDistanceNm = 5m;
        AdvanceClock(clock, TimeSpan.FromSeconds(5));
        Assert.Empty(commits);

        var committedValues = new List<decimal?>();
        vm.SessionCommitter.Committed += _ => committedValues.Add(vm.SessionDepartureAutoDeleteDistanceNm);
        vm.FlushPendingSessionSettings();

        Assert.Equal<SessionSettingKey>([SessionSettingKey.DepartureDistance], commits);
        Assert.Equal<decimal?>([5m], committedValues);
    }

    [AvaloniaFact]
    public void Steps_TrailingDebounce_SendsFinalValueOnce()
    {
        (MainViewModel vm, ManualTimeProvider clock, List<SessionSettingKey> commits) = NewDebouncedVm();

        for (int step = 1; step <= 5; step++)
        {
            vm.SessionCommandRunDelayMaxSeconds = step;
            vm.StepSessionSetting(SessionSettingKey.RunDelay);
            AdvanceClock(clock, TimeSpan.FromMilliseconds(50));
        }

        AdvanceClock(clock, TimeSpan.FromMilliseconds(149));
        Assert.Empty(commits);

        AdvanceClock(clock, TimeSpan.FromMilliseconds(1));

        Assert.Equal<SessionSettingKey>([SessionSettingKey.RunDelay], commits);
        Assert.Equal(5, vm.SessionCommandRunDelayMaxSeconds);
    }

    [AvaloniaFact]
    public void Flush_SendsEveryPendingKey_Once()
    {
        (MainViewModel vm, _, List<SessionSettingKey> commits) = NewDebouncedVm();

        vm.SessionAutoAcceptDelaySeconds = 7;
        vm.SessionCommandRunDelayMinSeconds = 3;
        vm.SessionLiveTrafficCeilingFt = 5_000;
        vm.SessionDepartureAutoDeleteDistanceNm = 25m;

        vm.FlushPendingSessionSettings();

        Assert.Equal(4, commits.Count);
        Assert.Equal(4, commits.Distinct().Count());

        vm.FlushPendingSessionSettings();
        Assert.Equal(4, commits.Count);
    }

    [AvaloniaFact]
    public void ServerPush_AfterAFlush_StaleMinFlagDoesNotBlockMin()
    {
        (MainViewModel vm, ManualTimeProvider clock, _) = NewDebouncedVm();
        var committedPairs = new List<(int Min, int Max)>();
        vm.SessionCommitter.Committed += _ => committedPairs.Add((vm.SessionCommandRunDelayMinSeconds, vm.SessionCommandRunDelayMaxSeconds));

        vm.SessionCommandRunDelayMaxSeconds = 20;
        vm.FlushPendingSessionSettings();
        Assert.Single(committedPairs);

        // Enter sent the max long ago: only min waits now, so max is free to take the pushed value.
        vm.SessionCommandRunDelayMinSeconds = 15;

        vm.ApplySessionSettings(SessionSettingsWithDepartureDistance(null) with { CommandRunDelayMinSeconds = 5, CommandRunDelayMaxSeconds = 9 });

        Assert.Equal(15, vm.SessionCommandRunDelayMinSeconds);
        Assert.Equal(9, vm.SessionCommandRunDelayMaxSeconds);

        AdvanceClock(clock, TimeSpan.FromSeconds(2));

        Assert.Equal(2, committedPairs.Count);
        Assert.Equal((15, 9), committedPairs[1]);
    }

    [AvaloniaFact]
    public void ServerPush_WhilePendingMin_UpdatesMaxAndCommitsTypedMinWithPushedMax()
    {
        (MainViewModel vm, ManualTimeProvider clock, _) = NewDebouncedVm();
        var committedPairs = new List<(int Min, int Max)>();
        vm.SessionCommitter.Committed += _ => committedPairs.Add((vm.SessionCommandRunDelayMinSeconds, vm.SessionCommandRunDelayMaxSeconds));

        vm.SessionCommandRunDelayMinSeconds = 15;

        vm.ApplySessionSettings(SessionSettingsWithDepartureDistance(null) with { CommandRunDelayMinSeconds = 5, CommandRunDelayMaxSeconds = 9 });

        Assert.Equal(15, vm.SessionCommandRunDelayMinSeconds);
        Assert.Equal(9, vm.SessionCommandRunDelayMaxSeconds);

        AdvanceClock(clock, TimeSpan.FromSeconds(2));

        Assert.Single(committedPairs);
        Assert.Equal((15, 9), committedPairs[0]);
    }

    [AvaloniaFact]
    public void ServerPush_WhilePendingMax_UpdatesMinAndCommitsPushedMinWithTypedMax()
    {
        (MainViewModel vm, ManualTimeProvider clock, _) = NewDebouncedVm();
        var committedPairs = new List<(int Min, int Max)>();
        vm.SessionCommitter.Committed += _ => committedPairs.Add((vm.SessionCommandRunDelayMinSeconds, vm.SessionCommandRunDelayMaxSeconds));

        vm.SessionCommandRunDelayMaxSeconds = 15;

        vm.ApplySessionSettings(SessionSettingsWithDepartureDistance(null) with { CommandRunDelayMinSeconds = 5, CommandRunDelayMaxSeconds = 9 });

        Assert.Equal(5, vm.SessionCommandRunDelayMinSeconds);
        Assert.Equal(15, vm.SessionCommandRunDelayMaxSeconds);

        AdvanceClock(clock, TimeSpan.FromSeconds(2));

        Assert.Single(committedPairs);
        Assert.Equal((5, 15), committedPairs[0]);
    }

    [AvaloniaFact]
    public void ServerPush_WhileBothHalvesTyped_DropsThePushWhole()
    {
        (MainViewModel vm, ManualTimeProvider clock, _) = NewDebouncedVm();
        var committedPairs = new List<(int Min, int Max)>();
        vm.SessionCommitter.Committed += _ => committedPairs.Add((vm.SessionCommandRunDelayMinSeconds, vm.SessionCommandRunDelayMaxSeconds));

        vm.SessionCommandRunDelayMinSeconds = 15;
        vm.SessionCommandRunDelayMaxSeconds = 20;

        vm.ApplySessionSettings(SessionSettingsWithDepartureDistance(null) with { CommandRunDelayMinSeconds = 5, CommandRunDelayMaxSeconds = 9 });

        Assert.Equal(15, vm.SessionCommandRunDelayMinSeconds);
        Assert.Equal(20, vm.SessionCommandRunDelayMaxSeconds);

        AdvanceClock(clock, TimeSpan.FromSeconds(2));

        Assert.Single(committedPairs);
        Assert.Equal((15, 20), committedPairs[0]);
    }

    [AvaloniaFact]
    public void ServerPush_AfterACommit_StaleFlagDoesNotBlockTheOtherHalf()
    {
        (MainViewModel vm, ManualTimeProvider clock, _) = NewDebouncedVm();
        var committedPairs = new List<(int Min, int Max)>();
        vm.SessionCommitter.Committed += _ => committedPairs.Add((vm.SessionCommandRunDelayMinSeconds, vm.SessionCommandRunDelayMaxSeconds));

        vm.SessionCommandRunDelayMinSeconds = 15;
        AdvanceClock(clock, TimeSpan.FromSeconds(2));
        Assert.Single(committedPairs);

        // The min edit was sent long ago: only max is waiting now, so min is free to take the pushed value.
        vm.SessionCommandRunDelayMaxSeconds = 20;

        vm.ApplySessionSettings(SessionSettingsWithDepartureDistance(null) with { CommandRunDelayMinSeconds = 4, CommandRunDelayMaxSeconds = 9 });

        Assert.Equal(4, vm.SessionCommandRunDelayMinSeconds);
        Assert.Equal(20, vm.SessionCommandRunDelayMaxSeconds);

        AdvanceClock(clock, TimeSpan.FromSeconds(2));

        Assert.Equal(2, committedPairs.Count);
        Assert.Equal((4, 20), committedPairs[1]);
    }

    [AvaloniaFact]
    public void ServerPush_WhileAutoAcceptDelayTyped_TurnsCheckboxOnAndKeepsTypedDelay()
    {
        (MainViewModel vm, ManualTimeProvider clock, _) = NewDebouncedVm();
        var committedWire = new List<int>();
        vm.SessionCommitter.Committed += _ =>
            committedWire.Add(SessionAutoAcceptWire.ToWire(vm.SessionAutoAcceptEnabled, vm.SessionAutoAcceptDelaySeconds));

        // The checkbox starts off, and only the delay is typed, so that is the half waiting to be sent.
        vm.SessionAutoAcceptDelaySeconds = 12;

        vm.ApplySessionSettings(SessionSettingsWithAutoAcceptDelay(30));

        Assert.True(vm.SessionAutoAcceptEnabled);
        Assert.Equal(12, vm.SessionAutoAcceptDelaySeconds);

        AdvanceClock(clock, TimeSpan.FromSeconds(2));

        Assert.Equal([12], committedWire);
    }

    [AvaloniaFact]
    public void ServerPush_WhileAutoAcceptDelayTyped_TurnsCheckboxOffAndKeepsTypedDelay()
    {
        (MainViewModel vm, ManualTimeProvider clock, _) = NewDebouncedVm();
        vm.SessionAutoAcceptEnabled = true;

        // The checkbox committed at once; the delay typed next is the half waiting to be sent.
        var committedWire = new List<int>();
        vm.SessionCommitter.Committed += _ =>
            committedWire.Add(SessionAutoAcceptWire.ToWire(vm.SessionAutoAcceptEnabled, vm.SessionAutoAcceptDelaySeconds));

        vm.SessionAutoAcceptDelaySeconds = 12;

        vm.ApplySessionSettings(SessionSettingsWithAutoAcceptDelay(-1));

        Assert.False(vm.SessionAutoAcceptEnabled);
        Assert.Equal(12, vm.SessionAutoAcceptDelaySeconds);

        AdvanceClock(clock, TimeSpan.FromSeconds(2));

        Assert.Equal([-1], committedWire);
    }

    [AvaloniaFact]
    public void ServerPush_WhenNothingPending_DoesNotSend()
    {
        (MainViewModel vm, ManualTimeProvider clock, List<SessionSettingKey> commits) = NewDebouncedVm();

        vm.ApplySessionSettings(SessionSettingsWithDepartureDistance(null) with { CommandRunDelayMinSeconds = 5, CommandRunDelayMaxSeconds = 9 });
        AdvanceClock(clock, TimeSpan.FromSeconds(5));

        Assert.Empty(commits);
        Assert.Equal(5, vm.SessionCommandRunDelayMinSeconds);
        Assert.Equal(9, vm.SessionCommandRunDelayMaxSeconds);
    }

    [AvaloniaFact]
    public void RunDelayPair_SendsLatestMinAndMax_Once()
    {
        (MainViewModel vm, ManualTimeProvider clock, List<SessionSettingKey> commits) = NewDebouncedVm();
        var committedPairs = new List<(int Min, int Max)>();
        vm.SessionCommitter.Committed += _ => committedPairs.Add((vm.SessionCommandRunDelayMinSeconds, vm.SessionCommandRunDelayMaxSeconds));

        vm.SessionCommandRunDelayMinSeconds = 2;
        vm.SessionCommandRunDelayMaxSeconds = 8;
        AdvanceClock(clock, TimeSpan.FromSeconds(1));

        Assert.Equal<SessionSettingKey>([SessionSettingKey.RunDelay], commits);
        Assert.Single(committedPairs);
        Assert.Equal((2, 8), committedPairs[0]);
    }

    [AvaloniaFact]
    public void AutoAcceptCheckbox_CommitsAtOnce_WithCurrentDelay()
    {
        (MainViewModel vm, ManualTimeProvider clock, List<SessionSettingKey> commits) = NewDebouncedVm();
        var committedPairs = new List<(bool Enabled, int Delay)>();
        vm.SessionCommitter.Committed += _ => committedPairs.Add((vm.SessionAutoAcceptEnabled, vm.SessionAutoAcceptDelaySeconds));

        vm.SessionAutoAcceptDelaySeconds = 12;
        Assert.Empty(commits);

        vm.SessionAutoAcceptEnabled = true;

        Assert.Equal<SessionSettingKey>([SessionSettingKey.AutoAccept], commits);
        Assert.Equal((true, 12), committedPairs.Single());

        AdvanceClock(clock, TimeSpan.FromSeconds(5));
        Assert.Single(commits);
    }

    [AvaloniaFact]
    public void LeavingRoom_DiscardsPending_WithoutSending()
    {
        (MainViewModel vm, ManualTimeProvider clock, List<SessionSettingKey> commits) = NewDebouncedVm();
        vm.SessionCommandRunDelayMinSeconds = 4;
        Assert.True(vm.SessionCommitter.IsPending(SessionSettingKey.RunDelay));

        vm.ClearScenarioState();
        AdvanceClock(clock, TimeSpan.FromSeconds(5));

        Assert.False(vm.SessionCommitter.IsPending(SessionSettingKey.RunDelay));
        Assert.Empty(commits);
    }

    [AvaloniaFact]
    public void TimerCommit_RunsOnTheUiThread()
    {
        (MainViewModel vm, ManualTimeProvider clock, List<SessionSettingKey> commits) = NewDebouncedVm();
        bool ranOnUiThread = false;
        vm.SessionCommitter.Committed += _ => ranOnUiThread = Dispatcher.UIThread.CheckAccess();

        vm.SessionCommandRunDelayMinSeconds = 3;

        // The real provider fires timers on the thread pool, never the UI thread.
        Task.Run(() => clock.Advance(TimeSpan.FromSeconds(1))).GetAwaiter().GetResult();
        Dispatcher.UIThread.RunJobs();

        Assert.True(ranOnUiThread);
        Assert.Equal<SessionSettingKey>([SessionSettingKey.RunDelay], commits);
    }

    [AvaloniaFact]
    public void ServerPush_BetweenTimerAndPostedCommit_KeepsTypedValue()
    {
        (MainViewModel vm, ManualTimeProvider clock, List<SessionSettingKey> commits) = NewDebouncedVm();
        var committedValues = new List<int>();
        vm.SessionCommitter.Committed += _ => committedValues.Add(vm.SessionCommandRunDelayMinSeconds);

        vm.SessionCommandRunDelayMinSeconds = 15;
        Task.Run(() => clock.Advance(TimeSpan.FromSeconds(1))).GetAwaiter().GetResult();
        Assert.True(vm.SessionCommitter.IsPending(SessionSettingKey.RunDelay));
        Assert.Empty(commits);

        // The timer has fired, but its commit is still queued behind this push.
        vm.ApplySessionSettings(SessionSettingsWithDepartureDistance(null) with { CommandRunDelayMinSeconds = 5, CommandRunDelayMaxSeconds = 9 });
        Assert.Equal(15, vm.SessionCommandRunDelayMinSeconds);

        Dispatcher.UIThread.RunJobs();

        Assert.Equal<SessionSettingKey>([SessionSettingKey.RunDelay], commits);
        Assert.Equal([15], committedValues);
    }

    [AvaloniaFact]
    public void QueuedTimerCommit_DoesNotSendAValueTypedAfterItFired()
    {
        (MainViewModel vm, ManualTimeProvider clock, List<SessionSettingKey> commits) = NewDebouncedVm();

        vm.SessionDepartureAutoDeleteDistanceNm = 20m;
        vm.StepSessionSetting(SessionSettingKey.DepartureDistance);

        // The step's timer fires on the pool thread, so its commit is queued for the UI thread.
        Task.Run(() => clock.Advance(TimeSpan.FromMilliseconds(200))).GetAwaiter().GetResult();

        // Typing re-marks the key before the queued commit gets to run: the typed value must not be sent on idle.
        vm.SessionDepartureAutoDeleteDistanceNm = 1m;
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(commits);
        Assert.True(vm.SessionCommitter.IsPending(SessionSettingKey.DepartureDistance));
    }

    /// <summary>Moves the clock, then lets the UI thread run what the fired timers posted.</summary>
    private static void AdvanceClock(ManualTimeProvider clock, TimeSpan delta)
    {
        clock.Advance(delta);
        Dispatcher.UIThread.RunJobs();
    }

    private static (MainViewModel Vm, ManualTimeProvider Clock, List<SessionSettingKey> Commits) NewDebouncedVm()
    {
        var clock = new ManualTimeProvider();
        var vm = new MainViewModel(new FakeFilePickerService(), clock);
        var commits = new List<SessionSettingKey>();
        vm.SessionCommitter.Committed += commits.Add;
        return (vm, clock, commits);
    }
}
