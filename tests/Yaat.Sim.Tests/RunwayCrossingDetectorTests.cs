using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Simulation;
using Yaat.Sim.Soak;
using Yaat.Sim.Testing;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests;

public class RunwayCrossingDetectorTests(ITestOutputHelper output)
{
    private const double FeetPerNm = GeoMath.FeetPerNm;

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>Build a simple N-S runway (heading ~0) starting at the given point, ~1nm long.</summary>
    private static GeoJsonParser.RunwayFeature NorthSouthRunway(double startLat = 37.0, double startLon = -122.0)
    {
        double endLat = startLat + 1.0 / 60.0; // ~1nm north
        return new GeoJsonParser.RunwayFeature("18/36", [(startLat, startLon), (endLat, startLon)]);
    }

    /// <summary>Build a 45-degree heading runway starting at the given point, ~1nm long.</summary>
    private static GeoJsonParser.RunwayFeature DiagonalRunway(double startLat = 37.0, double startLon = -122.0)
    {
        // Project ~1nm at 45 degrees
        (double endLat, double endLon) = GeoMath.ProjectPoint(startLat, startLon, new TrueHeading(45.0), 1.0);
        return new GeoJsonParser.RunwayFeature("4/22", [(startLat, startLon), (endLat, endLon)]);
    }

    private static AirportGroundLayout EmptyLayout() => new() { AirportId = "TEST" };

    /// <summary>Seats <paramref name="rwy"/>'s bars as the only runway of <paramref name="layout"/>, with no navdata for it.</summary>
    private static RunwayCrossingResult DetectAlone(
        GeoJsonParser.RunwayFeature rwy,
        AirportGroundLayout layout,
        CoordinateIndex coordIndex,
        ref int nextNodeId
    )
    {
        RunwayPlan plan = RunwayCrossingDetector.PlanRunway(rwy, layout.AirportId, null);
        return RunwayCrossingDetector.DetectRunwayCrossings(plan, [plan], layout, coordIndex, videoMap: null, ref nextNodeId);
    }

    /// <summary>The parse-time rectangle of <paramref name="rwy"/> as the detector builds it with no navdata for the runway.</summary>
    private static RunwayRectangle RectWithoutNavData(GeoJsonParser.RunwayFeature rwy, double widthFt, RunwayIdentifier combinedId)
    {
        HoldShortStandoffResult standoff = RunwayCrossingDetector.ResolveStandoff(rwy, RunwayStandoffInputs.WithoutNavData(widthFt));
        return RunwayCrossingDetector.BuildRunwayRectangle(rwy, widthFt, standoff.Ft, combinedId);
    }

    private static GroundNode MakeNode(int id, double lat, double lon, GroundNodeType type = GroundNodeType.TaxiwayIntersection)
    {
        return new GroundNode
        {
            Id = id,
            Position = new LatLon(lat, lon),
            Type = type,
        };
    }

    private static GroundEdge MakeEdge(AirportGroundLayout layout, int from, int to, string taxiway, double dist = 0.1)
    {
        return new GroundEdge
        {
            Nodes = [layout.Nodes[from], layout.Nodes[to]],
            TaxiwayName = taxiway,
            DistanceNm = dist,
        };
    }

    private static void WireEdge(AirportGroundLayout layout, GroundEdge edge)
    {
        layout.Edges.Add(edge);
        layout.RebuildAdjacencyLists();
    }

    // -------------------------------------------------------------------------
    // IsOnRunway — diagonal runway cross-track classification
    // -------------------------------------------------------------------------

    [Fact]
    public void IsOnRunway_CenterlinePoint_ReturnsTrue()
    {
        GeoJsonParser.RunwayFeature rwy = DiagonalRunway();
        RunwayRectangle rect = RectWithoutNavData(rwy, 150.0, RunwayIdentifier.Parse("4/22"));

        // Midpoint of the runway is on the centerline
        double midLat = (rwy.Coords[0].Lat + rwy.Coords[1].Lat) / 2.0;
        double midLon = (rwy.Coords[0].Lon + rwy.Coords[1].Lon) / 2.0;

        Assert.True(RunwayCrossingDetector.IsOnRunway(midLat, midLon, rect));
    }

    [Fact]
    public void IsOnRunway_DiagonalRunway_PointFarOffCenterline_ReturnsFalse()
    {
        GeoJsonParser.RunwayFeature rwy = DiagonalRunway();
        RunwayRectangle rect = RectWithoutNavData(rwy, 150.0, RunwayIdentifier.Parse("4/22"));

        // Point well to the side of the diagonal runway (~0.01 degrees offset perpendicular)
        double midLat = (rwy.Coords[0].Lat + rwy.Coords[1].Lat) / 2.0;
        double midLon = (rwy.Coords[0].Lon + rwy.Coords[1].Lon) / 2.0;

        // Offset perpendicular to 45° heading (i.e., at 135°) by ~500ft
        (double offLat, double offLon) = GeoMath.ProjectPoint(midLat, midLon, new TrueHeading(135.0), 500.0 / FeetPerNm);

        Assert.False(RunwayCrossingDetector.IsOnRunway(offLat, offLon, rect));
    }

    [Fact]
    public void IsOnRunway_DiagonalRunway_PointBeyondEnd_ReturnsFalse()
    {
        GeoJsonParser.RunwayFeature rwy = DiagonalRunway();
        RunwayRectangle rect = RectWithoutNavData(rwy, 150.0, RunwayIdentifier.Parse("4/22"));

        // Project past the far end
        (double beyondLat, double beyondLon) = GeoMath.ProjectPoint(rwy.Coords[1].Lat, rwy.Coords[1].Lon, new TrueHeading(45.0), 0.1);

        Assert.False(RunwayCrossingDetector.IsOnRunway(beyondLat, beyondLon, rect));
    }

    [Fact]
    public void IsOnRunway_NorthSouthRunway_PointSlightlyOffCenter_ReturnsTrue()
    {
        GeoJsonParser.RunwayFeature rwy = NorthSouthRunway();
        RunwayRectangle rect = RectWithoutNavData(rwy, 150.0, RunwayIdentifier.Parse("18/36"));

        // 50ft east of centerline (within 75ft half-width)
        double midLat = (rwy.Coords[0].Lat + rwy.Coords[1].Lat) / 2.0;
        (double offLat, double offLon) = GeoMath.ProjectPoint(midLat, rwy.Coords[0].Lon, new TrueHeading(90.0), 50.0 / FeetPerNm);

        Assert.True(RunwayCrossingDetector.IsOnRunway(offLat, offLon, rect));
    }

    // -------------------------------------------------------------------------
    // BuildRunwayRectangle — width-based hold-short distance
    // -------------------------------------------------------------------------

    // FAA AC 150/5300-13B Table 3-2 by HoldShortStandoff's rule, with no navdata: the length counts as at least 5,000 ft,
    // no ILS, elevation 0.
    [Theory]
    [InlineData(60.0, 125.0)] // < 75     → 125 ft  (ADG I/II)
    [InlineData(74.0, 125.0)]
    [InlineData(75.0, 150.0)] // 75-99    → 150 ft  (ADG II/III)
    [InlineData(99.0, 150.0)]
    [InlineData(100.0, 250.0)] // 100-149  → 250 ft  (L >= 5,000 ft when unknown)
    [InlineData(149.0, 250.0)]
    [InlineData(150.0, 250.0)] // 150-199  → 250 ft  (ADG IV/V)
    [InlineData(199.0, 250.0)]
    [InlineData(200.0, 280.0)] // >= 200   → 280 ft  (ADG V/VI / CAT III)
    [InlineData(250.0, 280.0)]
    public void BuildRunwayRectangle_HoldShortDistance_MatchesWidthCategory(double widthFt, double expectedHoldShortFt)
    {
        GeoJsonParser.RunwayFeature rwy = NorthSouthRunway();
        RunwayRectangle rect = RectWithoutNavData(rwy, widthFt, RunwayIdentifier.Parse("18/36"));

        double actualHoldShortFt = rect.HoldShortNm * FeetPerNm;
        Assert.Equal(expectedHoldShortFt, actualHoldShortFt, precision: 0);
    }

    [Fact]
    public void BuildRunwayRectangle_HalfWidth_DerivedFromWidthFt()
    {
        GeoJsonParser.RunwayFeature rwy = NorthSouthRunway();
        RunwayRectangle rect = RectWithoutNavData(rwy, 200.0, RunwayIdentifier.Parse("18/36"));

        double expectedHalfWidthFt = 100.0;
        double actualHalfWidthFt = rect.HalfWidthNm * FeetPerNm;
        Assert.Equal(expectedHalfWidthFt, actualHalfWidthFt, precision: 0);
    }

    // -------------------------------------------------------------------------
    // DetectRunwayCrossings — edge splitting
    // -------------------------------------------------------------------------

    [Fact]
    public void DetectRunwayCrossings_BoundaryEdge_SplitsIntoTwoEdges()
    {
        GeoJsonParser.RunwayFeature rwy = NorthSouthRunway();
        AirportGroundLayout layout = EmptyLayout();

        // Node 1: on the runway centerline (midpoint)
        double midLat = (rwy.Coords[0].Lat + rwy.Coords[1].Lat) / 2.0;
        GroundNode onNode = MakeNode(1, midLat, rwy.Coords[0].Lon);

        // Node 2: well off the runway (~1000ft east)
        (double offLat, double offLon) = GeoMath.ProjectPoint(midLat, rwy.Coords[0].Lon, new TrueHeading(90.0), 1000.0 / FeetPerNm);
        GroundNode offNode = MakeNode(2, offLat, offLon);

        layout.Nodes[1] = onNode;
        layout.Nodes[2] = offNode;

        GroundEdge edge = MakeEdge(layout, 1, 2, "A", GeoMath.DistanceNm(onNode.Position, offNode.Position));
        WireEdge(layout, edge);
        layout.RebuildAdjacencyLists();

        int nextNodeId = 100;
        var coordIndex = new CoordinateIndex(0.0001);
        coordIndex.Add(onNode.Position, 1);
        coordIndex.Add(offNode.Position, 2);

        DetectAlone(rwy, layout, coordIndex, ref nextNodeId);

        // Original edge should be removed, replaced by 2 new edges through the HS node
        Assert.DoesNotContain(edge, layout.Edges);
        // Should have 2 taxiway edges (the split) plus possibly RWY centerline edges
        int taxiEdges = layout.Edges.Count(e => e.TaxiwayName == "A");
        Assert.Equal(2, taxiEdges);

        // A new hold-short node should have been created
        var hsNodes = layout.Nodes.Values.Where(n => n.Type == GroundNodeType.RunwayHoldShort).ToList();
        Assert.Single(hsNodes);
        Assert.Equal(100, hsNodes[0].Id);
    }

    [Fact]
    public void DetectRunwayCrossings_BoundaryEdge_NewNodeHasCorrectRunwayId()
    {
        GeoJsonParser.RunwayFeature rwy = NorthSouthRunway();
        AirportGroundLayout layout = EmptyLayout();

        double midLat = (rwy.Coords[0].Lat + rwy.Coords[1].Lat) / 2.0;
        GroundNode onNode = MakeNode(1, midLat, rwy.Coords[0].Lon);
        (double offLat, double offLon) = GeoMath.ProjectPoint(midLat, rwy.Coords[0].Lon, new TrueHeading(90.0), 1000.0 / FeetPerNm);
        GroundNode offNode = MakeNode(2, offLat, offLon);

        layout.Nodes[1] = onNode;
        layout.Nodes[2] = offNode;
        WireEdge(layout, MakeEdge(layout, 1, 2, "A", GeoMath.DistanceNm(onNode.Position, offNode.Position)));
        layout.RebuildAdjacencyLists();

        int nextNodeId = 100;
        var coordIndex = new CoordinateIndex(0.0001);
        coordIndex.Add(onNode.Position, 1);
        coordIndex.Add(offNode.Position, 2);

        DetectAlone(rwy, layout, coordIndex, ref nextNodeId);

        GroundNode hsNode = layout.Nodes.Values.First(n => n.Type == GroundNodeType.RunwayHoldShort);
        Assert.NotNull(hsNode.RunwayId);
        Assert.Equal(RunwayIdentifier.Parse("18/36"), hsNode.RunwayId.Value);
    }

    // -------------------------------------------------------------------------
    // DetectRunwayCrossings — node reuse within the snap tolerance (5 ft)
    // -------------------------------------------------------------------------

    [Fact]
    public void DetectRunwayCrossings_OffNodeNearHoldShortDistance_ReusesExistingNode()
    {
        GeoJsonParser.RunwayFeature rwy = NorthSouthRunway();
        AirportGroundLayout layout = EmptyLayout();

        // For 150ft-wide runway (default), FAA Table 3-2 gives 250ft hold-short from centerline.
        double holdShortFt = 250.0;

        double midLat = (rwy.Coords[0].Lat + rwy.Coords[1].Lat) / 2.0;
        GroundNode onNode = MakeNode(1, midLat, rwy.Coords[0].Lon);

        // Place the off-node within the 5 ft snap tolerance of the ideal (3 ft short, at 247 ft) so
        // it is upgraded in place instead of minting a near-coincident node the fillet merge collapses.
        (double offLat, double offLon) = GeoMath.ProjectPoint(midLat, rwy.Coords[0].Lon, new TrueHeading(90.0), (holdShortFt - 3.0) / FeetPerNm);
        GroundNode offNode = MakeNode(2, offLat, offLon);

        layout.Nodes[1] = onNode;
        layout.Nodes[2] = offNode;
        WireEdge(layout, MakeEdge(layout, 1, 2, "A", GeoMath.DistanceNm(onNode.Position, offNode.Position)));
        layout.RebuildAdjacencyLists();

        int nextNodeId = 100;
        var coordIndex = new CoordinateIndex(0.0001);
        coordIndex.Add(onNode.Position, 1);
        coordIndex.Add(offNode.Position, 2);

        DetectAlone(rwy, layout, coordIndex, ref nextNodeId);

        // No new node should be created — node 2 should be upgraded in place
        Assert.Equal(100, nextNodeId); // unchanged
        Assert.Equal(GroundNodeType.RunwayHoldShort, layout.Nodes[2].Type);
        Assert.NotNull(layout.Nodes[2].RunwayId);
    }

    /// <summary>
    /// When an off-node sits inside the hold-short band (10 ft short of the 250 ft ideal) but the
    /// taxiway continues past the ideal, the detector must mint the hold-short at exactly the ideal
    /// standoff on the continuation segment — NOT snap it back to the closer shape-point. This is the
    /// core of the angle-independence fix: an acute exit's first shape point often lands short, and a
    /// generous reuse tolerance would pull the hold inside the runway safety area.
    /// </summary>
    [Fact]
    public void DetectRunwayCrossings_OffNodeInsideBandWithContinuation_PlacesNodeAtIdeal()
    {
        GeoJsonParser.RunwayFeature rwy = NorthSouthRunway();
        AirportGroundLayout layout = EmptyLayout();

        double holdShortFt = 250.0; // 150 ft-wide runway → FAA Table 3-2 250 ft
        double midLat = (rwy.Coords[0].Lat + rwy.Coords[1].Lat) / 2.0;
        GroundNode onNode = MakeNode(1, midLat, rwy.Coords[0].Lon);

        // Node 2 is 10 ft short of the ideal (inside the band, outside the 5 ft snap tolerance);
        // node 3 continues to 320 ft (past the ideal), so the continuation segment straddles 250 ft.
        (double offLat, double offLon) = GeoMath.ProjectPoint(midLat, rwy.Coords[0].Lon, new TrueHeading(90.0), (holdShortFt - 10.0) / FeetPerNm);
        (double farLat, double farLon) = GeoMath.ProjectPoint(midLat, rwy.Coords[0].Lon, new TrueHeading(90.0), 320.0 / FeetPerNm);
        GroundNode offNode = MakeNode(2, offLat, offLon);
        GroundNode farNode = MakeNode(3, farLat, farLon);

        layout.Nodes[1] = onNode;
        layout.Nodes[2] = offNode;
        layout.Nodes[3] = farNode;
        WireEdge(layout, MakeEdge(layout, 1, 2, "A", GeoMath.DistanceNm(onNode.Position, offNode.Position)));
        WireEdge(layout, MakeEdge(layout, 2, 3, "A", GeoMath.DistanceNm(offNode.Position, farNode.Position)));
        layout.RebuildAdjacencyLists();

        int nextNodeId = 100;
        var coordIndex = new CoordinateIndex(0.0001);
        coordIndex.Add(onNode.Position, 1);
        coordIndex.Add(offNode.Position, 2);
        coordIndex.Add(farNode.Position, 3);

        DetectAlone(rwy, layout, coordIndex, ref nextNodeId);

        // A new hold-short node is minted at exactly the ideal — node 2 is NOT reused.
        Assert.Equal(GroundNodeType.TaxiwayIntersection, layout.Nodes[2].Type);
        GroundNode hs = layout.Nodes.Values.Single(n => n.Type == GroundNodeType.RunwayHoldShort);

        // Node lies due east of the centerline (same latitude), so distance from the centerline
        // point equals the cross-track standoff.
        double crossTrackFt = GeoMath.DistanceNm(new LatLon(midLat, rwy.Coords[0].Lon), hs.Position) * FeetPerNm;
        Assert.InRange(crossTrackFt, 248.0, 252.0);
    }

    [Fact]
    public void DetectRunwayCrossings_OffNodeFarFromHoldShort_CreatesNewNode()
    {
        GeoJsonParser.RunwayFeature rwy = NorthSouthRunway();
        AirportGroundLayout layout = EmptyLayout();

        double midLat = (rwy.Coords[0].Lat + rwy.Coords[1].Lat) / 2.0;
        GroundNode onNode = MakeNode(1, midLat, rwy.Coords[0].Lon);

        // Place off-node at 800ft from centerline (550ft away from ideal 250ft HS point — well beyond 50ft reuse)
        (double offLat, double offLon) = GeoMath.ProjectPoint(midLat, rwy.Coords[0].Lon, new TrueHeading(90.0), 800.0 / FeetPerNm);
        GroundNode offNode = MakeNode(2, offLat, offLon);

        layout.Nodes[1] = onNode;
        layout.Nodes[2] = offNode;
        WireEdge(layout, MakeEdge(layout, 1, 2, "A", GeoMath.DistanceNm(onNode.Position, offNode.Position)));
        layout.RebuildAdjacencyLists();

        int nextNodeId = 100;
        var coordIndex = new CoordinateIndex(0.0001);
        coordIndex.Add(onNode.Position, 1);
        coordIndex.Add(offNode.Position, 2);

        DetectAlone(rwy, layout, coordIndex, ref nextNodeId);

        // New node should be created at id 100
        Assert.Equal(101, nextNodeId);
        Assert.True(layout.Nodes.ContainsKey(100));
        Assert.Equal(GroundNodeType.RunwayHoldShort, layout.Nodes[100].Type);
        // Original off-node stays as TaxiwayIntersection
        Assert.Equal(GroundNodeType.TaxiwayIntersection, layout.Nodes[2].Type);
    }

    // -------------------------------------------------------------------------
    // DetectRunwayCrossings — interpolation fraction clamping
    // -------------------------------------------------------------------------

    [Fact]
    public void DetectRunwayCrossings_InterpolatedHsNode_BetweenOnAndOffNodes()
    {
        GeoJsonParser.RunwayFeature rwy = NorthSouthRunway();
        AirportGroundLayout layout = EmptyLayout();

        double midLat = (rwy.Coords[0].Lat + rwy.Coords[1].Lat) / 2.0;
        GroundNode onNode = MakeNode(1, midLat, rwy.Coords[0].Lon);

        // Off-node at 900ft east — HS should be interpolated at ~250ft (150ft-wide runway → 250ft HS per FAA Table 3-2)
        (double offLat, double offLon) = GeoMath.ProjectPoint(midLat, rwy.Coords[0].Lon, new TrueHeading(90.0), 900.0 / FeetPerNm);
        GroundNode offNode = MakeNode(2, offLat, offLon);

        layout.Nodes[1] = onNode;
        layout.Nodes[2] = offNode;
        WireEdge(layout, MakeEdge(layout, 1, 2, "A", GeoMath.DistanceNm(onNode.Position, offNode.Position)));
        layout.RebuildAdjacencyLists();

        int nextNodeId = 100;
        var coordIndex = new CoordinateIndex(0.0001);
        coordIndex.Add(onNode.Position, 1);
        coordIndex.Add(offNode.Position, 2);

        DetectAlone(rwy, layout, coordIndex, ref nextNodeId);

        GroundNode hsNode = layout.Nodes[100];
        // HS node should be between on-node and off-node (latitude should be same since E-W edge,
        // longitude should be between the two)
        double hsDistFromCenter = GeoMath.DistanceNm(new LatLon(midLat, rwy.Coords[0].Lon), hsNode.Position) * FeetPerNm;
        // Should be near the hold-short distance (250ft for 150ft-wide runway per FAA Table 3-2)
        Assert.InRange(hsDistFromCenter, 200.0, 300.0);
    }

    // -------------------------------------------------------------------------
    // DetectRunwayCrossings — RWY edges skip
    // -------------------------------------------------------------------------

    [Fact]
    public void DetectRunwayCrossings_RwyEdge_NotProcessed()
    {
        GeoJsonParser.RunwayFeature rwy = NorthSouthRunway();
        AirportGroundLayout layout = EmptyLayout();

        double midLat = (rwy.Coords[0].Lat + rwy.Coords[1].Lat) / 2.0;
        GroundNode onNode = MakeNode(1, midLat, rwy.Coords[0].Lon);
        (double offLat, double offLon) = GeoMath.ProjectPoint(midLat, rwy.Coords[0].Lon, new TrueHeading(90.0), 1000.0 / FeetPerNm);
        GroundNode offNode = MakeNode(2, offLat, offLon);

        layout.Nodes[1] = onNode;
        layout.Nodes[2] = offNode;

        // Edge named "RWY18/36" — should be skipped
        WireEdge(layout, MakeEdge(layout, 1, 2, "RWY18/36", 0.1));
        layout.RebuildAdjacencyLists();

        int nextNodeId = 100;
        var coordIndex = new CoordinateIndex(0.0001);

        DetectAlone(rwy, layout, coordIndex, ref nextNodeId);

        // No new nodes created
        Assert.Equal(100, nextNodeId);
        Assert.Equal(2, layout.Nodes.Count);
    }

    // -------------------------------------------------------------------------
    // DetectRunwayCrossings — both nodes on same side (both on or both off)
    // -------------------------------------------------------------------------

    [Fact]
    public void DetectRunwayCrossings_BothNodesOffRunway_NoSplit()
    {
        GeoJsonParser.RunwayFeature rwy = NorthSouthRunway();
        AirportGroundLayout layout = EmptyLayout();

        double midLat = (rwy.Coords[0].Lat + rwy.Coords[1].Lat) / 2.0;

        // Both nodes far east of runway
        (double lat1, double lon1) = GeoMath.ProjectPoint(midLat, rwy.Coords[0].Lon, new TrueHeading(90.0), 500.0 / FeetPerNm);
        (double lat2, double lon2) = GeoMath.ProjectPoint(midLat, rwy.Coords[0].Lon, new TrueHeading(90.0), 800.0 / FeetPerNm);
        GroundNode node1 = MakeNode(1, lat1, lon1);
        GroundNode node2 = MakeNode(2, lat2, lon2);

        layout.Nodes[1] = node1;
        layout.Nodes[2] = node2;
        WireEdge(layout, MakeEdge(layout, 1, 2, "A", 0.05));
        layout.RebuildAdjacencyLists();

        int nextNodeId = 100;
        var coordIndex = new CoordinateIndex(0.0001);

        DetectAlone(rwy, layout, coordIndex, ref nextNodeId);

        Assert.Equal(100, nextNodeId); // no new nodes
        Assert.Single(layout.Edges); // original edge untouched
    }

    // -------------------------------------------------------------------------
    // SFO E/28L: the generated bar sits at the rule's standoff
    // -------------------------------------------------------------------------

    /// <summary>
    /// SFO 28L is 200 ft wide, so its base separation S is 280 ft plus a foot per 100 ft of elevation, and E crosses it at
    /// θ ≈ 70°: the bar on E sits where E's centerline meets the hold line, S + 37.5 · |cos θ| ≈ 293 ft from the 28L
    /// centerline. Loads the real sfo.geojson; θ is read from the bar's own E segment, which runs straight through the
    /// hold line.
    /// </summary>
    [Fact]
    public void DetectRunwayCrossings_Sfo_TaxiwayE_AtRunway28L_SitsAtTheRuleStandoff()
    {
        TestVnasData.EnsureInitialized();
        string path = Path.Combine("TestData", "sfo.geojson");
        if (!File.Exists(path))
        {
            return; // silently skip if TestData missing
        }

        AirportGroundLayout layout = GeoJsonParser.Parse("KSFO", File.ReadAllText(path), "KSFO", new LayoutInputs([], VideoMap: null));

        var combinedId = RunwayIdentifier.Parse("10R/28L");
        GroundRunway rwy = layout.Runways.Single(r => RunwayIdentifier.Parse(r.Name).Equals(combinedId));
        RunwayRectangle rect = RunwayCrossingDetector.BuildRunwayRectangle(rwy);
        double baseFt = rwy.HoldShortStandoffFt!.Value;

        List<GroundNode> eBars =
        [
            .. layout
                .Nodes.Values.Where(n => (n.Type == GroundNodeType.RunwayHoldShort) && (n.RunwayId is { } id) && id.Equals(combinedId))
                .Where(n => n.Edges.Any(e => !e.IsRunwayCenterline && e.MatchesTaxiway("E"))),
        ];
        Assert.NotEmpty(eBars);

        foreach (GroundNode bar in eBars)
        {
            GroundEdge eSegment = bar.Edges.OfType<GroundEdge>().First(e => !e.IsRunwayCenterline && e.MatchesTaxiway("E"));
            double thetaDeg = GeoMath.BearingTo(eSegment.Nodes[0].Position, eSegment.Nodes[1].Position) - rect.TrueHeading.Degrees;
            double ruleFt = HoldShortStandoff.CenterlineStandoffFt(baseFt, 200.0, thetaDeg);
            double crossFt = CrossTrackFt(rect, bar);
            output.WriteLine($"E bar #{bar.Id}: {crossFt:F1} ft; θ {thetaDeg:F1}°, S {baseFt:F1}, rule {ruleFt:F1} ft");

            Assert.InRange(crossFt, ruleFt - 6.0, ruleFt + 6.0);
            Assert.InRange(crossFt, 285.0, 300.0);
        }
    }

    // -------------------------------------------------------------------------
    // Angled taxiways: the centerline meets the hold line farther out (OAK)
    // -------------------------------------------------------------------------

    /// <summary>
    /// The hold line is painted perpendicular to the taxiway centerline with its nearest point at the base separation S,
    /// so an angled taxiway's centerline meets it farther out, by half the proxy taxiway width times |cos θ|, θ taken at
    /// the hold line. Loads the real oak.geojson and checks the published results: 28R/10L J ≈ 279 and P ≈ 273 ft,
    /// 28L/10R B ≈ 276 and P ≈ 274 ft, 30/12 W3, W4 and W5 ≈ 311 ft (each ±8 ft), while the perpendicular bars (B's
    /// second crossing of 28L/10R; 30/12 W1, W2, W6 and W7) stay within 6 ft of S. Each bar on a taxiway matches one of
    /// that runway and taxiway's rows, and each row has a bar.
    /// </summary>
    [Fact]
    public void DetectRunwayCrossings_Oak_AngledHoldShortsSitOutByTheAngleTerm()
    {
        TestVnasData.EnsureInitialized();
        string path = Path.Combine("TestData", "oak.geojson");
        if (!File.Exists(path))
        {
            return; // silently skip if TestData missing
        }

        AirportGroundLayout layout = GeoJsonParser.Parse("KOAK", File.ReadAllText(path), "KOAK", new LayoutInputs([], VideoMap: null));

        PublishedBar[] rows =
        [
            new("28R/10L", "J", 279.0, 8.0),
            new("28R/10L", "P", 273.0, 8.0),
            new("28L/10R", "B", 276.0, 8.0),
            new("28L/10R", "B", null, 6.0),
            new("28L/10R", "P", 274.0, 8.0),
            new("30/12", "W3", 311.0, 8.0),
            new("30/12", "W4", 311.0, 8.0),
            new("30/12", "W5", 311.0, 8.0),
            new("30/12", "W1", null, 6.0),
            new("30/12", "W2", null, 6.0),
            new("30/12", "W6", null, 6.0),
            new("30/12", "W7", null, 6.0),
        ];

        var misses = new List<string>();
        foreach (IGrouping<(string Runway, string Taxiway), PublishedBar> group in rows.GroupBy(row => (row.Runway, row.Taxiway)))
        {
            (string runwayName, string taxiway) = group.Key;
            var combinedId = RunwayIdentifier.Parse(runwayName);
            GroundRunway runway = layout.Runways.Single(r => RunwayIdentifier.Parse(r.Name).Equals(combinedId));
            RunwayRectangle rect = RunwayCrossingDetector.BuildRunwayRectangle(runway);
            double baseFt = runway.HoldShortStandoffFt!.Value;
            List<(double WantFt, double ToleranceFt)> wants = [.. group.Select(row => (row.ExpectedFt ?? baseFt, row.ToleranceFt))];
            List<double> barFt =
            [
                .. layout
                    .Nodes.Values.Where(node =>
                        (node.Type == GroundNodeType.RunwayHoldShort)
                        && (node.RunwayId is { } id)
                        && id.Equals(combinedId)
                        && node.Edges.Any(edge => !edge.IsRunwayCenterline && edge.MatchesTaxiway(taxiway))
                    )
                    .Select(node => CrossTrackFt(rect, node)),
            ];
            output.WriteLine($"{runwayName} {taxiway} bars: [{string.Join(", ", barFt.Select(ft => $"{ft:F1}"))}] ft");
            misses.AddRange(
                barFt
                    .Where(ft => !wants.Any(want => Math.Abs(ft - want.WantFt) <= want.ToleranceFt))
                    .Select(ft => $"{runwayName} {taxiway}: bar at {ft:F1} ft matches no row")
            );
            misses.AddRange(
                wants
                    .Where(want => !barFt.Any(ft => Math.Abs(ft - want.WantFt) <= want.ToleranceFt))
                    .Select(want => $"{runwayName} {taxiway}: no bar at {want.WantFt:F1} ± {want.ToleranceFt:F0} ft")
            );
        }

        Assert.True(misses.Count == 0, string.Join(Environment.NewLine, misses));
    }

    /// <summary>A published bar expectation: its runway and taxiway, the cross-track (null for the runway's S) and the band.</summary>
    private sealed record PublishedBar(string Runway, string Taxiway, double? ExpectedFt, double ToleranceFt);

    // -------------------------------------------------------------------------
    // Authoritative GeoJSON holdShortDistance (per-runway) overrides the width heuristic
    // -------------------------------------------------------------------------

    /// <summary>
    /// The vNAS airport map carries an explicit per-runway <c>holdShortDistance</c> (ft from centerline) for some runways.
    /// The rule overrides it when it is more than 15 ft below the rule value on a runway in the 250 ft class: at OAK,
    /// 28R/10L authors 225 ft against the rule's 250 + E/100 (150 ft wide, ILS), and 30/12 authors none and takes the
    /// rule's 280 + E/100 (CAT III).
    /// </summary>
    [Fact]
    public void BuildRunwayRectangle_Oak_UsesTheRuleOverAStaleAuthoredValue_ElseTheRule()
    {
        TestVnasData.EnsureInitialized();
        string path = Path.Combine("TestData", "oak.geojson");
        if (!File.Exists(path))
        {
            return; // silently skip if TestData missing
        }

        AirportGroundLayout layout = GeoJsonParser.Parse("KOAK", File.ReadAllText(path), "KOAK", new LayoutInputs([], VideoMap: null));

        GroundRunway rwy28R = layout.Runways.Single(r => RunwayIdentifier.Parse(r.Name).Equals(RunwayIdentifier.Parse("28R/10L")));
        double hs28R = RunwayCrossingDetector.BuildRunwayRectangle(rwy28R).HoldShortNm * FeetPerNm;
        Assert.Equal(250.0 + (HigherThresholdElevationFt("KOAK", "28R") / 100.0), hs28R, precision: 6); // rule over the authored 225

        GroundRunway rwy30 = layout.Runways.Single(r => RunwayIdentifier.Parse(r.Name).Equals(RunwayIdentifier.Parse("30/12")));
        double hs30 = RunwayCrossingDetector.BuildRunwayRectangle(rwy30).HoldShortNm * FeetPerNm;
        Assert.Equal(280.0 + (HigherThresholdElevationFt("KOAK", "30") / 100.0), hs30, precision: 6); // no authored value → rule (CAT III)
    }

    /// <summary>
    /// Each OAK runway's resolved standoff S, as the parse stores it on <see cref="GroundRunway.HoldShortStandoffFt"/>:
    /// 28R/10L the rule over the authored 225 and 30/12 the CAT III row, both with a foot per 100 ft of the higher
    /// threshold elevation; 28L/10R its authored 250, within 15 ft of the rule's 250 + E/100; 15/33 its authored 175,
    /// below the 250 ft class.
    /// </summary>
    [Theory]
    [InlineData("28R/10L", 250.0, true)]
    [InlineData("30/12", 280.0, true)]
    [InlineData("28L/10R", 250.0, false)]
    [InlineData("15/33", 175.0, false)]
    public void DetectRunwayCrossings_Oak_StoresTheResolvedStandoff(string runwayName, double baseFt, bool addsElevation)
    {
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("OAK");
        if (layout is null)
        {
            return;
        }

        var id = RunwayIdentifier.Parse(runwayName);
        GroundRunway runway = layout.Runways.Single(r => RunwayIdentifier.Parse(r.Name).Equals(id));
        double expectedFt = addsElevation ? baseFt + (HigherThresholdElevationFt("OAK", id.End1) / 100.0) : baseFt;

        Assert.NotNull(runway.HoldShortStandoffFt);
        Assert.Equal(expectedFt, runway.HoldShortStandoffFt.Value, precision: 6);
        Assert.Equal(expectedFt, RunwayCrossingDetector.BuildRunwayRectangle(runway).HoldShortNm * FeetPerNm, precision: 6);
    }

    /// <summary>OAK 28R/10L's authored 225 ft loses to the rule, and the parse logs one data warning naming both values.</summary>
    [Fact]
    public void DetectRunwayCrossings_Oak28R_RuleOverAuthored_LogsAWarning()
    {
        TestVnasData.EnsureInitialized();
        string path = Path.Combine("TestData", "oak.geojson");
        if (!File.Exists(path))
        {
            return;
        }

        using var tap = new CapturingSimLogProvider(LogLevel.Debug, 1000);
        SimLogBuilder.CreateForTest(output).CaptureInto(tap).InitializeSimLog();

        GeoJsonParser.Parse("oak", File.ReadAllText(path), "OAK", new LayoutInputs([], VideoMap: null));

        List<string> warnings = [.. tap.Drain().Where(r => r.Category == "HoldShortStandoff").Select(r => r.Message)];
        string warning = Assert.Single(warnings);
        Assert.Contains("28R - 10L", warning, StringComparison.Ordinal);
        Assert.Contains("225", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void GroundRunway_HoldShortStandoff_SurvivesAJsonRoundTrip()
    {
        GroundRunway runway = new()
        {
            Name = "28R - 10L",
            Coordinates = [(37.70, -122.22), (37.72, -122.20)],
            WidthFt = 150.0,
            HoldShortStandoffFt = 250.06,
        };

        string json = JsonSerializer.Serialize(runway, GroundLayoutSerializer.Options);
        GroundRunway read = JsonSerializer.Deserialize<GroundRunway>(json, GroundLayoutSerializer.Options)!;

        Assert.Equal(250.06, read.HoldShortStandoffFt);
    }

    [Fact]
    public void GroundRunway_NullHoldShortStandoff_IsAbsentFromTheJson()
    {
        GroundRunway runway = new()
        {
            Name = "28R - 10L",
            Coordinates = [(37.70, -122.22), (37.72, -122.20)],
            WidthFt = 150.0,
        };

        JsonObject json = JsonSerializer.SerializeToNode(runway, GroundLayoutSerializer.Options)!.AsObject();

        Assert.DoesNotContain(json, property => property.Key.Equals(nameof(GroundRunway.HoldShortStandoffFt), StringComparison.OrdinalIgnoreCase));
        Assert.Null(JsonSerializer.Deserialize<GroundRunway>(json, GroundLayoutSerializer.Options)!.HoldShortStandoffFt);
    }

    /// <summary>The higher of the two threshold elevations of the runway <paramref name="runwayEnd"/> belongs to, from navdata.</summary>
    private static double HigherThresholdElevationFt(string airport, string runwayEnd)
    {
        TestVnasData.EnsureInitialized();
        RunwayInfo runway = NavigationDatabase.Instance.GetRunway(airport, runwayEnd)!;
        return Math.Max(runway.Elevation1Ft, runway.Elevation2Ft);
    }

    // -------------------------------------------------------------------------
    // Dead-end fallback at a junction with a taxiway that carries the runway's own bar
    // -------------------------------------------------------------------------

    /// <summary>
    /// A branch taxiway that ends at another taxiway before the runway's holding distance gets no bar of its own when the
    /// joining taxiway meets the same runway: the two share the joining taxiway's marking (OAK P and J share J's 28R
    /// marking; MIA T8 joins S, which carries its own 12/30 bar).
    /// </summary>
    [Theory]
    [InlineData("OAK", "28R", "P", "J")]
    [InlineData("MIA", "12", "T8", "S")]
    public void DetectRunwayCrossings_BranchEndingAtATaxiwayWithItsOwnBar_GetsNoBar(string airport, string runwayEnd, string branch, string joining)
    {
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout(airport);
        if (layout is null)
        {
            return;
        }

        List<GroundNode> bars = RunwayBars(layout, runwayEnd);
        GroundRunway runway = layout.Runways.Single(r => RunwayIdentifier.Parse(r.Name).Contains(runwayEnd));
        RunwayRectangle rect = RunwayCrossingDetector.BuildRunwayRectangle(runway);
        double shortOfFt = (rect.HoldShortNm * FeetPerNm) - AirportGroundLayout.HoldingDistanceToleranceFt;

        Assert.DoesNotContain(bars, bar => bar.Edges.Any(edge => edge.MatchesTaxiway(branch)) && (CrossTrackFt(rect, bar) < shortOfFt));
        Assert.Contains(bars, bar => bar.Edges.Any(edge => edge.MatchesTaxiway(joining)) && (CrossTrackFt(rect, bar) >= shortOfFt));
    }

    /// <summary>
    /// A branch taxiway that dead-ends with no joining taxiway meeting the runway keeps its fallback bar short of the
    /// holding distance: COS B1 runs into runway 13/31, and its 17R/35L bar is the farthest point it can hold.
    /// </summary>
    [Fact]
    public void DetectRunwayCrossings_Cos_B1_KeepsItsShortFallbackBar()
    {
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("COS");
        if (layout is null)
        {
            return;
        }

        GroundRunway runway = layout.Runways.Single(r => RunwayIdentifier.Parse(r.Name).Contains("17R"));
        RunwayRectangle rect = RunwayCrossingDetector.BuildRunwayRectangle(runway);
        double shortOfFt = (rect.HoldShortNm * FeetPerNm) - AirportGroundLayout.HoldingDistanceToleranceFt;
        List<GroundNode> b1Bars = [.. RunwayBars(layout, "17R").Where(bar => bar.Edges.Any(edge => edge.MatchesTaxiway("B1")))];

        Assert.Contains(b1Bars, bar => CrossTrackFt(rect, bar) < shortOfFt);
    }

    /// <summary>
    /// A branch taxiway that ends at a junction keeps its fallback bar when a route out of the junction leaves the
    /// runway's holding area without crossing another bar of the runway within the holding distance: ATL R3/R7/R11 end
    /// at R, which runs along 09R/27L inside its holding distance (R7's N6 end reaches a bar only ~590 ft on); ATL C and
    /// A4 end at A.
    /// </summary>
    [Theory]
    [InlineData("ATL", "09R", "R3", 2)]
    [InlineData("ATL", "09R", "R7", 2)]
    [InlineData("ATL", "09R", "R11", 2)]
    [InlineData("ATL", "08L", "C", 2)]
    [InlineData("ATL", "08L", "A4", 1)]
    public void DetectRunwayCrossings_BranchEndingWhereARouteLeavesTheHoldingArea_KeepsItsBar(
        string airport,
        string runwayEnd,
        string branch,
        int expectedBars
    )
    {
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout(airport);
        if (layout is null)
        {
            return;
        }

        int branchBars = RunwayBars(layout, runwayEnd).Count(bar => bar.Edges.Any(edge => edge.MatchesTaxiway(branch)));

        Assert.Equal(expectedBars, branchBars);
    }

    /// <summary>
    /// Every route that reaches a runway from beyond its holding distance crosses a bar of that runway: a walk out from
    /// the runway's pavement that stops at the runway's bars never gets past the holding distance, on any committed
    /// layout. Beyond the runway ends (by more than the holding distance) the walk stops without a verdict, since the bars
    /// there are seated by lateral distance only.
    /// </summary>
    [Fact]
    public void EveryCommittedLayout_EveryRouteOntoARunwayFromBeyondItsHoldingDistance_CrossesABarOfThatRunway()
    {
        var violations = new List<string>();
        foreach (string path in Directory.EnumerateFiles("TestData", "*.geojson").Order(StringComparer.Ordinal))
        {
            string airport = Path.GetFileNameWithoutExtension(path);
            if (new TestAirportGroundData().GetLayout(airport) is not { } layout)
            {
                continue;
            }

            foreach (GroundRunway runway in layout.Runways)
            {
                IEnumerable<GroundNode> escapes = FindUnbarredEscapes(layout, runway);
                violations.AddRange(escapes.Select(node => $"{airport} {runway.Name}: {DescribeTaxiways(node)}"));
            }
        }

        violations.Sort(StringComparer.Ordinal);
        List<string> known = [.. KnownUnbarredEscapes.Order(StringComparer.Ordinal)];
        Assert.True(
            known.SequenceEqual(violations),
            "Unbarred routes onto a runway:" + Environment.NewLine + string.Join(Environment.NewLine, violations)
        );
    }

    /// <summary>
    /// The angle term never seats a bar where the taxiway to it touches another runway's holding area: on every committed
    /// layout, for a runway's bar that sits more than <see cref="AirportGroundLayout.HoldingDistanceToleranceFt"/> beyond
    /// its S (the term moved it), neither the bar nor any node on the taxiway path from the runway out to it touches
    /// another runway's holding area (<see cref="RunwayCrossingDetector.TouchesHoldingArea"/>). The path is every node
    /// reached from the bar by steps toward the runway's centerline, short of its pavement.
    /// </summary>
    [Fact]
    public void EveryCommittedLayout_NoAngledBarSitsInsideAnotherRunwaysHoldingArea()
    {
        var violations = new List<string>();
        foreach (string path in Directory.EnumerateFiles("TestData", "*.geojson").Order(StringComparer.Ordinal))
        {
            string airport = Path.GetFileNameWithoutExtension(path);
            if (new TestAirportGroundData().GetLayout(airport) is not { } layout)
            {
                continue;
            }

            List<(string Name, RunwayRectangle Rect)> runways =
            [
                .. layout.Runways.Select(runway => (runway.Name, RunwayCrossingDetector.BuildRunwayRectangle(runway))),
            ];
            foreach ((string name, RunwayRectangle rect) in runways)
            {
                violations.AddRange(AngledBarsInsideOtherHoldingAreas(airport, layout, name, rect, runways));
            }
        }

        Assert.True(
            violations.Count == 0,
            "Angled bars inside another runway's holding area:" + Environment.NewLine + string.Join(Environment.NewLine, violations)
        );
    }

    private static IEnumerable<string> AngledBarsInsideOtherHoldingAreas(
        string airport,
        AirportGroundLayout layout,
        string runwayName,
        RunwayRectangle rect,
        List<(string Name, RunwayRectangle Rect)> runways
    )
    {
        double baseFt = rect.HoldShortNm * FeetPerNm;
        foreach (GroundNode bar in layout.Nodes.Values)
        {
            bool isBar = (bar.Type == GroundNodeType.RunwayHoldShort) && (bar.RunwayId is { } id) && id.Equals(rect.CombinedId);
            double crossFt = CrossTrackFt(rect, bar);
            if (!isBar || (crossFt <= baseFt + AirportGroundLayout.HoldingDistanceToleranceFt))
            {
                continue;
            }

            List<(GroundNode From, GroundNode To)> path = PathInFromBar(bar, rect);
            foreach ((string otherName, RunwayRectangle other) in runways.Where(entry => !entry.Rect.CombinedId.Equals(rect.CombinedId)))
            {
                bool barTouches = RunwayCrossingDetector.TouchesHoldingArea(bar.Position, other);
                if (barTouches || path.Any(step => RunwayCrossingDetector.SegmentTouchesHoldingArea(step.From.Position, step.To.Position, other)))
                {
                    yield return $"{airport} #{bar.Id}: bar of {runwayName} at {crossFt:F1} ft (S {baseFt:F1}) is {CrossTrackFt(other, bar):F1} ft "
                        + $"from {otherName}; its path [{string.Join("→", path.Select(step => step.To.Id))}] touches its "
                        + $"{other.HoldShortNm * FeetPerNm:F1} ft holding area";
                }
            }
        }
    }

    /// <summary>
    /// The steps from the bar back along its own taxiway toward the runway's centerline, short of the runway's pavement,
    /// each to the neighbour nearest the centerline: the path the bar's walk took out to it from the boundary edge's
    /// off-node.
    /// </summary>
    private static List<(GroundNode From, GroundNode To)> PathInFromBar(GroundNode bar, RunwayRectangle rect)
    {
        string[] taxiways = [.. bar.Edges.OfType<GroundEdge>().Where(edge => !edge.IsRunwayCenterline).Select(edge => edge.TaxiwayName).Distinct()];
        var path = new List<(GroundNode From, GroundNode To)>();
        GroundNode node = bar;
        while (true)
        {
            GroundNode current = node;
            double crossFt = CrossTrackFt(rect, current);
            GroundNode? next = current
                .Edges.Where(edge => !edge.IsRunwayCenterline && taxiways.Any(edge.MatchesTaxiway))
                .Select(edge => edge.OtherNode(current))
                .Where(other => (CrossTrackFt(rect, other) < crossFt) && !RunwayCrossingDetector.IsOnRunway(other.Position, rect))
                .MinBy(other => CrossTrackFt(rect, other));
            if (next is null)
            {
                return path;
            }

            path.Add((current, next));
            node = next;
        }
    }

    /// <summary>
    /// The unbarred escapes the committed layouts already had before junction fallback bars could be dropped (no dropped
    /// bar adds one). Each is a bar-placement gap of its own, and a fix that closes one removes it. An entry is added only for a
    /// gap verified in the real layout data, citing the issue that tracks it (the HKY entries cite YAAT-60).
    /// </summary>
    private static readonly string[] KnownUnbarredEscapes =
    [
        "atl 9R - 27L: SJ/SJ - SJ2",
        "atl 9R - 27L: R12",
        "atl 9R - 27L: R6",
        "atl 9R - 27L: R10",
        "atl 8R - 26L: B2/E1",
        "atl 8L - 26R: B",
        "atl 8L - 26R: A",
        "atl 8L - 26R: B2/B - B2",
        "atl 8L - 26R: B",
        "atl 8L - 26R: A",
        // Routes that pass the holding distance set by the rule's S without a bar; tracked by YAAT-1214:
        "fll 10L - 28R: B1/B",
        "fll 10L - 28R: B12/B",
        "fll 10L - 28R: A/A8",
        "fll 10R - 28L: J12/J",
        "fll 10R - 28L: J4",
        // Pre-existing bar-placement gaps the hky fixture exposes, not regressions (YAAT-60):
        // the 01/19 bar is seated on the centreline (node #14), not at holding distance, and the escape reads at the A3/B
        // and B/RWY06/24 nodes.
        "hky 1 - 19: A3/A3 - B",
        "hky 1 - 19: B - RWY06/24",
        "mia 8R - 26L: M1L/M1L - Q1",
        "mia 8R - 26L: N/Q/Q1/M1L",
        "mia 8R - 26L: P/M1/M/PAD",
        "mia 8R - 26L: M/PAD - M",
        "mia 12 - 30: Q1/PAD - Q1",
        // Routes that pass the holding distance without a bar, read where the check's holding distance is S + W_twy / 2,
        // the angle term's ceiling; tracked by YAAT-1214:
        "atl 8L - 26R: A3/R20 - A3",
        "atl 8L - 26R: A5/A5 - RAMP",
        "atl 8L - 26R: A7/A7 - RAMP",
        "atl 8L - 26R: V",
        "atl 9R - 27L: T/N - T",
        "aus 18L - 36R: H/H - A",
        "fll 10L - 28R: A/A1",
        "fll 10R - 28L: J1",
        "fll 10R - 28L: J1/J",
        "hky 1 - 19: NRAMP",
        "lax 6R - 24L: E",
        "lax 6R - 24L: E6",
        "mia 12 - 30: M1L/M1L - PAD",
        "mia 12 - 30: PAD",
        "mia 12 - 30: PAD/PAD - Q1",
        "mia 12 - 30: PAD/Q1",
        "mia 12 - 30: Q1",
        "mia 8R - 26L: M11/M",
        "mia 8R - 26L: M11/N13/M",
        "mia 8R - 26L: Q1/M1L - Q1",
        "mia 9 - 27: V/T - V",
        "sea 34R - 16L: J/J - H",
        "aus 18L - 36R: H/G - H/H - G",
    ];

    private static string DescribeTaxiways(GroundNode node) =>
        string.Join("/", node.Edges.Where(edge => !edge.IsRunwayCenterline).Select(edge => edge.TaxiwayName).Distinct());

    /// <summary>The nodes beyond the runway's holding distance that a walk out from its pavement reaches without a bar.</summary>
    private static List<GroundNode> FindUnbarredEscapes(AirportGroundLayout layout, GroundRunway runway)
    {
        RunwayRectangle rect = RunwayCrossingDetector.BuildRunwayRectangle(runway);

        // An angled taxiway's bar sits out to S + W_twy / 2, the angle term's ceiling.
        double holdingFt = (rect.HoldShortNm * FeetPerNm) + (HoldShortStandoff.TaxiwayWidthProxyFt(RunwayCrossingDetector.RunwayWidthFt(rect)) / 2.0);
        double escapeFt = holdingFt + AirportGroundLayout.HoldingDistanceToleranceFt;
        var start = layout.Nodes.Values.Where(node => RunwayCrossingDetector.IsOnRunway(node.Position, rect)).ToList();
        var visited = new HashSet<int>(start.Select(node => node.Id));
        var queue = new Queue<GroundNode>(start);
        var escapes = new List<GroundNode>();
        while (queue.TryDequeue(out GroundNode? node))
        {
            foreach (IGroundEdge edge in node.Edges.Where(edge => !edge.IsRunwayCenterline))
            {
                GroundNode next = edge.OtherNode(node);
                if (!visited.Add(next.Id) || IsBarOf(next, rect) || IsBeyondRunwayEnds(rect, next, holdingFt))
                {
                    continue;
                }

                if (CrossTrackFt(rect, next) >= escapeFt)
                {
                    escapes.Add(next);
                    continue;
                }

                queue.Enqueue(next);
            }
        }

        return escapes;
    }

    private static bool IsBarOf(GroundNode node, in RunwayRectangle rect) =>
        (node.Type == GroundNodeType.RunwayHoldShort) && (node.RunwayId is { } id) && id.Equals(rect.CombinedId);

    private static bool IsBeyondRunwayEnds(in RunwayRectangle rect, GroundNode node, double holdingFt)
    {
        double alongFt = GeoMath.AlongTrackDistanceNm(node.Position, new LatLon(rect.RefLat, rect.RefLon), rect.TrueHeading) * FeetPerNm;
        return (alongFt < -holdingFt) || (alongFt > (rect.LengthNm * FeetPerNm) + holdingFt);
    }

    private static double CrossTrackFt(in RunwayRectangle rect, GroundNode node) =>
        Math.Abs(GeoMath.SignedCrossTrackDistanceNm(node.Position, new LatLon(rect.RefLat, rect.RefLon), rect.TrueHeading)) * FeetPerNm;

    private static List<GroundNode> RunwayBars(AirportGroundLayout layout, string runwayEnd) =>
        [.. layout.Nodes.Values.Where(n => (n.Type == GroundNodeType.RunwayHoldShort) && (n.RunwayId is { } id) && id.Contains(runwayEnd))];
}
