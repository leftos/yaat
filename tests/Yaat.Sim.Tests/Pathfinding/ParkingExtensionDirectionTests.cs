using Xunit;
using Xunit.Abstractions;
using Yaat.Sim;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Pathfinding;

/// <summary>
/// The final taxiway transition toward a PARKING/SPOT destination must steer the junction selection
/// toward the destination — just as it already does for a runway destination. OAK <c>TAXI G D @NEW1</c>:
/// NEW1 hangs off taxiway D (node 1244) to the north, but the cheapest G→D junction lands the aircraft
/// on D facing south (toward C). The destination-aware terminus search then can't reach NEW1 without an
/// inadmissible U-turn, so the route walks south to the C junction and detours ~80 segments back around
/// (down C, across E, back over runway 28R, up H, down D, to the ramp). The route must instead turn the
/// short way onto D toward NEW1.
/// </summary>
public class ParkingExtensionDirectionTests
{
    private readonly ITestOutputHelper _output;

    public ParkingExtensionDirectionTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    private static GroundNode NearestNodeOnTaxiway(AirportGroundLayout layout, string taxiway, double lat, double lon) =>
        layout
            .Nodes.Values.Where(n => n.Edges.Any(e => e.MatchesTaxiway(taxiway)))
            .OrderBy(n => GeoMath.DistanceNm(lat, lon, n.Position.Lat, n.Position.Lon))
            .First();

    [Fact]
    public void Oak_TaxiGD_ToNew1_TurnsTowardParking_NotAwayDownC()
    {
        var layout = new TestAirportGroundData(FilletMode.Standard).GetLayout("OAK");
        if (layout is null || TestVnasData.NavigationDb is null)
        {
            _output.WriteLine("oak layout / navdata unavailable — skipping");
            return;
        }

        var newParking = layout.FindParkingByName("NEW1");
        Assert.NotNull(newParking);

        // A node on taxiway G near the 28R crossing — the route continues G → D → NEW1.
        var start = NearestNodeOnTaxiway(layout, "G", 37.727440, -122.212859);
        _output.WriteLine($"start node = {start.Id}, NEW1 = {newParking.Id}");

        var route = TaxiPathfinder.ResolveExplicitPath(
            layout,
            start.Id,
            ["G", "D"],
            out string? failReason,
            new ExplicitPathOptions
            {
                AirportId = "OAK",
                DestinationHintNode = newParking,
                DiagnosticLog = msg => _output.WriteLine(msg),
            },
            AircraftCategory.Piston
        );

        Assert.NotNull(route);
        Assert.Null(failReason);
        _output.WriteLine($"Route: {route.Segments.Count} segments, {route.TotalDistanceNm:F2} nm → {route.ToSummary()}");

        // Reaches NEW1.
        Assert.Equal(newParking.Id, route.Segments[^1].ToNodeId);

        // The direct route is G → D → ramp to NEW1 (~0.9 nm). The wrong-direction detour signature is
        // doubling back onto runway 28R and threading down taxiway C — neither belongs on this taxi, and
        // the detour roughly triples the distance. (Segment count is not a proxy: D is densely noded.)
        Assert.DoesNotContain(route.Segments, s => s.Edge.Edge.IsRunwayCenterline);
        Assert.DoesNotContain(route.Segments, s => s.TaxiwayName == "C");
        Assert.True(route.TotalDistanceNm < 1.5, $"expected a short direct route to NEW1, got {route.TotalDistanceNm:F2} nm");
    }
}
