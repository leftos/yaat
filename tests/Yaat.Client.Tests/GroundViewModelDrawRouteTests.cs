using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Sim;

namespace Yaat.Client.Tests;

// Covers GroundViewModel.FinishDrawRoute: the ground "draw route" tool must commit the
// FULL ordered node sequence of the previewed route, not just the clicked waypoints, so
// the server reproduces exactly what the user drew instead of re-routing onto a parallel
// taxiway. It must also append the @parking / $spot token when the drawn route ends inside
// a stand, so the aircraft actually parks. Reproduces the OAK draw-tool fidelity bug where
// drawing W-V-T taxied W-U-T and drawing into parking skipped the turn.
public class GroundViewModelDrawRouteTests
{
    private const double Lat0 = 37.620;
    private const double Lon0 = -122.380;

    [Fact]
    public void FinishDrawRoute_EmitsEveryNode_NotJustClickedWaypoints()
    {
        var vm = MakeViewModel();
        vm.SetLayoutForTesting(LinearLayout());
        var ac = MakeAircraft(Lat0, Lon0);

        vm.StartDrawRoute(ac);
        // Click only the far node 3; node 2 is an UN-clicked intermediate on the path.
        Assert.True(vm.AddDrawWaypoint(3));
        var result = vm.FinishDrawRoute();

        Assert.NotNull(result);
        // Dense: the command carries the intermediate node 2, not just the clicked node 3.
        Assert.Equal("#2 #3", result!.Value.NodeRefPath);
        Assert.Null(result.Value.Spot);
    }

    [Fact]
    public void FinishDrawRoute_ParkingTerminus_AppendsAtToken()
    {
        var vm = MakeViewModel();
        vm.SetLayoutForTesting(LinearLayout());
        var ac = MakeAircraft(Lat0, Lon0);

        vm.StartDrawRoute(ac);
        Assert.True(vm.AddDrawWaypoint(3));
        Assert.True(vm.AddDrawWaypoint(4)); // node 4 = Parking "8B"
        var result = vm.FinishDrawRoute();

        Assert.NotNull(result);
        Assert.Equal("#2 #3 #4", result!.Value.NodeRefPath);
        Assert.NotNull(result.Value.Spot);
        Assert.Equal("@8B", result.Value.Spot!.Token);
    }

    [Fact]
    public void BuildDrawRouteCopyCommand_MidTaxiwayEndpoint_UsesReadableNamePlusTerminalPin()
    {
        var vm = MakeViewModel();
        vm.SetLayoutForTesting(LinearLayout());
        var ac = MakeAircraft(Lat0, Lon0);

        vm.StartDrawRoute(ac);
        Assert.True(vm.AddDrawWaypoint(3)); // mid-taxiway intersection, no stand
        var result = vm.FinishDrawRoute();
        Assert.NotNull(result);

        var (route, _, spot) = result!.Value;
        Assert.Null(spot);
        var command = vm.BuildDrawRouteCopyCommand(route, spot);

        // Readable taxiway name (not the dense "#2 #3"), pinned at the drawn endpoint node so the
        // aircraft stops where it was drawn instead of running to the end of taxiway V.
        Assert.Equal("TAXI V #3", command);
    }

    [Fact]
    public void BuildDrawRouteCopyCommand_ParkingEndpoint_UsesTokenNotNodeRef()
    {
        var vm = MakeViewModel();
        vm.SetLayoutForTesting(LinearLayout());
        var ac = MakeAircraft(Lat0, Lon0);

        vm.StartDrawRoute(ac);
        Assert.True(vm.AddDrawWaypoint(3));
        Assert.True(vm.AddDrawWaypoint(4)); // node 4 = Parking "8B"
        var result = vm.FinishDrawRoute();
        Assert.NotNull(result);

        var (route, _, spot) = result!.Value;
        var command = vm.BuildDrawRouteCopyCommand(route, spot);

        // The @parking token pins the stop, so no terminal node-ref is appended.
        Assert.Equal("TAXI V @8B", command);
        Assert.DoesNotContain('#', command);
    }

    [Fact]
    public void FinishDrawRoute_SpotTerminus_AppendsDollarToken()
    {
        var vm = MakeViewModel();
        vm.SetLayoutForTesting(SpotLayout());
        var ac = MakeAircraft(Lat0, Lon0);

        vm.StartDrawRoute(ac);
        Assert.True(vm.AddDrawWaypoint(3)); // node 3 = Spot "7"
        var result = vm.FinishDrawRoute();

        Assert.NotNull(result);
        Assert.NotNull(result!.Value.Spot);
        Assert.Equal("$7", result.Value.Spot!.Token);
    }

    private static GroundViewModel MakeViewModel()
    {
        var connection = new ServerConnection();
        return new GroundViewModel(connection, sendCommand: (_, _, _) => Task.CompletedTask);
    }

    private static AircraftModel MakeAircraft(double lat, double lon) => new() { Callsign = "TST123", Position = new LatLon(lat, lon) };

    // Linear chain 1-2-3 on taxiway V, then parking node 4 ("8B") via a RAMP edge.
    private static GroundLayoutDto LinearLayout() =>
        new(
            "TST",
            [
                new GroundNodeDto(1, Lat0, Lon0, "TaxiwayIntersection", null, null, null),
                new GroundNodeDto(2, Lat0 + 0.001, Lon0, "TaxiwayIntersection", null, null, null),
                new GroundNodeDto(3, Lat0 + 0.002, Lon0, "TaxiwayIntersection", null, null, null),
                new GroundNodeDto(4, Lat0 + 0.003, Lon0, "Parking", "8B", null, null),
            ],
            [new GroundEdgeDto(1, 2, "V", 0.06, null), new GroundEdgeDto(2, 3, "V", 0.06, null), new GroundEdgeDto(3, 4, "RAMP", 0.06, null)],
            null,
            null
        );

    // Linear chain 1-2 on taxiway V, then spot node 3 ("7") via a RAMP edge.
    private static GroundLayoutDto SpotLayout() =>
        new(
            "TST",
            [
                new GroundNodeDto(1, Lat0, Lon0, "TaxiwayIntersection", null, null, null),
                new GroundNodeDto(2, Lat0 + 0.001, Lon0, "TaxiwayIntersection", null, null, null),
                new GroundNodeDto(3, Lat0 + 0.002, Lon0, "Spot", "7", null, null),
            ],
            [new GroundEdgeDto(1, 2, "V", 0.06, null), new GroundEdgeDto(2, 3, "RAMP", 0.06, null)],
            null,
            null
        );
}
