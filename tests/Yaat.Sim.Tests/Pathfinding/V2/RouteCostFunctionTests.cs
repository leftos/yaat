using System.Collections.Immutable;
using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.V2;

namespace Yaat.Sim.Tests.Pathfinding.V2;

/// <summary>
/// Unit tests for <see cref="RouteCostFunction"/>. All tests use inline synthetic layouts —
/// no navdata or real airport data required.
/// </summary>
public class RouteCostFunctionTests
{
    // ---------------------------------------------------------------------------
    // Synthetic layout helpers
    // ---------------------------------------------------------------------------

    private static GroundNode MakeNode(int id, double lat, double lon, GroundNodeType type = GroundNodeType.TaxiwayIntersection) =>
        new()
        {
            Id = id,
            Position = new LatLon(lat, lon),
            Type = type,
        };

    private static GroundEdge MakeEdge(GroundNode a, GroundNode b, string taxiway)
    {
        double dist = GeoMath.DistanceNm(a.Position, b.Position);
        var edge = new GroundEdge
        {
            Nodes = [a, b],
            TaxiwayName = taxiway,
            DistanceNm = dist,
        };
        a.Edges.Add(edge);
        b.Edges.Add(edge);
        return edge;
    }

    private static AirportGroundLayout MakeLayout(params GroundNode[] nodes)
    {
        var layout = new AirportGroundLayout { AirportId = "TEST" };
        foreach (var n in nodes)
        {
            layout.Nodes[n.Id] = n;
        }

        return layout;
    }

    private static SearchContext MakeContext(
        AirportGroundLayout layout,
        int startNodeId,
        RoutePreference? preference = null,
        IReadOnlySet<string>? authorizedTaxiways = null
    ) =>
        new(
            layout,
            startNodeId,
            new DestinationDescriptor(null, null, null, null, DestinationKind.EndOfLastTaxiway),
            [],
            authorizedTaxiways,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            AircraftCategory.Jet,
            preference,
            null
        );

    private static PartialRoute StartRoute(int nodeId) => PartialRoute.StartAt(nodeId);

    private static PartialRoute ExtendRoute(PartialRoute current, IGroundEdge edge, GroundNode nextNode, SearchContext ctx)
    {
        GroundNode headNode = edge.Nodes[0].Id == current.HeadNodeId ? edge.Nodes[0] : edge.Nodes[1];
        double departureBearing = GeometricAdmissibility.GetDepartureBearing(edge, headNode, nextNode);
        double arrivalBearing = GeometricAdmissibility.GetArrivalBearing(edge, headNode, nextNode);
        double cost = RouteCostFunction.IncrementalCost(current, edge, nextNode, ctx);
        return current with
        {
            HeadNodeId = nextNode.Id,
            ArrivalBearing = arrivalBearing,
            LastEdge = edge,
            LastTaxiwayName = RouteCostFunction.ResolveTaxiwayName(edge, current.HeadNodeId),
            Previous = current,
            Depth = current.Depth + 1,
            AccumulatedCost = current.AccumulatedCost + cost,
            VisitedNodeIds = current.VisitedNodeIds.Add(nextNode.Id),
        };
    }

    // ---------------------------------------------------------------------------
    // Distance accumulation
    // ---------------------------------------------------------------------------

    [Fact]
    public void DistanceAccumulation_StraightRoute_EqualsSumOfSegmentDistances()
    {
        var n0 = MakeNode(0, 37.700, -122.200);
        var n1 = MakeNode(1, 37.701, -122.200);
        var n2 = MakeNode(2, 37.702, -122.200);
        var layout = MakeLayout(n0, n1, n2);
        var e01 = MakeEdge(n0, n1, "A");
        var e12 = MakeEdge(n1, n2, "A");

        var ctx = MakeContext(layout, 0, RoutePreference.Shortest);
        var route0 = StartRoute(0);
        var route1 = ExtendRoute(route0, e01, n1, ctx);
        var route2 = ExtendRoute(route1, e12, n2, ctx);

        double expected = e01.DistanceNm + e12.DistanceNm;
        Assert.Equal(expected, route2.AccumulatedCost, precision: 9);
    }

    // ---------------------------------------------------------------------------
    // Turn penalty
    // ---------------------------------------------------------------------------

    [Fact]
    public void TurnPenalty_90DegTurn_AddsExpectedCost()
    {
        // n0 → n1 heading north, n1 → n2 heading east: 90° turn.
        var n0 = MakeNode(0, 37.700, -122.200);
        var n1 = MakeNode(1, 37.701, -122.200);
        var n2 = MakeNode(2, 37.701, -122.199);
        var layout = MakeLayout(n0, n1, n2);
        var e01 = MakeEdge(n0, n1, "A");
        var e12 = MakeEdge(n1, n2, "A");

        var ctx = MakeContext(layout, 0);
        var route0 = StartRoute(0);
        var route1 = ExtendRoute(route0, e01, n1, ctx);

        double costBefore = route1.AccumulatedCost;
        var route2 = ExtendRoute(route1, e12, n2, ctx);
        double costAfter = route2.AccumulatedCost;

        double turnDelta = RouteCostFunction.HeadingDelta(
            GeometricAdmissibility.GetArrivalBearing(e01, n0, n1),
            GeometricAdmissibility.GetDepartureBearing(e12, n1, n2)
        );

        double expectedTurnCost = turnDelta * RouteCostFunction.TurnBudgetWeightNmPerDeg;
        double actualExtraCost = (costAfter - costBefore) - e12.DistanceNm;
        Assert.InRange(actualExtraCost, expectedTurnCost - 1e-9, expectedTurnCost + 1e-9);
    }

    [Fact]
    public void TurnPenalty_180DegTurn_ProducesExpectedCost()
    {
        // n0 → n1 heading north (bearing ≈ 0°), n1 → n2 heading south (bearing ≈ 180°).
        var n0 = MakeNode(0, 37.700, -122.200);
        var n1 = MakeNode(1, 37.701, -122.200);
        var n2 = MakeNode(2, 37.700, -122.200);

        // Duplicate position would be degenerate — nudge n2 slightly south.
        var n2b = MakeNode(2, 37.6995, -122.200);
        var layout = MakeLayout(n0, n1, n2b);
        var e01 = MakeEdge(n0, n1, "A");
        var e12 = MakeEdge(n1, n2b, "A");

        var ctx = MakeContext(layout, 0);
        var route0 = StartRoute(0);
        var route1 = ExtendRoute(route0, e01, n1, ctx);
        var route2 = ExtendRoute(route1, e12, n2b, ctx);

        double costSegment2 = route2.AccumulatedCost - route1.AccumulatedCost;
        double distCost = e12.DistanceNm;
        double turnCost = costSegment2 - distCost;

        // 180° turn penalty ≈ 180 × 0.0005 = 0.09 nm
        Assert.InRange(turnCost, 0.085, 0.095);
    }

    // ---------------------------------------------------------------------------
    // Transition penalty
    // ---------------------------------------------------------------------------

    [Fact]
    public void TransitionPenalty_ThreeNamedTaxiways_AddsTwoTransitions()
    {
        // Route A → B → C (two transitions).
        var n0 = MakeNode(0, 37.700, -122.200);
        var n1 = MakeNode(1, 37.701, -122.200);
        var n2 = MakeNode(2, 37.702, -122.200);
        var n3 = MakeNode(3, 37.703, -122.200);
        var layout = MakeLayout(n0, n1, n2, n3);
        var eAB = MakeEdge(n0, n1, "A");
        var eBB = MakeEdge(n1, n2, "B");
        var eBC = MakeEdge(n2, n3, "C");

        var ctx = MakeContext(layout, 0);
        var r0 = StartRoute(0);

        double costE1 = RouteCostFunction.IncrementalCost(r0, eAB, n1, ctx);
        var r1 = ExtendRoute(r0, eAB, n1, ctx);

        double costE2 = RouteCostFunction.IncrementalCost(r1, eBB, n2, ctx);
        var r2 = ExtendRoute(r1, eBB, n2, ctx);

        double costE3 = RouteCostFunction.IncrementalCost(r2, eBC, n3, ctx);

        // costE2 and costE3 each include a transition penalty of 0.05 nm.
        Assert.InRange(costE2 - eBB.DistanceNm, RouteCostFunction.TaxiwayTransitionCostNm - 1e-9, double.MaxValue);
        Assert.InRange(costE3 - eBC.DistanceNm, RouteCostFunction.TaxiwayTransitionCostNm - 1e-9, double.MaxValue);
    }

    // ---------------------------------------------------------------------------
    // Runway crossing
    // ---------------------------------------------------------------------------

    [Fact]
    public void RunwayCrossing_HoldShortNode_AddsRunwayCrossingCost()
    {
        var n0 = MakeNode(0, 37.700, -122.200);
        var n1 = MakeNode(1, 37.701, -122.200, GroundNodeType.RunwayHoldShort);
        n1.RunwayId = new RunwayIdentifier("28R", "10L");
        var layout = MakeLayout(n0, n1);
        var e = MakeEdge(n0, n1, "A");

        var ctx = MakeContext(layout, 0);
        var r0 = StartRoute(0);
        double cost = RouteCostFunction.IncrementalCost(r0, e, n1, ctx);

        double distCost = e.DistanceNm;
        Assert.InRange(
            cost,
            distCost + RouteCostFunction.RunwayCrossingCostNm - 1e-9,
            distCost + RouteCostFunction.RunwayCrossingCostNm + 1e-9 + 1.0
        );
    }

    // ---------------------------------------------------------------------------
    // Reverse arc
    // ---------------------------------------------------------------------------

    [Fact]
    public void ReverseArc_IsReverseTraversed_AddsPenalty()
    {
        var n0 = MakeNode(0, 37.700, -122.200);
        var n1 = MakeNode(1, 37.701, -122.200);
        var n2 = MakeNode(2, 37.701, -122.199);

        var eForward = MakeEdge(n0, n1, "A");

        // Construct arc with Nodes[0]=n2, Nodes[1]=n1 (natural: n2→n1).
        // Traversing from n1 to n2 would be reverse.
        var arc = new GroundArc
        {
            Nodes = [n2, n1],
            TaxiwayNames = ["A"],
            DistanceNm = GeoMath.DistanceNm(n1.Position, n2.Position),
            P1Lat = n2.Position.Lat,
            P1Lon = n2.Position.Lon,
            P2Lat = n1.Position.Lat,
            P2Lon = n1.Position.Lon,
            MinRadiusOfCurvatureFt = 200.0,
        };
        n1.Edges.Add(arc);
        n2.Edges.Add(arc);

        var layout = MakeLayout(n0, n1, n2);
        var ctx = MakeContext(layout, 0);

        var r0 = StartRoute(0);
        var r1 = ExtendRoute(r0, eForward, n1, ctx);

        // Traversing arc from n1→n2 is reverse (arc.Nodes[0]=n2, traversal from n1).
        double costWithReverse = RouteCostFunction.IncrementalCost(r1, arc, n2, ctx);

        Assert.InRange(costWithReverse, arc.DistanceNm + RouteCostFunction.ReverseArcCostNm - 1e-9, double.MaxValue);
    }

    // ---------------------------------------------------------------------------
    // Unauthorized taxiway
    // ---------------------------------------------------------------------------

    [Fact]
    public void UnauthorizedTaxiway_FirstUse_AddsPenalty()
    {
        var n0 = MakeNode(0, 37.700, -122.200);
        var n1 = MakeNode(1, 37.701, -122.200);
        var layout = MakeLayout(n0, n1);
        var e = MakeEdge(n0, n1, "X");

        var authorized = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "A" };
        var ctx = MakeContext(layout, 0, null, authorized);

        var r0 = StartRoute(0);
        double cost = RouteCostFunction.IncrementalCost(r0, e, n1, ctx);

        Assert.InRange(cost, e.DistanceNm + RouteCostFunction.UnauthorizedTaxiwayFirstUseCostNm - 1e-9, double.MaxValue);
    }

    [Fact]
    public void UnauthorizedTaxiway_SubsequentUse_NoExtraCost()
    {
        var n0 = MakeNode(0, 37.700, -122.200);
        var n1 = MakeNode(1, 37.701, -122.200);
        var n2 = MakeNode(2, 37.702, -122.200);
        var layout = MakeLayout(n0, n1, n2);
        var e01 = MakeEdge(n0, n1, "X");
        var e12 = MakeEdge(n1, n2, "X");

        var authorized = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "A" };
        var ctx = MakeContext(layout, 0, null, authorized);

        var r0 = StartRoute(0);
        var r1 = ExtendRoute(r0, e01, n1, ctx);
        double cost2 = RouteCostFunction.IncrementalCost(r1, e12, n2, ctx);

        // Second segment on same unauthorized taxiway — no additional unauthorized penalty.
        double distAndTurnCost =
            e12.DistanceNm
            + RouteCostFunction.HeadingDelta(r1.ArrivalBearing, GeoMath.BearingTo(n1.Position, n2.Position))
                * RouteCostFunction.TurnBudgetWeightNmPerDeg;
        Assert.InRange(cost2, distAndTurnCost - 1e-6, distAndTurnCost + 1e-3);
    }

    // ---------------------------------------------------------------------------
    // Preference selector
    // ---------------------------------------------------------------------------

    [Fact]
    public void FewestTurns_TurnWeightMultiplied()
    {
        var n0 = MakeNode(0, 37.700, -122.200);
        var n1 = MakeNode(1, 37.701, -122.200);
        var n2 = MakeNode(2, 37.701, -122.199);
        var layout = MakeLayout(n0, n1, n2);
        var e01 = MakeEdge(n0, n1, "A");
        var e12 = MakeEdge(n1, n2, "B");

        var ctxDefault = MakeContext(layout, 0);
        var ctxFewest = MakeContext(layout, 0, RoutePreference.FewestTurns);

        var r0 = StartRoute(0);
        var r1Default = ExtendRoute(r0, e01, n1, ctxDefault);
        var r1Fewest = ExtendRoute(r0, e01, n1, ctxFewest);

        double costDefaultSeg2 = RouteCostFunction.IncrementalCost(r1Default, e12, n2, ctxDefault);
        double costFewestSeg2 = RouteCostFunction.IncrementalCost(r1Fewest, e12, n2, ctxFewest);

        // FewestTurns multiplies both turn and transition weights by 5; cost must be higher.
        Assert.True(costFewestSeg2 > costDefaultSeg2, $"FewestTurns {costFewestSeg2} should exceed default {costDefaultSeg2}");
    }

    [Fact]
    public void Shortest_NonDistanceWeightsAreZero()
    {
        // A 90° turn with Shortest preference should cost only the segment distance.
        var n0 = MakeNode(0, 37.700, -122.200);
        var n1 = MakeNode(1, 37.701, -122.200);
        var n2 = MakeNode(2, 37.701, -122.199);
        var layout = MakeLayout(n0, n1, n2);
        var e01 = MakeEdge(n0, n1, "A");
        var e12 = MakeEdge(n1, n2, "B");

        var ctx = MakeContext(layout, 0, RoutePreference.Shortest);
        var r0 = StartRoute(0);
        var r1 = ExtendRoute(r0, e01, n1, ctx);
        double cost2 = RouteCostFunction.IncrementalCost(r1, e12, n2, ctx);

        Assert.Equal(e12.DistanceNm, cost2, precision: 9);
    }

    // ---------------------------------------------------------------------------
    // Heuristic admissibility
    // ---------------------------------------------------------------------------

    [Fact]
    public void Heuristic_StraightLine_NeverExceedsTrueCost()
    {
        // A straight route from A to B: true cost = segment distance.
        // Heuristic = great-circle distance = segment distance for a straight edge.
        var n0 = MakeNode(0, 37.700, -122.200);
        var n1 = MakeNode(1, 37.710, -122.200);
        var layout = MakeLayout(n0, n1);
        MakeEdge(n0, n1, "A");

        var ctx = MakeContext(layout, 0, RoutePreference.Shortest);
        var r0 = StartRoute(0);
        var e = n0.Edges[0];

        double trueCost = RouteCostFunction.IncrementalCost(r0, e, n1, ctx);
        double heuristic = RouteCostFunction.Heuristic(n0, n1);

        Assert.True(heuristic <= trueCost + 1e-9, $"h={heuristic} should not exceed trueCost={trueCost}");
    }

    [Fact]
    public void Heuristic_TwoHopRoute_NeverExceedsTrueCost()
    {
        // Route n0→n1→n2; heuristic from n0 is straight-line to n2 which is ≤ sum of segments.
        var n0 = MakeNode(0, 37.700, -122.200);
        var n1 = MakeNode(1, 37.701, -122.200);
        var n2 = MakeNode(2, 37.701, -122.190);
        var layout = MakeLayout(n0, n1, n2);
        var e01 = MakeEdge(n0, n1, "A");
        var e12 = MakeEdge(n1, n2, "A");

        var ctx = MakeContext(layout, 0, RoutePreference.Shortest);
        var r0 = StartRoute(0);

        double cost1 = RouteCostFunction.IncrementalCost(r0, e01, n1, ctx);
        var r1 = ExtendRoute(r0, e01, n1, ctx);
        double cost2 = RouteCostFunction.IncrementalCost(r1, e12, n2, ctx);
        double trueCost = cost1 + cost2;

        double heuristic = RouteCostFunction.Heuristic(n0, n2);

        Assert.True(heuristic <= trueCost + 1e-9, $"h={heuristic} should not exceed trueCost={trueCost}");
    }

    [Fact]
    public void HeadingDelta_Wraps_AtZeroTo360Boundary()
    {
        Assert.Equal(1.0, RouteCostFunction.HeadingDelta(0.0, 359.0), precision: 6);
        Assert.Equal(1.0, RouteCostFunction.HeadingDelta(359.0, 0.0), precision: 6);
        Assert.Equal(180.0, RouteCostFunction.HeadingDelta(0.0, 180.0), precision: 6);
    }

    // ---------------------------------------------------------------------------
    // Fix A: Destination hold-short does not double-count as crossing
    // ---------------------------------------------------------------------------

    [Fact]
    public void FixA_DestinationHoldShort_NoCrossingPenalty()
    {
        // A hold-short node that IS the destination runway should not add a crossing penalty.
        var n0 = MakeNode(0, 37.700, -122.200);
        var n1 = MakeNode(1, 37.701, -122.200, GroundNodeType.RunwayHoldShort);
        n1.RunwayId = new RunwayIdentifier("28R", "10L");
        var layout = MakeLayout(n0, n1);
        var e = MakeEdge(n0, n1, "A");

        var dest = new DestinationDescriptor(null, "28R", null, null, DestinationKind.Runway);
        var ctx = new SearchContext(
            layout,
            0,
            dest,
            [],
            null,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            AircraftCategory.Jet,
            null,
            null
        );

        var r0 = StartRoute(0);
        double cost = RouteCostFunction.IncrementalCost(r0, e, n1, ctx);

        // Cost should be ONLY the segment distance — no crossing penalty.
        Assert.Equal(e.DistanceNm, cost, precision: 9);
    }

    [Fact]
    public void FixA_NonDestinationHoldShort_AddsCrossingPenalty()
    {
        // A hold-short node on a DIFFERENT runway than the destination should still add the penalty.
        var n0 = MakeNode(0, 37.700, -122.200);
        var n1 = MakeNode(1, 37.701, -122.200, GroundNodeType.RunwayHoldShort);
        n1.RunwayId = new RunwayIdentifier("10L", "28R");
        var layout = MakeLayout(n0, n1);
        var e = MakeEdge(n0, n1, "A");

        // Destination is 30, not 10L/28R — so this hold-short IS a crossing.
        var dest = new DestinationDescriptor(null, "30", null, null, DestinationKind.Runway);
        var ctx = new SearchContext(
            layout,
            0,
            dest,
            [],
            null,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            AircraftCategory.Jet,
            null,
            null
        );

        var r0 = StartRoute(0);
        double cost = RouteCostFunction.IncrementalCost(r0, e, n1, ctx);

        Assert.InRange(cost, e.DistanceNm + RouteCostFunction.RunwayCrossingCostNm - 1e-9, double.MaxValue);
    }

    // ---------------------------------------------------------------------------
    // Fix B: Fastest preference adds time-cost term
    // ---------------------------------------------------------------------------

    [Fact]
    public void FixB_Fastest_StraightEdge_AddsTimeCost()
    {
        // A straight edge (MaxSafeSpeedKts = MaxValue) should add near-zero time cost.
        // This test verifies the code path executes without error and the distance cost is primary.
        var n0 = MakeNode(0, 37.700, -122.200);
        var n1 = MakeNode(1, 37.701, -122.200);
        var layout = MakeLayout(n0, n1);
        var e = MakeEdge(n0, n1, "A");

        var ctxDefault = MakeContext(layout, 0);
        var ctxFastest = MakeContext(layout, 0, RoutePreference.Fastest);

        var r0 = StartRoute(0);
        double costDefault = RouteCostFunction.IncrementalCost(r0, e, n1, ctxDefault);
        double costFastest = RouteCostFunction.IncrementalCost(r0, e, n1, ctxFastest);

        // Fastest adds a time term. For a straight edge MaxSafeSpeedKts = MaxValue → time cost ≈ 0.
        // Both should essentially equal segment distance (within floating point).
        Assert.True(costFastest >= e.DistanceNm, $"Fastest cost {costFastest} must be >= distance {e.DistanceNm}");
    }

    [Fact]
    public void FixB_Fastest_Arc_AddsTimeCostAboveDistance()
    {
        // An arc with finite MaxSafeSpeedKts should add a meaningful time cost.
        var n0 = MakeNode(0, 37.700, -122.200);
        var n1 = MakeNode(1, 37.701, -122.200);
        var n2 = MakeNode(2, 37.701, -122.199);

        var eForward = MakeEdge(n0, n1, "A");

        // Arc with tight radius (200 ft) — MaxSafeSpeedKts will be small.
        var arc = new GroundArc
        {
            Nodes = [n1, n2],
            TaxiwayNames = ["A"],
            DistanceNm = GeoMath.DistanceNm(n1.Position, n2.Position),
            P1Lat = n1.Position.Lat + (n2.Position.Lat - n1.Position.Lat) / 3.0,
            P1Lon = n1.Position.Lon + (n2.Position.Lon - n1.Position.Lon) / 3.0,
            P2Lat = n1.Position.Lat + 2.0 * (n2.Position.Lat - n1.Position.Lat) / 3.0,
            P2Lon = n1.Position.Lon + 2.0 * (n2.Position.Lon - n1.Position.Lon) / 3.0,
            MinRadiusOfCurvatureFt = 200.0,
        };
        n1.Edges.Add(arc);
        n2.Edges.Add(arc);

        var layout = MakeLayout(n0, n1, n2);
        var ctxFastest = MakeContext(layout, 0, RoutePreference.Fastest);

        var r0 = StartRoute(0);
        var r1 = ExtendRoute(r0, eForward, n1, ctxFastest);
        double costWithArc = RouteCostFunction.IncrementalCost(r1, arc, n2, ctxFastest);

        // Fastest adds distance / (maxSafeSpeedNmPerSec). For a tight-radius arc this is large.
        double maxSafeKts = arc.MaxSafeSpeedKts(ctxFastest.Category);
        double timeCost = arc.DistanceNm / (maxSafeKts / 3600.0);
        Assert.True(costWithArc >= arc.DistanceNm + timeCost - 1e-9, $"Expected time cost term {timeCost} in arc cost {costWithArc}");
    }

    // ---------------------------------------------------------------------------
    // Fix D: No phantom transition penalty on first edge (Depth == 0)
    // ---------------------------------------------------------------------------

    [Fact]
    public void FixD_FirstEdge_NoTransitionPenalty()
    {
        // At Depth==0, transitioning from empty LastTaxiwayName to "A" must not add a transition penalty.
        var n0 = MakeNode(0, 37.700, -122.200);
        var n1 = MakeNode(1, 37.701, -122.200);
        var layout = MakeLayout(n0, n1);
        var e = MakeEdge(n0, n1, "A");

        var ctx = MakeContext(layout, 0);
        var r0 = StartRoute(0); // Depth == 0, LastTaxiwayName == ""

        double cost = RouteCostFunction.IncrementalCost(r0, e, n1, ctx);

        // Only segment distance — no transition penalty.
        Assert.Equal(e.DistanceNm, cost, precision: 9);
    }

    [Fact]
    public void FixD_SecondEdge_SameTaxiway_NoTransitionPenalty()
    {
        // Depth==1, same taxiway name — no transition penalty.
        var n0 = MakeNode(0, 37.700, -122.200);
        var n1 = MakeNode(1, 37.701, -122.200);
        var n2 = MakeNode(2, 37.702, -122.200);
        var layout = MakeLayout(n0, n1, n2);
        var e01 = MakeEdge(n0, n1, "A");
        var e12 = MakeEdge(n1, n2, "A");

        var ctx = MakeContext(layout, 0, RoutePreference.Shortest);
        var r0 = StartRoute(0);
        var r1 = ExtendRoute(r0, e01, n1, ctx);

        double cost = RouteCostFunction.IncrementalCost(r1, e12, n2, ctx);
        // With Shortest preference: only distance.
        Assert.Equal(e12.DistanceNm, cost, precision: 9);
    }

    [Fact]
    public void FixD_SecondEdge_DifferentTaxiway_AddsTransitionPenalty()
    {
        // Depth==1, different taxiway name — transition penalty must fire.
        var n0 = MakeNode(0, 37.700, -122.200);
        var n1 = MakeNode(1, 37.701, -122.200);
        var n2 = MakeNode(2, 37.702, -122.200);
        var layout = MakeLayout(n0, n1, n2);
        var e01 = MakeEdge(n0, n1, "A");
        var e12 = MakeEdge(n1, n2, "B");

        var ctx = MakeContext(layout, 0);
        var r0 = StartRoute(0);
        var r1 = ExtendRoute(r0, e01, n1, ctx);

        double cost2 = RouteCostFunction.IncrementalCost(r1, e12, n2, ctx);

        Assert.InRange(cost2 - e12.DistanceNm, RouteCostFunction.TaxiwayTransitionCostNm - 1e-9, double.MaxValue);
    }
}
