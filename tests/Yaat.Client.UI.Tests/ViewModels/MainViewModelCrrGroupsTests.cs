using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;

namespace Yaat.Client.UI.Tests.ViewModels;

/// <summary>
/// The client's mirror of the room's ERAM CRR groups (<see cref="MainViewModel.CrrGroups"/>): each
/// <c>EramCrrGroupsChanged</c> replaces it whole, a join fills it from <see cref="RoomStateDto.EramCrrGroups"/>, and
/// leaving the scenario empties it.
/// </summary>
public class MainViewModelCrrGroupsTests
{
    private static readonly EramCrrGroupHubDto Bosox = new("BOSOX", "White", 37.72, -122.22);

    private static readonly EramCrrGroupHubDto Redsox = new("REDSOX", "Coral", 37.5, -122.0);

    private static RoomStateDto RoomWith(List<EramCrrGroupHubDto>? groups) =>
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
            SessionStartUtc: null,
            EramCrrGroups: groups
        );

    [AvaloniaFact]
    public async Task EramCrrGroupsChanged_ReplacesTheList()
    {
        MainViewModel vm = await ScopeTestFixture.MainAsync();
        Assert.Empty(vm.CrrGroups);

        vm.OnEramCrrGroupsChanged(new EramCrrGroupsChangedDto([Bosox, Redsox]));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal([Bosox, Redsox], vm.CrrGroups);

        vm.OnEramCrrGroupsChanged(new EramCrrGroupsChangedDto([Redsox]));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal([Redsox], vm.CrrGroups);
    }

    [AvaloniaFact]
    public async Task Join_FillsTheListFromTheRoomState()
    {
        MainViewModel vm = await ScopeTestFixture.MainAsync();

        vm.ApplyRoomState(RoomWith([Bosox]));

        Assert.Equal([Bosox], vm.CrrGroups);
    }

    [AvaloniaFact]
    public async Task Join_WithNoList_LeavesItEmpty()
    {
        MainViewModel vm = await ScopeTestFixture.MainAsync();
        vm.OnEramCrrGroupsChanged(new EramCrrGroupsChangedDto([Bosox]));
        Dispatcher.UIThread.RunJobs();

        vm.ApplyRoomState(RoomWith(null));

        Assert.Empty(vm.CrrGroups);
    }

    [AvaloniaFact]
    public async Task ScenarioUnload_ClearsTheList()
    {
        MainViewModel vm = await ScopeTestFixture.MainAsync();
        vm.ApplyRoomState(RoomWith([Bosox, Redsox]));

        vm.OnScenarioUnloaded(new ScenarioUnloadedDto("ZOA"));
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(vm.CrrGroups);
    }
}
