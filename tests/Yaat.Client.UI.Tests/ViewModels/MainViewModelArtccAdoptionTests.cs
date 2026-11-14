using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;

namespace Yaat.Client.UI.Tests.ViewModels;

/// <summary>
/// The ARTCC in effect follows the room. A visiting mentor who creates a ZMA room must get ZMA's scenario
/// catalog, live-session picker, weather and CRC alias file — every ARTCC-scoped consumer reads the one
/// stored preference — and it stays in effect on leaving. <see cref="MainViewModel.ApplyRoomState"/>
/// adopts the room's ARTCC; <see cref="MainViewModel.ClearRoomState"/> keeps it as the last used one.
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
            AllAircraft: [],
            AircraftGenerators: [],
            VfrArrivalGenerators: [],
            OverflightGenerators: [],
            Positions: [],
            ActiveRunways: [],
            ToweredAirports: []
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
    public void ClearRoomState_KeepsTheLastUsedArtcc()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        vm.PermittedArtccs.Add("ZOA");
        vm.PermittedArtccs.Add("ZMA");
        vm.SelectedCreateArtccId = "ZMA";
        vm.Preferences.SetArtccId("ZOA");
        vm.ApplyRoomState(RoomState("ZMA"));
        Assert.Equal("ZMA", vm.Preferences.ArtccId);

        vm.ClearRoomState();

        Assert.Equal("ZMA", vm.Preferences.ArtccId);
        Assert.Equal("ZMA", vm.SelectedCreateArtccId);
    }

    [Fact]
    public void PickCreateArtcc_SavedIsPermitted_ReturnsSaved() =>
        Assert.Equal("ZOA", MainViewModel.PickCreateArtcc(["ZAB", "ZAN", "ZOA"], "ZOA", null));

    [Fact]
    public void PickCreateArtcc_SavedIsNotPermitted_ReturnsHome() =>
        Assert.Equal("ZAN", MainViewModel.PickCreateArtcc(["ZAB", "ZAN", "ZOA"], "ZMA", "ZAN"));

    [Fact]
    public void PickCreateArtcc_NeitherIsPermitted_ReturnsFirst() =>
        Assert.Equal("ZAB", MainViewModel.PickCreateArtcc(["ZAB", "ZAN", "ZOA"], "ZMA", "ZNY"));

    [Fact]
    public void PickCreateArtcc_NothingPermitted_ReturnsNull() => Assert.Null(MainViewModel.PickCreateArtcc([], "ZOA", "ZAB"));

    [AvaloniaFact]
    public void ClearRoomState_KeepsTheSavedArtccWhenItIsPermitted()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        vm.PermittedArtccs.Add("ZAB");
        vm.PermittedArtccs.Add("ZAN");
        vm.PermittedArtccs.Add("ZOA");
        vm.Preferences.SetArtccId("ZOA");

        vm.ClearRoomState();

        Assert.Equal("ZOA", vm.SelectedCreateArtccId);
        Assert.Equal("ZOA", vm.Preferences.ArtccId);
    }
}
