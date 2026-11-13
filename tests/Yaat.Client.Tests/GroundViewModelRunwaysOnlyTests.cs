using Xunit;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;

namespace Yaat.Client.Tests;

/// <summary>
/// A layout the server sends for an airport with no ground map (<see cref="GroundLayoutDto.IsRunwaysOnly"/>) has runways and
/// no nodes: the view model reports it runways-only and centres the airport on the runways' ends, where a node centroid would
/// leave the centre at (0, 0) and the canvas's 10 nm visibility bound would drop every airborne aircraft.
/// </summary>
public class GroundViewModelRunwaysOnlyTests
{
    public GroundViewModelRunwaysOnlyTests() => Yaat.Sim.Testing.TestVnasData.EnsureInitialized();

    [Fact]
    public void ApplyLoadedLayout_RunwaysOnly_IsRunwaysOnly_AndCentresOnTheRunwayEnds()
    {
        IReadOnlyList<RunwayInfo> runways = NavigationDatabase.Instance.GetRunways("SMF");
        Assert.NotEmpty(runways);
        List<GroundRunwayDto> runwayDtos =
        [
            .. runways.Select(r => new GroundRunwayDto(
                r.Id.ToString(),
                [
                    [r.Lat1, r.Lon1],
                    [r.Lat2, r.Lon2],
                ],
                r.WidthFt
            )),
        ];
        GroundViewModel vm = NewViewModel();

        vm.ApplyLoadedLayout(new GroundLayoutDto("SMF", [], [], [], runwayDtos, [], IsRunwaysOnly: true));

        Assert.True(vm.IsRunwaysOnly);
        double expectedLat = runways.SelectMany(r => new[] { r.Lat1, r.Lat2 }).Average();
        double expectedLon = runways.SelectMany(r => new[] { r.Lon1, r.Lon2 }).Average();
        Assert.Equal(expectedLat, vm.AirportCenterLat, 9);
        Assert.Equal(expectedLon, vm.AirportCenterLon, 9);
    }

    [Fact]
    public void ApplyLoadedLayout_FullLayout_IsNotRunwaysOnly_AndCentresOnTheNodes()
    {
        GroundViewModel vm = NewViewModel();
        var nodes = new List<GroundNodeDto>
        {
            new(1, 38.69, -121.60, "Taxiway", null, null, null),
            new(2, 38.71, -121.58, "Taxiway", null, null, null),
        };

        vm.ApplyLoadedLayout(new GroundLayoutDto("SMF", nodes, [], null, null, null, IsRunwaysOnly: false));

        Assert.False(vm.IsRunwaysOnly);
        Assert.Equal(38.70, vm.AirportCenterLat, 9);
        Assert.Equal(-121.59, vm.AirportCenterLon, 9);
    }

    private static GroundViewModel NewViewModel() =>
        new(new ServerConnection(), sendCommand: (_, _, _) => Task.CompletedTask)
        {
            RoomActiveRunways = () => new Dictionary<string, IReadOnlyList<string>>(),
        };
}
