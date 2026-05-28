using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.V2;

namespace Yaat.Sim.Tests.Pathfinding.V2;

/// <summary>
/// Unit tests for <see cref="RouteMaterialiser"/>.
/// All tests use inline synthetic layouts — no navdata required.
/// </summary>
public class RouteMaterialiserTests
{
    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private static GroundNode Node(int id, double lat, double lon, GroundNodeType type = GroundNodeType.TaxiwayIntersection, string? name = null) =>
        new()
        {
            Id = id,
            Position = new LatLon(lat, lon),
            Type = type,
            Name = name,
        };

    private static GroundEdge Edge(GroundNode a, GroundNode b, string twy = "A")
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
        return edge;
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

    private static DirectionalEdge Directed(IGroundEdge e, GroundNode from, GroundNode to) => e.Directed(from, to);

    private static SearchContext Context(
        AirportGroundLayout layout,
        DestinationDescriptor? dest = null,
        IReadOnlySet<string>? authorized = null,
        IReadOnlySet<string>? holdShorts = null,
        AircraftCategory category = AircraftCategory.Jet
    ) =>
        new(
            layout,
            0,
            dest ?? new DestinationDescriptor(null, null, null, null, DestinationKind.EndOfLastTaxiway),
            [],
            authorized,
            holdShorts ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            category,
            null,
            null
        );

    // ---------------------------------------------------------------------------
    // Empty route
    // ---------------------------------------------------------------------------

    [Fact]
    public void EmptyEdges_ProducesEmptyRoute()
    {
        var n0 = Node(0, 37.700, -122.200);
        var layout = Layout(n0);
        var ctx = Context(layout);

        var route = RouteMaterialiser.Materialise([], ctx);

        Assert.Empty(route.Segments);
        Assert.Empty(route.HoldShortPoints);
        Assert.Equal(0, route.CurrentSegmentIndex);
    }

    // ---------------------------------------------------------------------------
    // Single straight edge
    // ---------------------------------------------------------------------------

    [Fact]
    public void SingleStraightEdge_ProducesOneSegment_NoHoldShorts()
    {
        var n0 = Node(0, 37.700, -122.200);
        var n1 = Node(1, 37.701, -122.200);
        var layout = Layout(n0, n1);
        var e = Edge(n0, n1, "A");

        var ctx = Context(layout);
        var edges = new List<DirectionalEdge> { Directed(e, n0, n1) };
        var route = RouteMaterialiser.Materialise(edges, ctx);

        Assert.Single(route.Segments);
        Assert.Equal("A", route.Segments[0].TaxiwayName);
        Assert.Empty(route.HoldShortPoints);
    }

    // ---------------------------------------------------------------------------
    // Runway crossing annotation
    // ---------------------------------------------------------------------------

    [Fact]
    public void RunwayHoldShortNode_AnnotatedAsRunwayCrossing()
    {
        var n0 = Node(0, 37.700, -122.200);
        var n1 = Node(1, 37.701, -122.200, GroundNodeType.RunwayHoldShort);
        n1.RunwayId = new RunwayIdentifier("28R", "10L");
        var n2 = Node(2, 37.702, -122.200);
        var layout = Layout(n0, n1, n2);
        var e01 = Edge(n0, n1, "A");
        var e12 = Edge(n1, n2, "A");

        var ctx = Context(layout);
        var edges = new List<DirectionalEdge> { Directed(e01, n0, n1), Directed(e12, n1, n2) };
        var route = RouteMaterialiser.Materialise(edges, ctx);

        Assert.Single(route.HoldShortPoints);
        Assert.Equal(n1.Id, route.HoldShortPoints[0].NodeId);
        Assert.Equal(HoldShortReason.RunwayCrossing, route.HoldShortPoints[0].Reason);
    }

    // ---------------------------------------------------------------------------
    // Explicit hold-short annotation
    // ---------------------------------------------------------------------------

    [Fact]
    public void ExplicitHoldShort_ConfiguredInContext_TaggedCorrectly()
    {
        var n0 = Node(0, 37.700, -122.200);
        var n1 = Node(1, 37.701, -122.200, GroundNodeType.RunwayHoldShort);
        n1.RunwayId = new RunwayIdentifier("28R", "10L");
        var n2 = Node(2, 37.702, -122.200);
        var layout = Layout(n0, n1, n2);
        var e01 = Edge(n0, n1, "A");
        var e12 = Edge(n1, n2, "A");

        var holdShortSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "28R/10L" };
        var ctx = Context(layout, holdShorts: holdShortSet);

        var edges = new List<DirectionalEdge> { Directed(e01, n0, n1), Directed(e12, n1, n2) };
        var route = RouteMaterialiser.Materialise(edges, ctx);

        Assert.Single(route.HoldShortPoints);
        Assert.Equal(HoldShortReason.ExplicitHoldShort, route.HoldShortPoints[0].Reason);
    }

    // ---------------------------------------------------------------------------
    // Truncation
    // ---------------------------------------------------------------------------

    [Fact]
    public void Truncation_RouteWithEdgesPastDestination_TruncatesToOnePastDestination()
    {
        var n0 = Node(0, 37.700, -122.200);
        var n1 = Node(1, 37.701, -122.200);
        var n2 = Node(2, 37.702, -122.200);
        var n3 = Node(3, 37.703, -122.200);
        var layout = Layout(n0, n1, n2, n3);
        var e01 = Edge(n0, n1, "A");
        var e12 = Edge(n1, n2, "A");
        var e23 = Edge(n2, n3, "A");

        // Destination is n1 — route should be truncated to two segments (n0→n1→n2, one past dest).
        var dest = new DestinationDescriptor(n1.Id, null, null, null, DestinationKind.Node);
        var ctx = Context(layout, dest);

        var edges = new List<DirectionalEdge> { Directed(e01, n0, n1), Directed(e12, n1, n2), Directed(e23, n2, n3) };

        var route = RouteMaterialiser.Materialise(edges, ctx);

        Assert.Equal(2, route.Segments.Count);
        Assert.Equal(n2.Id, route.Segments[1].ToNodeId);
    }

    // ---------------------------------------------------------------------------
    // Parking destination
    // ---------------------------------------------------------------------------

    [Fact]
    public void ParkingDestination_DestinationParkingPopulated()
    {
        var n0 = Node(0, 37.700, -122.200);
        var n1 = Node(1, 37.701, -122.200, GroundNodeType.Parking, "D8");
        var layout = Layout(n0, n1);
        var e = Edge(n0, n1, "A");

        var dest = new DestinationDescriptor(n1.Id, null, "D8", null, DestinationKind.Parking);
        var ctx = Context(layout, dest);

        var edges = new List<DirectionalEdge> { Directed(e, n0, n1) };
        var route = RouteMaterialiser.Materialise(edges, ctx);

        Assert.Equal("D8", route.DestinationParking);
        Assert.Null(route.DestinationSpot);
    }

    // ---------------------------------------------------------------------------
    // Spot destination
    // ---------------------------------------------------------------------------

    [Fact]
    public void SpotDestination_DestinationSpotPopulated()
    {
        var n0 = Node(0, 37.700, -122.200);
        var n1 = Node(1, 37.701, -122.200, GroundNodeType.Spot, "32");
        var layout = Layout(n0, n1);
        var e = Edge(n0, n1, "A");

        var dest = new DestinationDescriptor(n1.Id, null, null, "32", DestinationKind.Spot);
        var ctx = Context(layout, dest);

        var edges = new List<DirectionalEdge> { Directed(e, n0, n1) };
        var route = RouteMaterialiser.Materialise(edges, ctx);

        Assert.Null(route.DestinationParking);
        Assert.Equal("32", route.DestinationSpot);
    }

    // ---------------------------------------------------------------------------
    // Warning for unauthorized letter taxiway
    // ---------------------------------------------------------------------------

    [Fact]
    public void UnauthorizedLetterTaxiway_EmitsWarning()
    {
        var n0 = Node(0, 37.700, -122.200);
        var n1 = Node(1, 37.701, -122.200);
        var layout = Layout(n0, n1);
        var e = Edge(n0, n1, "X");

        var authorized = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "A" };
        var ctx = Context(layout, authorized: authorized);

        var edges = new List<DirectionalEdge> { Directed(e, n0, n1) };
        var route = RouteMaterialiser.Materialise(edges, ctx);

        Assert.Single(route.Warnings);
        Assert.Contains("X", route.Warnings[0]);
    }

    [Fact]
    public void AuthorizedTaxiway_NoWarning()
    {
        var n0 = Node(0, 37.700, -122.200);
        var n1 = Node(1, 37.701, -122.200);
        var layout = Layout(n0, n1);
        var e = Edge(n0, n1, "A");

        var authorized = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "A" };
        var ctx = Context(layout, authorized: authorized);

        var edges = new List<DirectionalEdge> { Directed(e, n0, n1) };
        var route = RouteMaterialiser.Materialise(edges, ctx);

        Assert.Empty(route.Warnings);
    }

    // ---------------------------------------------------------------------------
    // FindFullLengthLineupHoldShort
    // ---------------------------------------------------------------------------

    [Fact]
    public void FindFullLengthLineupHoldShort_EmptyList_ReturnsStartNode()
    {
        var n0 = Node(0, 37.700, -122.200);
        var layout = Layout(n0);

        var result = RouteMaterialiser.FindFullLengthLineupHoldShort(layout, n0, "28R", []);
        Assert.Equal(n0.Id, result.Id);
    }

    [Fact]
    public void FindFullLengthLineupHoldShort_SingleCandidate_ReturnsThatNode()
    {
        var n0 = Node(0, 37.700, -122.200);
        var n1 = Node(1, 37.701, -122.200, GroundNodeType.RunwayHoldShort);
        n1.RunwayId = new RunwayIdentifier("28R", "10L");
        var layout = Layout(n0, n1);

        var result = RouteMaterialiser.FindFullLengthLineupHoldShort(layout, n0, "28R", [n1]);
        Assert.Equal(n1.Id, result.Id);
    }

    [Fact]
    public void FindFullLengthLineupHoldShort_MultipleHoldShorts_PicksNearestToThreshold()
    {
        // Runway 28R runs roughly east-west; threshold (departure end for 28R) is at the west end.
        // Near-threshold hold-short: nNear (west side), intermediate hold-short: nFar (east side).
        // We place runway centerline edges so the algorithm can find the threshold proxy.
        var n0 = Node(0, 37.700, -122.210); // start node (aircraft position)
        var nNear = Node(1, 37.700, -122.208, GroundNodeType.RunwayHoldShort); // near threshold
        nNear.RunwayId = new RunwayIdentifier("28R", "10L");
        var nFar = Node(2, 37.700, -122.202, GroundNodeType.RunwayHoldShort); // far from threshold
        nFar.RunwayId = new RunwayIdentifier("28R", "10L");

        // Two runway centerline nodes: threshold-end at west (-122.209), other end at east (-122.200).
        var rwyWest = Node(10, 37.700, -122.209);
        var rwyEast = Node(11, 37.700, -122.200);

        var layout = Layout(n0, nNear, nFar, rwyWest, rwyEast);

        // Add a runway centerline edge so MatchesRunway("28R") returns true.
        var rwyEdge = new GroundEdge
        {
            Nodes = [rwyWest, rwyEast],
            TaxiwayName = "RWY28R/10L",
            DistanceNm = GeoMath.DistanceNm(rwyWest.Position, rwyEast.Position),
        };
        rwyWest.Edges.Add(rwyEdge);
        rwyEast.Edges.Add(rwyEdge);
        layout.Edges.Add(rwyEdge);

        // nNear is the full-length lineup hold-short (nearest to threshold = rwyWest at -122.209).
        var result = RouteMaterialiser.FindFullLengthLineupHoldShort(layout, n0, "28R", [nNear, nFar]);
        Assert.Equal(nNear.Id, result.Id);
    }
}
