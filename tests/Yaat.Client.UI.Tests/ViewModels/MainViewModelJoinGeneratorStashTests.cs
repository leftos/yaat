using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;
using Yaat.Sim.Scenarios;

namespace Yaat.Client.UI.Tests.ViewModels;

/// <summary>
/// Joining or reconnecting to a room is the third scenario-activation path, next to the loader
/// (<c>ApplyScenarioResult</c>) and the broadcast (<c>OnScenarioLoaded</c>). Both of those stash the
/// room's generators and ARTCC positions for the generator editor; the join path dropped them, so a
/// joiner opened "Edit Aircraft Generators…" against empty lists and could wipe the room's live
/// generators by pressing Apply.
/// </summary>
public class MainViewModelJoinGeneratorStashTests
{
    /// <summary>
    /// A room state as the server sends it: a loaded scenario carries the room's generators and ARTCC
    /// positions, a scenario-less room carries four empty lists.
    /// </summary>
    private static RoomStateDto RoomState(string? scenarioId, bool withGenerators) =>
        new(
            RoomId: "ROOM-A",
            CreatorInitials: "CX",
            CreatorArtccId: "ZOA",
            Members: [],
            ScenarioName: scenarioId is null ? null : "OAK Ground 7",
            ScenarioId: scenarioId,
            IsPaused: true,
            SimRate: 1.0,
            PrimaryAirportId: scenarioId is null ? null : "OAK",
            AllAircraft: [],
            AircraftGenerators: withGenerators ? [new ScenarioGeneratorConfig { Id = "ARR-1", Runway = "28R" }] : [],
            VfrArrivalGenerators: withGenerators ? [new VfrArrivalGeneratorConfig { Id = "VFR-1" }] : [],
            OverflightGenerators: withGenerators ? [new OverflightGeneratorConfig { Id = "OVF-1" }] : [],
            Positions: withGenerators ? [new ScenarioPositionDto("01GEAT", "OAK_TWR", "Oakland Tower")] : []
        );

    [AvaloniaFact]
    public void ApplyRoomState_WithScenario_StashesGeneratorsAndPositionsForTheEditor()
    {
        var vm = new MainViewModel(new FakeFilePickerService());

        vm.ApplyRoomState(RoomState("scenario-7", withGenerators: true));

        Assert.Equal("ARR-1", Assert.Single(vm.LatestArrivalGenerators).Id);
        Assert.Equal("VFR-1", Assert.Single(vm.LatestVfrArrivalGenerators).Id);
        Assert.Equal("OVF-1", Assert.Single(vm.LatestOverflightGenerators).Id);
        Assert.Equal("OAK_TWR", Assert.Single(vm.LatestPositions).Callsign);
    }

    /// <summary>
    /// Joining a scenario-less room after one that had generators must leave the editor empty, not holding
    /// the previous room's set: the editor's Apply posts what it shows, so a stale set is a wipe waiting to
    /// happen in a room that never had those generators.
    /// </summary>
    [AvaloniaFact]
    public void ApplyRoomState_WithoutScenario_ClearsEditorSourcesLeftByAnEarlierRoom()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        vm.ApplyRoomState(RoomState("scenario-7", withGenerators: true));

        vm.ApplyRoomState(RoomState(scenarioId: null, withGenerators: false));

        Assert.Empty(vm.LatestArrivalGenerators);
        Assert.Empty(vm.LatestVfrArrivalGenerators);
        Assert.Empty(vm.LatestOverflightGenerators);
        Assert.Empty(vm.LatestPositions);
    }
}
