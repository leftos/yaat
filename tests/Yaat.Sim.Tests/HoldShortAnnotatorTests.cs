using Xunit;
using Yaat.Sim.Data.Airport;

namespace Yaat.Sim.Tests;

public class HoldShortAnnotatorTests
{
    // -------------------------------------------------------------------------
    // Layout / segment helpers
    // -------------------------------------------------------------------------

    private static AirportGroundLayout EmptyLayout() => new() { AirportId = "TEST" };

    private static GroundNode TaxiNode(int id) =>
        new()
        {
            Id = id,
            Position = new LatLon(0, 0),
            Type = GroundNodeType.TaxiwayIntersection,
        };

    private static GroundNode HoldShortNode(int id, string runwayDesignator) =>
        new()
        {
            Id = id,
            Position = new LatLon(0, 0),
            Type = GroundNodeType.RunwayHoldShort,
            RunwayId = new RunwayIdentifier(runwayDesignator),
        };

    private static GroundEdge MakeEdge(int from, int to, string taxiway = "A")
    {
        var fromNode = new GroundNode
        {
            Id = from,
            Position = new LatLon(0, 0),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var toNode = new GroundNode
        {
            Id = to,
            Position = new LatLon(0, 0),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        return new GroundEdge
        {
            Nodes = [fromNode, toNode],
            TaxiwayName = taxiway,
            DistanceNm = 0.1,
        };
    }

    private static TaxiRouteSegment Seg(int from, int to, string taxiway = "A")
    {
        GroundEdge edge = MakeEdge(from, to, taxiway);
        return new TaxiRouteSegment { TaxiwayName = taxiway, Edge = edge.Directed(edge.Nodes[0], edge.Nodes[1]) };
    }

    private static AirportGroundLayout LayoutWith(params GroundNode[] nodes)
    {
        AirportGroundLayout layout = EmptyLayout();
        foreach (GroundNode n in nodes)
        {
            layout.Nodes[n.Id] = n;
        }

        return layout;
    }

    // -------------------------------------------------------------------------
    // AddImplicitRunwayHoldShorts
    // -------------------------------------------------------------------------

    [Fact]
    public void AddImplicitRunwayHoldShorts_SingleHoldShortNode_AddsOneEntry()
    {
        GroundNode hsNode = HoldShortNode(2, "28R");
        AirportGroundLayout layout = LayoutWith(TaxiNode(1), hsNode);
        var segments = new List<TaxiRouteSegment> { Seg(1, 2) };
        var holdShorts = new List<HoldShortPoint>();

        HoldShortAnnotator.AddImplicitRunwayHoldShorts(layout, segments, holdShorts);

        HoldShortPoint hs = Assert.Single(holdShorts);
        Assert.Equal(2, hs.NodeId);
        Assert.Equal(HoldShortReason.RunwayCrossing, hs.Reason);
        Assert.Equal("28R/10L", hs.TargetName);
    }

    [Fact]
    public void AddImplicitRunwayHoldShorts_EntryExitPair_OnlyEntryAdded()
    {
        // Node 2 = entry side hold-short, node 3 = exit side hold-short for same runway.
        var rwy = new RunwayIdentifier("28R", "10L");
        var entryNode = new GroundNode
        {
            Id = 2,
            Position = new LatLon(0, 0),
            Type = GroundNodeType.RunwayHoldShort,
            RunwayId = rwy,
        };
        var exitNode = new GroundNode
        {
            Id = 3,
            Position = new LatLon(0, 0),
            Type = GroundNodeType.RunwayHoldShort,
            RunwayId = rwy,
        };
        AirportGroundLayout layout = LayoutWith(TaxiNode(1), entryNode, exitNode, TaxiNode(4));
        var segments = new List<TaxiRouteSegment> { Seg(1, 2), Seg(2, 3), Seg(3, 4) };
        var holdShorts = new List<HoldShortPoint>();

        HoldShortAnnotator.AddImplicitRunwayHoldShorts(layout, segments, holdShorts);

        HoldShortPoint hs = Assert.Single(holdShorts);
        Assert.Equal(2, hs.NodeId);
    }

    [Fact]
    public void AddImplicitRunwayHoldShorts_TwoDifferentRunways_AddsBoth()
    {
        GroundNode hs28 = HoldShortNode(2, "28R");
        GroundNode hs15 = HoldShortNode(4, "15");
        AirportGroundLayout layout = LayoutWith(TaxiNode(1), hs28, TaxiNode(3), hs15);
        var segments = new List<TaxiRouteSegment> { Seg(1, 2), Seg(2, 3), Seg(3, 4) };
        var holdShorts = new List<HoldShortPoint>();

        HoldShortAnnotator.AddImplicitRunwayHoldShorts(layout, segments, holdShorts);

        Assert.Equal(2, holdShorts.Count);
        Assert.Contains(holdShorts, h => h.NodeId == 2);
        Assert.Contains(holdShorts, h => h.NodeId == 4);
    }

    [Fact]
    public void AddImplicitRunwayHoldShorts_DuplicateNode_NotAddedTwice()
    {
        GroundNode hsNode = HoldShortNode(2, "28R");
        AirportGroundLayout layout = LayoutWith(TaxiNode(1), hsNode);
        var segments = new List<TaxiRouteSegment> { Seg(1, 2) };
        var holdShorts = new List<HoldShortPoint>
        {
            new()
            {
                NodeId = 2,
                Reason = HoldShortReason.RunwayCrossing,
                TargetName = "28R/10L",
            },
        };

        HoldShortAnnotator.AddImplicitRunwayHoldShorts(layout, segments, holdShorts);

        Assert.Single(holdShorts);
    }

    [Fact]
    public void AddImplicitRunwayHoldShorts_NonHoldShortNodes_Skipped()
    {
        AirportGroundLayout layout = LayoutWith(TaxiNode(1), TaxiNode(2), TaxiNode(3));
        var segments = new List<TaxiRouteSegment> { Seg(1, 2), Seg(2, 3) };
        var holdShorts = new List<HoldShortPoint>();

        HoldShortAnnotator.AddImplicitRunwayHoldShorts(layout, segments, holdShorts);

        Assert.Empty(holdShorts);
    }

    [Fact]
    public void AddImplicitRunwayHoldShorts_EmptySegments_NoHoldShortsAdded()
    {
        AirportGroundLayout layout = LayoutWith(HoldShortNode(1, "28R"));
        var holdShorts = new List<HoldShortPoint>();

        HoldShortAnnotator.AddImplicitRunwayHoldShorts(layout, [], holdShorts);

        Assert.Empty(holdShorts);
    }

    // -------------------------------------------------------------------------
    // AddExplicitHoldShort
    // -------------------------------------------------------------------------

    private static TaxiRoute RouteOf(List<TaxiRouteSegment> segments, List<HoldShortPoint> holdShorts, int currentSegmentIndex)
    {
        return new TaxiRoute
        {
            Segments = segments,
            HoldShortPoints = holdShorts,
            CurrentSegmentIndex = currentSegmentIndex,
        };
    }

    /// <summary>
    /// Node 1 taxiway, node 2 the entry bar for 28R/10L, node 3 the runway centerline, node 4 the
    /// exit bar, node 5 taxiway — the shape taxiway B makes crossing OAK's 28R.
    /// </summary>
    private static (AirportGroundLayout Layout, List<TaxiRouteSegment> Segments) CrossingLayout()
    {
        var rwy = new RunwayIdentifier("28R", "10L");
        GroundNode Bar(int id) =>
            new()
            {
                Id = id,
                Position = new LatLon(0, 0),
                Type = GroundNodeType.RunwayHoldShort,
                RunwayId = rwy,
            };

        AirportGroundLayout layout = LayoutWith(TaxiNode(1), Bar(2), TaxiNode(3), Bar(4), TaxiNode(5));
        List<TaxiRouteSegment> segments = [Seg(1, 2), Seg(2, 3), Seg(3, 4), Seg(4, 5)];
        return (layout, segments);
    }

    private static HoldShortPoint Crossing28R(bool isCleared) =>
        new()
        {
            NodeId = 2,
            Reason = HoldShortReason.RunwayCrossing,
            TargetName = "28R/10L",
            IsCleared = isCleared,
            ClearedByAutoCross = isCleared,
        };

    [Fact]
    public void PlanExplicitHoldShort_MatchingRunwayHoldShortNode_AddsExplicitEntry()
    {
        GroundNode hsNode = HoldShortNode(2, "28R");
        AirportGroundLayout layout = LayoutWith(TaxiNode(1), hsNode);
        TaxiRoute route = RouteOf([Seg(1, 2)], [], 0);

        ExplicitHoldShortPlan plan = HoldShortAnnotator.PlanExplicitHoldShort(layout, route, HoldShortTarget.Parse("28R"));
        HoldShortAnnotator.ApplyExplicitHoldShort(route, plan, HoldShortTarget.Parse("28R"));

        Assert.Equal(ExplicitHoldShortOutcome.Add, plan.Outcome);
        HoldShortPoint hs = Assert.Single(route.HoldShortPoints);
        Assert.Equal(2, hs.NodeId);
        Assert.Equal(HoldShortReason.ExplicitHoldShort, hs.Reason);
        // Named with the node's combined runway id, matching AddImplicitRunwayHoldShorts — so a later
        // CROSS/HS for either end resolves against the same hold-short.
        Assert.Equal("28R/10L", hs.TargetName);
    }

    [Fact]
    public void PlanExplicitHoldShort_NonMatchingRunway_FallsThroughToTaxiway()
    {
        // Node 2 is a hold-short for runway 15 — not a match for target "A".
        // Node 3 is a taxiway intersection with edge on taxiway "A".
        // Hold-short should be placed at node 3 (the intersection node).
        GroundNode hs15 = HoldShortNode(2, "15");
        var intersectionNode = new GroundNode
        {
            Id = 3,
            Position = new LatLon(0, 0),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        intersectionNode.Edges.Add(MakeEdge(3, 99, "A"));
        AirportGroundLayout layout = LayoutWith(TaxiNode(1), hs15, intersectionNode);
        TaxiRoute route = RouteOf([Seg(1, 2), Seg(2, 3)], [], 0);

        ExplicitHoldShortPlan plan = HoldShortAnnotator.PlanExplicitHoldShort(layout, route, HoldShortTarget.Parse("A"));
        HoldShortAnnotator.ApplyExplicitHoldShort(route, plan, HoldShortTarget.Parse("A"));

        HoldShortPoint hs = Assert.Single(route.HoldShortPoints);
        Assert.Equal(3, hs.NodeId);
        Assert.Equal(HoldShortReason.ExplicitHoldShort, hs.Reason);
        Assert.Equal("A", hs.TargetName);
    }

    [Fact]
    public void PlanExplicitHoldShort_TaxiwayIntersectionFound_AddsHoldShortAtFirstMatch()
    {
        // Two candidate intersections — only the first in segment order should be picked.
        var node2 = new GroundNode
        {
            Id = 2,
            Position = new LatLon(0, 0),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        node2.Edges.Add(MakeEdge(2, 99, "B"));
        var node3 = new GroundNode
        {
            Id = 3,
            Position = new LatLon(0, 0),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        node3.Edges.Add(MakeEdge(3, 99, "B"));
        AirportGroundLayout layout = LayoutWith(TaxiNode(1), node2, node3);
        TaxiRoute route = RouteOf([Seg(1, 2), Seg(2, 3)], [], 0);

        ExplicitHoldShortPlan plan = HoldShortAnnotator.PlanExplicitHoldShort(layout, route, HoldShortTarget.Parse("B"));
        HoldShortAnnotator.ApplyExplicitHoldShort(route, plan, HoldShortTarget.Parse("B"));

        HoldShortPoint hs = Assert.Single(route.HoldShortPoints);
        Assert.Equal(2, hs.NodeId);
    }

    [Fact]
    public void PlanExplicitHoldShort_NoMatch_NothingAdded()
    {
        AirportGroundLayout layout = LayoutWith(TaxiNode(1), TaxiNode(2));
        TaxiRoute route = RouteOf([Seg(1, 2)], [], 0);

        ExplicitHoldShortPlan plan = HoldShortAnnotator.PlanExplicitHoldShort(layout, route, HoldShortTarget.Parse("ZZZZ"));
        HoldShortAnnotator.ApplyExplicitHoldShort(route, plan, HoldShortTarget.Parse("ZZZZ"));

        Assert.Equal(ExplicitHoldShortOutcome.NotOnRoute, plan.Outcome);
        Assert.Empty(route.HoldShortPoints);
    }

    [Fact]
    public void PlanExplicitHoldShort_AutoClearedCrossingAhead_ReArmsNearSideBar()
    {
        (AirportGroundLayout? layout, List<TaxiRouteSegment>? segments) = CrossingLayout();
        TaxiRoute route = RouteOf(segments, [Crossing28R(isCleared: true)], 0);

        ExplicitHoldShortPlan plan = HoldShortAnnotator.PlanExplicitHoldShort(layout, route, HoldShortTarget.Parse("28R"));
        HoldShortAnnotator.ApplyExplicitHoldShort(route, plan, HoldShortTarget.Parse("28R"));

        Assert.Equal(ExplicitHoldShortOutcome.ReArm, plan.Outcome);
        HoldShortPoint hs = Assert.Single(route.HoldShortPoints);
        Assert.Equal(2, hs.NodeId);
        Assert.Equal(HoldShortReason.ExplicitHoldShort, hs.Reason);
        Assert.False(hs.IsCleared);
        Assert.False(hs.ClearedByAutoCross);
    }

    [Fact]
    public void PlanExplicitHoldShort_ClearedBarBehindAircraft_ReturnsAlreadyEntered()
    {
        // CurrentSegmentIndex 2 => the aircraft is on segment 3→4, i.e. out on the runway.
        (AirportGroundLayout? layout, List<TaxiRouteSegment>? segments) = CrossingLayout();
        TaxiRoute route = RouteOf(segments, [Crossing28R(isCleared: true)], 2);

        ExplicitHoldShortPlan plan = HoldShortAnnotator.PlanExplicitHoldShort(layout, route, HoldShortTarget.Parse("28R"));

        Assert.Equal(ExplicitHoldShortOutcome.AlreadyEntered, plan.Outcome);
    }

    [Fact]
    public void PlanExplicitHoldShort_UnclearedBarBehindByResumeBump_StillReArms()
    {
        // TaxiingPhase.BuildResumePhases bumps CurrentSegmentIndex past the bar the aircraft is
        // stopped at, so index alone would read as "passed". An uncleared bar can never have been
        // passed — the taxi gate would have stopped the aircraft.
        (AirportGroundLayout? layout, List<TaxiRouteSegment>? segments) = CrossingLayout();
        TaxiRoute route = RouteOf(segments, [Crossing28R(isCleared: false)], 1);

        ExplicitHoldShortPlan plan = HoldShortAnnotator.PlanExplicitHoldShort(layout, route, HoldShortTarget.Parse("28R"));

        Assert.Equal(ExplicitHoldShortOutcome.ReArm, plan.Outcome);
    }

    [Fact]
    public void PlanExplicitHoldShort_DestinationRunway_IsNoOp()
    {
        (AirportGroundLayout? layout, List<TaxiRouteSegment>? segments) = CrossingLayout();
        HoldShortPoint dest = Crossing28R(isCleared: false);
        dest.Reason = HoldShortReason.DestinationRunway;
        TaxiRoute route = RouteOf(segments, [dest], 0);

        ExplicitHoldShortPlan plan = HoldShortAnnotator.PlanExplicitHoldShort(layout, route, HoldShortTarget.Parse("28R"));
        HoldShortAnnotator.ApplyExplicitHoldShort(route, plan, HoldShortTarget.Parse("28R"));

        Assert.Equal(ExplicitHoldShortOutcome.NoOp, plan.Outcome);
        Assert.Equal(HoldShortReason.DestinationRunway, Assert.Single(route.HoldShortPoints).Reason);
    }

    [Fact]
    public void PlanExplicitHoldShort_NoLayout_MatchesExistingPointOnly()
    {
        (AirportGroundLayout _, List<TaxiRouteSegment>? segments) = CrossingLayout();
        TaxiRoute route = RouteOf(segments, [Crossing28R(isCleared: true)], 0);

        Assert.Equal(ExplicitHoldShortOutcome.ReArm, HoldShortAnnotator.PlanExplicitHoldShort(null, route, HoldShortTarget.Parse("28R")).Outcome);
        Assert.Equal(ExplicitHoldShortOutcome.NotOnRoute, HoldShortAnnotator.PlanExplicitHoldShort(null, route, HoldShortTarget.Parse("B")).Outcome);
    }

    // -------------------------------------------------------------------------
    // AddDestinationHoldShort
    // -------------------------------------------------------------------------

    [Fact]
    public void AddDestinationHoldShort_AddsHoldShortAtLastSegmentNode()
    {
        AirportGroundLayout layout = EmptyLayout();
        var segments = new List<TaxiRouteSegment> { Seg(1, 2), Seg(2, 3) };
        var holdShorts = new List<HoldShortPoint>();

        HoldShortAnnotator.AddDestinationHoldShort(layout, segments, holdShorts, "28R");

        HoldShortPoint hs = Assert.Single(holdShorts);
        Assert.Equal(3, hs.NodeId);
        Assert.Equal(HoldShortReason.DestinationRunway, hs.Reason);
        Assert.Equal("28R", hs.TargetName);
    }

    [Fact]
    public void AddDestinationHoldShort_EmptySegments_NoHoldShortAdded()
    {
        AirportGroundLayout layout = EmptyLayout();
        var holdShorts = new List<HoldShortPoint>();

        HoldShortAnnotator.AddDestinationHoldShort(layout, [], holdShorts, "28R");

        Assert.Empty(holdShorts);
    }

    // -------------------------------------------------------------------------
    // HoldShortExists
    // -------------------------------------------------------------------------

    [Fact]
    public void HoldShortExists_NodePresentInList_ReturnsTrue()
    {
        var holdShorts = new List<HoldShortPoint>
        {
            new() { NodeId = 5, Reason = HoldShortReason.RunwayCrossing },
            new() { NodeId = 10, Reason = HoldShortReason.ExplicitHoldShort },
        };

        Assert.True(HoldShortAnnotator.HoldShortExists(holdShorts, 10));
    }

    [Fact]
    public void HoldShortExists_NodeAbsentFromList_ReturnsFalse()
    {
        var holdShorts = new List<HoldShortPoint>
        {
            new() { NodeId = 5, Reason = HoldShortReason.RunwayCrossing },
        };

        Assert.False(HoldShortAnnotator.HoldShortExists(holdShorts, 99));
    }
}
