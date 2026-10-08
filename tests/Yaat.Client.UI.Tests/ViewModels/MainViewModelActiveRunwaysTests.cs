using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;

namespace Yaat.Client.UI.Tests.ViewModels;

/// <summary>
/// The client's copy of the room's active runways: every feed (load result, <c>ScenarioLoaded</c>, the join's room state,
/// a recording load, <c>ActiveRunwaysChanged</c> and the restart) replaces it wholesale, whatever order they arrive in, and
/// leaving the scenario clears it.
/// </summary>
public class MainViewModelActiveRunwaysTests
{
    private static MainViewModel NewVm() => new(new FakeFilePickerService());

    private static Dictionary<string, List<string>> Runways(params (string Airport, string[] Tokens)[] entries) =>
        entries.ToDictionary(entry => entry.Airport, entry => entry.Tokens.ToList(), StringComparer.Ordinal);

    private static Dictionary<string, string[]> Held(MainViewModel vm) =>
        vm.RoomActiveRunways.ToDictionary(entry => entry.Key, entry => entry.Value.ToArray(), StringComparer.Ordinal);

    private static MainViewModel VmHolding(params (string Airport, string[] Tokens)[] entries)
    {
        MainViewModel vm = NewVm();
        vm.ApplyActiveRunways(Runways(entries));
        return vm;
    }

    internal static LoadScenarioResultDto LoadResult(
        Dictionary<string, List<string>> activeRunways,
        Dictionary<string, List<string>> prefill,
        bool promptNeeded
    ) =>
        new(
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
            ActiveRunways: activeRunways,
            ActiveRunwaysPrefill: prefill,
            ActiveRunwaysPromptNeeded: promptNeeded,
            IsLiveSession: false
        );

    private static RoomStateDto RoomState(string? scenarioId, Dictionary<string, List<string>> activeRunways) =>
        new(
            RoomId: "room-1",
            CreatorInitials: "CX",
            CreatorArtccId: "ZOA",
            Members: [],
            ScenarioName: scenarioId is null ? null : "OAK Ground",
            ScenarioId: scenarioId,
            IsPaused: true,
            SimRate: 1.0,
            PrimaryAirportId: scenarioId is null ? null : "OAK",
            AllAircraft: [],
            AircraftGenerators: [],
            VfrArrivalGenerators: [],
            OverflightGenerators: [],
            Positions: [],
            ActiveRunways: activeRunways
        );

    [AvaloniaFact]
    public void ScenarioLoaded_ReplacesTheListWholesale()
    {
        MainViewModel vm = VmHolding(("SFO", ["28R"]), ("OAK", ["30"]));

        vm.OnScenarioLoaded(new ScenarioLoadedDto("scenario-2", "OAK Ground", "OAK", true, 1, [], Runways(("OAK", ["D28L"]))));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new Dictionary<string, string[]> { ["OAK"] = ["D28L"] }, Held(vm));
    }

    [AvaloniaFact]
    public void RoomState_ReplacesTheListWholesale_ScenarioOrNot()
    {
        MainViewModel vm = VmHolding(("SFO", ["28R"]));

        vm.ApplyRoomState(RoomState("scenario-1", Runways(("OAK", ["D28L", "A28R"]))));
        Assert.Equal(new Dictionary<string, string[]> { ["OAK"] = ["D28L", "A28R"] }, Held(vm));

        vm.ApplyRoomState(RoomState(scenarioId: null, []));
        Assert.Empty(vm.RoomActiveRunways);
    }

    [AvaloniaFact]
    public void RecordingResult_ReplacesTheListWholesale()
    {
        MainViewModel vm = VmHolding(("SFO", ["28R"]), ("OAK", ["30"]));

        vm.ApplyRecordingResult(new RewindResultDto(true, null, Runways(("OAK", ["12"])), [], "scenario-3", "OAK Ground", "OAK"));

        Assert.Equal(new Dictionary<string, string[]> { ["OAK"] = ["12"] }, Held(vm));
    }

    [AvaloniaFact]
    public void RewindResult_ReplacesTheListWholesale()
    {
        MainViewModel vm = VmHolding(("SFO", ["28R"]), ("OAK", ["30"]));

        vm.ApplyRewindResult(new RewindResultDto(true, null, Runways(("OAK", ["12"]))), 30, "Rewound to 00:30");

        Assert.Equal(new Dictionary<string, string[]> { ["OAK"] = ["12"] }, Held(vm));
    }

    [AvaloniaFact]
    public void ActiveRunwaysChanged_ReplacesTheListWholesale()
    {
        MainViewModel vm = VmHolding(("SFO", ["28R"]), ("OAK", ["30"]));

        vm.OnActiveRunwaysChanged(new ActiveRunwaysChangedDto(Runways(("OAK", ["D28L"]))));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new Dictionary<string, string[]> { ["OAK"] = ["D28L"] }, Held(vm));
    }

    [AvaloniaFact]
    public void ScenarioRestarted_ReplacesTheListWholesale()
    {
        MainViewModel vm = VmHolding(("SFO", ["28R"]), ("OAK", ["30"]));

        vm.OnScenarioRestarted(new ScenarioRestartedDto([], Runways(("OAK", ["D28L", "A28R"]))));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new Dictionary<string, string[]> { ["OAK"] = ["D28L", "A28R"] }, Held(vm));
    }

    [AvaloniaFact]
    public void ChangedBeforeTheLoadResult_LeavesTheLoadResultsList_AndTheLoadResultDoesNotClearIt()
    {
        MainViewModel vm = NewVm();

        vm.OnActiveRunwaysChanged(new ActiveRunwaysChangedDto(Runways(("OAK", ["D28L"]))));
        Dispatcher.UIThread.RunJobs();
        vm.ApplyLoadResultActiveRunways(LoadResult(Runways(("OAK", ["28R"])), Runways(("OAK", ["D28R"])), promptNeeded: false));

        Assert.Equal(new Dictionary<string, string[]> { ["OAK"] = ["28R"] }, Held(vm));
    }

    [AvaloniaFact]
    public void ScenarioLoadedBeforeChanged_EndsOnTheChangedList()
    {
        MainViewModel vm = NewVm();

        vm.OnScenarioLoaded(new ScenarioLoadedDto("scenario-2", "OAK Ground", "OAK", true, 1, [], Runways(("OAK", ["30"]))));
        vm.OnActiveRunwaysChanged(new ActiveRunwaysChangedDto(Runways(("OAK", ["D28L", "A28R"]), ("SFO", ["28R"]))));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new Dictionary<string, string[]> { ["OAK"] = ["D28L", "A28R"], ["SFO"] = ["28R"] }, Held(vm));
    }

    [AvaloniaFact]
    public void ScenarioUnloaded_ClearsTheList()
    {
        MainViewModel vm = VmHolding(("OAK", ["30"]));

        vm.ClearScenarioState();

        Assert.Empty(vm.RoomActiveRunways);
    }

    [AvaloniaFact]
    public void LeavingTheRoom_ClearsTheList()
    {
        MainViewModel vm = VmHolding(("OAK", ["30"]));

        vm.ClearRoomState();

        Assert.Empty(vm.RoomActiveRunways);
    }
}
