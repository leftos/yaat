using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;

namespace Yaat.Client.UI.Tests.ViewModels;

/// <summary>
/// The Room Members panel used to stay empty after joining a room. Its only writer was the
/// <c>RoomMemberChanged</c> push, which the server broadcasts to the room group from inside
/// <c>JoinRoom</c> — so it can arrive before <c>ActiveRoomId</c> is assigned and get dropped by
/// the handler's room guard. Nothing re-fetched afterwards, so an instructor comparing the panel
/// against the room list saw "1 member" in one place and an empty list in the other.
/// <see cref="MainViewModel.ApplyRoomState"/> now seeds the panel from the join response itself.
/// </summary>
public class MainViewModelRoomMembersTests
{
    private static RoomStateDto RoomStateWith(params RoomMemberDto[] members) =>
        new(
            RoomId: "ROOM-A",
            CreatorInitials: "CX",
            CreatorArtccId: "ZOA",
            Members: [.. members],
            ScenarioName: null,
            ScenarioId: null,
            IsPaused: true,
            SimRate: 1.0,
            PrimaryAirportId: null,
            AllAircraft: []
        );

    private static RoomMemberDto Member(string initials, string kind, string connectionId) =>
        new(Cid: "1234567", Initials: initials, ArtccId: "ZOA", Kind: kind, JoinedAtUtc: DateTime.UtcNow, ConnectionId: connectionId);

    [AvaloniaFact]
    public void ApplyRoomState_SeedsRoomMembersFromJoinResponse()
    {
        var vm = new MainViewModel(new FakeFilePickerService());

        vm.ApplyRoomState(RoomStateWith(Member("CX", Yaat.Sim.ClientKind.Main, "conn-main")));

        var member = Assert.Single(vm.RoomMembers);
        Assert.Equal("CX", member.Initials);
        Assert.Equal("conn-main", member.ConnectionId);
    }

    // A controller running the desktop client alongside a vStrips browser tab holds two
    // connections. Both count toward the room's member total, so the panel has to name the kind
    // — otherwise an abandoned tab is indistinguishable from a session in progress.
    [AvaloniaFact]
    public void ApplyRoomState_LabelsBrowserTabsDistinctlyFromTheDesktopClient()
    {
        var vm = new MainViewModel(new FakeFilePickerService());

        vm.ApplyRoomState(
            RoomStateWith(Member("CX", Yaat.Sim.ClientKind.Main, "conn-main"), Member("CX", Yaat.Sim.ClientKind.VStrips, "conn-strips"))
        );

        Assert.Equal(2, vm.RoomMembers.Count);
        Assert.Equal(["Flight Strips", "YAAT Client"], vm.RoomMembers.Select(m => m.KindLabel).Order());
    }

    [AvaloniaFact]
    public void ApplyRoomState_ReplacesMembersFromAPreviousRoom()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        vm.ApplyRoomState(RoomStateWith(Member("AB", Yaat.Sim.ClientKind.Main, "conn-old")));

        vm.ApplyRoomState(RoomStateWith(Member("CX", Yaat.Sim.ClientKind.VTdls, "conn-new")));

        var member = Assert.Single(vm.RoomMembers);
        Assert.Equal("conn-new", member.ConnectionId);
        Assert.Equal("vTDLS", member.KindLabel);
    }
}
