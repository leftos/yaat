using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;

namespace Yaat.Client.UI.Tests.ViewModels;

/// <summary>
/// The main window's side of a scenario load: progress events reach the overlay through the UI thread, and a load running
/// in the room (another member's, from the joined room's state) disables the lifecycle commands the server refuses.
/// </summary>
public class MainViewModelLoadOverlayTests
{
    private static MainViewModel MentorInRoom() =>
        new(new FakeFilePickerService())
        {
            IsConnected = true,
            ActiveRoomId = "room-1",
            IsNonMentor = false,
            ActiveScenarioId = "scenario-1",
        };

    private static RoomStateDto RoomState(string? scenarioName, string? loadingBy) =>
        new(
            RoomId: "room-1",
            CreatorInitials: "CX",
            CreatorArtccId: "ZOA",
            Members: [],
            ScenarioName: scenarioName,
            ScenarioId: scenarioName is null ? null : "scenario-1",
            IsPaused: true,
            SimRate: 1.0,
            PrimaryAirportId: scenarioName is null ? null : "OAK",
            AllAircraft: [],
            AircraftGenerators: [],
            VfrArrivalGenerators: [],
            OverflightGenerators: [],
            Positions: []
        )
        {
            LoadingBy = loadingBy,
        };

    private static bool[] LifecycleCanExecute(MainViewModel vm) =>
        [
            vm.CanLoadScenario,
            vm.LoadScenarioCommand.CanExecute(null),
            vm.UnloadScenarioCommand.CanExecute(null),
            vm.RestartScenarioCommand.CanExecute(null),
            vm.RewindToStartCommand.CanExecute(null),
            vm.RewindBack30Command.CanExecute(null),
            vm.RewindBack15Command.CanExecute(null),
            vm.SkipForward15Command.CanExecute(null),
            vm.SkipForward30Command.CanExecute(null),
            vm.JumpToEndCommand.CanExecute(null),
        ];

    /// <summary>Records each lifecycle command's <c>CanExecuteChanged</c> under its own name.</summary>
    private static Dictionary<string, bool> TrackLifecycleNotifications(MainViewModel vm)
    {
        var raised = new Dictionary<string, bool>();
        void Listen(IRelayCommand command, string name)
        {
            raised[name] = false;
            command.CanExecuteChanged += (_, _) => raised[name] = true;
        }

        Listen(vm.LoadScenarioCommand, "LoadScenario");
        Listen(vm.UnloadScenarioCommand, "UnloadScenario");
        Listen(vm.RestartScenarioCommand, "RestartScenario");
        Listen(vm.RewindToStartCommand, "RewindToStart");
        Listen(vm.RewindBack30Command, "RewindBack30");
        Listen(vm.RewindBack15Command, "RewindBack15");
        Listen(vm.SkipForward15Command, "SkipForward15");
        Listen(vm.SkipForward30Command, "SkipForward30");
        Listen(vm.JumpToEndCommand, "JumpToEnd");
        return raised;
    }

    private static void AssertAllNotified(Dictionary<string, bool> raised) =>
        Assert.All(raised, entry => Assert.True(entry.Value, $"{entry.Key} did not raise CanExecuteChanged"));

    [AvaloniaFact]
    public void OnScenarioLoadProgress_PostsToOverlay()
    {
        MainViewModel vm = MentorInRoom();
        vm.LoadOverlay.BeginLocal("oak-ground.json");
        var progress = new ScenarioLoadProgressDto(
            "load-a",
            1,
            "OAK Ground 7",
            false,
            [
                new LoadStepDto("read", "Read scenario", "done", "OAK Ground 7", []),
                new LoadStepDto("artcc", "ARTCC configuration", "running", null, []),
            ]
        );

        vm.OnScenarioLoadProgress(progress);

        Assert.Equal("Read scenario", Assert.Single(vm.LoadOverlay.Steps).Label);
        Assert.Equal("running", vm.LoadOverlay.Steps[0].State);

        Dispatcher.UIThread.RunJobs();

        Assert.Equal(["done", "running"], vm.LoadOverlay.Steps.Select(s => s.State));
        Assert.Equal("OAK Ground 7", vm.LoadOverlay.Title);
    }

    [AvaloniaFact]
    public void RoomStateLoadingBy_DisablesLoadUnloadRestartRewind()
    {
        MainViewModel vm = MentorInRoom();
        vm.ApplyRoomState(RoomState("OAK Ground 7", loadingBy: null));
        Assert.All(LifecycleCanExecute(vm), Assert.True);

        vm.ApplyRoomState(RoomState("OAK Ground 7", loadingBy: "AB"));

        Assert.Equal("AB", vm.RoomLoadingBy);
        Assert.All(LifecycleCanExecute(vm), Assert.False);

        vm.ApplyRoomState(RoomState("OAK Ground 7", loadingBy: null));

        Assert.Null(vm.RoomLoadingBy);
        Assert.All(LifecycleCanExecute(vm), Assert.True);
    }

    /// <summary>Mid-load the room state still names the previous scenario, so the status never does.</summary>
    [AvaloniaFact]
    public void RoomStateLoadingBy_SetsStatusText()
    {
        MainViewModel vm = MentorInRoom();

        vm.ApplyRoomState(RoomState("OAK Ground 7", loadingBy: "AB"));
        Assert.Equal("Loading a scenario (by AB)…", vm.StatusText);

        vm.ApplyRoomState(RoomState(scenarioName: null, loadingBy: "CD"));
        Assert.Equal("Loading a scenario (by CD)…", vm.StatusText);
    }

    [AvaloniaFact]
    public void OnRoomLoadingChanged_SetsAndClearsGating()
    {
        MainViewModel vm = MentorInRoom();
        vm.ApplyRoomState(RoomState("OAK Ground 7", loadingBy: null));

        vm.OnRoomLoadingChanged("AB");
        Assert.Null(vm.RoomLoadingBy);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("AB", vm.RoomLoadingBy);
        Assert.Equal("Loading a scenario (by AB)…", vm.StatusText);
        Assert.All(LifecycleCanExecute(vm), Assert.False);

        vm.OnRoomLoadingChanged(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Null(vm.RoomLoadingBy);
        Assert.All(LifecycleCanExecute(vm), Assert.True);
    }

    [AvaloniaFact]
    public void OwnIncompleteOverlay_DisablesLoadUnloadRestartRewind()
    {
        MainViewModel vm = MentorInRoom();
        vm.ApplyRoomState(RoomState("OAK Ground 7", loadingBy: null));

        vm.LoadOverlay.BeginLocal("oak-ground.json");

        Assert.True(vm.IsRoomLoading);
        Assert.All(LifecycleCanExecute(vm), Assert.False);

        vm.LoadOverlay.ApplyProgress(
            new ScenarioLoadProgressDto("load-a", 1, "OAK Ground 7", true, [new LoadStepDto("read", "Read scenario", "warning", null, ["x"])])
        );

        Assert.True(vm.LoadOverlay.IsOpen);
        Assert.False(vm.IsRoomLoading);
        Assert.All(LifecycleCanExecute(vm), Assert.True);
    }

    /// <summary>Another member's load ending says so, rather than leaving their loading status standing.</summary>
    [AvaloniaFact]
    public void OnRoomLoadingChanged_Cleared_ReplacesLoadingStatus()
    {
        MainViewModel vm = MentorInRoom();
        vm.OnRoomLoadingChanged("AB");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Loading a scenario (by AB)…", vm.StatusText);

        vm.OnRoomLoadingChanged(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Null(vm.RoomLoadingBy);
        Assert.Equal("Load by AB ended", vm.StatusText);
    }

    /// <summary>A status line written while the load ran is not overwritten by the load ending.</summary>
    [AvaloniaFact]
    public void OnRoomLoadingChanged_Cleared_KeepsOtherStatus()
    {
        MainViewModel vm = MentorInRoom();
        vm.OnRoomLoadingChanged("AB");
        Dispatcher.UIThread.RunJobs();

        vm.StatusText = "Joined room room-1";
        vm.OnRoomLoadingChanged(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Joined room room-1", vm.StatusText);
    }

    [AvaloniaFact]
    public void RoomLoadingBy_Change_RaisesCanExecuteChanged()
    {
        MainViewModel vm = MentorInRoom();
        Dictionary<string, bool> raised = TrackLifecycleNotifications(vm);

        vm.RoomLoadingBy = "AB";

        Assert.Equal("AB", vm.RoomLoadingBy);
        AssertAllNotified(raised);
    }

    [AvaloniaFact]
    public void OwnOverlayInFlight_Change_RaisesCanExecuteChanged()
    {
        MainViewModel vm = MentorInRoom();
        Dictionary<string, bool> raised = TrackLifecycleNotifications(vm);

        vm.LoadOverlay.BeginLocal("oak-ground.json");

        Assert.True(vm.IsRoomLoading);
        AssertAllNotified(raised);
    }

    /// <summary>
    /// A rewind while a scenario loads is not sent at all: with no connection a send would have surfaced
    /// <c>Rewind error: …</c> instead of this refusal.
    /// </summary>
    [AvaloniaFact]
    public async Task RewindToSeconds_WhileRoomLoading_SendsNothingAndSetsStatus()
    {
        MainViewModel vm = MentorInRoom();
        vm.OnRoomLoadingChanged("AB");
        Dispatcher.UIThread.RunJobs();

        await vm.RewindToSeconds(30);

        Assert.Equal("Rewind unavailable while a scenario loads", vm.StatusText);
    }
}
