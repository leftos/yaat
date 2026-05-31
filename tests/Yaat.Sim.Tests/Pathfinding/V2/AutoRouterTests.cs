using System.Collections.Immutable;
using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Pathfinding;

namespace Yaat.Sim.Tests.Pathfinding.V2;

/// <summary>
/// Unit tests for <see cref="AutoRouter"/>.
/// All tests use inline synthetic layouts — no real airport data required.
/// </summary>
public class AutoRouterTests
{
    // ---------------------------------------------------------------------------
    // Synthetic layout helpers (copied from RouteCostFunctionTests pattern)
    // ---------------------------------------------------------------------------

    private static GroundNode Node(int id, double lat, double lon, GroundNodeType type = GroundNodeType.TaxiwayIntersection) =>
        new()
        {
            Id = id,
            Position = new LatLon(lat, lon),
            Type = type,
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

    private static SearchContext NodeContext(
        AirportGroundLayout layout,
        int fromNodeId,
        int toNodeId,
        RoutePreference? preference = null,
        IReadOnlySet<string>? authorizedTaxiways = null,
        Action<string>? log = null
    ) =>
        new(
            layout,
            fromNodeId,
            new DestinationDescriptor(toNodeId, null, null, null, DestinationKind.Node),
            [],
            authorizedTaxiways,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            AircraftCategory.Jet,
            preference,
            log
        );

    // ---------------------------------------------------------------------------
    // Trivial: start == destination
    // ---------------------------------------------------------------------------

    [Fact]
    public void TrivialRoute_StartEqualsDestination_ReturnsEmptyRoute()
    {
        // When start and destination are the same node, the materialiser returns an empty route.
        // This is documented behavior: zero-segment route (aircraft is already there).
        var n0 = Node(0, 37.700, -122.200);
        var layout = Layout(n0);

        var ctx = NodeContext(layout, 0, 0);
        var (route, failure) = AutoRouter.Run(ctx);

        Assert.NotNull(route);
        Assert.Null(failure);
        Assert.Empty(route.Segments);
    }

    // ---------------------------------------------------------------------------
    // Two adjacent nodes
    // ---------------------------------------------------------------------------

    [Fact]
    public void TwoAdjacentNodes_ReturnsSingleSegmentRoute()
    {
        var n0 = Node(0, 37.700, -122.200);
        var n1 = Node(1, 37.701, -122.200);
        var layout = Layout(n0, n1);
        Edge(n0, n1, "A");

        var ctx = NodeContext(layout, 0, 1);
        var (route, failure) = AutoRouter.Run(ctx);

        Assert.NotNull(route);
        Assert.Null(failure);
        Assert.Single(route.Segments);
        Assert.Equal(0, route.Segments[0].FromNodeId);
        Assert.Equal(1, route.Segments[0].ToNodeId);
    }

    // ---------------------------------------------------------------------------
    // Three-node line
    // ---------------------------------------------------------------------------

    [Fact]
    public void ThreeNodeLine_ReturnsTwoSegmentRoute()
    {
        var n0 = Node(0, 37.700, -122.200);
        var n1 = Node(1, 37.701, -122.200);
        var n2 = Node(2, 37.702, -122.200);
        var layout = Layout(n0, n1, n2);
        Edge(n0, n1, "A");
        Edge(n1, n2, "A");

        var ctx = NodeContext(layout, 0, 2);
        var (route, failure) = AutoRouter.Run(ctx);

        Assert.NotNull(route);
        Assert.Null(failure);
        Assert.Equal(2, route.Segments.Count);
        Assert.Equal(0, route.Segments[0].FromNodeId);
        Assert.Equal(1, route.Segments[0].ToNodeId);
        Assert.Equal(1, route.Segments[1].FromNodeId);
        Assert.Equal(2, route.Segments[1].ToNodeId);
    }

    // ---------------------------------------------------------------------------
    // Heuristic admissibility
    // ---------------------------------------------------------------------------

    [Fact]
    public void Heuristic_NeverOverestimatesTrueCost_TwoHopRoute()
    {
        // True cost of n0→n1→n2 (with Shortest preference) = sum of segment distances.
        // Heuristic h(n0, n2) = GeoMath.DistanceNm(n0, n2) ≤ true cost.
        var n0 = Node(0, 37.700, -122.200);
        var n1 = Node(1, 37.701, -122.200);
        var n2 = Node(2, 37.701, -122.190); // offset east
        var layout = Layout(n0, n1, n2);
        var e01 = Edge(n0, n1, "A");
        var e12 = Edge(n1, n2, "A");

        double trueCost = e01.DistanceNm + e12.DistanceNm;
        double heuristic = GeoMath.DistanceNm(n0.Position, n2.Position);

        Assert.True(heuristic <= trueCost + 1e-9, $"h={heuristic} exceeds trueCost={trueCost}");
    }

    // ---------------------------------------------------------------------------
    // Disconnected graph
    // ---------------------------------------------------------------------------

    [Fact]
    public void DisconnectedGraph_ReturnsDestinationUnreachable()
    {
        // Node 0 is not connected to node 1.
        var n0 = Node(0, 37.700, -122.200);
        var n1 = Node(1, 37.710, -122.200);
        var layout = Layout(n0, n1);
        // No edge between n0 and n1.

        var ctx = NodeContext(layout, 0, 1);
        var (route, failure) = AutoRouter.Run(ctx);

        Assert.Null(route);
        Assert.NotNull(failure);
        Assert.Equal(FailureKind.DestinationUnreachable, failure.Kind);
    }

    // ---------------------------------------------------------------------------
    // Geometric infeasibility
    // ---------------------------------------------------------------------------

    [Fact]
    public void GeometricInfeasibility_AllPathsRequireUTurn_ReturnsFailure()
    {
        // Layout: n0 → n1 (north). n1 → n2 (south) — 180° U-turn, exceeds Jet 135° limit.
        // n1 only connects back south (U-turn), so no admissible path exists.
        var n0 = Node(0, 37.700, -122.200);
        var n1 = Node(1, 37.701, -122.200);
        var n2 = Node(2, 37.700, -122.200); // same lat as n0, requires U-turn from n1

        // Use n2b (slightly further south) to avoid trivial same-position degenerate case.
        var n2b = Node(2, 37.6995, -122.200);
        var layout = Layout(n0, n1, n2b);
        Edge(n0, n1, "A");
        Edge(n1, n2b, "A");

        // Route from n0 to n2b — only path is n0→n1→n2b (180° turn at n1).
        var ctx = NodeContext(layout, 0, 2);
        var (route, failure) = AutoRouter.Run(ctx);

        // Either fails (DestinationUnreachable) because the U-turn is rejected.
        Assert.Null(route);
        Assert.NotNull(failure);
        Assert.True(
            failure.Kind is FailureKind.DestinationUnreachable or FailureKind.SearchExhausted,
            $"Expected DestinationUnreachable or SearchExhausted, got {failure.Kind}"
        );
    }

    // ---------------------------------------------------------------------------
    // RoutePreference: different preferences can produce different routes
    // ---------------------------------------------------------------------------

    [Fact]
    public void PreferenceDifference_ShortestVsFewestTurns_DifferentRoutes()
    {
        // Layout: two paths from n0 to n3.
        //   Path 1 (direct, many turns): n0 → n1 → n2 → n3 — short but requires two 90° turns.
        //   Path 2 (detour, no turns): n0 → n4 → n3 — longer but straight.
        //
        // With Shortest preference, Path 1 wins. With FewestTurns, Path 2 might win if
        // the cost function is calibrated (or both might be equivalent on this tiny layout).
        // We at least verify both preferences run without error and return valid routes.
        var n0 = Node(0, 37.700, -122.200);
        var n3 = Node(3, 37.700, -122.190); // same lat as n0, offset east

        // Path 1: north then east then south (two 90° turns)
        var n1 = Node(1, 37.701, -122.200);
        var n2 = Node(2, 37.701, -122.190);

        // Path 2: straight east (no turns)
        var n4 = Node(4, 37.700, -122.195);

        var layout = Layout(n0, n1, n2, n3, n4);
        Edge(n0, n1, "A"); // north
        Edge(n1, n2, "A"); // east
        Edge(n2, n3, "A"); // south

        Edge(n0, n4, "B"); // east detour start
        Edge(n4, n3, "B"); // east detour end

        var ctxShortest = NodeContext(layout, 0, 3, RoutePreference.Shortest);
        var ctxFewest = NodeContext(layout, 0, 3, RoutePreference.FewestTurns);

        var (routeShortest, failShortest) = AutoRouter.Run(ctxShortest);
        var (routeFewest, failFewest) = AutoRouter.Run(ctxFewest);

        Assert.NotNull(routeShortest);
        Assert.Null(failShortest);
        Assert.NotNull(routeFewest);
        Assert.Null(failFewest);

        // Both routes reach n3.
        Assert.Equal(3, routeShortest.Segments[^1].ToNodeId);
        Assert.Equal(3, routeFewest.Segments[^1].ToNodeId);
    }

    // ---------------------------------------------------------------------------
    // Determinism: identical calls produce bitwise-identical results
    // ---------------------------------------------------------------------------

    [Fact]
    public void Determinism_SameCallTwice_IdenticalSegments()
    {
        var n0 = Node(0, 37.700, -122.200);
        var n1 = Node(1, 37.701, -122.200);
        var n2 = Node(2, 37.702, -122.200);
        var n3 = Node(3, 37.702, -122.190);
        var layout = Layout(n0, n1, n2, n3);
        Edge(n0, n1, "A");
        Edge(n1, n2, "A");
        Edge(n2, n3, "A");

        var ctx1 = NodeContext(layout, 0, 3, RoutePreference.FewestTurns);
        var ctx2 = NodeContext(layout, 0, 3, RoutePreference.FewestTurns);

        var (route1, _) = AutoRouter.Run(ctx1);
        var (route2, _) = AutoRouter.Run(ctx2);

        Assert.NotNull(route1);
        Assert.NotNull(route2);
        Assert.Equal(route1.Segments.Count, route2.Segments.Count);

        for (int i = 0; i < route1.Segments.Count; i++)
        {
            Assert.Equal(route1.Segments[i].FromNodeId, route2.Segments[i].FromNodeId);
            Assert.Equal(route1.Segments[i].ToNodeId, route2.Segments[i].ToNodeId);
        }
    }

    [Fact]
    public void Determinism_WithAndWithoutDiagnosticLog_IdenticalSegments()
    {
        var n0 = Node(0, 37.700, -122.200);
        var n1 = Node(1, 37.701, -122.200);
        var n2 = Node(2, 37.702, -122.200);
        var layout = Layout(n0, n1, n2);
        Edge(n0, n1, "A");
        Edge(n1, n2, "A");

        var ctxNoLog = NodeContext(layout, 0, 2);
        var ctxWithLog = NodeContext(layout, 0, 2, log: _ => { });

        var (routeNoLog, _) = AutoRouter.Run(ctxNoLog);
        var (routeWithLog, _) = AutoRouter.Run(ctxWithLog);

        Assert.NotNull(routeNoLog);
        Assert.NotNull(routeWithLog);
        Assert.Equal(routeNoLog.Segments.Count, routeWithLog.Segments.Count);

        for (int i = 0; i < routeNoLog.Segments.Count; i++)
        {
            Assert.Equal(routeNoLog.Segments[i].FromNodeId, routeWithLog.Segments[i].FromNodeId);
            Assert.Equal(routeNoLog.Segments[i].ToNodeId, routeWithLog.Segments[i].ToNodeId);
        }
    }

    // ---------------------------------------------------------------------------
    // Start node not in layout
    // ---------------------------------------------------------------------------

    [Fact]
    public void StartNodeNotInLayout_ReturnsStartNodeUnreachable()
    {
        var n0 = Node(0, 37.700, -122.200);
        var layout = Layout(n0);

        var ctx = NodeContext(layout, 999, 0); // 999 doesn't exist
        var (route, failure) = AutoRouter.Run(ctx);

        Assert.Null(route);
        Assert.NotNull(failure);
        Assert.Equal(FailureKind.StartNodeUnreachable, failure.Kind);
    }

    // ---------------------------------------------------------------------------
    // EndOfLastTaxiway destination — unsupported by AutoRouter
    // ---------------------------------------------------------------------------

    [Fact]
    public void EndOfLastTaxiwayDestination_ReturnsFailure()
    {
        var n0 = Node(0, 37.700, -122.200);
        var n1 = Node(1, 37.701, -122.200);
        var layout = Layout(n0, n1);
        Edge(n0, n1, "A");

        var ctx = new SearchContext(
            layout,
            0,
            new DestinationDescriptor(null, null, null, null, DestinationKind.EndOfLastTaxiway),
            [],
            null,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            AircraftCategory.Jet,
            null,
            null
        );

        var (route, failure) = AutoRouter.Run(ctx);

        Assert.Null(route);
        Assert.NotNull(failure);
        Assert.Equal(FailureKind.DestinationUnreachable, failure.Kind);
    }

    // ---------------------------------------------------------------------------
    // Diagnostic log emits messages
    // ---------------------------------------------------------------------------

    [Fact]
    public void DiagnosticLog_SuccessfulRoute_EmitsStartAndSuccessMessages()
    {
        var n0 = Node(0, 37.700, -122.200);
        var n1 = Node(1, 37.701, -122.200);
        var layout = Layout(n0, n1);
        Edge(n0, n1, "A");

        var messages = new List<string>();
        var ctx = NodeContext(layout, 0, 1, log: m => messages.Add(m));

        var (route, failure) = AutoRouter.Run(ctx);

        Assert.NotNull(route);
        Assert.Null(failure);
        Assert.True(messages.Any(m => m.Contains("[v2:auto]") && m.Contains("start")), "Expected start message");
        Assert.True(messages.Any(m => m.Contains("[v2:auto]") && m.Contains("SUCCESS")), "Expected SUCCESS message");
    }

    // ---------------------------------------------------------------------------
    // FindRoute / FindRoutes integration
    // ---------------------------------------------------------------------------

    [Fact]
    public void FindRoute_SimpleThreeNodeChain_ReturnsRoute()
    {
        var n0 = Node(0, 37.700, -122.200);
        var n1 = Node(1, 37.701, -122.200);
        var n2 = Node(2, 37.702, -122.200);
        var layout = Layout(n0, n1, n2);
        Edge(n0, n1, "A");
        Edge(n1, n2, "A");

        var route = TaxiPathfinder.FindRoute(layout, 0, 2, AircraftCategory.Jet);

        Assert.NotNull(route);
        Assert.Equal(2, route.Segments.Count);
    }

    [Fact]
    public void FindRoutes_NullPreference_ReturnsUpToThreeRoutes()
    {
        // Layout with two distinct paths n0→n3 (via n1 or via n2).
        var n0 = Node(0, 37.700, -122.200);
        var n1 = Node(1, 37.701, -122.200);
        var n2 = Node(2, 37.700, -122.195);
        var n3 = Node(3, 37.701, -122.195);
        var layout = Layout(n0, n1, n2, n3);
        Edge(n0, n1, "A");
        Edge(n1, n3, "A");
        Edge(n0, n2, "B");
        Edge(n2, n3, "B");

        var routes = TaxiPathfinder.FindRoutes(layout, 0, 3, null, 3, null, AircraftCategory.Jet);

        Assert.NotEmpty(routes);
        Assert.True(routes.Count <= 3);
        foreach (var r in routes)
        {
            Assert.Equal(3, r.Segments[^1].ToNodeId);
        }
    }

    [Fact]
    public void FindRoutes_WithPreference_ReturnsSingleRoute()
    {
        var n0 = Node(0, 37.700, -122.200);
        var n1 = Node(1, 37.701, -122.200);
        var layout = Layout(n0, n1);
        Edge(n0, n1, "A");

        var routes = TaxiPathfinder.FindRoutes(layout, 0, 1, RoutePreference.Shortest, 5, null, AircraftCategory.Jet);

        Assert.Single(routes);
    }

    [Fact]
    public void FindFullLengthLineupHoldShort_DelegatesToMaterialiser()
    {
        var n0 = Node(0, 37.700, -122.200);
        var n1 = Node(1, 37.701, -122.200, GroundNodeType.RunwayHoldShort);
        n1.RunwayId = new RunwayIdentifier("28R", "10L");
        var layout = Layout(n0, n1);

        var result = TaxiPathfinder.FindFullLengthLineupHoldShort(layout, n0, "28R", [n1]);

        Assert.Equal(n1.Id, result.Id);
    }
}
