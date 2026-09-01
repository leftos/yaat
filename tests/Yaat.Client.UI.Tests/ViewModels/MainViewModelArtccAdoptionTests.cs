using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;

namespace Yaat.Client.UI.Tests.ViewModels;

/// <summary>
/// The ARTCC in effect follows the room. A visiting mentor who creates a ZMA room must get ZMA's scenario
/// catalog, live-session picker, weather and CRC alias file — every ARTCC-scoped consumer reads the one
/// stored preference — and must get their home ARTCC back on leaving. <see cref="MainViewModel.ApplyRoomState"/>
/// adopts the room's ARTCC; <see cref="MainViewModel.ClearRoomState"/> restores home.
/// </summary>
public class MainViewModelArtccAdoptionTests
{
    private static RoomStateDto RoomState(string creatorArtccId) =>
        new(
            RoomId: "ROOM-A",
            CreatorInitials: "CX",
            CreatorArtccId: creatorArtccId,
            Members: [],
            ScenarioName: null,
            ScenarioId: null,
            IsPaused: true,
            SimRate: 1.0,
            PrimaryAirportId: null,
            AllAircraft: []
        );

    [AvaloniaFact]
    public void ApplyRoomState_AdoptsRoomArtccAsActive()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        vm.Preferences.SetArtccId("ZOA");

        vm.ApplyRoomState(RoomState("ZMA"));

        Assert.Equal("ZMA", vm.Preferences.ArtccId);
    }

    [AvaloniaFact]
    public void ApplyRoomState_SameArtccDifferentCase_KeepsValue()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        vm.Preferences.SetArtccId("ZOA");

        vm.ApplyRoomState(RoomState("zoa"));

        Assert.Equal("ZOA", vm.Preferences.ArtccId);
    }

    [AvaloniaFact]
    public void ClearRoomState_RestoresHomeArtccAndCreatePickerDefault()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        vm.PermittedArtccs.Add("ZOA");
        vm.PermittedArtccs.Add("ZMA");
        vm.SelectedCreateArtccId = "ZMA";
        vm.Preferences.SetArtccId("ZOA");
        vm.ApplyRoomState(RoomState("ZMA"));
        Assert.Equal("ZMA", vm.Preferences.ArtccId);

        vm.ClearRoomState();

        Assert.Equal("ZOA", vm.Preferences.ArtccId);
        Assert.Equal("ZOA", vm.SelectedCreateArtccId);
    }
}
