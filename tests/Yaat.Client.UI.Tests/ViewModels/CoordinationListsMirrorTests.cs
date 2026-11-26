using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;

namespace Yaat.Client.UI.Tests.ViewModels;

/// <summary>
/// The client's mirror of the room's STARS coordination (RD) lists (<see cref="MainViewModel.CoordinationLists"/>): each
/// <c>CoordinationListsChanged</c> replaces it whole, a join fills it from <see cref="RoomStateDto.CoordinationLists"/>, and
/// leaving the scenario empties it.
/// </summary>
public class CoordinationListsMirrorTests
{
    private static readonly CoordinationListHubDto Pfat = List("PFAT");

    private static readonly CoordinationListHubDto Star = List("S");

    [AvaloniaFact]
    public async Task CoordinationListsChanged_ReplacesTheSetWhole()
    {
        MainViewModel vm = await ScopeTestFixture.MainAsync();
        Assert.Empty(vm.CoordinationLists);

        vm.OnCoordinationListsChanged(new CoordinationListsChangedDto([Pfat, Star]));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(["PFAT", "S"], vm.CoordinationLists.Select(list => list.ListId));

        vm.OnCoordinationListsChanged(new CoordinationListsChangedDto([Star]));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(["S"], vm.CoordinationLists.Select(list => list.ListId));
    }

    [AvaloniaFact]
    public async Task ApplyNull_ClearsTheSet()
    {
        MainViewModel vm = await ScopeTestFixture.MainAsync();
        vm.ApplyCoordinationLists([Pfat, Star]);

        vm.ApplyCoordinationLists(null);

        Assert.Empty(vm.CoordinationLists);
    }

    [AvaloniaFact]
    public async Task Join_FillsTheSetFromTheRoomState()
    {
        MainViewModel vm = await ScopeTestFixture.MainAsync();

        vm.ApplyRoomState(RoomWith([Pfat], scenarioId: "scenario-1"));

        Assert.Equal(["PFAT"], vm.CoordinationLists.Select(list => list.ListId));
    }

    [AvaloniaFact]
    public async Task JoinWithNoLists_LeavesTheSetEmpty()
    {
        MainViewModel vm = await ScopeTestFixture.MainAsync();
        vm.ApplyCoordinationLists([Pfat, Star]);

        vm.ApplyRoomState(RoomWith(null, scenarioId: null));

        Assert.Empty(vm.CoordinationLists);
    }

    [AvaloniaFact]
    public async Task ScenarioUnload_ClearsTheSet()
    {
        MainViewModel vm = await ScopeTestFixture.MainAsync();
        vm.ApplyRoomState(RoomWith([Pfat, Star], scenarioId: "scenario-1"));

        vm.OnScenarioUnloaded(new ScenarioUnloadedDto("ZOA"));
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(vm.CoordinationLists);
    }

    private static RoomStateDto RoomWith(List<CoordinationListHubDto>? lists, string? scenarioId) =>
        new(
            RoomId: "ROOM-A",
            CreatorInitials: "CX",
            CreatorArtccId: "ZOA",
            Members: [],
            ScenarioName: "OAK Ground",
            ScenarioId: scenarioId,
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
            SessionStartUtc: null,
            EramCrrGroups: null,
            CoordinationLists: lists
        );

    private static CoordinationListHubDto List(string listId) =>
        new(
            ListId: listId,
            Title: "Oakland departure releases",
            AutoAcknowledge: false,
            SenderTcps: ["3O"],
            ReceiverTcps: ["4O"],
            Items: [new CoordinationItemHubDto("POAK-1", "SWA123", "Unsent", 7, "B738", "0425", "F", "SJC", "RWY 30", "3O", null)]
        );
}
