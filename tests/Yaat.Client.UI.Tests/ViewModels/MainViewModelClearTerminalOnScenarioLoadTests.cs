using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;

namespace Yaat.Client.UI.Tests.ViewModels;

/// <summary>
/// The "Clear the terminal when a scenario loads or restarts" setting. A load clears the pane when the room's load flag
/// goes up (<c>RoomLoadingChanged</c>), which the server sends to every member before the load's own warnings and its
/// "loaded scenario" line, so those survive. A restart clears the pane when <c>ScenarioRestarted</c> arrives, after the
/// server's own restart line, so the client says it restarted itself. A join, a reconnect and the end of a load never
/// clear, and the Up/Down command history is never touched.
/// </summary>
public class MainViewModelClearTerminalOnScenarioLoadTests
{
    private const string OldLine = "A line from the previous run";

    [AvaloniaFact]
    public void BroadcastLoad_SettingOn_ClearsWhenTheLoadStarts_AndKeepsTheLoadsOwnLines() =>
        WithSetting(
            on: true,
            vm =>
            {
                vm.OnRoomLoadingChanged("AB");
                vm.OnTerminalEntry(Warning("Could not resolve ATC position OAK_GND"));
                vm.OnScenarioLoaded(new ScenarioLoadedDto("scenario-1", "OAK Ground", "OAK", true, 1, [], [], []));
                vm.OnRoomLoadingChanged(null);
                Dispatcher.UIThread.RunJobs();

                Assert.Equal(["Could not resolve ATC position OAK_GND", "Scenario loaded: OAK Ground (0 aircraft)"], Messages(vm));
            }
        );

    [AvaloniaFact]
    public void LoaderResult_SettingOn_ClearsWhenTheLoadStarts_AndKeepsTheLoadsOwnLines() =>
        WithSetting(
            on: true,
            vm =>
            {
                vm.OnRoomLoadingChanged("AB");
                vm.OnTerminalEntry(Warning("Could not resolve ATC position OAK_GND"));
                Dispatcher.UIThread.RunJobs();
                vm.ApplyScenarioResult(MainViewModelActiveRunwaysTests.LoadResult([], [], promptNeeded: false));

                Assert.Equal(["Could not resolve ATC position OAK_GND"], Messages(vm));
            }
        );

    [AvaloniaFact]
    public void FailedLoad_SettingOn_TheFailureReasonFollowsTheClear() =>
        WithSetting(
            on: true,
            vm =>
            {
                vm.OnRoomLoadingChanged("AB");
                vm.OnRoomLoadingChanged(null);
                Dispatcher.UIThread.RunJobs();
                LoadScenarioResultDto failed = MainViewModelActiveRunwaysTests.LoadResult([], [], promptNeeded: false) with
                {
                    Success = false,
                    Warnings = ["The scenario file is not valid JSON"],
                };

                vm.ReportLoadFailure(failed);

                Assert.Equal(["The scenario file is not valid JSON"], Messages(vm));
            }
        );

    [AvaloniaFact]
    public void Load_SettingOff_KeepsTheEarlierLines() =>
        WithSetting(
            on: false,
            vm =>
            {
                vm.OnRoomLoadingChanged("AB");
                Dispatcher.UIThread.RunJobs();
                vm.OnScenarioLoaded(new ScenarioLoadedDto("scenario-1", "OAK Ground", "OAK", true, 1, [], [], []));
                Dispatcher.UIThread.RunJobs();
                vm.ApplyScenarioResult(MainViewModelActiveRunwaysTests.LoadResult([], [], promptNeeded: false));

                Assert.Equal([OldLine, "Scenario loaded: OAK Ground (0 aircraft)"], Messages(vm));
            }
        );

    [AvaloniaFact]
    public void Restart_SettingOn_LeavesOnlyTheClientsRestartedLine() =>
        WithSetting(
            on: true,
            vm =>
            {
                vm.OnTerminalEntry(new TerminalBroadcastDto("", "System", "", "[Restart] Scenario restarted with new traffic", DateTime.UtcNow, 0));
                Dispatcher.UIThread.RunJobs();

                vm.ApplyScenarioRestart([]);

                Assert.Equal(["Scenario restarted with new traffic"], Messages(vm));
                Assert.Equal(TerminalEntryKind.System, Assert.Single(vm.TerminalEntries).Kind);
            }
        );

    [AvaloniaFact]
    public void Restart_SettingOff_KeepsEveryLine() =>
        WithSetting(
            on: false,
            vm =>
            {
                vm.OnTerminalEntry(new TerminalBroadcastDto("", "System", "", "[Restart] Scenario restarted with new traffic", DateTime.UtcNow, 0));
                Dispatcher.UIThread.RunJobs();

                vm.ApplyScenarioRestart([]);

                Assert.Equal([OldLine, "[Restart] Scenario restarted with new traffic"], Messages(vm));
            }
        );

    [AvaloniaFact]
    public void JoinOrReconnect_SettingOn_KeepsTheLines() =>
        WithSetting(
            on: true,
            vm =>
            {
                vm.ApplyRoomState(RoomStateWithAScenario());
                vm.ApplyRoomState(RoomStateWithAScenario());

                Assert.Contains(OldLine, Messages(vm));
            }
        );

    [AvaloniaFact]
    public void JoinWhileAMemberLoads_SettingOn_KeepsTheLines() =>
        WithSetting(
            on: true,
            vm =>
            {
                vm.ApplyRoomState(RoomStateWithAScenario() with { LoadingBy = "AB" });

                Assert.Equal([OldLine], Messages(vm));
            }
        );

    [AvaloniaFact]
    public void ReconnectDuringALoad_SettingOn_KeepsTheLinesSinceTheLoadStarted() =>
        WithSetting(
            on: true,
            vm =>
            {
                vm.OnRoomLoadingChanged("AB");
                vm.OnTerminalEntry(Warning("Could not resolve ATC position OAK_GND"));
                Dispatcher.UIThread.RunJobs();

                vm.ApplyRoomState(RoomStateWithAScenario() with { LoadingBy = "AB" });

                Assert.Contains("Could not resolve ATC position OAK_GND", Messages(vm));
            }
        );

    [AvaloniaFact]
    public void ARepeatedLoadFlag_SettingOn_KeepsTheLinesSinceTheLoadStarted() =>
        WithSetting(
            on: true,
            vm =>
            {
                vm.OnRoomLoadingChanged("AB");
                vm.OnTerminalEntry(Warning("Could not resolve ATC position OAK_GND"));
                vm.OnRoomLoadingChanged("AB");
                Dispatcher.UIThread.RunJobs();

                Assert.Equal(["Could not resolve ATC position OAK_GND"], Messages(vm));
            }
        );

    /// <summary>
    /// Runs <paramref name="body"/> on a view model already running scenario-1, the scenario every load here loads again,
    /// whose pane holds only <see cref="OldLine"/> and whose Up/Down history starts with a fresh command, with the setting
    /// <paramref name="on"/>; the history must come out unchanged. Loading the same scenario keeps its history, so only a
    /// clear could change it. The history is compared with itself before the run, not with a fixed list: the bootstrap
    /// reloads scenario-1's saved history from the preferences file other tests share.
    /// </summary>
    private static void WithSetting(bool on, Action<MainViewModel> body)
    {
        using var scope = new PreferencesFileScope();
        var vm = new MainViewModel(new FakeFilePickerService());
        vm.Preferences.SetClearTerminalOnScenarioLoad(on);
        vm.ApplyScenarioBootstrap(
            new ScenarioBootstrap
            {
                ScenarioId = "scenario-1",
                ScenarioName = "OAK Ground",
                PrimaryAirportId = "OAK",
                PositionDisplayConfig = null,
                FlightStripsConfig = null,
                Aircraft = [],
                ElapsedSeconds = 0,
            }
        );
        vm.TerminalEntries.Clear();
        vm.AddSystemEntry(OldLine);
        vm.AddHistory("SWA101", "FH 270");
        CommandHistoryEntry[] historyBefore = [.. vm.CommandHistory];
        Assert.Equal(new CommandHistoryEntry("SWA101", "FH 270"), historyBefore[0]);

        body(vm);

        Assert.Equal(historyBefore, vm.CommandHistory);
    }

    private static TerminalBroadcastDto Warning(string message) => new("", "Warning", "", message, DateTime.UtcNow, 0);

    private static string[] Messages(MainViewModel vm) => [.. vm.TerminalEntries.Select(e => e.Message)];

    private static RoomStateDto RoomStateWithAScenario() =>
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
            ElapsedSeconds: 0
        );
}
