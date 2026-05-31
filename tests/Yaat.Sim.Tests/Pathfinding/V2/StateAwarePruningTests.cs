using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Pathfinding;
using Yaat.Sim.Tests.Helpers;
using Yaat.Sim.Tests.Simulation.GroundTaxi;

namespace Yaat.Sim.Tests.Pathfinding.V2;

/// <summary>
/// Regression tests for the state-aware A* pruning fix (#4). Onward-edge admissibility
/// (<see cref="GeometricAdmissibility.IsAdmissible"/>) depends on arrival bearing, so closing
/// the A* open set by node id alone lets a cheaper arrival with a dead-end bearing suppress the
/// only viable (costlier, different-bearing) arrival — a false <see cref="FailureKind.DestinationUnreachable"/>.
/// Keying the closed set by (nodeId, bearing-bucket) fixes it.
/// </summary>
public class StateAwarePruningTests
{
    private readonly ITestOutputHelper output;

    public StateAwarePruningTests(ITestOutputHelper output)
    {
        this.output = output;
        TestVnasData.EnsureInitialized();
    }

    private static GroundNode Node(int id, double lat, double lon, GroundNodeType type = GroundNodeType.TaxiwayIntersection) =>
        new()
        {
            Id = id,
            Position = new LatLon(lat, lon),
            Type = type,
        };

    private static void Edge(GroundNode a, GroundNode b, string twy = "A")
    {
        double dist = GeoMath.DistanceNm(a.Position, b.Position);
        var edge = new GroundEdge
        {
            Nodes = [a, b],
            TaxiwayName = twy,
            DistanceNm = dist,
        };
        a.Edges.Add(edge);
        b.Edges.Add(edge);
    }

    private static AirportGroundLayout Layout(params GroundNode[] nodes)
    {
        var layout = new AirportGroundLayout { AirportId = "TEST" };
        foreach (var n in nodes)
        {
            layout.Nodes[n.Id] = n;
        }

        return layout;
    }

    private static SearchContext NodeContext(AirportGroundLayout layout, int from, int to) =>
        new(
            layout,
            from,
            new DestinationDescriptor(to, null, null, null, DestinationKind.Node),
            [],
            null,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            AircraftCategory.Jet,
            RoutePreference.FewestTurns,
            null
        );

    // ---------------------------------------------------------------------------
    // Synthetic repro: cheap arrival at the pre-destination junction dead-ends;
    // the only viable arrival is costlier and arrives at a different bearing.
    // ---------------------------------------------------------------------------

    [Fact]
    public void CheapArrivalDeadEnds_CostlyArrivalReachesDestination()
    {
        // Topology (lat/lon; +lon = east, +lat = north):
        //   D (dest) sits due EAST of N, so N->D departs at ~90deg.
        //   Cheap arm  S->A->N arrives at N heading WEST (~270deg) => N->D is a 180deg turn (inadmissible).
        //   Costly arm S->C->E->B->N arrives at N heading SOUTH (~180deg) => N->D is a 90deg turn (admissible).
        // The cheap arm is shorter / fewer-turns, so node-id pruning closes N at the dead-end
        // bearing and prunes the costly arm -> false DestinationUnreachable. State-aware pruning
        // keeps both arrivals (different bearing buckets) and routes via the costly arm.
        var n = Node(1, 37.7000, -122.2000);
        var d = Node(2, 37.7000, -122.1980);
        var a = Node(3, 37.7000, -122.1990);
        var s = Node(4, 37.7000, -122.1985);
        var c = Node(5, 37.7020, -122.1985);
        var e = Node(6, 37.7020, -122.2000);
        var b = Node(7, 37.7010, -122.2000);
        var layout = Layout(n, d, a, s, c, e, b);

        Edge(s, a); // cheap arm
        Edge(a, n);
        Edge(n, d);
        Edge(s, c); // costly arm
        Edge(c, e);
        Edge(e, b);
        Edge(b, n);

        var ctx = NodeContext(layout, s.Id, d.Id);
        var (route, failure) = AutoRouter.Run(ctx);

        output.WriteLine($"route={(route is null ? "NULL" : route.Segments.Count + " segs")}  failure={failure?.Kind.ToString() ?? "none"}");

        Assert.Null(failure);
        Assert.NotNull(route);
        Assert.Equal(d.Id, route.Segments[^1].ToNodeId);

        // Must have routed via the costly arm (through B), proving the bearing-aware arrival was recovered.
        Assert.Contains(route.Segments, seg => (seg.FromNodeId == b.Id) || (seg.ToNodeId == b.Id));
    }

    // ---------------------------------------------------------------------------
    // Real-airport repros (V2 fillets). Both were HARD failures in the necessity
    // sweep: production returns no route; the bearing-aware search resolves one.
    // ---------------------------------------------------------------------------

    [Theory]
    [InlineData("OAK", "28R", "S8B")]
    [InlineData("FLL", "10L", "SHE4")]
    public void RunwayToParking_FormerlyUnreachable_ResolvesUnderStateAwarePruning(string airport, string runway, string parking)
    {
        var layout = new TestAirportGroundData(FilletMode.Standard).GetLayout(airport);
        if (layout is null)
        {
            output.WriteLine($"SKIP {airport}: layout unavailable");
            return;
        }

        var holdShorts = layout.GetRunwayHoldShortNodes(runway);
        var parkingNode = layout.FindParkingByName(parking);
        if (holdShorts.Count == 0 || parkingNode is null)
        {
            output.WriteLine($"SKIP {airport} {runway}->{parking}: endpoints unavailable");
            return;
        }

        var lineup = RouteMaterialiser.FindFullLengthLineupHoldShort(layout, holdShorts[0], runway, holdShorts);

        var route = TaxiPathfinder.FindRoute(layout, lineup.Id, parkingNode.Id, AircraftCategory.Jet);

        output.WriteLine(
            $"{airport} {runway}({lineup.Id})->{parking}({parkingNode.Id}): {(route is null ? "NULL" : route.Segments.Count + " segs")}"
        );

        Assert.NotNull(route);
        Assert.Equal(parkingNode.Id, route.Segments[^1].ToNodeId);
    }
}
