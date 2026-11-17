using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;

namespace Yaat.Client.UI.Tests.ViewModels;

/// <summary>
/// The session clock (<see cref="MainViewModel.SessionNowUtc"/>) is the scenario's session start, from whichever message
/// set the scenario up, plus the elapsed seconds: a rewind keeps the start, a restart replaces it and starts again at
/// zero, and leaving the scenario clears it.
/// </summary>
public class MainViewModelSessionClockTests
{
    private static readonly DateTime Start = new(2026, 8, 26, 12, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime NewStart = new(2026, 8, 26, 15, 0, 0, DateTimeKind.Utc);

    private static RoomStateDto RoomAt(double elapsedSeconds) =>
        new(
            RoomId: "ROOM-A",
            CreatorInitials: "CX",
            CreatorArtccId: "ZOA",
            Members: [],
            ScenarioName: "OAK Ground",
            ScenarioId: "scenario-1",
            IsPaused: true,
            SimRate: 1.0,
            PrimaryAirportId: "OAK",
            AllAircraft: [],
            AircraftGenerators: [],
            VfrArrivalGenerators: [],
            OverflightGenerators: [],
            Positions: [],
            ActiveRunways: [],
            ToweredAirports: [],
            SessionStartUtc: Start,
            EramCrrGroups: null,
            ElapsedSeconds: elapsedSeconds
        );

    [AvaloniaFact]
    public void ApplyScenarioResult_StartsTheClockAtTheSessionStart()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        vm.ApplyRoomState(RoomAt(90));

        vm.ApplyScenarioResult(
            new LoadScenarioResultDto(
                Success: true,
                Name: "OAK Ground",
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
                SessionStartUtc: NewStart
            )
        );

        Assert.Equal(NewStart, vm.SessionStartUtc);
        Assert.Equal(0, vm.ScenarioElapsedSeconds);
        Assert.Equal(NewStart, vm.SessionNowUtc);
    }

    [AvaloniaFact]
    public void OnScenarioLoaded_StartsTheClockAtTheSessionStart()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        vm.ApplyRoomState(RoomAt(90));

        vm.OnScenarioLoaded(new ScenarioLoadedDto("scenario-2", "OAK Ground", "OAK", true, 1, [], [], [], NewStart));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(NewStart, vm.SessionStartUtc);
        Assert.Equal(0, vm.ScenarioElapsedSeconds);
        Assert.Equal(NewStart, vm.SessionNowUtc);
    }

    [AvaloniaFact]
    public void ApplyRoomState_RunsTheClockFromTheRoomsElapsedTime()
    {
        var vm = new MainViewModel(new FakeFilePickerService());

        vm.ApplyRoomState(RoomAt(90));

        Assert.Equal(Start.AddSeconds(90), vm.SessionNowUtc);
    }

    [AvaloniaFact]
    public void ApplyRecordingResult_RunsTheClockFromTheRecordedStart()
    {
        var vm = new MainViewModel(new FakeFilePickerService());

        vm.ApplyRecordingResult(
            new RewindResultDto(true, null, [], [], Start, [], "scenario-3", "OAK Ground", "OAK", ElapsedSeconds: 90, IsPlayback: true, TapeEnd: 120)
        );

        Assert.Equal(Start.AddSeconds(90), vm.SessionNowUtc);
    }

    [AvaloniaFact]
    public void ApplyRewindResult_KeepsTheStart_AndMovesToTheRewoundTime()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        vm.ApplyRoomState(RoomAt(90));

        vm.ApplyRewindResult(new RewindResultDto(true, null, [], [], SessionStartUtc: null), 30, "Rewound to 00:30");

        Assert.Equal(Start.AddSeconds(30), vm.SessionNowUtc);
    }

    [AvaloniaFact]
    public void ApplyScenarioRestart_RestartsTheClockAtTheNewStart()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        vm.ApplyRoomState(RoomAt(90));
        DateTime newStart = Start.AddHours(2);

        vm.ApplyScenarioRestart([], newStart);

        Assert.Equal(newStart, vm.SessionNowUtc);
    }

    [AvaloniaFact]
    public void ClearScenarioState_ClearsTheClock()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        vm.ApplyRoomState(RoomAt(90));

        vm.ClearScenarioState();

        Assert.Null(vm.SessionNowUtc);
    }
}
