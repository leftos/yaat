using Xunit;
using Yaat.Sim;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Pathfinding;
using Yaat.Sim.Phases;

namespace Yaat.Sim.Tests.Pathfinding;

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

        var route = RouteMaterialiser.Materialise([], ctx, []);

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
        var route = RouteMaterialiser.Materialise(edges, ctx, []);

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
        var route = RouteMaterialiser.Materialise(edges, ctx, []);

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

        // A controller says "hold short 28R" — a single designator, never the combined "28R/10L".
        // Reciprocal matching must tag the 28R/10L bar as the explicit hold.
        var holdShortSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "28R" };
        var ctx = Context(layout, holdShorts: holdShortSet);

        var edges = new List<DirectionalEdge> { Directed(e01, n0, n1), Directed(e12, n1, n2) };
        var route = RouteMaterialiser.Materialise(edges, ctx, []);

        Assert.Single(route.HoldShortPoints);
        Assert.Equal(HoldShortReason.ExplicitHoldShort, route.HoldShortPoints[0].Reason);
    }

    // ---------------------------------------------------------------------------
    // Destination runway annotation
    // ---------------------------------------------------------------------------

    [Fact]
    public void RunwayDestination_TerminalBarIsDestinationRunway_EnRouteCrossingStaysCrossing()
    {
        // Taxiing to runway 28R: the en-route 01L/19R bar is a crossing; the terminal 28R/10L bar
        // is the destination runway (held short for departure, never auto-crossed onto).
        var n0 = Node(0, 37.700, -122.200);
        var nCross = Node(1, 37.701, -122.200, GroundNodeType.RunwayHoldShort);
        nCross.RunwayId = new RunwayIdentifier("01L", "19R");
        var n2 = Node(2, 37.702, -122.200);
        var nDest = Node(3, 37.703, -122.200, GroundNodeType.RunwayHoldShort);
        nDest.RunwayId = new RunwayIdentifier("28R", "10L");
        var layout = Layout(n0, nCross, n2, nDest);
        var e01 = Edge(n0, nCross, "A");
        var e12 = Edge(nCross, n2, "A");
        var e23 = Edge(n2, nDest, "A");

        var dest = new DestinationDescriptor(null, "28R", null, null, DestinationKind.Runway);
        var ctx = Context(layout, dest);

        var edges = new List<DirectionalEdge> { Directed(e01, n0, nCross), Directed(e12, nCross, n2), Directed(e23, n2, nDest) };
        var route = RouteMaterialiser.Materialise(edges, ctx, []);

        var crossHs = route.HoldShortPoints.Single(h => h.NodeId == nCross.Id);
        Assert.Equal(HoldShortReason.RunwayCrossing, crossHs.Reason);

        var destHs = route.HoldShortPoints.Single(h => h.NodeId == nDest.Id);
        Assert.Equal(HoldShortReason.DestinationRunway, destHs.Reason);
        Assert.Equal("28R", destHs.TargetName);
    }

    [Fact]
    public void RunwayDestination_RouteWalksPastHoldShort_TruncatesAtHoldShort()
    {
        // A departure-runway taxi route must STOP at the runway hold-short. If the search walked one
        // segment past it (onto the runway on-ramp), the materialiser must truncate AT the hold-short —
        // proceeding onto the runway is clearance-gated by the LineUp / Crossing phases, not baked into
        // the taxi route. (Unlike a node destination, a runway destination gets no "one past" buffer.)
        var n0 = Node(0, 37.700, -122.200);
        var n1 = Node(1, 37.701, -122.200);
        var hs = Node(2, 37.702, -122.200, GroundNodeType.RunwayHoldShort);
        hs.RunwayId = new RunwayIdentifier("28R", "10L");
        var past = Node(3, 37.703, -122.200); // past the hold-short, toward the runway
        var layout = Layout(n0, n1, hs, past);
        var e01 = Edge(n0, n1, "B");
        var e1hs = Edge(n1, hs, "B");
        var ehspast = Edge(hs, past, "B");

        var dest = new DestinationDescriptor(null, "28R", null, null, DestinationKind.Runway);
        var ctx = Context(layout, dest);

        var edges = new List<DirectionalEdge> { Directed(e01, n0, n1), Directed(e1hs, n1, hs), Directed(ehspast, hs, past) };
        var route = RouteMaterialiser.Materialise(edges, ctx, []);

        Assert.Equal(hs.Id, route.Segments[^1].ToNodeId);
        Assert.DoesNotContain(route.Segments, s => s.ToNodeId == past.Id);
    }

    [Fact]
    public void RunwayDestination_ToSummary_IncludesRwyDesignator()
    {
        // Codex HIGH #1: the DestinationRunway hold-short reason is what makes TaxiRoute.ToSummary()
        // surface the "RWY <id>" semantics downstream code/tests rely on. A runway-destination route
        // must emit that reason AND produce a summary containing "RWY 28R".
        var n0 = Node(0, 37.700, -122.200);
        var n1 = Node(1, 37.701, -122.200);
        var nDest = Node(2, 37.702, -122.200, GroundNodeType.RunwayHoldShort);
        nDest.RunwayId = new RunwayIdentifier("28R", "10L");
        var layout = Layout(n0, n1, nDest);
        var e01 = Edge(n0, n1, "A");
        var e12 = Edge(n1, nDest, "A");

        var dest = new DestinationDescriptor(null, "28R", null, null, DestinationKind.Runway);
        var ctx = Context(layout, dest);

        var edges = new List<DirectionalEdge> { Directed(e01, n0, n1), Directed(e12, n1, nDest) };
        var route = RouteMaterialiser.Materialise(edges, ctx, []);

        var destHs = route.HoldShortPoints.Single(h => h.NodeId == nDest.Id);
        Assert.Equal(HoldShortReason.DestinationRunway, destHs.Reason);

        // The summary loses the RWY semantics if the reason is dropped (the exact Codex regression).
        Assert.Contains("RWY 28R", route.ToSummary());
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

        var route = RouteMaterialiser.Materialise(edges, ctx, []);

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
        var route = RouteMaterialiser.Materialise(edges, ctx, []);

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
        var route = RouteMaterialiser.Materialise(edges, ctx, []);

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
        var route = RouteMaterialiser.Materialise(edges, ctx, []);

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
        var route = RouteMaterialiser.Materialise(edges, ctx, []);

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
    public void FindFullLengthLineupHoldShort_MultipleHoldShorts_PicksBarNearestRequestedThreshold()
    {
        // Runway 28R/10L: the 28R threshold (full-length departure end for a 28R takeoff) is the EAST
        // end. The full-length lineup bar is the hold-short nearest that designator's threshold — nEast.
        // The aircraft starts to the WEST (nearer the wrong bar), so a nearest-start or wrong-end
        // heuristic would pick nWest; only the authoritative per-designator threshold picks nEast.
        var n0 = Node(0, 37.700, -122.210); // start node, west of both bars
        var nWest = Node(1, 37.700, -122.208, GroundNodeType.RunwayHoldShort);
        nWest.RunwayId = new RunwayIdentifier("28R", "10L");
        var nEast = Node(2, 37.700, -122.202, GroundNodeType.RunwayHoldShort);
        nEast.RunwayId = new RunwayIdentifier("28R", "10L");
        var layout = Layout(n0, nWest, nEast);

        // End1 = 28R threshold at the EAST end; End2 = 10L threshold at the WEST end.
        var runway = new RunwayInfo
        {
            AirportId = "TEST",
            Id = new RunwayIdentifier("28R", "10L"),
            Designator = "28R",
            Lat1 = 37.700,
            Lon1 = -122.200,
            Elevation1Ft = 0,
            TrueHeading1 = new TrueHeading(280),
            Lat2 = 37.700,
            Lon2 = -122.209,
            Elevation2Ft = 0,
            TrueHeading2 = new TrueHeading(100),
            LengthFt = 9000,
            WidthFt = 150,
        };
        using var scope = NavigationDatabase.ScopedOverride(TestNavDbFactory.WithRunways(runway));

        var result = RouteMaterialiser.FindFullLengthLineupHoldShort(layout, n0, "28R", [nWest, nEast]);
        Assert.Equal(nEast.Id, result.Id);
    }
}
