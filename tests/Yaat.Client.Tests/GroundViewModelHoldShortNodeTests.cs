using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Sim;

namespace Yaat.Client.Tests;

// Covers GroundViewModel.FindNearestHoldShortNodeForRunwayEnd — the resolver
// the runway-end click target uses to pick a representative hold-short node
// for the existing taxi-to-runway submenu. The test layout sits at SFO-ish
// coordinates with a single straight taxiway and a fan of HS nodes.
public class GroundViewModelHoldShortNodeTests
{
    private const double StartLat = 37.620;
    private const double StartLon = -122.380;

    [Fact]
    public void PicksLowestCostHoldShort_NotEuclideanNearest()
    {
        // Two hold-short nodes for 28L, both reachable. The "near" one sits at
        // a 0.05nm hop; the "far" one at 0.30nm. The resolver should pick the
        // near one because its path cost (sum of edge distances) is lowest.
        var vm = MakeViewModel();
        var dto = new GroundLayoutDto(
            "TST",
            [
                new GroundNodeDto(1, StartLat, StartLon, "TaxiwayIntersection", null, null, null),
                new GroundNodeDto(2, StartLat + 0.001, StartLon, "RunwayHoldShort", null, null, "28L/10R"),
                new GroundNodeDto(3, StartLat + 0.005, StartLon, "RunwayHoldShort", null, null, "28L/10R"),
            ],
            [
                new GroundEdgeDto(1, 2, "A", DistanceNm: 0.05, IntermediatePoints: null),
                new GroundEdgeDto(1, 3, "A", DistanceNm: 0.30, IntermediatePoints: null),
            ],
            null,
            null
        );
        vm.SetLayoutForTesting(dto);

        var ac = MakeAircraft(StartLat, StartLon);

        var result = vm.FindNearestHoldShortNodeForRunwayEnd(ac, "28L");

        Assert.Equal(2, result);
    }

    [Fact]
    public void IgnoresHoldShortsForOtherRunways()
    {
        // HS for 19L is the closest by cost, but the resolver should skip it
        // and return the more-distant HS on 28L.
        var vm = MakeViewModel();
        var dto = new GroundLayoutDto(
            "TST",
            [
                new GroundNodeDto(1, StartLat, StartLon, "TaxiwayIntersection", null, null, null),
                new GroundNodeDto(2, StartLat + 0.001, StartLon, "RunwayHoldShort", null, null, "19L/01R"),
                new GroundNodeDto(3, StartLat + 0.005, StartLon, "RunwayHoldShort", null, null, "28L/10R"),
            ],
            [
                new GroundEdgeDto(1, 2, "A", DistanceNm: 0.05, IntermediatePoints: null),
                new GroundEdgeDto(1, 3, "A", DistanceNm: 0.30, IntermediatePoints: null),
            ],
            null,
            null
        );
        vm.SetLayoutForTesting(dto);

        var ac = MakeAircraft(StartLat, StartLon);

        var result = vm.FindNearestHoldShortNodeForRunwayEnd(ac, "28L");

        Assert.Equal(3, result);
    }

    [Fact]
    public void ReturnsNullWhenRunwayHasNoHoldShorts()
    {
        var vm = MakeViewModel();
        var dto = new GroundLayoutDto(
            "TST",
            [
                new GroundNodeDto(1, StartLat, StartLon, "TaxiwayIntersection", null, null, null),
                new GroundNodeDto(2, StartLat + 0.001, StartLon, "RunwayHoldShort", null, null, "19L/01R"),
            ],
            [new GroundEdgeDto(1, 2, "A", DistanceNm: 0.05, IntermediatePoints: null)],
            null,
            null
        );
        vm.SetLayoutForTesting(dto);

        var ac = MakeAircraft(StartLat, StartLon);

        var result = vm.FindNearestHoldShortNodeForRunwayEnd(ac, "28L");

        Assert.Null(result);
    }

    [Fact]
    public void ReturnsNullWhenLayoutNotLoaded()
    {
        var vm = MakeViewModel();
        var ac = MakeAircraft(StartLat, StartLon);

        var result = vm.FindNearestHoldShortNodeForRunwayEnd(ac, "28L");

        Assert.Null(result);
    }

    private static GroundViewModel MakeViewModel()
    {
        var connection = new ServerConnection();
        return new GroundViewModel(connection, sendCommand: (_, _, _) => Task.CompletedTask);
    }

    private static AircraftModel MakeAircraft(double lat, double lon)
    {
        return new AircraftModel { Callsign = "TST123", Position = new LatLon(lat, lon) };
    }
}
