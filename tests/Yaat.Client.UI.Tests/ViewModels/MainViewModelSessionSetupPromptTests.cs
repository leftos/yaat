using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Testing;

namespace Yaat.Client.UI.Tests.ViewModels;

/// <summary>
/// The session-setup popup: its active-runways section for the loading mentor and its worked-positions section for the
/// solo student, opened from the load result or the reply to switching solo training on, answered with one OK that sends
/// the runways first and the level second, and closed by Cancel without sending anything. Also the flyout's
/// "Positions you work" row, which sends a change at once and puts a refused one back.
/// </summary>
public class MainViewModelSessionSetupPromptTests
{
    private static MainViewModel NewVm() => new(new FakeFilePickerService());

    private static NavigationDatabase NavDb()
    {
        TestVnasData.EnsureInitialized();
        return TestVnasData.NavigationDb ?? throw new InvalidOperationException("Test navdata did not load");
    }

    private static ActiveRunwaysSetupDto OakRunways() => new(new Dictionary<string, List<string>>(StringComparer.Ordinal) { ["OAK"] = ["28R"] }, []);

    private static WorkedPositionsSetupDto GroundPositions() => new("GND", "GND", ["GND", "DEL"]);

    private static LoadScenarioResultDto LoadResult(ActiveRunwaysSetupDto? runways, WorkedPositionsSetupDto? positions) =>
        MainViewModelActiveRunwaysTests.LoadResult([], [], promptNeeded: false) with
        {
            SessionSetup = new SessionSetupPromptDto(runways, positions),
        };

    private static MainViewModel PromptedVm(ActiveRunwaysSetupDto? runways, WorkedPositionsSetupDto? positions)
    {
        MainViewModel vm = NewVm();
        vm.ApplyLoadResultSessionSetup(LoadResult(runways, positions));
        return vm;
    }

    /// <summary>A stand-in for the hub: records every command and level sent, in order, and answers each as told.</summary>
    private sealed class RecordingHub(Func<string, CommandResultDto> levelReply)
    {
        public List<string> Calls { get; } = [];

        public Task<CommandResultDto> SendCommand(string command)
        {
            Calls.Add(command);
            return Task.FromResult(new CommandResultDto(true, null));
        }

        public Task<CommandResultDto> SetLevel(string level)
        {
            Calls.Add($"SetStudentLowestWorkedLevel {level}");
            return Task.FromResult(levelReply(level));
        }
    }

    private static RecordingHub AcceptAll() => new(_ => new CommandResultDto(true, null));

    private static SessionSettingsDto SoloSettings(bool solo, string? lowest, string? own) =>
        new(
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
            SoloTrainingMode: solo,
            SoloParkingInitialCallupRatePercent: 100,
            SoloArrivalGeneratorRatePercent: 100,
            SoloGoAroundProbabilityPercent: 0,
            HasSoloParkingInitialCallupSource: false,
            HasSoloArrivalGeneratorSource: false,
            RpoShowPilotSpeech: false,
            StudentLowestWorkedLevel: lowest,
            StudentOwnLevel: own
        );

    // --- Opening ---

    [AvaloniaFact]
    public void RunwaysOnly_OpensWithTheRunwaysSection()
    {
        MainViewModel vm = PromptedVm(OakRunways(), null);

        Assert.True(vm.ShowSessionSetup);
        Assert.True(vm.ShowActiveRunwaysPrompt);
        Assert.False(vm.ShowWorkedPositionsPrompt);
        Assert.False(vm.ShowSessionSetupDivider);
        Assert.Equal(["OAK"], vm.ActiveRunwaysPromptRows.Select(row => row.Airport));
    }

    [AvaloniaFact]
    public void PositionsOnly_FromTheLoadResult_PrefillsOwnLevel()
    {
        MainViewModel vm = PromptedVm(null, GroundPositions());

        Assert.True(vm.ShowSessionSetup);
        Assert.False(vm.ShowActiveRunwaysPrompt);
        Assert.True(vm.ShowWorkedPositionsPrompt);
        Assert.Equal(
            [("GND", "Ground only", true), ("DEL", "Ground and Delivery", false)],
            vm.WorkedPositionsPromptChoices.Select(row => (row.Code, row.Label, row.IsSelected))
        );
    }

    [AvaloniaFact]
    public void PositionsSection_InALiveSession_NeverOpens()
    {
        MainViewModel vm = NewVm();

        vm.ApplyLoadResultSessionSetup(LoadResult(null, GroundPositions()) with { IsLiveSession = true });

        Assert.False(vm.ShowWorkedPositionsPrompt);
        Assert.False(vm.ShowSessionSetup);
    }

    [AvaloniaFact]
    public void SoloSwitchedOn_ReplyOpensThePositionsSection()
    {
        MainViewModel vm = PromptedVm(OakRunways(), null);

        vm.ApplySoloTrainingModeReply(new SessionSetupPromptDto(null, new WorkedPositionsSetupDto("APP", "TWR", ["APP", "TWR", "GND", "DEL"])));

        Assert.True(vm.ShowWorkedPositionsPrompt);
        Assert.True(vm.ShowActiveRunwaysPrompt);
        Assert.True(vm.ShowSessionSetupDivider);
        Assert.Equal(["OAK"], vm.ActiveRunwaysPromptRows.Select(row => row.Airport));
        Assert.Equal(["APP", "TWR", "GND", "DEL"], vm.WorkedPositionsPromptChoices.Select(row => row.Code));
        Assert.Equal("TWR", Assert.Single(vm.WorkedPositionsPromptChoices, row => row.IsSelected).Code);
    }

    [AvaloniaFact]
    public void SoloReply_WithSectionOpen_KeepsTheStudentsChoice()
    {
        MainViewModel vm = PromptedVm(null, GroundPositions());
        vm.WorkedPositionsPromptChoices[1].IsSelected = true;

        vm.ApplySoloTrainingModeReply(new SessionSetupPromptDto(null, GroundPositions()));

        Assert.Equal("DEL", Assert.Single(vm.WorkedPositionsPromptChoices, row => row.IsSelected).Code);
    }

    [AvaloniaFact]
    public async Task StaleSoloReply_AfterAnotherLoad_OpensNothing()
    {
        MainViewModel vm = NewVm();
        var reply = new TaskCompletionSource<SessionSetupPromptDto?>();
        Task pending = vm.RequestSoloTrainingModeAsync(() => reply.Task);

        vm.OnScenarioLoaded(new ScenarioLoadedDto("scenario-2", "OAK Ground", "OAK", true, 1, [], [], []));
        Dispatcher.UIThread.RunJobs();
        reply.SetResult(new SessionSetupPromptDto(null, GroundPositions()));
        await pending;
        Dispatcher.UIThread.RunJobs();

        Assert.False(vm.ShowWorkedPositionsPrompt);
        Assert.False(vm.ShowSessionSetup);
    }

    // --- Answering ---

    [AvaloniaFact]
    public async Task BothSections_Ok_SendsArwyThenTheLevel()
    {
        MainViewModel vm = PromptedVm(OakRunways(), GroundPositions());
        Assert.True(vm.ShowSessionSetupDivider);
        vm.WorkedPositionsPromptChoices[1].IsSelected = true;
        RecordingHub hub = AcceptAll();

        await vm.SubmitSessionSetupAsync(NavDb(), hub.SendCommand, hub.SetLevel);

        Assert.Equal(["ARWY OAK 28R", "SetStudentLowestWorkedLevel DEL"], hub.Calls);
        Assert.False(vm.ShowSessionSetup);
    }

    [AvaloniaFact]
    public async Task RefusedLevel_KeepsThePositionsSectionOpen_WithTheMessage()
    {
        MainViewModel vm = PromptedVm(null, GroundPositions());
        var hub = new RecordingHub(_ => new CommandResultDto(false, "Solo training is off"));

        await vm.SubmitSessionSetupAsync(NavDb(), hub.SendCommand, hub.SetLevel);

        Assert.Equal(["SetStudentLowestWorkedLevel GND"], hub.Calls);
        Assert.True(vm.ShowSessionSetup);
        Assert.True(vm.ShowWorkedPositionsPrompt);
        Assert.Equal("Solo training is off", vm.WorkedPositionsPromptError);
    }

    [AvaloniaFact]
    public async Task AcceptedLevel_ClosesTheSection()
    {
        MainViewModel vm = PromptedVm(null, GroundPositions());
        RecordingHub hub = AcceptAll();

        await vm.SubmitSessionSetupAsync(NavDb(), hub.SendCommand, hub.SetLevel);

        Assert.Equal(["SetStudentLowestWorkedLevel GND"], hub.Calls);
        Assert.False(vm.ShowWorkedPositionsPrompt);
        Assert.False(vm.ShowSessionSetup);
        Assert.Empty(vm.WorkedPositionsPromptChoices);
    }

    [AvaloniaFact]
    public void Cancel_SendsNothing()
    {
        MainViewModel vm = PromptedVm(OakRunways(), GroundPositions());
        RecordingHub hub = AcceptAll();
        vm.StudentLowestWorkedLevelSender = hub.SetLevel;

        vm.CancelSessionSetupCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(hub.Calls);
        Assert.False(vm.ShowSessionSetup);
        Assert.False(vm.ShowActiveRunwaysPrompt);
        Assert.False(vm.ShowWorkedPositionsPrompt);
    }

    [AvaloniaFact]
    public void Unload_ClosesThePositionsSection()
    {
        MainViewModel vm = PromptedVm(null, GroundPositions());

        vm.ClearScenarioState();

        Assert.False(vm.ShowWorkedPositionsPrompt);
        Assert.False(vm.ShowSessionSetup);
    }

    [AvaloniaFact]
    public void OnScenarioLoaded_ClosesAnOpenPositionsSection()
    {
        MainViewModel vm = PromptedVm(null, GroundPositions());

        vm.OnScenarioLoaded(new ScenarioLoadedDto("scenario-2", "OAK Ground", "OAK", true, 1, [], [], []));
        Dispatcher.UIThread.RunJobs();

        Assert.False(vm.ShowWorkedPositionsPrompt);
        Assert.False(vm.ShowSessionSetup);
    }

    [AvaloniaFact]
    public void SoloSwitchedOff_ClosesThePositionsSection()
    {
        MainViewModel vm = PromptedVm(null, GroundPositions());
        vm.ApplySessionSettings(SoloSettings(solo: true, lowest: "GND", own: "GND"));
        Assert.True(vm.ShowWorkedPositionsPrompt);

        vm.ApplySessionSettings(SoloSettings(solo: false, lowest: "GND", own: "GND"));

        Assert.False(vm.ShowWorkedPositionsPrompt);
        Assert.False(vm.ShowSessionSetup);
    }

    // --- The flyout row ---

    [AvaloniaFact]
    public void FlyoutChange_WhilePopupOpen_MovesTheRadio()
    {
        MainViewModel vm = PromptedVm(null, GroundPositions());
        RecordingHub hub = AcceptAll();
        vm.StudentLowestWorkedLevelSender = hub.SetLevel;
        vm.ApplySessionSettings(SoloSettings(solo: true, lowest: "GND", own: "GND"));

        vm.SessionStudentLowestWorkedLevel = "DEL";
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(["SetStudentLowestWorkedLevel DEL"], hub.Calls);
        Assert.True(vm.ShowWorkedPositionsPrompt);
        Assert.Equal("DEL", Assert.Single(vm.WorkedPositionsPromptChoices, row => row.IsSelected).Code);
    }

    [AvaloniaFact]
    public void FlyoutChange_CallsTheHub()
    {
        MainViewModel vm = NewVm();
        RecordingHub hub = AcceptAll();
        vm.StudentLowestWorkedLevelSender = hub.SetLevel;
        vm.ApplySessionSettings(SoloSettings(solo: true, lowest: "GND", own: "GND"));

        Assert.Equal(["Ground only", "Ground and Delivery"], vm.SessionWorkedPositionsChoices.Select(choice => choice.Label));
        Assert.Equal("GND", vm.SessionStudentLowestWorkedLevel);
        Assert.Empty(hub.Calls);

        vm.SessionStudentLowestWorkedLevel = "DEL";
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(["SetStudentLowestWorkedLevel DEL"], hub.Calls);
    }

    [AvaloniaFact]
    public void FlyoutRefused_PutsTheSelectionBack()
    {
        MainViewModel vm = NewVm();
        var hub = new RecordingHub(_ => new CommandResultDto(false, "Solo training is off"));
        vm.StudentLowestWorkedLevelSender = hub.SetLevel;
        vm.ApplySessionSettings(SoloSettings(solo: true, lowest: "GND", own: "GND"));

        vm.SessionStudentLowestWorkedLevel = "DEL";
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("GND", vm.SessionStudentLowestWorkedLevel);
        Assert.Equal(["SetStudentLowestWorkedLevel DEL"], hub.Calls);
        Assert.Equal("Solo training is off", vm.TerminalEntries[^1].Message);
    }

    [AvaloniaFact]
    public void Flyout_HiddenWhenNotSolo_OrDeliveryStudent()
    {
        MainViewModel vm = NewVm();

        vm.ApplySessionSettings(SoloSettings(solo: true, lowest: "GND", own: "GND"));
        Assert.True(vm.ShowSessionWorkedPositions);

        vm.ApplySessionSettings(SoloSettings(solo: false, lowest: "GND", own: "GND"));
        Assert.False(vm.ShowSessionWorkedPositions);

        vm.ApplySessionSettings(SoloSettings(solo: true, lowest: "DEL", own: "DEL"));
        Assert.False(vm.ShowSessionWorkedPositions);

        vm.ApplySessionSettings(SoloSettings(solo: true, lowest: null, own: null));
        Assert.False(vm.ShowSessionWorkedPositions);
    }
}
