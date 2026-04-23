using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;

namespace Yaat.Sim.Tests;

/// <summary>
/// Unit tests for <see cref="TaxiIngressResolver"/>. Builds minimal
/// synthetic graphs (just enough nodes/edges for the case under test) and
/// asserts the resolver picks the expected ingress target, rejects
/// candidates that would cross other edges, and correctly identifies the
/// replace-first-route-segment case for mid-edge ingress.
/// </summary>
public class TaxiIngressResolverTests(ITestOutputHelper output)
{
    private static GroundNode MakeNode(int id, double lat, double lon, GroundNodeType type = GroundNodeType.TaxiwayIntersection) =>
        new()
        {
            Id = id,
            Latitude = lat,
            Longitude = lon,
            Type = type,
        };

    private static GroundEdge MakeEdge(GroundNode a, GroundNode b, string taxiwayName = "A")
    {
        double distNm = GeoMath.DistanceNm(a.Latitude, a.Longitude, b.Latitude, b.Longitude);
        return new GroundEdge
        {
            Nodes = [a, b],
            TaxiwayName = taxiwayName,
            DistanceNm = distNm,
        };
    }

    private static AirportGroundLayout MakeLayout(GroundNode[] nodes, GroundEdge[] edges)
    {
        var nodeDict = nodes.ToDictionary(n => n.Id);
        var layout = new AirportGroundLayout
        {
            AirportId = "KTEST",
            Nodes = nodeDict,
            Edges = edges.ToList(),
        };
        layout.RebuildAdjacencyLists();
        return layout;
    }

    private static TaxiRouteSegment MakeRouteSegment(GroundNode from, GroundNode to, string taxiwayName = "A")
    {
        var edge = MakeEdge(from, to, taxiwayName);
        var directed = new DirectionalEdge { Edge = edge, FromNode = from, ToNode = to };
        return new TaxiRouteSegment { Edge = directed, TaxiwayName = taxiwayName };
    }

    // ---- Threshold behavior ----

    [Fact]
    public void Resolve_AircraftAtNode_ReturnsEmptyPlan()
    {
        var a = MakeNode(1, 37.0, -122.0);
        var b = MakeNode(2, 37.0, -121.9);
        var layout = MakeLayout([a, b], [MakeEdge(a, b)]);

        var plan = TaxiIngressResolver.Resolve(layout, a, a.Latitude, a.Longitude);

        Assert.Empty(plan.Segments);
        Assert.False(plan.ReplaceFirstRouteSegment);
    }

    [Fact]
    public void Resolve_AircraftWithinThreshold_ReturnsEmptyPlan()
    {
        // Aircraft 5 ft north of the node (below the 10 ft threshold).
        var a = MakeNode(1, 37.0, -122.0);
        var b = MakeNode(2, 37.0, -121.9);
        var layout = MakeLayout([a, b], [MakeEdge(a, b)]);
        var (acLat, acLon) = GeoMath.ProjectPoint(a.Latitude, a.Longitude, new TrueHeading(0.0), 5.0 / GeoMath.FeetPerNm);

        var plan = TaxiIngressResolver.Resolve(layout, a, acLat, acLon);

        Assert.Empty(plan.Segments);
    }

    // ---- Straight ingress ----

    [Fact]
    public void Resolve_AircraftOffGraph_NoFirstSegmentHint_ReturnsStraightIngressToNearestNode()
    {
        var a = MakeNode(1, 37.0, -122.0);
        var b = MakeNode(2, 37.0, -121.9);
        var layout = MakeLayout([a, b], [MakeEdge(a, b)]);
        // Aircraft 35 ft north of A.
        var (acLat, acLon) = GeoMath.ProjectPoint(a.Latitude, a.Longitude, new TrueHeading(0.0), 35.0 / GeoMath.FeetPerNm);

        var plan = TaxiIngressResolver.Resolve(layout, a, acLat, acLon);

        Assert.Single(plan.Segments);
        Assert.False(plan.ReplaceFirstRouteSegment);
        Assert.Equal(a.Id, plan.Segments[0].Edge.ToNode.Id);
        Assert.Equal("ingress", plan.Segments[0].TaxiwayName);
    }

    // ---- Mid-edge ingress ----

    [Fact]
    public void Resolve_AircraftAbeamFirstRouteEdge_ReturnsMidEdgeIngress_WithReplaceFlag()
    {
        // Two-node graph. Aircraft is abeam the midpoint of edge A-B, 35 ft
        // north (segment goes east). Route's first segment is (A, B) via X.
        var a = MakeNode(1, 37.0, -122.0);
        var (bLat, bLon) = GeoMath.ProjectPoint(a.Latitude, a.Longitude, new TrueHeading(90.0), 1000.0 / GeoMath.FeetPerNm);
        var b = MakeNode(2, bLat, bLon);
        var layout = MakeLayout([a, b], [MakeEdge(a, b, "X")]);

        // Aircraft 35 ft north of segment midpoint.
        var (midLat, midLon) = GeoMath.ProjectPoint(a.Latitude, a.Longitude, new TrueHeading(90.0), 500.0 / GeoMath.FeetPerNm);
        var (acLat, acLon) = GeoMath.ProjectPoint(midLat, midLon, new TrueHeading(0.0), 35.0 / GeoMath.FeetPerNm);

        var firstSeg = MakeRouteSegment(a, b, "X");
        var plan = TaxiIngressResolver.Resolve(layout, a, acLat, acLon, firstSeg);

        output.WriteLine($"plan: segments={plan.Segments.Count} replace={plan.ReplaceFirstRouteSegment}");
        Assert.Equal(2, plan.Segments.Count);
        Assert.True(plan.ReplaceFirstRouteSegment);
        Assert.Equal("ingress", plan.Segments[0].TaxiwayName);
        Assert.Equal("X", plan.Segments[1].TaxiwayName);
        Assert.Equal(b.Id, plan.Segments[1].Edge.ToNode.Id);
    }

    // ---- Edge-crossing rejection ----

    [Fact]
    public void Resolve_IngressLineCrossesAnotherEdge_FallsBackToEmpty()
    {
        // Layout: A at origin, B 1000 ft east of A (the "target" taxiway).
        // Also a perpendicular edge C-D running north-south, between
        // aircraft and A.
        var a = MakeNode(1, 37.0, -122.0);
        var (bLat, bLon) = GeoMath.ProjectPoint(a.Latitude, a.Longitude, new TrueHeading(90.0), 1000.0 / GeoMath.FeetPerNm);
        var b = MakeNode(2, bLat, bLon);
        // Aircraft 50 ft north of A.
        var (acLat, acLon) = GeoMath.ProjectPoint(a.Latitude, a.Longitude, new TrueHeading(0.0), 50.0 / GeoMath.FeetPerNm);
        // Blocking edge: from (25 ft west of midpoint between A and aircraft, 25 ft east of ...)
        var (cLat, cLon) = GeoMath.ProjectPoint(a.Latitude, a.Longitude, new TrueHeading(0.0), 25.0 / GeoMath.FeetPerNm);
        var (dLatEast, dLonEast) = GeoMath.ProjectPoint(cLat, cLon, new TrueHeading(90.0), 100.0 / GeoMath.FeetPerNm);
        var (cLatWest, cLonWest) = GeoMath.ProjectPoint(cLat, cLon, new TrueHeading(270.0), 100.0 / GeoMath.FeetPerNm);
        var c = MakeNode(3, cLatWest, cLonWest);
        var d = MakeNode(4, dLatEast, dLonEast);
        var layout = MakeLayout([a, b, c, d], [MakeEdge(a, b, "X"), MakeEdge(c, d, "Y")]);

        var plan = TaxiIngressResolver.Resolve(layout, a, acLat, acLon);

        output.WriteLine($"blocked: segments={plan.Segments.Count} replace={plan.ReplaceFirstRouteSegment}");
        Assert.Empty(plan.Segments);
    }

    // ---- Apply: route mutation ----

    [Fact]
    public void Apply_StraightIngressPlan_PrependsSegmentLeavingFirstIntact()
    {
        var a = MakeNode(1, 37.0, -122.0);
        var b = MakeNode(2, 37.0, -121.9);
        var originalFirst = MakeRouteSegment(a, b, "X");
        var route = new TaxiRoute { Segments = [originalFirst], HoldShortPoints = [] };

        var virtualAc = VirtualNode.Create(37.001, -122.001);
        var ingressSeg = VirtualNode.CreateSegment(virtualAc, a, "ingress");
        var plan = new IngressPlan([ingressSeg], ReplaceFirstRouteSegment: false);

        TaxiIngressResolver.Apply(route, plan);

        Assert.Equal(2, route.Segments.Count);
        Assert.Equal(ingressSeg, route.Segments[0]);
        Assert.Equal(originalFirst, route.Segments[1]);
    }

    [Fact]
    public void Apply_MidEdgePlan_ReplacesFirstRouteSegment()
    {
        var a = MakeNode(1, 37.0, -122.0);
        var b = MakeNode(2, 37.0, -121.9);
        var originalFirst = MakeRouteSegment(a, b, "X");
        var route = new TaxiRoute { Segments = [originalFirst], HoldShortPoints = [] };

        var virtualAc = VirtualNode.Create(37.001, -122.001);
        var virtualFoot = VirtualNode.Create(37.0, -121.95);
        var seg1 = VirtualNode.CreateSegment(virtualAc, virtualFoot, "ingress");
        var seg2 = VirtualNode.CreateSegment(virtualFoot, b, "X");
        var plan = new IngressPlan([seg1, seg2], ReplaceFirstRouteSegment: true);

        TaxiIngressResolver.Apply(route, plan);

        Assert.Equal(2, route.Segments.Count);
        Assert.Equal(seg1, route.Segments[0]);
        Assert.Equal(seg2, route.Segments[1]);
        // originalFirst was replaced by seg2.
    }
}
