using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests;

public class HoldShortAnnotatorTests(ITestOutputHelper output)
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

        HoldShortAnnotator.AddImplicitRunwayHoldShorts(layout, segments, holdShorts, startPose: null);

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

        HoldShortAnnotator.AddImplicitRunwayHoldShorts(layout, segments, holdShorts, startPose: null);

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

        HoldShortAnnotator.AddImplicitRunwayHoldShorts(layout, segments, holdShorts, startPose: null);

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

        HoldShortAnnotator.AddImplicitRunwayHoldShorts(layout, segments, holdShorts, startPose: null);

        Assert.Single(holdShorts);
    }

    [Fact]
    public void AddImplicitRunwayHoldShorts_NonHoldShortNodes_Skipped()
    {
        AirportGroundLayout layout = LayoutWith(TaxiNode(1), TaxiNode(2), TaxiNode(3));
        var segments = new List<TaxiRouteSegment> { Seg(1, 2), Seg(2, 3) };
        var holdShorts = new List<HoldShortPoint>();

        HoldShortAnnotator.AddImplicitRunwayHoldShorts(layout, segments, holdShorts, startPose: null);

        Assert.Empty(holdShorts);
    }

    [Fact]
    public void AddImplicitRunwayHoldShorts_EmptySegments_NoHoldShortsAdded()
    {
        AirportGroundLayout layout = LayoutWith(HoldShortNode(1, "28R"));
        var holdShorts = new List<HoldShortPoint>();

        HoldShortAnnotator.AddImplicitRunwayHoldShorts(layout, [], holdShorts, startPose: null);

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
        var segments = new List<TaxiRouteSegment> { Seg(1, 2), Seg(2, 3) };
        var holdShorts = new List<HoldShortPoint>();

        HoldShortAnnotator.AddDestinationHoldShort(segments, holdShorts, "28R");

        HoldShortPoint hs = Assert.Single(holdShorts);
        Assert.Equal(3, hs.NodeId);
        Assert.Equal(HoldShortReason.DestinationRunway, hs.Reason);
        Assert.Equal("28R", hs.TargetName);
    }

    [Fact]
    public void AddDestinationHoldShort_EmptySegments_NoHoldShortAdded()
    {
        var holdShorts = new List<HoldShortPoint>();

        HoldShortAnnotator.AddDestinationHoldShort([], holdShorts, "28R");

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

    // -------------------------------------------------------------------------
    // Taxiway hold-short setback on real SFO geometry (#458)
    // -------------------------------------------------------------------------

    /// <summary>At DLH455's (B744) pose on B at the GC 28/01 bundle's t=1930, nosed 117.96° true toward T.</summary>
    private static AircraftState SpawnAtDlh455PoseOnB(SfoGround ground, string callsign, string type) =>
        SfoGroundHarness.SpawnAt(
            ground,
            callsign,
            type,
            (VirtualNode.Create(37.62162293056421, -122.38495164701898), new TrueHeading(117.95751231891961)),
            new HoldingInPositionPhase()
        );

    private static double LengthFt(string type) =>
        FaaAircraftDatabase.Get(type)?.LengthFt ?? throw new InvalidOperationException($"FAA aircraft database has no {type} length");

    /// <summary>The nose point of an aircraft stopped at <paramref name="bar"/>: half a length ahead of the centre, toward the bar's node.</summary>
    /// <remarks>Assumes the route runs straight from the stop to the bar's node: the nose is projected along that chord.</remarks>
    private static LatLon NoseAt(AirportGroundLayout layout, HoldShortPoint bar, double lengthFt)
    {
        LatLon centre = new(bar.Latitude!.Value, bar.Longitude!.Value);
        double bearing = GeoMath.BearingTo(centre, layout.Nodes[bar.NodeId].Position);
        return GeoMath.ProjectPoint(centre, new TrueHeading(bearing), (lengthFt / 2.0) / GeoMath.FeetPerNm);
    }

    /// <summary>
    /// SFO taxiway B, westbound toward T: node 138 behind, <see cref="SfoBAtT"/> the first node on B with an edge on T (where
    /// <c>HS T</c> binds), 137 next.
    /// </summary>
    private const int SfoBBehindNodeId = 138;

    private const int SfoBNextNodeId = 137;

    /// <summary>
    /// The node on B between 138 and 137 with an edge on T, joined to 137 along B: a fillet tangent node, found by what it is
    /// because the fillet pass numbers it after every node it mints earlier in the layout.
    /// </summary>
    private static GroundNode SfoBAtT(AirportGroundLayout layout)
    {
        GroundNode behind = layout.Nodes[SfoBBehindNodeId];
        return layout
                .Nodes.Values.Where(n =>
                    n.Edges.Any(e => e.MatchesTaxiway("B") && (e.OtherNode(n).Id == SfoBNextNodeId)) && n.Edges.Any(e => e.MatchesTaxiway("T"))
                )
                .MinBy(n => GeoMath.DistanceNm(n.Position, behind.Position))
            ?? throw new InvalidOperationException("SFO layout has no node on B next to node 137 with an edge on T");
    }

    /// <summary>
    /// How far short of the <see cref="SfoBAtT"/> node on B the aircraft stands: inside the approach leg's 450 ft free-space
    /// bound.
    /// </summary>
    private const double ShortOfBarNodeFt = 300.0;

    /// <summary>How far short of the <see cref="SfoBAtT"/> node on B an aircraft stands inside a B738's 159.5 ft taxiway setback.</summary>
    private const double InsideTheSetbackFt = 100.0;

    /// <summary>The pose <paramref name="shortOfNodeFt"/> short of the <see cref="SfoBAtT"/> node on SFO's B, westbound, facing the node.</summary>
    private static (LatLon Position, TrueHeading Heading) OnBShortOfT(AirportGroundLayout layout, double shortOfNodeFt)
    {
        GroundNode behind = layout.Nodes[SfoBBehindNodeId];
        GroundNode barNode = SfoBAtT(layout);
        LatLon position = GeoMath.ProjectPoint(
            barNode.Position,
            new TrueHeading(GeoMath.BearingTo(barNode.Position, behind.Position)),
            shortOfNodeFt / GeoMath.FeetPerNm
        );
        return (position, new TrueHeading(GeoMath.BearingTo(position, barNode.Position)));
    }

    /// <summary>
    /// A B738 route from <paramref name="shortOfNodeFt"/> short of the <see cref="SfoBAtT"/> node on SFO's B: the free-space approach leg to
    /// that node, then B on to 137, with <c>HS T</c> bound there and its stop computed.
    /// </summary>
    private static (TaxiRoute Route, LatLon Position, HoldShortPoint Bar) ApproachLegOnBToT(AirportGroundLayout layout, double shortOfNodeFt)
    {
        GroundNode barNode = SfoBAtT(layout);
        GroundNode next = layout.Nodes[SfoBNextNodeId];
        IGroundEdge? onward = barNode.Edges.FirstOrDefault(e => e.OtherNode(barNode).Id == SfoBNextNodeId);
        Assert.NotNull(onward);

        (LatLon position, TrueHeading heading) = OnBShortOfT(layout, shortOfNodeFt);
        var graphRoute = new TaxiRoute
        {
            Segments = [new TaxiRouteSegment { TaxiwayName = onward.TaxiwayName, Edge = onward.Directed(barNode, next) }],
            HoldShortPoints = [],
        };

        TaxiRoute route = TaxiApproachLeg.Prepend(layout, position, heading, graphRoute, AircraftLength.ResolveFt("B738"));
        Assert.True(route.Segments[0].FromNodeId < 0, $"no free-space leg: the route starts at layout node {route.Segments[0].FromNodeId}");
        Assert.Equal(barNode.Id, route.Segments[0].ToNodeId);

        var bar = new HoldShortPoint
        {
            NodeId = barNode.Id,
            Reason = HoldShortReason.ExplicitHoldShort,
            TargetName = "T",
        };
        route.HoldShortPoints.Add(bar);
        HoldShortAnnotator.ComputeHoldShortPositions(layout, route, LengthFt("B738"), null, HoldShortAnnotator.CombinedName);
        return (route, position, bar);
    }

    /// <summary>
    /// A taxiway hold-short whose node ends the route's free-space approach leg is set back along that leg, exactly as it
    /// is along a graph edge. An aircraft standing on B 300 ft short of the <see cref="SfoBAtT"/> node gets a 300 ft RAMP leg to it, and
    /// <c>HS T</c> binds that node: the stop must sit a fuselage plus 30 ft back from the node, not on it — on the node the
    /// aircraft's nose is in the B/T junction and a bar inside the braking distance reads as makeable.
    /// </summary>
    [Fact]
    public void TaxiwayHoldShort_AtTheEndOfAFreeSpaceLeg_SetsBackAlongTheLeg()
    {
        SfoGround? built = SfoGroundHarness.Build(output, autoCrossMode: AutoCrossRunwayMode.All);
        if (built is null)
        {
            return;
        }

        AirportGroundLayout layout = built.Value.Layout;
        GroundNode barNode = SfoBAtT(layout);
        (TaxiRoute route, LatLon position, HoldShortPoint bar) = ApproachLegOnBToT(layout, ShortOfBarNodeFt);
        double lengthFt = LengthFt("B738");

        double stopToNodeFt = GeoMath.DistanceNm(new LatLon(bar.Latitude!.Value, bar.Longitude!.Value), barNode.Position) * GeoMath.FeetPerNm;
        double alongFt = TaxiingPhase.AlongRouteDistanceToHoldShortFt(layout, route, position, bar);
        output.WriteLine(
            $"leg {route.Segments[0].Edge.DistanceNm * GeoMath.FeetPerNm:F0} ft; stop {stopToNodeFt:F1} ft back; along-route {alongFt:F1} ft"
        );

        Assert.True(
            stopToNodeFt >= lengthFt + 30.0 - 1.0,
            $"HS T stop is {stopToNodeFt:F1} ft back from node {barNode.Id}; a B738 taxiway setback is {lengthFt + 30.0:F1} ft"
        );
        Assert.True(
            alongFt <= ShortOfBarNodeFt - (lengthFt + 30.0) + 1.0,
            $"along-route distance to the stop reads {alongFt:F1} ft from {ShortOfBarNodeFt:F0} ft short of the node"
        );
    }

    /// <summary>
    /// A free-space leg shorter than the setback: an aircraft on B 100 ft short of the <see cref="SfoBAtT"/> node has less leg ahead of it than
    /// a B738's 159.5 ft taxiway setback, so the walk runs back to the leg's start. The stop is where the aircraft stands,
    /// never projected on behind it, where the aircraft would already be past its own stop.
    /// </summary>
    [Fact]
    public void TaxiwayHoldShort_LegShorterThanSetback_StopsAtTheLegStart()
    {
        SfoGround? built = SfoGroundHarness.Build(output, autoCrossMode: AutoCrossRunwayMode.All);
        if (built is null)
        {
            return;
        }

        (TaxiRoute route, LatLon position, HoldShortPoint bar) = ApproachLegOnBToT(built.Value.Layout, InsideTheSetbackFt);

        double stopToAircraftFt = GeoMath.DistanceNm(new LatLon(bar.Latitude!.Value, bar.Longitude!.Value), position) * GeoMath.FeetPerNm;
        output.WriteLine($"leg {route.Segments[0].Edge.DistanceNm * GeoMath.FeetPerNm:F0} ft; stop {stopToAircraftFt:F1} ft from the aircraft");
        Assert.True(
            stopToAircraftFt <= 1.0,
            $"HS T stop is {stopToAircraftFt:F1} ft from the aircraft standing {InsideTheSetbackFt:F0} ft short of node {bar.NodeId}"
        );
    }

    /// <summary>
    /// A stopped aircraft cannot overrun a bar. Standing on B 100 ft short of the <see cref="SfoBAtT"/> node, inside a B738's taxiway setback,
    /// at 0 kt, it is cleared <c>TAXI B T</c> and then told <c>HS T</c>: it reads back the hold short and holds where it
    /// is, rather than answering "unable" for a stop it is already standing at.
    /// </summary>
    [Fact]
    public void StoppedAircraftInsideTheSetback_ReadsBackHoldShort()
    {
        if (SfoGroundHarness.Build(output, autoCrossMode: AutoCrossRunwayMode.All) is not { } ground)
        {
            return;
        }

        (LatLon position, TrueHeading heading) = OnBShortOfT(ground.Layout, InsideTheSetbackFt);
        AircraftState aircraft = SfoGroundHarness.SpawnAt(
            ground,
            "SKW5416",
            "B738",
            (VirtualNode.Create(position.Lat, position.Lon), heading),
            new HoldingInPositionPhase()
        );
        aircraft.Ground.CurrentTaxiway = "B";

        CommandResult taxi = ground.Engine.SendCommand(aircraft.Callsign, "TAXI B T");
        Assert.True(taxi.Success, $"'TAXI B T' was refused: {taxi.Message}");
        CommandResult hold = ground.Engine.SendCommand(aircraft.Callsign, "HS T");
        SfoGroundHarness.DumpRoute(output, aircraft.Ground.AssignedTaxiRoute!);
        output.WriteLine($"GS {aircraft.GroundSpeed:F1} kt; HS T: {hold.Success} — {hold.Message}");

        Assert.True(hold.Success, $"'HS T' was refused: {hold.Message}");
        Assert.DoesNotContain("unable", hold.Message ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Hold short of T", hold.Message ?? "", StringComparison.Ordinal);

        // Holding where it stands: ten seconds on, it has crept no closer to the bar's node.
        GroundNode barNode = SfoBAtT(ground.Layout);
        double startToNodeFt = GeoMath.DistanceNm(aircraft.Position, barNode.Position) * GeoMath.FeetPerNm;
        SfoGroundHarness.TickUntil(ground.Engine, () => false, HoldTickSeconds, null);
        double endToNodeFt = GeoMath.DistanceNm(aircraft.Position, barNode.Position) * GeoMath.FeetPerNm;
        output.WriteLine(
            $"after {HoldTickSeconds}s: {endToNodeFt:F1} ft from node {barNode.Id} (was {startToNodeFt:F1}), GS {aircraft.GroundSpeed:F1} kt"
        );
        Assert.True(
            endToNodeFt >= startToNodeFt - 1.0,
            $"the aircraft moved {startToNodeFt - endToNodeFt:F1} ft toward node {barNode.Id} after reading back HS T"
        );
    }

    /// <summary>How long the stopped aircraft is ticked after its hold-short readback.</summary>
    private const int HoldTickSeconds = 10;

    private (SfoGround Ground, AircraftState Aircraft, HoldShortPoint Bar)? HoldShortOfTOnB(string callsign, string type)
    {
        SfoGround? built = SfoGroundHarness.Build(output, autoCrossMode: AutoCrossRunwayMode.All);
        if (built is null)
        {
            return null;
        }

        SfoGround ground = built.Value;
        AircraftState aircraft = SpawnAtDlh455PoseOnB(ground, callsign, type);
        CommandResult result = ground.Engine.SendCommand(callsign, "TAXI B HS T");
        Assert.True(result.Success, result.Message);
        TaxiRoute route = aircraft.Ground.AssignedTaxiRoute!;
        DumpRouteNodes(ground.Layout, route, "T");
        HoldShortPoint bar = Assert.Single(route.HoldShortPoints, h => h.Reason == HoldShortReason.ExplicitHoldShort);
        return (ground, aircraft, bar);
    }

    private void DumpRouteNodes(AirportGroundLayout layout, TaxiRoute route, string crossedTaxiway)
    {
        SfoGroundHarness.DumpRoute(output, route);
        foreach (TaxiRouteSegment seg in route.Segments)
        {
            if (!layout.Nodes.TryGetValue(seg.ToNodeId, out GroundNode? node))
            {
                continue;
            }

            string edges = string.Join(",", node.Edges.Select(e => e.TaxiwayName));
            double toCrossedFt = SfoGroundHarness.DistanceToTaxiwayFt(layout, crossedTaxiway, node.Position);
            output.WriteLine($"  seg {seg.TaxiwayName} -> node {node.Id} {node.Type} edges=[{edges}] to {crossedTaxiway}={toCrossedFt:F0}ft");
        }
    }

    [Fact]
    public void TaxiwayHoldShort_OnASpotTypedJunctionNode_KeepsTheTaxiwaySetback()
    {
        SfoGround? built = SfoGroundHarness.Build(output, autoCrossMode: AutoCrossRunwayMode.All);
        if (built is null)
        {
            return;
        }

        SfoGround ground = built.Value;
        AircraftState holder = SpawnAtDlh455PoseOnB(ground, "DLH455", "B744");
        CommandResult result = ground.Engine.SendCommand("DLH455", "TAXI B HS T");
        Assert.True(result.Success, result.Message);
        TaxiRoute route = holder.Ground.AssignedTaxiRoute!;
        DumpRouteNodes(ground.Layout, route, "T");

        HoldShortPoint bar = Assert.Single(route.HoldShortPoints, h => h.Reason == HoldShortReason.ExplicitHoldShort);
        Assert.Equal(GroundNodeType.Spot, ground.Layout.Nodes[bar.NodeId].Type);
        double lengthFt = LengthFt("B744");
        LatLon centre = new(bar.Latitude!.Value, bar.Longitude!.Value);
        double centreToNodeFt = GeoMath.DistanceNm(centre, ground.Layout.Nodes[bar.NodeId].Position) * GeoMath.FeetPerNm;
        Assert.True(
            centreToNodeFt >= lengthFt + 25.0,
            $"HS T stop is {centreToNodeFt:F0} ft from node {bar.NodeId}: the spot setback, not length+30"
        );
    }

    /// <summary>
    /// No committed test layout has a widest runway under 75 ft, so the width → floor buckets are pinned on the method
    /// itself, at every boundary: half the ADG's span ceiling plus 25 ft, with 150-199 ft mapped to ADG V.
    /// </summary>
    [Theory]
    [InlineData(0.0, 49.5)]
    [InlineData(74.9, 49.5)]
    [InlineData(75.0, 64.5)]
    [InlineData(99.9, 64.5)]
    [InlineData(100.0, 84.0)]
    [InlineData(149.9, 84.0)]
    [InlineData(150.0, 132.0)]
    [InlineData(199.9, 132.0)]
    [InlineData(200.0, 156.0)]
    [InlineData(300.0, 156.0)]
    public void WingtipClearanceFloor_ByWidestRunwayWidth(double widestRunwayWidthFt, double expectedFloorFt) =>
        Assert.Equal(expectedFloorFt, HoldShortAnnotator.WingtipClearanceFloorFt(widestRunwayWidthFt));

    [Fact]
    public void TaxiwayHoldShort_WideBodyAtSfo_NoseClearsTheCrossedCentrelineByTheAirportFloor()
    {
        if (HoldShortOfTOnB("DLH455", "B744") is not { } held)
        {
            return;
        }

        // SFO's widest runway is 200 ft (ADG VI proxy): the floor is half a 262 ft span plus 25 ft.
        LatLon nose = NoseAt(held.Ground.Layout, held.Bar, LengthFt("B744"));
        double noseToTFt = SfoGroundHarness.DistanceToTaxiwayFt(held.Ground.Layout, "T", nose);
        output.WriteLine($"nose to T centreline: {noseToTFt:F1} ft");
        Assert.True(noseToTFt >= 156.0 - 0.5, $"B744 nose stops {noseToTFt:F0} ft from T's centreline; the SFO floor is 156 ft");
    }

    [Fact]
    public void TaxiwayHoldShort_CrosserOnT_IsNotPinnedByTheHolder()
    {
        if (HoldShortOfTOnB("DLH455", "B744") is not { } held)
        {
            return;
        }

        SimulationEngine engine = held.Ground.Engine;
        int settled = SfoGroundHarness.TickUntil(
            engine,
            () => (held.Aircraft.GroundSpeed < SfoGroundHarness.StationarySpeedKts) && (held.Aircraft.Phases?.CurrentPhase is HoldingShortPhase),
            300,
            null
        );
        Assert.True(settled > 0, "DLH455 never came to rest holding short of T");

        // UAL733 (A320) on T at the bundle's t=1930, heading 208.98° true across B.
        AircraftState crosser = SfoGroundHarness.SpawnAt(
            held.Ground,
            "UAL733",
            "A320",
            (VirtualNode.Create(37.62094280885291, -122.38247885498703), new TrueHeading(208.98)),
            new HoldingInPositionPhase()
        );
        CommandResult result = engine.SendCommand("UAL733", "TAXI A @E8");
        Assert.True(result.Success, result.Message);
        LatLon start = crosser.Position;
        SfoGroundHarness.TickUntil(engine, () => false, 20, null);

        double movedFt = GeoMath.DistanceNm(start, crosser.Position) * GeoMath.FeetPerNm;
        output.WriteLine($"UAL733 moved {movedFt:F0} ft in 20 s");
        Assert.True(movedFt > 50.0, $"UAL733 on T moved only {movedFt:F0} ft in 20 s past DLH455 holding short of T");
    }

    [Fact]
    public void TaxiwayHoldShort_FloorPastThePreviousJunction_WalksBackPastItAndMeetsTheFloor()
    {
        // A B738 told HS T from DLH455's pose: the SFO floor (156 ft nose to T) puts its tail behind the B/D fillet
        // junction one node back. A taxiway hold short has no intersection backstop, so the stop walks back past that
        // junction until the nose meets the floor, and the route carries no wingtip warning.
        if (HoldShortOfTOnB("SWA1", "B738") is not { } held)
        {
            return;
        }

        AirportGroundLayout layout = held.Ground.Layout;
        TaxiRoute route = held.Aircraft.Ground.AssignedTaxiRoute!;
        GroundNode junction = layout.Nodes[route.Segments.First(s => s.ToNodeId == held.Bar.NodeId).FromNodeId];
        Assert.True(junction.Edges.Count > 2, $"node {junction.Id} before the bar is not a junction (the shape under test)");

        double lengthFt = LengthFt("B738");
        LatLon centre = new(held.Bar.Latitude!.Value, held.Bar.Longitude!.Value);
        double tailToJunctionFt = (GeoMath.DistanceNm(centre, junction.Position) * GeoMath.FeetPerNm) - (lengthFt / 2.0);
        Assert.True(tailToJunctionFt < -0.5, $"tail is {tailToJunctionFt:F1} ft from the junction; expected it behind the junction");

        double noseToTFt = SfoGroundHarness.DistanceToTaxiwayFt(layout, "T", NoseAt(layout, held.Bar, lengthFt));
        Assert.True(noseToTFt >= 156.0 - 1.0, $"nose is {noseToTFt:F0} ft from T, short of the 156 ft floor");
        Assert.DoesNotContain(route.Warnings, w => w.StartsWith(WingtipWarningPrefixT, StringComparison.Ordinal));
    }

    private const string WingtipWarningPrefixT = "holding short of TWY T — wingtip clearance from T not assured (";

    [Fact]
    public void TaxiwayHoldShort_Recompute_KeepsOneWingtipWarningForTheBar()
    {
        if (HoldShortOfTOnB("SWA1", "B738") is not { } held)
        {
            return;
        }

        // With the aircraft already at the bar's node the walk back cannot go behind it, so the stop stays at the length
        // setback, the nose short of the floor, and the route warns.
        TaxiRoute route = held.Aircraft.Ground.AssignedTaxiRoute!;
        route.CurrentSegmentIndex = route.Segments.FindIndex(s => s.ToNodeId == held.Bar.NodeId) + 1;
        HoldShortAnnotator.ComputeHoldShortPositions(held.Ground.Layout, route, LengthFt("B738"), null, HoldShortAnnotator.CombinedName);
        string first = Assert.Single(route.Warnings, w => w.StartsWith(WingtipWarningPrefixT, StringComparison.Ordinal));

        // A recompute that lands the nose at a different distance (here a longer fuselage against the same bound)
        // replaces the warning instead of adding a second one for the same bar.
        HoldShortAnnotator.ComputeHoldShortPositions(held.Ground.Layout, route, LengthFt("B738") + 20.0, null, HoldShortAnnotator.CombinedName);

        string second = Assert.Single(route.Warnings, w => w.StartsWith(WingtipWarningPrefixT, StringComparison.Ordinal));
        Assert.NotEqual(first, second);
    }

    private SfoGround? BuildAirport(string airportId)
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        AirportGroundLayout? layout = groundData.GetLayout(airportId);
        if (layout is null)
        {
            return null;
        }

        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        var engine = new SimulationEngine(groundData, PushPlanStore.CreateShipped())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = $"test-{airportId}-hold-short",
                ScenarioName = $"{airportId} hold short",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = airportId,
                AutoCrossRunwayMode = AutoCrossRunwayMode.All,
            },
        };
        return new SfoGround(engine, layout);
    }

    [Fact]
    public void TaxiwayHoldShort_LongAircraftNoseBetweenFloorAndLengthSetback_DoesNotWarn()
    {
        if (BuildAirport("SMF") is not { } ground)
        {
            return;
        }

        double floorFt = HoldShortAnnotator.WingtipClearanceFloorFt(ground.Layout.Runways.Max(r => r.WidthFt));
        Assert.Equal(132.0, floorFt);

        // A B744 on SMF D, 346 ft north of where D9 branches off at 37° on the approach side, facing south. The nose's
        // distance to D9's centreline grows only ~0.6 ft per foot back along D: the length+30 setback (146 ft) cannot be
        // reached before the walk-back clamps at the route's start, where the nose is ~138 ft from D9 — past the 132 ft floor.
        AircraftState b744 = SfoGroundHarness.SpawnAt(
            ground,
            "DLH9",
            "B744",
            (VirtualNode.Create(38.6908273, -121.5829799), new TrueHeading(180.8)),
            new HoldingInPositionPhase()
        );
        CommandResult result = ground.Engine.SendCommand("DLH9", "TAXI D HS D9");
        Assert.True(result.Success, result.Message);
        TaxiRoute route = b744.Ground.AssignedTaxiRoute!;
        DumpRouteNodes(ground.Layout, route, "D9");

        HoldShortPoint bar = Assert.Single(route.HoldShortPoints, h => h.Reason == HoldShortReason.ExplicitHoldShort);
        double lengthFt = LengthFt("B744");
        LatLon centre = new(bar.Latitude!.Value, bar.Longitude!.Value);
        double centreToStartFt = GeoMath.DistanceNm(centre, route.Segments[0].Edge.FromNode.Position) * GeoMath.FeetPerNm;
        Assert.True(centreToStartFt <= 1.0, $"stop is {centreToStartFt:F1} ft from the route start; expected the walk-back to clamp there");

        double noseToD9Ft = SfoGroundHarness.DistanceToTaxiwayFt(ground.Layout, "D9", NoseAt(ground.Layout, bar, lengthFt));
        output.WriteLine($"nose to D9 centreline: {noseToD9Ft:F1} ft; warnings=[{string.Join(" | ", route.Warnings)}]");
        Assert.InRange(noseToD9Ft, floorFt, (lengthFt / 2.0) + 30.0 - 1.0);
        Assert.DoesNotContain(route.Warnings, w => w.Contains("wingtip clearance", StringComparison.Ordinal));
    }

    [Fact]
    public void TaxiwayHoldShort_ArmedMidTaxiWithTheFloorBehindTheAircraft_StopsWhereItStandsAndIsUnable()
    {
        SfoGround? built = SfoGroundHarness.Build(output, autoCrossMode: AutoCrossRunwayMode.All);
        if (built is null)
        {
            return;
        }

        SfoGround ground = built.Value;
        AircraftState c172 = SpawnAtDlh455PoseOnB(ground, "N172", "C172");
        CommandResult taxi = ground.Engine.SendCommand("N172", "TAXI B >T");
        Assert.True(taxi.Success, taxi.Message);
        int closeIn = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => SfoGroundHarness.DistanceToTaxiwayFt(ground.Layout, "T", c172.Position) < 150.0,
            300,
            null
        );
        Assert.True(closeIn > 0, "N172 never came within 150 ft of T");

        // The SFO floor wants the C172's nose 156 ft from T, which puts its centre ~170 ft out — behind where it is now, so
        // the stop is placed where the aircraft stands and the hold cannot be made.
        CommandResult hs = ground.Engine.SendCommand("N172", "HS T");
        output.WriteLine($"HS T: {hs.Success} — {hs.Message}");
        TaxiRoute route = c172.Ground.AssignedTaxiRoute!;
        HoldShortPoint bar = Assert.Single(route.HoldShortPoints, h => h.Reason == HoldShortReason.ExplicitHoldShort);
        double aheadFt = TaxiingPhase.AlongRouteDistanceToHoldShortFt(ground.Layout, route, c172.Position, bar);
        Assert.True(Math.Abs(aheadFt) <= 1.0, $"the stop is {aheadFt:F1} ft ahead; expected it where the aircraft stands");
        Assert.True(bar.Unable, "a hold-short stop the aircraft cannot brake for must be unmakeable");
        Assert.Contains("Unable to hold short of T", hs.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A real runway exit: the runway-side neighbour of a runway hold-short bar, the bar, and the straight taxiway nodes
    /// beyond it up to the first node another taxiway joins (by a straight edge or a fillet arc), with that taxiway's
    /// name. Of the exits whose bar-to-junction distance lies in the window, the shortest wins (ties by node id).
    /// </summary>
    private static (List<GroundNode> Nodes, string Crossed, double BarToJunctionFt)? FindRunwayExitToJunction(
        AirportGroundLayout layout,
        double minFt,
        double maxFt
    )
    {
        (List<GroundNode> Nodes, string Crossed, double BarToJunctionFt)? best = null;
        foreach (GroundNode bar in layout.Nodes.Values.Where(n => n.Type == GroundNodeType.RunwayHoldShort).OrderBy(n => n.Id))
        {
            if (ExitThroughBar(layout, bar) is not { } walk || (walk.BarToJunctionFt <= minFt) || (walk.BarToJunctionFt >= maxFt))
            {
                continue;
            }

            if ((best is null) || (walk.BarToJunctionFt < best.Value.BarToJunctionFt))
            {
                best = walk;
            }
        }

        return best;
    }

    /// <summary>
    /// The route through <paramref name="bar"/>: the runway-side neighbour, the bar, and the straight taxiway nodes beyond it up
    /// to the first node another taxiway joins (by a straight edge or a fillet arc), with that taxiway's name.
    /// </summary>
    private static (List<GroundNode> Nodes, string Crossed, double BarToJunctionFt)? ExitThroughBar(AirportGroundLayout layout, GroundNode bar)
    {
        if ((bar.Edges.Count != 2) || (bar.RunwayId is not { } rwyId) || (layout.FindRunway(rwyId.End1) is not { } runway))
        {
            return null;
        }

        GroundNode a = bar.Edges[0].OtherNode(bar)!;
        GroundNode b = bar.Edges[1].OtherNode(bar)!;
        (GroundNode start, IGroundEdge outEdge) =
            DistanceToRunwayFt(runway, a.Position) < DistanceToRunwayFt(runway, b.Position) ? (a, bar.Edges[1]) : (b, bar.Edges[0]);
        if (WalkToJunction(bar, outEdge) is not { } walk)
        {
            return null;
        }

        return ([start, bar, .. walk.Nodes], walk.Crossed, walk.DistanceFt);
    }

    private static double DistanceToRunwayFt(GroundRunway runway, LatLon point) =>
        GeoMath.DistanceToSegmentFt(
            point,
            new LatLon(runway.Coordinates[0].Lat, runway.Coordinates[0].Lon),
            new LatLon(runway.Coordinates[^1].Lat, runway.Coordinates[^1].Lon)
        );

    private static string[] EdgeNames(IGroundEdge edge) => edge is GroundArc arc ? arc.TaxiwayNames : [edge.TaxiwayName];

    /// <summary>Follows <paramref name="outEdge"/>'s taxiway away from <paramref name="bar"/> to the first node another taxiway joins.</summary>
    private static (List<GroundNode> Nodes, string Crossed, double DistanceFt)? WalkToJunction(GroundNode bar, IGroundEdge outEdge)
    {
        string taxiway = outEdge.TaxiwayName;
        var nodes = new List<GroundNode>();
        GroundNode prev = bar;
        IGroundEdge edge = outEdge;
        double distanceFt = 0.0;
        for (int hop = 0; hop < 20; hop++)
        {
            GroundNode cur = edge.OtherNode(prev)!;
            distanceFt += GeoMath.DistanceNm(prev.Position, cur.Position) * GeoMath.FeetPerNm;
            nodes.Add(cur);
            List<IGroundEdge> links = [.. cur.Edges.Where(e => !e.IsRamp)];
            string? crossed = links.SelectMany(EdgeNames).FirstOrDefault(n => (n != taxiway) && !n.StartsWith("RWY", StringComparison.Ordinal));
            if (crossed is not null)
            {
                return (nodes, crossed, distanceFt);
            }

            if ((cur.Type == GroundNodeType.RunwayHoldShort) || (links.Count != 2))
            {
                return null;
            }

            GroundNode back = prev;
            edge = links.First(e => e.OtherNode(cur) != back);
            prev = cur;
        }

        return null;
    }

    private static TaxiRoute RouteThrough(List<GroundNode> nodes, string target)
    {
        var segments = new List<TaxiRouteSegment>();
        for (int i = 0; i + 1 < nodes.Count; i++)
        {
            GroundNode from = nodes[i];
            GroundNode to = nodes[i + 1];
            IGroundEdge edge = from.Edges.First(e => e.OtherNode(from) == to);
            segments.Add(new TaxiRouteSegment { TaxiwayName = edge.TaxiwayName, Edge = edge.Directed(from, to) });
        }

        var hold = new HoldShortPoint
        {
            NodeId = nodes[^1].Id,
            Reason = HoldShortReason.ExplicitHoldShort,
            TargetName = target,
        };
        return RouteOf(segments, [hold], 0);
    }

    [Fact]
    public void TaxiwayHoldShort_FloorWalkBack_StopsWithTheTailAtTheRunwayHoldShortBehind()
    {
        TestVnasData.EnsureInitialized();
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("SFO");
        if (layout is null)
        {
            return;
        }

        // SFO's 10R/28L bar on S3, its junction with S2 about 115 ft beyond: a C172's floor wants its nose 156 ft from
        // the crossed taxiway, so the floor alone would put the tail inside the bar and only the runway-bar clamp keeps
        // it off the runway. Pinned by runway, taxiway and junction rather than found: the exit the window's finder picks
        // is not one the floor applies to.
        double lengthFt = LengthFt("C172");
        GroundNode bar = Assert.Single(
            TestLayoutNodes.RunwayHoldShortsOnTaxiway(layout, "10R", "S3"),
            candidate => ExitThroughBar(layout, candidate) is { } walk && walk.Crossed == "S2"
        );
        (List<GroundNode> Nodes, string Crossed, double BarToJunctionFt)? exit = ExitThroughBar(layout, bar);
        Assert.True(exit is not null, $"SFO's 10R/28L bar on S3 (node {bar.Id}) has no straight walk to a taxiway junction");
        (List<GroundNode> nodes, string crossed, double barToJunctionFt) = exit.Value;
        output.WriteLine($"exit: bar {bar.Id} -> junction {nodes[^1].Id} on {crossed}, {barToJunctionFt:F0} ft");
        TaxiRoute route = RouteThrough(nodes, crossed);

        HoldShortAnnotator.ComputeHoldShortPositions(layout, route, lengthFt, null, HoldShortAnnotator.CombinedName);

        HoldShortPoint hold = Assert.Single(route.HoldShortPoints);
        LatLon centre = new(hold.Latitude!.Value, hold.Longitude!.Value);
        double tailToBarFt = (GeoMath.DistanceNm(centre, bar.Position) * GeoMath.FeetPerNm) - (lengthFt / 2.0);
        Assert.True(
            Math.Abs(tailToBarFt - Yaat.Sim.Phases.Ground.RunwayTailClearance.MarginFt) <= 0.5,
            $"tail is {tailToBarFt:F1} ft from runway bar {bar.Id}; expected the clamp to put it the crossing margin past it"
        );
        Assert.Null(hold.TailOverRunwayNodeId);
    }

    /// <summary>
    /// A B748 holding short just past a runway it cannot clear: the tail-over warning names that runway by the end the room lists
    /// as active, not both ends.
    /// </summary>
    [Fact]
    public void TaxiwayHoldShort_TailOverAnActiveRunway_WarningNamesTheActiveEnd()
    {
        TestVnasData.EnsureInitialized();
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("SFO");
        if (layout is null)
        {
            return;
        }

        (List<GroundNode> Nodes, string Crossed, double BarToJunctionFt)? exit = FindRunwayExitToJunction(layout, 65.0, 140.0);
        Assert.True(exit is not null, "SFO has no runway exit whose bar is 65-140 ft before a taxiway junction");
        (List<GroundNode> nodes, string crossed, _) = exit.Value;
        RunwayIdentifier runway = nodes[1].RunwayId!.Value;
        ActiveRunways active = ActiveRunways.Empty.With("SFO", [new ActiveRunway(runway.End2, ActiveRunwayUse.Both)]);
        TaxiRoute route = RouteThrough(nodes, crossed);

        HoldShortAnnotator.ComputeHoldShortPositions(
            layout,
            route,
            LengthFt("B748"),
            null,
            RunwayCrossingEnd.TailOverNamer(null, layout, active, null)
        );

        string warning = Assert.Single(route.Warnings, w => w.Contains("leaves the tail over RWY", StringComparison.Ordinal));
        Assert.Contains($"over RWY {RunwayIdentifier.ToDisplayDesignator(runway.End2)} —", warning, StringComparison.Ordinal);
    }

    /// <summary>
    /// A B748 landed on OAK 28R, with <c>OAK 28R 10L</c> active, exits past mid-field onto a taxiway whose junction is too close
    /// to clear the runway: the tail-over warning names its own runway by the assigned end 28R, not 10L, the active end nearer
    /// the aircraft. Red proof: with the namer ignoring the assigned runway the warning read "holding short of C leaves the
    /// tail over RWY 10L".
    /// </summary>
    [Fact]
    public void TaxiwayHoldShort_TailOverItsOwnRunway_WarningNamesTheAssignedEnd()
    {
        TestVnasData.EnsureInitialized();
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("OAK");
        if (layout is null)
        {
            return;
        }

        RunwayInfo assigned = NavigationDatabase.Instance.GetRunway("OAK", "28R") ?? throw new InvalidOperationException("OAK 28R missing");
        RunwayInfo farEnd = NavigationDatabase.Instance.GetRunway("OAK", "10L") ?? throw new InvalidOperationException("OAK 10L missing");
        double lengthFt = LengthFt("B748");
        (List<GroundNode> Nodes, string Crossed, double BarToJunctionFt)? exit = layout
            .Nodes.Values.Where(n => (n.Type == GroundNodeType.RunwayHoldShort) && (n.RunwayId is { } id) && id.Overlaps(assigned.Id))
            .Where(bar => PastMidField(bar.Position, assigned, farEnd))
            .OrderBy(n => n.Id)
            .Select(bar => ExitThroughBar(layout, bar))
            .FirstOrDefault(walk => (walk is { } w) && (w.BarToJunctionFt > 0.0) && (w.BarToJunctionFt < lengthFt));
        Assert.True(exit is not null, "OAK has no 28R exit past mid-field whose bar is closer than a B748 to a taxiway junction");
        (List<GroundNode> nodes, string crossed, _) = exit.Value;
        ActiveRunways active = ActiveRunways.Empty.With(
            "OAK",
            [new ActiveRunway("28R", ActiveRunwayUse.Both), new ActiveRunway("10L", ActiveRunwayUse.Both)]
        );
        TaxiRoute route = RouteThrough(nodes, crossed);

        HoldShortAnnotator.ComputeHoldShortPositions(
            layout,
            route,
            lengthFt,
            null,
            RunwayCrossingEnd.TailOverNamer(nodes[0].Position, layout, active, assigned)
        );

        string warning = Assert.Single(route.Warnings, w => w.Contains("leaves the tail over RWY", StringComparison.Ordinal));
        Assert.Contains("over RWY 28R —", warning, StringComparison.Ordinal);
    }

    /// <summary>Whether <paramref name="point"/> is nearer <paramref name="farEnd"/>'s threshold than the landing end's.</summary>
    private static bool PastMidField(LatLon point, RunwayInfo landing, RunwayInfo farEnd) =>
        GeoMath.DistanceNm(point, new LatLon(farEnd.ThresholdLatitude, farEnd.ThresholdLongitude))
        < GeoMath.DistanceNm(point, new LatLon(landing.ThresholdLatitude, landing.ThresholdLongitude));

    [Fact]
    public void TaxiwayHoldShort_JustPastRunway_KeepsTheNoseAtTheLineWithoutTheFloor()
    {
        TestVnasData.EnsureInitialized();
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("SFO");
        if (layout is null)
        {
            return;
        }

        // A B738 at the same kind of exit: the bar is inside its length + 30 ft (~160 ft), so the just-past-runway path
        // wins — nose at the taxiway line, clamped at the runway bar — and the floor adds neither a move nor a warning.
        double lengthFt = LengthFt("B738");
        (List<GroundNode> Nodes, string Crossed, double BarToJunctionFt)? exit = FindRunwayExitToJunction(layout, 65.0, 140.0);
        Assert.True(exit is not null, "SFO has no runway exit whose bar is 65-140 ft before a taxiway junction");
        (List<GroundNode> nodes, string crossed, _) = exit.Value;
        TaxiRoute route = RouteThrough(nodes, crossed);

        HoldShortAnnotator.ComputeHoldShortPositions(layout, route, lengthFt, null, HoldShortAnnotator.CombinedName);

        HoldShortPoint hold = Assert.Single(route.HoldShortPoints);
        GroundNode expected = VirtualNode.OffsetBefore(
            layout,
            route,
            hold.NodeId,
            (lengthFt / 2.0) / GeoMath.FeetPerNm,
            HoldShortStopKind.TaxiwayJustPastRunway
        );
        Assert.Equal(expected.Position.Lat, hold.Latitude);
        Assert.Equal(expected.Position.Lon, hold.Longitude);
        Assert.DoesNotContain(route.Warnings, w => w.Contains("wingtip clearance", StringComparison.Ordinal));
    }

    /// <summary>How far (ft) short of the far runway hold line the aircraft stands when its just-past-runway stop is placed.</summary>
    private const double ShortOfTheFarHoldLineFt = 15.0;

    /// <summary>
    /// A long aircraft at a runway exit whose bar lies less than half its length past the runway hold line: the stop is
    /// clamped at that line, so the real setback is the gap, not half the length. With the aircraft's centre short of the
    /// line but past where a half-length setback would put the stop, the stop stays at the line, ahead of the aircraft.
    /// </summary>
    [Fact]
    public void TaxiwayJustPastRunway_AircraftBetweenHalfALengthBackAndTheFarHoldLine_KeepsTheStopAtTheLine()
    {
        TestVnasData.EnsureInitialized();
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("SFO");
        if (layout is null)
        {
            return;
        }

        double lengthFt = LengthFt("B744");
        double halfFt = lengthFt / 2.0;
        (List<GroundNode> Nodes, string Crossed, double BarToJunctionFt)? exit = FindRunwayExitToJunction(
            layout,
            0.0,
            halfFt - ShortOfTheFarHoldLineFt - 10.0
        );
        Assert.True(exit is not null, $"SFO has no runway exit whose bar is under {halfFt - ShortOfTheFarHoldLineFt - 10.0:F0} ft before a junction");
        (List<GroundNode> nodes, string crossed, double gapFt) = exit.Value;
        output.WriteLine($"exit: runway bar {nodes[1].Id} -> junction {nodes[^1].Id} on {crossed}: G {gapFt:F1} ft, L/2 {halfFt:F1} ft");
        Assert.True(gapFt < halfFt - 10.0, $"the gap {gapFt:F1} ft is not under L/2 - 10 ft ({halfFt - 10.0:F1} ft)");
        Assert.True(
            FeetBetween(nodes[0].Position, nodes[1].Position) > ShortOfTheFarHoldLineFt,
            "the edge onto the runway bar is too short to stand the aircraft on"
        );

        TaxiRoute route = RouteThrough(nodes, crossed);
        var towardRunway = new TrueHeading(GeoMath.BearingTo(nodes[1].Position, nodes[0].Position));
        LatLon aircraft = GeoMath.ProjectPoint(nodes[1].Position, towardRunway, ShortOfTheFarHoldLineFt / GeoMath.FeetPerNm);

        HoldShortAnnotator.ComputeHoldShortPositions(layout, route, lengthFt, aircraft, HoldShortAnnotator.CombinedName);

        LatLon stop = PlacedStopOf(route);
        output.WriteLine(
            $"stop {FeetBetween(stop, nodes[1].Position):F1} ft from the runway bar, {FeetBetween(stop, aircraft):F1} ft from the aircraft"
        );
        Assert.True(
            FeetBetween(stop, nodes[1].Position) <= 1.0,
            $"the stop lies {FeetBetween(stop, nodes[1].Position):F1} ft from the runway bar {nodes[1].Id}; expected it at the line"
        );
    }

    /// <summary>The speed (kt) the B738 on the doubled-back route rolls at when its stop is judged.</summary>
    private const double DoubledBackRollingKts = 8.0;

    /// <summary>How far (ft) past the turn-about node, on the leg back, the B738 on the doubled-back route stands.</summary>
    private const double DoubledBackPastTheTurnFt = 5.0;

    /// <summary>
    /// A route that doubles back over its own stop: out along SFO B away from T to the next node, a turn about there, and
    /// back the same way to hold short of T. The B738 has turned and rolls on the leg back, short of the stop. The stop
    /// lies on both the leg out and the leg back; it is where a route that only drives the leg back puts it, ahead of the
    /// aircraft on that leg, not moved to where the aircraft stands, and the rolling aircraft can make it.
    /// </summary>
    [Fact]
    public void TaxiwayHoldShort_RouteDoublingBackOverItsStop_KeepsTheStopAheadOnTheLegBack()
    {
        if (StageDoubledBackRoute(currentSegmentIndex: 1) is not { } staged)
        {
            return;
        }

        AirportGroundLayout layout = staged.Layout;
        TaxiRoute doubled = staged.Route;
        LatLon reference = staged.Reference;
        var legBack = new TrueHeading(GeoMath.BearingTo(staged.TurnNode.Position, staged.NearNode.Position));
        LatLon onLegBack = GeoMath.ProjectPoint(staged.TurnNode.Position, legBack, DoubledBackPastTheTurnFt / GeoMath.FeetPerNm);
        HoldShortAnnotator.ComputeHoldShortPositions(layout, doubled, AircraftLength.ResolveFt("B738"), onLegBack, HoldShortAnnotator.CombinedName);
        LatLon stop = PlacedStopOf(doubled);
        double aheadFt =
            FeetBetween(onLegBack, stop)
            * Math.Cos(GeoMath.AbsBearingDifference(legBack.Degrees, GeoMath.BearingTo(onLegBack, stop)) * Math.PI / 180.0);
        output.WriteLine($"doubled-back stop {FeetBetween(stop, reference):F1} ft from the reference, {aheadFt:F1} ft ahead of the aircraft");

        Assert.True(FeetBetween(stop, reference) <= 1.0, $"the stop lies {FeetBetween(stop, reference):F1} ft from where the leg back alone puts it");
        Assert.True(aheadFt > 1.0, $"the stop was moved {aheadFt:F1} ft ahead, to where the aircraft stands or behind it");
        AircraftState aircraft = DoubledBackB738(onLegBack, legBack);
        HoldShortPoint hold = Assert.Single(doubled.HoldShortPoints);
        Assert.False(
            TaxiingPhase.IsHoldShortUnmakeable(layout, doubled, aircraft, hold, 1.0 / SimulationEngine.PhysicsSubTickRate),
            $"the stop {aheadFt:F1} ft ahead was judged unmakeable at {DoubledBackRollingKts:F0} kt"
        );
    }

    /// <summary>
    /// The same doubled-back route with the B738 still on the leg out, halfway between where the route starts and the stop it
    /// passes on the way out. The stop it holds at is the one it reaches on the leg back, after the turn about, so the
    /// rolling aircraft can make it.
    /// </summary>
    [Fact]
    public void TaxiwayHoldShort_RouteDoublingBackOverItsStop_OnTheLegOut_IsMakeable()
    {
        if (StageDoubledBackRoute(currentSegmentIndex: 0) is not { } staged)
        {
            return;
        }

        AirportGroundLayout layout = staged.Layout;
        TaxiRoute doubled = staged.Route;
        var outward = new TrueHeading(GeoMath.BearingTo(staged.NearNode.Position, staged.TurnNode.Position));
        LatLon outFrom = doubled.Segments[0].Edge.FromNode.Position;
        LatLon onLegOut = GeoMath.ProjectPoint(outFrom, outward, FeetBetween(outFrom, staged.Reference) / 2.0 / GeoMath.FeetPerNm);
        HoldShortAnnotator.ComputeHoldShortPositions(layout, doubled, AircraftLength.ResolveFt("B738"), onLegOut, HoldShortAnnotator.CombinedName);
        LatLon stop = PlacedStopOf(doubled);
        output.WriteLine(
            $"doubled-back stop {FeetBetween(stop, staged.Reference):F1} ft from the reference, "
                + $"{FeetBetween(onLegOut, stop):F1} ft from the aircraft on the leg out"
        );

        AircraftState aircraft = DoubledBackB738(onLegOut, outward);
        HoldShortPoint hold = Assert.Single(doubled.HoldShortPoints);
        Assert.False(
            TaxiingPhase.IsHoldShortUnmakeable(layout, doubled, aircraft, hold, 1.0 / SimulationEngine.PhysicsSubTickRate),
            $"the stop was judged unmakeable from the leg out at {DoubledBackRollingKts:F0} kt"
        );
    }

    /// <summary>
    /// The doubled-back route at SFO and the points its tests stand the B738 against: the stop a straight route along B to T
    /// places (the reference), and the leg's near node and turn-about node that stop lies between.
    /// </summary>
    private sealed record DoubledBackStaging(AirportGroundLayout Layout, TaxiRoute Route, LatLon Reference, GroundNode NearNode, GroundNode TurnNode);

    /// <summary>
    /// Stages the doubled-back route: out along SFO B away from T, from a point between the leg's near node and the
    /// reference stop, to the turn-about node, then back the same way to hold short of T, with segment
    /// <paramref name="currentSegmentIndex"/> in progress. The hold-short has no stop placed yet. Null when the SFO layout
    /// is not available.
    /// </summary>
    private DoubledBackStaging? StageDoubledBackRoute(int currentSegmentIndex)
    {
        SfoGround? built = SfoGroundHarness.Build(output, autoCrossMode: AutoCrossRunwayMode.All);
        if (built is null)
        {
            return null;
        }

        AirportGroundLayout layout = built.Value.Layout;
        GroundNode bar = Assert.IsType<GroundNode>(layout.FindIntersectionNode("B", "T"));
        GroundNode? eastOrNull = bar
            .Edges.Where(e => (e is GroundEdge) && e.MatchesTaxiway("B"))
            .Select(e => e.OtherNode(bar))
            .MaxBy(n => n.Position.Lon);
        Assert.NotNull(eastOrNull);
        GroundNode east = eastOrNull;
        List<GroundNode> back =
        [
            bar,
            .. Simulation.GroundTaxi.TaxiStartsOnOccupiedTaxiwayTests.NodesAhead(east, GeoMath.BearingTo(bar.Position, east.Position), "B"),
        ];

        int far = Math.Min(back.Count - 1, 8);
        TaxiRoute straight = HoldingShortOfTAlong([.. Enumerable.Range(0, far + 1).Select(i => back[far - i])], bar);
        HoldShortAnnotator.ComputeHoldShortPositions(
            layout,
            straight,
            AircraftLength.ResolveFt("B738"),
            back[far].Position,
            HoldShortAnnotator.CombinedName
        );
        LatLon reference = PlacedStopOf(straight);
        int k = Enumerable.Range(0, far).First(i => OffLegFt(back[i], back[i + 1], reference) < 1.0);
        double fromTurnFt = FeetBetween(back[k + 1].Position, reference);
        output.WriteLine($"reference stop on B between nodes {back[k].Id} and {back[k + 1].Id}, {fromTurnFt:F1} ft from {back[k + 1].Id}");
        Assert.True(fromTurnFt > DoubledBackPastTheTurnFt + 15.0, "the stop is too close to the turn-about node for the shape under test");

        // Out from a point between the leg's near node and the stop, so the route passes the bar's node only at its end.
        var outward = new TrueHeading(GeoMath.BearingTo(back[k].Position, back[k + 1].Position));
        LatLon outFrom = GeoMath.ProjectPoint(back[k].Position, outward, FeetBetween(back[k].Position, reference) / 2.0 / GeoMath.FeetPerNm);
        TaxiRoute backToT = HoldingShortOfTAlong([back[k + 1], .. Enumerable.Range(0, k + 1).Select(i => back[k - i])], bar);
        var doubled = new TaxiRoute
        {
            Segments = [VirtualNode.CreateSegment(VirtualNode.Create(outFrom.Lat, outFrom.Lon), back[k + 1], "B"), .. backToT.Segments],
            HoldShortPoints = backToT.HoldShortPoints,
            CurrentSegmentIndex = currentSegmentIndex,
        };
        return new DoubledBackStaging(layout, doubled, reference, back[k], back[k + 1]);
    }

    /// <summary>The B738 rolling at <see cref="DoubledBackRollingKts"/> on the doubled-back route.</summary>
    private static AircraftState DoubledBackB738(LatLon position, TrueHeading heading) =>
        new()
        {
            Callsign = "N738",
            AircraftType = "B738",
            Position = position,
            TrueHeading = heading,
            TrueTrack = heading,
            IndicatedAirspeed = DoubledBackRollingKts,
            IsOnGround = true,
        };

    /// <summary>A route along B through <paramref name="nodes"/>, holding short of T at <paramref name="bar"/>.</summary>
    private static TaxiRoute HoldingShortOfTAlong(List<GroundNode> nodes, GroundNode bar) =>
        new()
        {
            Segments =
            [
                .. nodes
                    .Zip(nodes.Skip(1))
                    .Select(pair => new TaxiRouteSegment
                    {
                        TaxiwayName = "B",
                        Edge = pair.First.Edges.First(e => e.OtherNode(pair.First).Id == pair.Second.Id).Directed(pair.First, pair.Second),
                    }),
            ],
            HoldShortPoints =
            [
                new HoldShortPoint
                {
                    NodeId = bar.Id,
                    Reason = HoldShortReason.ExplicitHoldShort,
                    TargetName = "T",
                },
            ],
        };

    private static LatLon PlacedStopOf(TaxiRoute route) =>
        Assert.Single(route.HoldShortPoints) is { Latitude: { } lat, Longitude: { } lon }
            ? new LatLon(lat, lon)
            : throw new InvalidOperationException("no stop placed");

    /// <summary>
    /// How far (ft) the path through <paramref name="point"/> is longer than the leg from <paramref name="a"/> to
    /// <paramref name="b"/>: zero on the leg.
    /// </summary>
    private static double OffLegFt(GroundNode a, GroundNode b, LatLon point) =>
        FeetBetween(a.Position, point) + FeetBetween(point, b.Position) - FeetBetween(a.Position, b.Position);

    private static double FeetBetween(LatLon a, LatLon b) => GeoMath.DistanceNm(a, b) * GeoMath.FeetPerNm;

    /// <summary>
    /// OAK's 28R bar on B, north of the runway (the one on the C/A side), and its B neighbours toward and away from the runway.
    /// </summary>
    private static (GroundNode Bar, GroundNode TowardRunway, GroundNode AwayFromRunway) Oak28RBarOnB()
    {
        AirportGroundLayout layout = new TestAirportGroundData().GetLayout("OAK") ?? throw new InvalidOperationException("no OAK test layout");
        GroundNode bar = GroundNodeLookup.HoldShortBar(layout, "28R", "B", GroundNodeLookup.Junction(layout, "C", "A"));
        Assert.Equal(GroundNodeType.RunwayHoldShort, bar.Type);
        Assert.True((bar.RunwayId is { } id) && id.Contains("28R"), $"node {bar.Id} is no 28R bar: {bar.RunwayId}");
        List<GroundNode> onB = [.. bar.Edges.OfType<GroundEdge>().Where(e => e.TaxiwayName == "B").Select(e => e.OtherNode(bar))];
        return (bar, onB.MinBy(n => n.Position.Lat)!, onB.MaxBy(n => n.Position.Lat)!);
    }

    private static LatLon Toward(GroundNode from, GroundNode to, double ft) =>
        GeoMath.ProjectPoint(from.Position, new TrueHeading(GeoMath.BearingTo(from.Position, to.Position)), ft / GeoMath.FeetPerNm);

    /// <summary>
    /// The pose of an aircraft of <paramref name="body"/> whose nose is at <paramref name="nose"/>, heading <paramref name="headingDeg"/>.
    /// </summary>
    private static StartPose PoseFromNose(LatLon nose, double headingDeg, AircraftBody body) =>
        new(GeoMath.ProjectPoint(nose, new TrueHeading(headingDeg).ToReciprocal(), body.LengthFt / 2.0 / GeoMath.FeetPerNm), headingDeg, body);

    /// <summary>
    /// N462NX leaving 28R northbound on B, its nose 150 ft short of the north bar #188 on the runway side, on a route whose
    /// first leg leads north away from the runway: the bar is still ahead.
    /// </summary>
    [Fact]
    public void StartBarIsAhead_LeavingTheRunwayShortOfTheBar_IsAhead()
    {
        TestVnasData.EnsureInitialized();
        (GroundNode bar, GroundNode toward, GroundNode away) = Oak28RBarOnB();
        var c172 = AircraftBody.Of("C172");
        StartPose pose = PoseFromNose(Toward(bar, toward, 150.0), GeoMath.BearingTo(bar.Position, away.Position), c172);

        Assert.True(HoldShortAnnotator.StartBarIsAhead(bar, away, pose));
    }

    /// <summary>An aircraft holding at the line outside, nose on the bar, on a route leading onto the runway: the bar is ahead.</summary>
    [Fact]
    public void StartBarIsAhead_HoldingAtTheLineBeforeEntering_IsAhead()
    {
        TestVnasData.EnsureInitialized();
        (GroundNode bar, GroundNode toward, GroundNode _) = Oak28RBarOnB();
        var c172 = AircraftBody.Of("C172");
        StartPose pose = PoseFromNose(bar.Position, GeoMath.BearingTo(bar.Position, toward.Position), c172);

        Assert.True(HoldShortAnnotator.StartBarIsAhead(bar, toward, pose));
    }

    /// <summary>
    /// An aircraft whose nose is already 50 ft past the bar on the runway side, on a route leading on toward the runway: it
    /// has passed the line, so the bar is not ahead.
    /// </summary>
    [Fact]
    public void StartBarIsAhead_NosePastTheLineTowardTheRunway_IsNotAhead()
    {
        TestVnasData.EnsureInitialized();
        (GroundNode bar, GroundNode toward, GroundNode _) = Oak28RBarOnB();
        var c172 = AircraftBody.Of("C172");
        StartPose pose = PoseFromNose(Toward(bar, toward, 50.0), GeoMath.BearingTo(bar.Position, toward.Position), c172);

        Assert.False(HoldShortAnnotator.StartBarIsAhead(bar, toward, pose));
    }

    /// <summary>
    /// A C172 north of bar #188, its nose 1 ft short of it, on a route whose first leg leads north away from the runway. Facing
    /// the bar, its tail is the part furthest along the leg, well past the bar: the bar is not ahead. Turned to face along the
    /// leg with the nose where it was, the nose leads, 1 ft past the bar, inside the tolerance: the bar is ahead.
    /// </summary>
    [Fact]
    public void StartBarIsAhead_JudgesThePartFurthestAlongTheLeg_NotTheNose()
    {
        TestVnasData.EnsureInitialized();
        (GroundNode bar, GroundNode _, GroundNode away) = Oak28RBarOnB();
        LatLon nose = Toward(bar, away, 1.0);
        var c172 = AircraftBody.Of("C172");
        double legDeg = GeoMath.BearingTo(bar.Position, away.Position);
        double facingTheBarDeg = GeoMath.BearingTo(away.Position, bar.Position);

        Assert.False(HoldShortAnnotator.StartBarIsAhead(bar, away, PoseFromNose(nose, facingTheBarDeg, c172)));
        Assert.True(HoldShortAnnotator.StartBarIsAhead(bar, away, PoseFromNose(nose, legDeg, c172)));
    }

    /// <summary>
    /// A B738 that stopped on a HOLD while exiting 28R northbound on B, straddling bar #188 with its nose 2.5 ft past the line
    /// (inside <see cref="RunwayOccupancy.HoldLineFoulToleranceFt"/>), then taxied on north away from the runway: the bar marks
    /// where it is clear of the runway, not a hold, so the route's start bar is not held.
    /// </summary>
    [Fact]
    public void Taxi_AfterHoldDuringExit_StraddlingLineLeavingRunway_NotHeldAtStartBar()
    {
        TestVnasData.EnsureInitialized();
        AirportGroundLayout layout = new TestAirportGroundData().GetLayout("OAK") ?? throw new InvalidOperationException("no OAK test layout");
        (GroundNode bar, GroundNode _, GroundNode away) = Oak28RBarOnB();
        double legDeg = GeoMath.BearingTo(bar.Position, away.Position);
        StartPose pose = PoseFromNose(Toward(bar, away, 2.5), legDeg, AircraftBody.Of("B738"));
        List<HoldShortPoint> holds = [];

        HoldShortAnnotator.AddImplicitRunwayHoldShorts(layout, RouteThrough([bar, away], "B").Segments, holds, pose);

        Assert.DoesNotContain(holds, h => h.NodeId == bar.Id);
    }

    /// <summary>
    /// A C172 following traffic while leaving 28R northbound on B, its nose 20 ft short of bar #188 on the runway side, on a way
    /// back leading north away from the runway: the start bar is no runway entry, so the way back is not refused.
    /// </summary>
    [Fact]
    public void Follow_WhileLeavingRunwayAcrossStartBar_NotRefusedAsEntry()
    {
        TestVnasData.EnsureInitialized();
        AirportGroundLayout layout = new TestAirportGroundData().GetLayout("OAK") ?? throw new InvalidOperationException("no OAK test layout");
        (GroundNode bar, GroundNode toward, GroundNode away) = Oak28RBarOnB();
        StartPose pose = PoseFromNose(Toward(bar, toward, 20.0), GeoMath.BearingTo(bar.Position, away.Position), AircraftBody.Of("C172"));
        var onto = new FollowRoutePlanner.RouteFromHere(RouteThrough([bar, away], "B").Segments, away.Id);

        Assert.DoesNotContain(bar.Id, FollowingPhase.RunwayEntries(layout, onto, pose));
    }

    /// <summary>
    /// A route south on B from the node north of OAK's 28R bar #188 to that bar, with a cleared taxiway hold-short at its start
    /// node: the start hold is cleared and no runway's, so the first runway bar ahead is #188.
    /// </summary>
    [Fact]
    public void FirstRunwayBarAhead_ClearedTaxiwayHoldAtStart_ReturnsTheRunwayBar()
    {
        TestVnasData.EnsureInitialized();
        AirportGroundLayout layout = new TestAirportGroundData().GetLayout("OAK") ?? throw new InvalidOperationException("no OAK test layout");
        (GroundNode bar, GroundNode _, GroundNode away) = Oak28RBarOnB();
        Assert.NotEqual(GroundNodeType.RunwayHoldShort, away.Type);
        var route = new TaxiRoute
        {
            Segments = RouteThrough([away, bar], "B").Segments,
            HoldShortPoints =
            [
                new HoldShortPoint
                {
                    NodeId = away.Id,
                    Reason = HoldShortReason.ExplicitHoldShort,
                    TargetName = "C",
                    IsCleared = true,
                },
                new HoldShortPoint
                {
                    NodeId = bar.Id,
                    Reason = HoldShortReason.RunwayCrossing,
                    TargetName = "28R",
                },
            ],
            CurrentSegmentIndex = 0,
        };

        (HoldShortPoint Bar, int SegmentIndex)? first = TaxiingPhase.FirstRunwayBarAhead(route, layout);

        Assert.NotNull(first);
        Assert.Equal(bar.Id, first.Value.Bar.NodeId);
        Assert.Equal(0, first.Value.SegmentIndex);
    }

    /// <summary>
    /// A C172 holding short of bar #188 outside 28R, nose on the bar, on a route whose first leg leads south onto the runway:
    /// the start bar is held.
    /// </summary>
    [Fact]
    public void Taxi_ShortOfStartBar_FirstLegTowardRunway_HoldsAtStartBar()
    {
        TestVnasData.EnsureInitialized();
        AirportGroundLayout layout = new TestAirportGroundData().GetLayout("OAK") ?? throw new InvalidOperationException("no OAK test layout");
        (GroundNode bar, GroundNode toward, GroundNode _) = Oak28RBarOnB();
        StartPose pose = PoseFromNose(bar.Position, GeoMath.BearingTo(bar.Position, toward.Position), AircraftBody.Of("C172"));
        List<HoldShortPoint> holds = [];

        HoldShortAnnotator.AddImplicitRunwayHoldShorts(layout, RouteThrough([bar, toward], "B").Segments, holds, pose);

        HoldShortPoint hold = Assert.Single(holds, h => h.NodeId == bar.Id);
        Assert.Equal(HoldShortReason.RunwayCrossing, hold.Reason);
    }
}
