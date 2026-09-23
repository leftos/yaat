using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;
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

    private (SfoGround Ground, AircraftState Aircraft, HoldShortPoint Bar)? HoldShortOfTOnB(string callsign, string type)
    {
        SfoGround? built = SfoGroundHarness.Build(output, autoCross: true);
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
        SfoGround? built = SfoGroundHarness.Build(output, autoCross: true);
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
    public void TaxiwayHoldShort_FloorCappedAtThePreviousJunction_WarnsWingtipClearanceNotAssured()
    {
        // A B738 told HS T from DLH455's pose: the SFO floor (156 ft nose to T) would put its tail behind the
        // B/D fillet junction one node back, so the stop clamps with the tail at that junction and warns.
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
        Assert.True(Math.Abs(tailToJunctionFt) <= 0.5, $"tail is {tailToJunctionFt:F1} ft from the junction; expected the clamp to put it there");

        double noseToTFt = SfoGroundHarness.DistanceToTaxiwayFt(layout, "T", NoseAt(layout, held.Bar, lengthFt));
        Assert.True(noseToTFt < 156.0, $"nose is {noseToTFt:F0} ft from T: the clamp did not bind");
        const string prefix = "holding short of TWY T — wingtip clearance from T not assured (";
        string warning = Assert.Single(route.Warnings, w => w.StartsWith(prefix, StringComparison.Ordinal));
        Assert.EndsWith(" ft)", warning);
        int reportedFt = int.Parse(warning[prefix.Length..^" ft)".Length], System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(Math.Abs(reportedFt - noseToTFt) <= 1.0, $"warning reports {reportedFt} ft; the nose is {noseToTFt:F1} ft from T");
    }

    private const string WingtipWarningPrefixT = "holding short of TWY T — wingtip clearance from T not assured (";

    [Fact]
    public void TaxiwayHoldShort_Recompute_KeepsOneWingtipWarningForTheBar()
    {
        if (HoldShortOfTOnB("SWA1", "B738") is not { } held)
        {
            return;
        }

        TaxiRoute route = held.Aircraft.Ground.AssignedTaxiRoute!;
        string first = Assert.Single(route.Warnings, w => w.StartsWith(WingtipWarningPrefixT, StringComparison.Ordinal));

        // A recompute that lands the nose at a different distance (here a longer fuselage against the same junction
        // clamp) replaces the warning instead of adding a second one for the same bar.
        HoldShortAnnotator.ComputeHoldShortPositions(held.Ground.Layout, route, LengthFt("B738") + 20.0);

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
        var engine = new SimulationEngine(groundData)
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = $"test-{airportId}-hold-short",
                ScenarioName = $"{airportId} hold short",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = airportId,
                AutoCrossRunway = true,
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
    public void TaxiwayHoldShort_ArmedMidTaxiWithTheFloorBehindTheAircraft_IsUnmakeable()
    {
        SfoGround? built = SfoGroundHarness.Build(output, autoCross: true);
        if (built is null)
        {
            return;
        }

        SfoGround ground = built.Value;
        AircraftState c172 = SpawnAtDlh455PoseOnB(ground, "N172", "C172");
        CommandResult taxi = ground.Engine.SendCommand("N172", "TAXI B T");
        Assert.True(taxi.Success, taxi.Message);
        int closeIn = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => SfoGroundHarness.DistanceToTaxiwayFt(ground.Layout, "T", c172.Position) < 150.0,
            300,
            null
        );
        Assert.True(closeIn > 0, "N172 never came within 150 ft of T");

        // The SFO floor wants the C172's nose 156 ft from T, which puts its centre ~170 ft out — behind where it is now.
        CommandResult hs = ground.Engine.SendCommand("N172", "HS T");
        output.WriteLine($"HS T: {hs.Success} — {hs.Message}");
        TaxiRoute route = c172.Ground.AssignedTaxiRoute!;
        HoldShortPoint bar = Assert.Single(route.HoldShortPoints, h => h.Reason == HoldShortReason.ExplicitHoldShort);
        double aheadFt = TaxiingPhase.AlongRouteDistanceToHoldShortFt(ground.Layout, route, c172.Position, bar);
        Assert.True(aheadFt < 0.0, $"the floor stop is {aheadFt:F0} ft ahead; the shape under test is a stop behind the aircraft");
        Assert.True(bar.Unable, "a hold-short stop behind the aircraft must be unmakeable");
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
            if ((bar.Edges.Count != 2) || (bar.RunwayId is not { } rwyId) || (layout.FindRunway(rwyId.End1) is not { } runway))
            {
                continue;
            }

            GroundNode a = bar.Edges[0].OtherNode(bar)!;
            GroundNode b = bar.Edges[1].OtherNode(bar)!;
            (GroundNode start, IGroundEdge outEdge) =
                DistanceToRunwayFt(runway, a.Position) < DistanceToRunwayFt(runway, b.Position) ? (a, bar.Edges[1]) : (b, bar.Edges[0]);
            if (WalkToJunction(bar, outEdge) is not { } walk || (walk.DistanceFt <= minFt) || (walk.DistanceFt >= maxFt))
            {
                continue;
            }

            if ((best is null) || (walk.DistanceFt < best.Value.BarToJunctionFt))
            {
                best = ([start, bar, .. walk.Nodes], walk.Crossed, walk.DistanceFt);
            }
        }

        return best;
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

        // A C172: the just-past-runway window is its length + 30 ft (~57 ft), and the SFO floor wants its nose 156 ft
        // from the crossed taxiway. An exit whose bar sits far enough before the junction that the length + 30 stop
        // leaves the tail clear of the bar (1.5 L + 30 ft), yet within 140 ft, is outside the window and inside the
        // floor's walk-back, so only the runway-bar clamp in the junction search keeps the tail off the runway.
        double lengthFt = LengthFt("C172");
        (List<GroundNode> Nodes, string Crossed, double BarToJunctionFt)? exit = FindRunwayExitToJunction(
            layout,
            (1.5 * lengthFt) + 30.0 + 2.0,
            140.0
        );
        Assert.True(exit is not null, "SFO has no runway exit whose bar is 1.5 L + 32 to 140 ft before a taxiway junction");
        (List<GroundNode> nodes, string crossed, double barToJunctionFt) = exit.Value;
        output.WriteLine($"exit: bar {nodes[1].Id} -> junction {nodes[^1].Id} on {crossed}, {barToJunctionFt:F0} ft");
        TaxiRoute route = RouteThrough(nodes, crossed);

        HoldShortAnnotator.ComputeHoldShortPositions(layout, route, lengthFt);

        HoldShortPoint hold = Assert.Single(route.HoldShortPoints);
        LatLon centre = new(hold.Latitude!.Value, hold.Longitude!.Value);
        double tailToBarFt = (GeoMath.DistanceNm(centre, nodes[1].Position) * GeoMath.FeetPerNm) - (lengthFt / 2.0);
        Assert.True(Math.Abs(tailToBarFt) <= 0.5, $"tail is {tailToBarFt:F1} ft from runway bar {nodes[1].Id}; expected the clamp to put it there");
        Assert.Null(hold.TailOverRunwayNodeId);
        Assert.Contains(route.Warnings, w => w.StartsWith($"holding short of TWY {crossed} — wingtip clearance", StringComparison.Ordinal));
    }

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

        HoldShortAnnotator.ComputeHoldShortPositions(layout, route, lengthFt);

        HoldShortPoint hold = Assert.Single(route.HoldShortPoints);
        GroundNode expected = VirtualNode.OffsetBefore(layout, route, hold.NodeId, (lengthFt / 2.0) / GeoMath.FeetPerNm, stopAtRunwayHoldShort: true);
        Assert.Equal(expected.Position.Lat, hold.Latitude);
        Assert.Equal(expected.Position.Lon, hold.Longitude);
        Assert.DoesNotContain(route.Warnings, w => w.Contains("wingtip clearance", StringComparison.Ordinal));
    }
}
