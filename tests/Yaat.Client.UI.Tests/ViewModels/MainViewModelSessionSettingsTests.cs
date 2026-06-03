using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
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
                AutoAcceptDelaySeconds: 5,
                AutoClearedToLand: true,
                AutoCrossRunway: true,
                AutoPullUpToParallel: true,
                ValidateDctFixes: true,
                SoloTrainingMode: true,
                SoloParkingInitialCallupRatePercent: 50,
                SoloArrivalGeneratorRatePercent: 75,
                SoloGoAroundProbabilityPercent: 0,
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
                AutoAcceptDelaySeconds: 5,
                AutoClearedToLand: false,
                AutoCrossRunway: false,
                AutoPullUpToParallel: false,
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
                AutoAcceptDelaySeconds: -1,
                AutoClearedToLand: false,
                AutoCrossRunway: false,
                AutoPullUpToParallel: false,
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
}
