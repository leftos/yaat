using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;

namespace Yaat.Client.UI.Tests.ViewModels;

/// <summary>
/// The room title carries the room's ARTCC label: every message that changes the room's scenario brings the label the
/// server computed after the change, and the title is rebuilt from it with the creator's initials. A message without a
/// label leaves the title as it was.
/// </summary>
public class MainViewModelRoomTitleTests
{
    private static MainViewModel VmInRoomLabelled(string artccLabel)
    {
        MainViewModel vm = new(new FakeFilePickerService());
        vm.ApplyRoomState(
            new RoomStateDto(
                RoomId: "room-1",
                CreatorInitials: "AB",
                CreatorArtccId: "ZOA",
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
                ToweredAirports: [],
                ArtccId: artccLabel
            )
        );
        return vm;
    }

    private static ScenarioLoadedDto ScenarioLoaded(string? artccLabel) =>
        new("scenario-2", "OAK Ground", "OAK", true, 1, [], [], [], ArtccId: artccLabel);

    [AvaloniaFact]
    public void ScenarioLoaded_FromAnotherArtcc_RelabelsTheRoomTitle()
    {
        MainViewModel vm = VmInRoomLabelled("ZOA");

        vm.OnScenarioLoaded(ScenarioLoaded("ZLA"));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("(ZLA) AB's Room", vm.ActiveRoomName);
    }

    [AvaloniaFact]
    public void ScenarioLoaded_WithoutALabel_KeepsTheRoomTitle()
    {
        MainViewModel vm = VmInRoomLabelled("ZOA");

        vm.OnScenarioLoaded(ScenarioLoaded(null));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("(ZOA) AB's Room", vm.ActiveRoomName);
    }

    [AvaloniaFact]
    public void RecordingLoaded_FromAnotherArtcc_RelabelsTheRoomTitle()
    {
        MainViewModel vm = VmInRoomLabelled("ZOA");

        vm.ApplyRecordingResult(new RewindResultDto(true, null, [], [], [], "scenario-3", "OAK Ground", "OAK", ArtccId: "ZLA"));

        Assert.Equal("(ZLA) AB's Room", vm.ActiveRoomName);
    }

    [AvaloniaFact]
    public void OwnScenarioLoad_FromAnotherArtcc_RelabelsTheRoomTitle()
    {
        MainViewModel vm = VmInRoomLabelled("ZOA");

        vm.ApplyScenarioResult(MainViewModelActiveRunwaysTests.LoadResult([], [], promptNeeded: false) with { ArtccId = "ZLA" });

        Assert.Equal("(ZLA) AB's Room", vm.ActiveRoomName);
    }

    [AvaloniaFact]
    public void OwnSoloUnload_RelabelsTheRoomTitleToTheCreatorsArtcc()
    {
        MainViewModel vm = VmInRoomLabelled("ZLA");

        vm.ApplyUnloadResult(new UnloadScenarioResultDto(false, 0, "Deleted", "ZOA"));

        Assert.Equal("(ZOA) AB's Room", vm.ActiveRoomName);
    }

    [AvaloniaFact]
    public void ScenarioUnloaded_RelabelsTheRoomTitleToTheCreatorsArtcc()
    {
        // Joined while the room ran a ZLA scenario; the creator's ARTCC is ZOA.
        MainViewModel vm = VmInRoomLabelled("ZLA");

        vm.OnScenarioUnloaded(new ScenarioUnloadedDto("ZOA"));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("(ZOA) AB's Room", vm.ActiveRoomName);
    }
}
