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
            Latitude = 0,
            Longitude = 0,
            Type = GroundNodeType.TaxiwayIntersection,
        };

    private static GroundNode HoldShortNode(int id, string runwayDesignator) =>
        new()
        {
            Id = id,
            Latitude = 0,
            Longitude = 0,
            Type = GroundNodeType.RunwayHoldShort,
            RunwayId = new RunwayIdentifier(runwayDesignator),
        };

    private static GroundEdge MakeEdge(int from, int to, string taxiway = "A") =>
        new()
        {
            FromNodeId = from,
            ToNodeId = to,
            TaxiwayName = taxiway,
            DistanceNm = 0.1,
        };

    private static TaxiRouteSegment Seg(int from, int to, string taxiway = "A") =>
        new()
        {
            FromNodeId = from,
            ToNodeId = to,
            TaxiwayName = taxiway,
            Edge = MakeEdge(from, to, taxiway),
        };

    private static AirportGroundLayout LayoutWith(params GroundNode[] nodes)
    {
        var layout = EmptyLayout();
        foreach (var n in nodes)
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
        var hsNode = HoldShortNode(2, "28R");
        var layout = LayoutWith(TaxiNode(1), hsNode);
        var segments = new List<TaxiRouteSegment> { Seg(1, 2) };
        var holdShorts = new List<HoldShortPoint>();

        HoldShortAnnotator.AddImplicitRunwayHoldShorts(layout, segments, holdShorts);

        var hs = Assert.Single(holdShorts);
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
            Latitude = 0,
            Longitude = 0,
            Type = GroundNodeType.RunwayHoldShort,
            RunwayId = rwy,
        };
        var exitNode = new GroundNode
        {
            Id = 3,
            Latitude = 0,
            Longitude = 0,
            Type = GroundNodeType.RunwayHoldShort,
            RunwayId = rwy,
        };
        var layout = LayoutWith(TaxiNode(1), entryNode, exitNode, TaxiNode(4));
        var segments = new List<TaxiRouteSegment> { Seg(1, 2), Seg(2, 3), Seg(3, 4) };
        var holdShorts = new List<HoldShortPoint>();

        HoldShortAnnotator.AddImplicitRunwayHoldShorts(layout, segments, holdShorts);

        var hs = Assert.Single(holdShorts);
        Assert.Equal(2, hs.NodeId);
    }

    [Fact]
    public void AddImplicitRunwayHoldShorts_TwoDifferentRunways_AddsBoth()
    {
        var hs28 = HoldShortNode(2, "28R");
        var hs15 = HoldShortNode(4, "15");
        var layout = LayoutWith(TaxiNode(1), hs28, TaxiNode(3), hs15);
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
        var hsNode = HoldShortNode(2, "28R");
        var layout = LayoutWith(TaxiNode(1), hsNode);
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
        var layout = LayoutWith(TaxiNode(1), TaxiNode(2), TaxiNode(3));
        var segments = new List<TaxiRouteSegment> { Seg(1, 2), Seg(2, 3) };
        var holdShorts = new List<HoldShortPoint>();

        HoldShortAnnotator.AddImplicitRunwayHoldShorts(layout, segments, holdShorts);

        Assert.Empty(holdShorts);
    }

    [Fact]
    public void AddImplicitRunwayHoldShorts_EmptySegments_NoHoldShortsAdded()
    {
        var layout = LayoutWith(HoldShortNode(1, "28R"));
        var holdShorts = new List<HoldShortPoint>();

        HoldShortAnnotator.AddImplicitRunwayHoldShorts(layout, [], holdShorts);

        Assert.Empty(holdShorts);
    }

    // -------------------------------------------------------------------------
    // AddExplicitHoldShort
    // -------------------------------------------------------------------------

    [Fact]
    public void AddExplicitHoldShort_MatchingRunwayHoldShortNode_AddsExplicitEntry()
    {
        var hsNode = HoldShortNode(2, "28R");
        var layout = LayoutWith(TaxiNode(1), hsNode);
        var segments = new List<TaxiRouteSegment> { Seg(1, 2) };
        var holdShorts = new List<HoldShortPoint>();

        HoldShortAnnotator.AddExplicitHoldShort(layout, segments, holdShorts, "28R");

        var hs = Assert.Single(holdShorts);
        Assert.Equal(2, hs.NodeId);
        Assert.Equal(HoldShortReason.ExplicitHoldShort, hs.Reason);
        Assert.Equal("28R", hs.TargetName);
    }

    [Fact]
    public void AddExplicitHoldShort_NonMatchingRunway_FallsThroughToTaxiway()
    {
        // Node 2 is a hold-short for runway 15 — not a match for target "A".
        // Node 3 is a taxiway intersection with edge on taxiway "A".
        // Hold-short should be placed at node 2 (BEFORE the intersection), not node 3.
        var hs15 = HoldShortNode(2, "15");
        var intersectionNode = new GroundNode
        {
            Id = 3,
            Latitude = 0,
            Longitude = 0,
            Type = GroundNodeType.TaxiwayIntersection,
        };
        intersectionNode.Edges.Add(MakeEdge(3, 99, "A"));
        var layout = LayoutWith(TaxiNode(1), hs15, intersectionNode);
        var segments = new List<TaxiRouteSegment> { Seg(1, 2), Seg(2, 3) };
        var holdShorts = new List<HoldShortPoint>();

        HoldShortAnnotator.AddExplicitHoldShort(layout, segments, holdShorts, "A");

        var hs = Assert.Single(holdShorts);
        Assert.Equal(2, hs.NodeId);
        Assert.Equal(HoldShortReason.ExplicitHoldShort, hs.Reason);
        Assert.Equal("A", hs.TargetName);
    }

    [Fact]
    public void AddExplicitHoldShort_TaxiwayIntersectionFound_AddsHoldShortAtFirstMatch()
    {
        // Two candidate intersections — only the first in segment order should be picked.
        var node2 = new GroundNode
        {
            Id = 2,
            Latitude = 0,
            Longitude = 0,
            Type = GroundNodeType.TaxiwayIntersection,
        };
        node2.Edges.Add(MakeEdge(2, 99, "B"));
        var node3 = new GroundNode
        {
            Id = 3,
            Latitude = 0,
            Longitude = 0,
            Type = GroundNodeType.TaxiwayIntersection,
        };
        node3.Edges.Add(MakeEdge(3, 99, "B"));
        var layout = LayoutWith(TaxiNode(1), node2, node3);
        var segments = new List<TaxiRouteSegment> { Seg(1, 2), Seg(2, 3) };
        var holdShorts = new List<HoldShortPoint>();

        HoldShortAnnotator.AddExplicitHoldShort(layout, segments, holdShorts, "B");

        var hs = Assert.Single(holdShorts);
        Assert.Equal(2, hs.NodeId);
    }

    [Fact]
    public void AddExplicitHoldShort_NoMatch_NothingAdded()
    {
        var layout = LayoutWith(TaxiNode(1), TaxiNode(2));
        var segments = new List<TaxiRouteSegment> { Seg(1, 2) };
        var holdShorts = new List<HoldShortPoint>();

        HoldShortAnnotator.AddExplicitHoldShort(layout, segments, holdShorts, "ZZZZ");

        Assert.Empty(holdShorts);
    }

    // -------------------------------------------------------------------------
    // AddDestinationHoldShort
    // -------------------------------------------------------------------------

    [Fact]
    public void AddDestinationHoldShort_AddsHoldShortAtLastSegmentNode()
    {
        var layout = EmptyLayout();
        var segments = new List<TaxiRouteSegment> { Seg(1, 2), Seg(2, 3) };
        var holdShorts = new List<HoldShortPoint>();

        HoldShortAnnotator.AddDestinationHoldShort(layout, segments, holdShorts, "28R");

        var hs = Assert.Single(holdShorts);
        Assert.Equal(3, hs.NodeId);
        Assert.Equal(HoldShortReason.DestinationRunway, hs.Reason);
        Assert.Equal("28R", hs.TargetName);
    }

    [Fact]
    public void AddDestinationHoldShort_EmptySegments_NoHoldShortAdded()
    {
        var layout = EmptyLayout();
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
