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
        foreach (GroundNode n in nodes)
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
        IReadOnlySet<HoldShortTarget>? holdShorts = null,
        AircraftCategory category = AircraftCategory.Jet,
        IReadOnlyList<string>? waypointSequence = null
    ) =>
        new(
            layout,
            0,
            dest ?? new DestinationDescriptor(null, null, null, null, DestinationKind.EndOfLastTaxiway),
            waypointSequence ?? [],
            authorized,
            holdShorts ?? new HashSet<HoldShortTarget>(),
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
        GroundNode n0 = Node(0, 37.700, -122.200);
        AirportGroundLayout layout = Layout(n0);
        SearchContext ctx = Context(layout);

        TaxiRoute route = RouteMaterialiser.Materialise([], ctx, []);

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
        GroundNode n0 = Node(0, 37.700, -122.200);
        GroundNode n1 = Node(1, 37.701, -122.200);
        AirportGroundLayout layout = Layout(n0, n1);
        GroundEdge e = Edge(n0, n1, "A");

        SearchContext ctx = Context(layout);
        var edges = new List<DirectionalEdge> { Directed(e, n0, n1) };
        TaxiRoute route = RouteMaterialiser.Materialise(edges, ctx, []);

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
        GroundNode n0 = Node(0, 37.700, -122.200);
        GroundNode n1 = Node(1, 37.701, -122.200, GroundNodeType.RunwayHoldShort);
        n1.RunwayId = new RunwayIdentifier("28R", "10L");
        GroundNode n2 = Node(2, 37.702, -122.200);
        AirportGroundLayout layout = Layout(n0, n1, n2);
        GroundEdge e01 = Edge(n0, n1, "A");
        GroundEdge e12 = Edge(n1, n2, "A");

        SearchContext ctx = Context(layout);
        var edges = new List<DirectionalEdge> { Directed(e01, n0, n1), Directed(e12, n1, n2) };
        TaxiRoute route = RouteMaterialiser.Materialise(edges, ctx, []);

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
        GroundNode n0 = Node(0, 37.700, -122.200);
        GroundNode n1 = Node(1, 37.701, -122.200, GroundNodeType.RunwayHoldShort);
        n1.RunwayId = new RunwayIdentifier("28R", "10L");
        GroundNode n2 = Node(2, 37.702, -122.200);
        AirportGroundLayout layout = Layout(n0, n1, n2);
        GroundEdge e01 = Edge(n0, n1, "A");
        GroundEdge e12 = Edge(n1, n2, "A");

        // A controller says "hold short 28R" — a single designator, never the combined "28R/10L".
        // Reciprocal matching must tag the 28R/10L bar as the explicit hold.
        var holdShortSet = new HashSet<HoldShortTarget> { HoldShortTarget.Parse("28R") };
        SearchContext ctx = Context(layout, holdShorts: holdShortSet);

        var edges = new List<DirectionalEdge> { Directed(e01, n0, n1), Directed(e12, n1, n2) };
        TaxiRoute route = RouteMaterialiser.Materialise(edges, ctx, []);

        Assert.Single(route.HoldShortPoints);
        Assert.Equal(HoldShortReason.ExplicitHoldShort, route.HoldShortPoints[0].Reason);
    }

    [Fact]
    public void LocatedRunwayHoldShort_MatchingLocation_PromotedToExplicit()
    {
        // HS 28R@J — the bar sits at the A/J junction, so the located target promotes it.
        GroundNode n0 = Node(0, 37.700, -122.200);
        GroundNode n1 = Node(1, 37.701, -122.200, GroundNodeType.RunwayHoldShort);
        n1.RunwayId = new RunwayIdentifier("28R", "10L");
        GroundNode n2 = Node(2, 37.702, -122.200);
        GroundNode nJ = Node(3, 37.701, -122.201);
        AirportGroundLayout layout = Layout(n0, n1, n2, nJ);
        GroundEdge e01 = Edge(n0, n1, "A");
        GroundEdge e12 = Edge(n1, n2, "A");
        Edge(n1, nJ, "J");

        SearchContext ctx = Context(layout, holdShorts: new HashSet<HoldShortTarget> { HoldShortTarget.Parse("28R@J") });
        var edges = new List<DirectionalEdge> { Directed(e01, n0, n1), Directed(e12, n1, n2) };
        TaxiRoute route = RouteMaterialiser.Materialise(edges, ctx, []);

        HoldShortPoint hs = Assert.Single(route.HoldShortPoints);
        Assert.Equal(HoldShortReason.ExplicitHoldShort, hs.Reason);
        Assert.DoesNotContain(route.Warnings, w => w.Contains("not applied", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void LocatedRunwayHoldShort_MismatchedLocation_StaysCrossing_AndWarns()
    {
        // HS 28R@Z — the route crosses 28R, but not at a node on Z. The crossing must stay a plain
        // RunwayCrossing (eligible for AutoCross), and the controller must be told the located
        // hold-short bound nothing — a bare name match would silently swallow it (issue #358 review).
        GroundNode n0 = Node(0, 37.700, -122.200);
        GroundNode n1 = Node(1, 37.701, -122.200, GroundNodeType.RunwayHoldShort);
        n1.RunwayId = new RunwayIdentifier("28R", "10L");
        GroundNode n2 = Node(2, 37.702, -122.200);
        AirportGroundLayout layout = Layout(n0, n1, n2);
        GroundEdge e01 = Edge(n0, n1, "A");
        GroundEdge e12 = Edge(n1, n2, "A");

        SearchContext ctx = Context(layout, holdShorts: new HashSet<HoldShortTarget> { HoldShortTarget.Parse("28R@Z") });
        var edges = new List<DirectionalEdge> { Directed(e01, n0, n1), Directed(e12, n1, n2) };
        TaxiRoute route = RouteMaterialiser.Materialise(edges, ctx, []);

        HoldShortPoint hs = Assert.Single(route.HoldShortPoints);
        Assert.Equal(HoldShortReason.RunwayCrossing, hs.Reason);
        Assert.Contains(route.Warnings, w => w.Contains("HS 28R@Z not applied", StringComparison.OrdinalIgnoreCase));
    }

    // ---------------------------------------------------------------------------
    // Destination runway annotation
    // ---------------------------------------------------------------------------

    [Fact]
    public void RunwayDestination_TerminalBarIsDestinationRunway_EnRouteCrossingStaysCrossing()
    {
        // Taxiing to runway 28R: the en-route 01L/19R bar is a crossing; the terminal 28R/10L bar
        // is the destination runway (held short for departure, never auto-crossed onto).
        GroundNode n0 = Node(0, 37.700, -122.200);
        GroundNode nCross = Node(1, 37.701, -122.200, GroundNodeType.RunwayHoldShort);
        nCross.RunwayId = new RunwayIdentifier("01L", "19R");
        GroundNode n2 = Node(2, 37.702, -122.200);
        GroundNode nDest = Node(3, 37.703, -122.200, GroundNodeType.RunwayHoldShort);
        nDest.RunwayId = new RunwayIdentifier("28R", "10L");
        AirportGroundLayout layout = Layout(n0, nCross, n2, nDest);
        GroundEdge e01 = Edge(n0, nCross, "A");
        GroundEdge e12 = Edge(nCross, n2, "A");
        GroundEdge e23 = Edge(n2, nDest, "A");

        var dest = new DestinationDescriptor(null, "28R", null, null, DestinationKind.Runway);
        SearchContext ctx = Context(layout, dest);

        var edges = new List<DirectionalEdge> { Directed(e01, n0, nCross), Directed(e12, nCross, n2), Directed(e23, n2, nDest) };
        TaxiRoute route = RouteMaterialiser.Materialise(edges, ctx, []);

        HoldShortPoint crossHs = route.HoldShortPoints.Single(h => h.NodeId == nCross.Id);
        Assert.Equal(HoldShortReason.RunwayCrossing, crossHs.Reason);

        HoldShortPoint destHs = route.HoldShortPoints.Single(h => h.NodeId == nDest.Id);
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
        GroundNode n0 = Node(0, 37.700, -122.200);
        GroundNode n1 = Node(1, 37.701, -122.200);
        GroundNode hs = Node(2, 37.702, -122.200, GroundNodeType.RunwayHoldShort);
        hs.RunwayId = new RunwayIdentifier("28R", "10L");
        GroundNode past = Node(3, 37.703, -122.200); // past the hold-short, toward the runway
        AirportGroundLayout layout = Layout(n0, n1, hs, past);
        GroundEdge e01 = Edge(n0, n1, "B");
        GroundEdge e1hs = Edge(n1, hs, "B");
        GroundEdge ehspast = Edge(hs, past, "B");

        var dest = new DestinationDescriptor(null, "28R", null, null, DestinationKind.Runway);
        SearchContext ctx = Context(layout, dest);

        var edges = new List<DirectionalEdge> { Directed(e01, n0, n1), Directed(e1hs, n1, hs), Directed(ehspast, hs, past) };
        TaxiRoute route = RouteMaterialiser.Materialise(edges, ctx, []);

        Assert.Equal(hs.Id, route.Segments[^1].ToNodeId);
        Assert.DoesNotContain(route.Segments, s => s.ToNodeId == past.Id);
    }

    [Fact]
    public void RunwayDestination_ToSummary_IncludesRwyDesignator()
    {
        // Codex HIGH #1: the DestinationRunway hold-short reason is what makes TaxiRoute.ToSummary()
        // surface the "RWY <id>" semantics downstream code/tests rely on. A runway-destination route
        // must emit that reason AND produce a summary containing "RWY 28R".
        GroundNode n0 = Node(0, 37.700, -122.200);
        GroundNode n1 = Node(1, 37.701, -122.200);
        GroundNode nDest = Node(2, 37.702, -122.200, GroundNodeType.RunwayHoldShort);
        nDest.RunwayId = new RunwayIdentifier("28R", "10L");
        AirportGroundLayout layout = Layout(n0, n1, nDest);
        GroundEdge e01 = Edge(n0, n1, "A");
        GroundEdge e12 = Edge(n1, nDest, "A");

        var dest = new DestinationDescriptor(null, "28R", null, null, DestinationKind.Runway);
        SearchContext ctx = Context(layout, dest);

        var edges = new List<DirectionalEdge> { Directed(e01, n0, n1), Directed(e12, n1, nDest) };
        TaxiRoute route = RouteMaterialiser.Materialise(edges, ctx, []);

        HoldShortPoint destHs = route.HoldShortPoints.Single(h => h.NodeId == nDest.Id);
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
        GroundNode n0 = Node(0, 37.700, -122.200);
        GroundNode n1 = Node(1, 37.701, -122.200);
        GroundNode n2 = Node(2, 37.702, -122.200);
        GroundNode n3 = Node(3, 37.703, -122.200);
        AirportGroundLayout layout = Layout(n0, n1, n2, n3);
        GroundEdge e01 = Edge(n0, n1, "A");
        GroundEdge e12 = Edge(n1, n2, "A");
        GroundEdge e23 = Edge(n2, n3, "A");

        // Destination is n1 — route should be truncated to two segments (n0→n1→n2, one past dest).
        var dest = new DestinationDescriptor(n1.Id, null, null, null, DestinationKind.Node);
        SearchContext ctx = Context(layout, dest);

        var edges = new List<DirectionalEdge> { Directed(e01, n0, n1), Directed(e12, n1, n2), Directed(e23, n2, n3) };

        TaxiRoute route = RouteMaterialiser.Materialise(edges, ctx, []);

        Assert.Equal(2, route.Segments.Count);
        Assert.Equal(n2.Id, route.Segments[1].ToNodeId);
    }

    // ---------------------------------------------------------------------------
    // Explicit hold-short of a runway the cleared taxiway crosses and continues past
    // ---------------------------------------------------------------------------

    [Fact]
    public void ExplicitHoldShort_RunwayCrossedByThroughTaxiway_ExtendsRouteToFarSideBar()
    {
        // "TAXI A HS 28R" where taxiway A crosses 28R/10L (near-side bar -> runway -> far-side bar)
        // and continues. The hold-short is an en-route restriction, not the terminus: the route must
        // extend THROUGH the crossing to the far-side bar so a later CROSS leaves the aircraft just
        // clear on the far side — not truncated one segment past the near bar, stranded on the runway.
        GroundNode n0 = Node(0, 37.700, -122.200);
        GroundNode nearHs = Node(1, 37.701, -122.200, GroundNodeType.RunwayHoldShort);
        nearHs.RunwayId = new RunwayIdentifier("28R", "10L");
        GroundNode mid = Node(2, 37.702, -122.200); // on the runway surface, between the bars
        GroundNode farHs = Node(3, 37.703, -122.200, GroundNodeType.RunwayHoldShort);
        farHs.RunwayId = new RunwayIdentifier("28R", "10L");
        GroundNode beyond = Node(4, 37.704, -122.200);
        AirportGroundLayout layout = Layout(n0, nearHs, mid, farHs, beyond);
        GroundEdge e01 = Edge(n0, nearHs, "A");
        GroundEdge e12 = Edge(nearHs, mid, "A");
        GroundEdge e23 = Edge(mid, farHs, "A");
        GroundEdge e34 = Edge(farHs, beyond, "A");

        var holdShorts = new HashSet<HoldShortTarget> { HoldShortTarget.Parse("28R") };
        SearchContext ctx = Context(layout, holdShorts: holdShorts, waypointSequence: ["A"]);

        var edges = new List<DirectionalEdge>
        {
            Directed(e01, n0, nearHs),
            Directed(e12, nearHs, mid),
            Directed(e23, mid, farHs),
            Directed(e34, farHs, beyond),
        };
        TaxiRoute route = RouteMaterialiser.Materialise(edges, ctx, []);

        // Route extends through the crossing and ends AT the far-side bar (just clear), not at the
        // mid-runway node one segment past the near bar.
        Assert.Equal(farHs.Id, route.Segments[^1].ToNodeId);

        // The near-side bar is the explicit hold-short; the far-side bar is the dropped exit pair.
        HoldShortPoint hs = Assert.Single(route.HoldShortPoints);
        Assert.Equal(nearHs.Id, hs.NodeId);
        Assert.Equal(HoldShortReason.ExplicitHoldShort, hs.Reason);
    }

    [Fact]
    public void ExplicitHoldShort_RunwayDeadEndNoPairedExitBar_TruncatesOnePastTheBar()
    {
        // Same shape but the taxiway DEAD-ENDS at the runway (no far-side bar of the same runway).
        // The hold-short is genuinely the terminus, so the route still stops one segment past it.
        GroundNode n0 = Node(0, 37.700, -122.200);
        GroundNode nearHs = Node(1, 37.701, -122.200, GroundNodeType.RunwayHoldShort);
        nearHs.RunwayId = new RunwayIdentifier("28R", "10L");
        GroundNode past = Node(2, 37.702, -122.200);
        GroundNode beyond = Node(3, 37.703, -122.200);
        AirportGroundLayout layout = Layout(n0, nearHs, past, beyond);
        GroundEdge e01 = Edge(n0, nearHs, "A");
        GroundEdge e12 = Edge(nearHs, past, "A");
        GroundEdge e23 = Edge(past, beyond, "A");

        var holdShorts = new HashSet<HoldShortTarget> { HoldShortTarget.Parse("28R") };
        SearchContext ctx = Context(layout, holdShorts: holdShorts, waypointSequence: ["A"]);

        var edges = new List<DirectionalEdge> { Directed(e01, n0, nearHs), Directed(e12, nearHs, past), Directed(e23, past, beyond) };
        TaxiRoute route = RouteMaterialiser.Materialise(edges, ctx, []);

        Assert.Equal(2, route.Segments.Count);
        Assert.Equal(past.Id, route.Segments[^1].ToNodeId);
    }

    // ---------------------------------------------------------------------------
    // Parking destination
    // ---------------------------------------------------------------------------

    [Fact]
    public void ParkingDestination_DestinationParkingPopulated()
    {
        GroundNode n0 = Node(0, 37.700, -122.200);
        GroundNode n1 = Node(1, 37.701, -122.200, GroundNodeType.Parking, "D8");
        AirportGroundLayout layout = Layout(n0, n1);
        GroundEdge e = Edge(n0, n1, "A");

        var dest = new DestinationDescriptor(n1.Id, null, "D8", null, DestinationKind.Parking);
        SearchContext ctx = Context(layout, dest);

        var edges = new List<DirectionalEdge> { Directed(e, n0, n1) };
        TaxiRoute route = RouteMaterialiser.Materialise(edges, ctx, []);

        Assert.Equal("D8", route.DestinationParking);
        Assert.Null(route.DestinationSpot);
    }

    // ---------------------------------------------------------------------------
    // Spot destination
    // ---------------------------------------------------------------------------

    [Fact]
    public void SpotDestination_DestinationSpotPopulated()
    {
        GroundNode n0 = Node(0, 37.700, -122.200);
        GroundNode n1 = Node(1, 37.701, -122.200, GroundNodeType.Spot, "32");
        AirportGroundLayout layout = Layout(n0, n1);
        GroundEdge e = Edge(n0, n1, "A");

        var dest = new DestinationDescriptor(n1.Id, null, null, "32", DestinationKind.Spot);
        SearchContext ctx = Context(layout, dest);

        var edges = new List<DirectionalEdge> { Directed(e, n0, n1) };
        TaxiRoute route = RouteMaterialiser.Materialise(edges, ctx, []);

        Assert.Null(route.DestinationParking);
        Assert.Equal("32", route.DestinationSpot);
    }

    // ---------------------------------------------------------------------------
    // Warning for unauthorized letter taxiway
    // ---------------------------------------------------------------------------

    [Fact]
    public void UnauthorizedLetterTaxiway_EmitsWarning()
    {
        GroundNode n0 = Node(0, 37.700, -122.200);
        GroundNode n1 = Node(1, 37.701, -122.200);
        AirportGroundLayout layout = Layout(n0, n1);
        GroundEdge e = Edge(n0, n1, "X");

        var authorized = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "A" };
        SearchContext ctx = Context(layout, authorized: authorized);

        var edges = new List<DirectionalEdge> { Directed(e, n0, n1) };
        TaxiRoute route = RouteMaterialiser.Materialise(edges, ctx, []);

        Assert.Single(route.Warnings);
        Assert.Contains("X", route.Warnings[0]);
    }

    [Fact]
    public void AuthorizedTaxiway_NoWarning()
    {
        GroundNode n0 = Node(0, 37.700, -122.200);
        GroundNode n1 = Node(1, 37.701, -122.200);
        AirportGroundLayout layout = Layout(n0, n1);
        GroundEdge e = Edge(n0, n1, "A");

        var authorized = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "A" };
        SearchContext ctx = Context(layout, authorized: authorized);

        var edges = new List<DirectionalEdge> { Directed(e, n0, n1) };
        TaxiRoute route = RouteMaterialiser.Materialise(edges, ctx, []);

        Assert.Empty(route.Warnings);
    }

    // ---------------------------------------------------------------------------
    // FindFullLengthLineupHoldShort
    // ---------------------------------------------------------------------------

    [Fact]
    public void FindFullLengthLineupHoldShort_EmptyList_ReturnsStartNode()
    {
        GroundNode n0 = Node(0, 37.700, -122.200);
        AirportGroundLayout layout = Layout(n0);

        GroundNode result = RouteMaterialiser.FindFullLengthLineupHoldShort(layout, n0, "28R", []);
        Assert.Equal(n0.Id, result.Id);
    }

    [Fact]
    public void FindFullLengthLineupHoldShort_SingleCandidate_ReturnsThatNode()
    {
        GroundNode n0 = Node(0, 37.700, -122.200);
        GroundNode n1 = Node(1, 37.701, -122.200, GroundNodeType.RunwayHoldShort);
        n1.RunwayId = new RunwayIdentifier("28R", "10L");
        AirportGroundLayout layout = Layout(n0, n1);

        GroundNode result = RouteMaterialiser.FindFullLengthLineupHoldShort(layout, n0, "28R", [n1]);
        Assert.Equal(n1.Id, result.Id);
    }

    [Fact]
    public void FindFullLengthLineupHoldShort_MultipleHoldShorts_PicksBarNearestRequestedThreshold()
    {
        // Runway 28R/10L: the 28R threshold (full-length departure end for a 28R takeoff) is the EAST
        // end. The full-length lineup bar is the hold-short nearest that designator's threshold — nEast.
        // The aircraft starts to the WEST (nearer the wrong bar), so a nearest-start or wrong-end
        // heuristic would pick nWest; only the authoritative per-designator threshold picks nEast.
        GroundNode n0 = Node(0, 37.700, -122.210); // start node, west of both bars
        GroundNode nWest = Node(1, 37.700, -122.208, GroundNodeType.RunwayHoldShort);
        nWest.RunwayId = new RunwayIdentifier("28R", "10L");
        GroundNode nEast = Node(2, 37.700, -122.202, GroundNodeType.RunwayHoldShort);
        nEast.RunwayId = new RunwayIdentifier("28R", "10L");
        AirportGroundLayout layout = Layout(n0, nWest, nEast);

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
            WidthFt = 150,
        };
        using IDisposable scope = NavigationDatabase.ScopedOverride(TestNavDbFactory.WithRunways(runway));

        GroundNode result = RouteMaterialiser.FindFullLengthLineupHoldShort(layout, n0, "28R", [nWest, nEast]);
        Assert.Equal(nEast.Id, result.Id);
    }
}
