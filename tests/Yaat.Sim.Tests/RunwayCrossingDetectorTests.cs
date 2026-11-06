using System.Globalization;
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
using Yaat.Sim.Tests.Data.Airport;
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
        RunwayPlan plan = RunwayCrossingDetector.PlanRunway(rwy, layout.AirportId, null, calibratedFt: null);
        return RunwayCrossingDetector.DetectRunwayCrossings(plan, [plan], layout, coordIndex, ref nextNodeId);
    }

    /// <summary>The parse-time rectangle of <paramref name="rwy"/> as the detector builds it with no navdata for the runway.</summary>
    private static RunwayRectangle RectWithoutNavData(GeoJsonParser.RunwayFeature rwy, double widthFt, RunwayIdentifier combinedId)
    {
        HoldShortStandoffResult standoff = RunwayCrossingDetector.ResolveStandoff(
            rwy,
            RunwayStandoffInputs.WithoutNavData(widthFt),
            calibratedFt: null
        );
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
    /// below the 250 ft class. Parsed without OAK's tower-cab map, which calibrates S from the painted bars instead.
    /// </summary>
    [Theory]
    [InlineData("28R/10L", 250.0, true)]
    [InlineData("30/12", 280.0, true)]
    [InlineData("28L/10R", 250.0, false)]
    [InlineData("15/33", 175.0, false)]
    public void DetectRunwayCrossings_Oak_StoresTheResolvedStandoff(string runwayName, double baseFt, bool addsElevation)
    {
        AirportGroundLayout layout = RunwayPaintSnapTests.ParseOak(map: null, FilletMode.Standard);
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

    // -------------------------------------------------------------------------
    // The angle term near another runway's holding area
    // -------------------------------------------------------------------------

    /// <summary>
    /// A taxiway that enters another runway's holding area between S and the angled bar point D, and leaves it again
    /// before D, has its bar clamped just before that entry: ABQ C's 3/21 bar enters 12/30's angled holding area at
    /// 549.8 ft from 3/21, so the bar sits 5 ft inside that, short of D (553.2 ft).
    /// </summary>
    [Fact]
    public void Abq_3_21_C_ClampsBeforeRunway12_30sHoldingArea() => AssertBarAt("ABQ", "21", "C", 544.8, 2.0);

    /// <summary>
    /// A taxiway whose only touch of another runway's holding area lies inside S keeps the angle term: CYS B1 meets 9/27's
    /// own bar inside 13/31's S, and the path from S out to D never enters 9/27's area, so 13/31's bar sits at D.
    /// </summary>
    [Fact]
    public void Cys_13_31_B1_GoesToD_WhenTheTouchIsInsideS() => AssertBarAt("CYS", "31", "B1", 333.4, 2.0);

    /// <summary>As <see cref="Cys_13_31_B1_GoesToD_WhenTheTouchIsInsideS"/>, on CYS E.</summary>
    [Fact]
    public void Cys_13_31_E_GoesToD_WhenTheTouchIsInsideS() => AssertBarAt("CYS", "31", "E", 334.8, 2.0);

    /// <summary>
    /// A taxiway whose S point already lies inside another runway's holding area keeps its bar at S: the angle term never
    /// moves the bar into that area.
    /// </summary>
    [Theory]
    [InlineData("HKY", "24", "A3", 261.9)]
    [InlineData("HKY", "24", "B", 261.9)]
    [InlineData("MIA", "12", "Q10", 280.0)]
    [InlineData("COS", "31", "B1", 311.7)]
    public void FullOverlap_StaysAtS(string airport, string runwayEnd, string taxiway, double expectedFt) =>
        AssertBarAt(airport, runwayEnd, taxiway, expectedFt, 2.0, every: true);

    /// <summary>
    /// A path segment running alongside another runway, under 30° to it, judges that runway's holding area by S + 5 ft
    /// alone, with no angle term: HKY A runs about 301 ft off 6/24, beyond its S + 5 ft, so 1/19's bars on A stay at D.
    /// </summary>
    [Fact]
    public void ParallelSegment_DoesNotWidenTheOtherRunwaysArea() => AssertBarAt("HKY", "19", "A", 275.5, 0.5);

    /// <summary>
    /// Fails unless the committed layout of <paramref name="airport"/> has a bar of <paramref name="runwayEnd"/>'s runway on
    /// <paramref name="taxiway"/> within <paramref name="toleranceFt"/> of <paramref name="expectedFt"/> from its centerline;
    /// with <paramref name="every"/>, unless it has at least one such bar and every one of them is.
    /// </summary>
    private static void AssertBarAt(string airport, string runwayEnd, string taxiway, double expectedFt, double toleranceFt, bool every = false)
    {
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout(airport);
        Assert.NotNull(layout);

        GroundRunway runway = layout.Runways.Single(r => RunwayIdentifier.Parse(r.Name).Contains(runwayEnd));
        RunwayRectangle rect = RunwayCrossingDetector.BuildRunwayRectangle(runway);
        List<double> distances =
        [
            .. RunwayBars(layout, runwayEnd)
                .Where(bar => bar.Edges.Any(edge => edge.MatchesTaxiway(taxiway)))
                .Select(bar => CrossTrackFt(rect, bar))
                .Order(),
        ];
        string seen = string.Join(", ", distances.Select(ft => ft.ToString("F1", CultureInfo.InvariantCulture)));
        bool matches = every
            ? (distances.Count > 0) && distances.All(ft => Math.Abs(ft - expectedFt) <= toleranceFt)
            : distances.Any(ft => Math.Abs(ft - expectedFt) <= toleranceFt);
        Assert.True(
            matches,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{airport} {runwayEnd} {taxiway}: no bar at {expectedFt:F1} ± {toleranceFt:F1} ft; bars at [{seen}]"
            )
        );
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

        AssertMatchesBaseline("Unbarred routes onto a runway", KnownUnbarredEscapes, violations);
    }

    /// <summary>
    /// A walk out from a runway's pavement reaches a fallback junction whose routes out come to a node lying between the
    /// rule's ceiling and the ceiling a painted bar raises on one of their taxiways: judged on the unpainted step as well as
    /// the painted one, that node escapes whichever edge order the layout gives.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FindUnbarredEscapes_JudgesANodeOnEveryArrivingEdge_WhicheverEdgeOrderTheLayoutGives(bool reversed)
    {
        var scenario = EscapeBetweenTheRuleAndPaintCeilings.Build(reversed);

        Assert.Contains(scenario.Escape, FindUnbarredEscapes(scenario.Layout, scenario.Rect));
    }

    /// <summary>
    /// The generator's own walk over the same layout keeps the fallback bar whichever edge order the layout gives: the
    /// node's escape along the unpainted step is judged even when the painted step reached it first, so a route out of the
    /// junction is known to pass the holding distance without a bar.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FallbackBar_AscriptionToTheEscapingRouteOut_IsKeptWhicheverEdgeOrderTheLayoutGives(bool reversed)
    {
        var scenario = EscapeBetweenTheRuleAndPaintCeilings.Build(reversed);

        RunwayCrossingDetector.DropRedundantFallbackBars(scenario.Layout, [scenario.Fallback], scenario.Paint);
        Assert.Equal(GroundNodeType.RunwayHoldShort, scenario.Fallback.Bar.Type);
    }

    /// <summary>
    /// A purpose-made layout for <see cref="FindUnbarredEscapes_JudgesANodeOnEveryArrivingEdge_WhicheverEdgeOrderTheLayoutGives"/>
    /// and <see cref="FallbackBar_AscriptionToTheEscapingRouteOut_IsKeptWhicheverEdgeOrderTheLayoutGives"/>:
    /// a runway, a fallback junction inside the rule's ceiling, a node between that ceiling and the painted bar's reached
    /// from the junction along a painted taxiway and along an unpainted one, and the painted bar the ceiling comes from.
    /// </summary>
    private sealed record EscapeBetweenTheRuleAndPaintCeilings(
        AirportGroundLayout Layout,
        RunwayRectangle Rect,
        GroundNode Escape,
        FallbackBar Fallback,
        Dictionary<(RunwayIdentifier Runway, string Taxiway), double> Paint
    )
    {
        private const string PaintedTaxiway = "P";

        public static EscapeBetweenTheRuleAndPaintCeilings Build(bool reversed)
        {
            GeoJsonParser.RunwayFeature rwy = NorthSouthRunway();
            RunwayRectangle rect = RectWithoutNavData(rwy, 150.0, RunwayIdentifier.Parse("18/36"));
            double ruleFt = RunwayCrossingDetector.RuleCeilingFt(rect);
            double paintFt = ruleFt + 200.0;
            double betweenFt = ruleFt + 100.0;

            double midLat = (rwy.Coords[0].Lat + rwy.Coords[1].Lat) / 2.0;
            double midLon = (rwy.Coords[0].Lon + rwy.Coords[1].Lon) / 2.0;
            LatLon At(double alongFt, double crossFt)
            {
                (double alongLat, double alongLon) = GeoMath.ProjectPoint(midLat, midLon, new TrueHeading(0.0), alongFt / FeetPerNm);
                (double crossLat, double crossLon) = GeoMath.ProjectPoint(alongLat, alongLon, new TrueHeading(90.0), crossFt / FeetPerNm);
                return new LatLon(crossLat, crossLon);
            }

            var layout = new AirportGroundLayout { AirportId = "TEST" };
            GroundNode Add(int id, LatLon position, GroundNodeType type = GroundNodeType.TaxiwayIntersection)
            {
                var node = new GroundNode
                {
                    Id = id,
                    Position = position,
                    Type = type,
                };
                layout.Nodes[id] = node;
                return node;
            }

            GroundNode onRunway = Add(1, At(0.0, 0.0));
            GroundNode junction = Add(2, At(0.0, ruleFt - 150.0));
            GroundNode south = Add(3, At(-50.0, ruleFt - 100.0));
            GroundNode north = Add(4, At(150.0, ruleFt - 100.0));
            GroundNode escape = Add(5, At(0.0, betweenFt));
            GroundNode bar = Add(6, At(0.0, paintFt), GroundNodeType.RunwayHoldShort);
            bar.RunwayId = rect.CombinedId;
            bar.PaintSegmentIndex = 0;

            List<(int From, int To, string Taxiway)> links =
            [
                (onRunway.Id, junction.Id, "A"),
                (junction.Id, south.Id, "A"),
                (junction.Id, north.Id, "B"),
                (south.Id, escape.Id, PaintedTaxiway),
                (north.Id, escape.Id, "Q"),
                (onRunway.Id, bar.Id, PaintedTaxiway),
            ];
            if (reversed)
            {
                links.Reverse();
            }

            foreach ((int from, int to, string taxiway) in links)
            {
                layout.Edges.Add(
                    new GroundEdge
                    {
                        Nodes = [layout.Nodes[from], layout.Nodes[to]],
                        TaxiwayName = taxiway,
                        DistanceNm = GeoMath.DistanceNm(layout.Nodes[from].Position, layout.Nodes[to].Position),
                    }
                );
            }

            layout.RebuildAdjacencyLists();

            Assert.Equal(PaintedTaxiway, RunwayHoldBars.BarTaxiwayName(bar));
            Assert.True(RunwayCrossingDetector.IsOnRunway(onRunway.Position, rect));
            Assert.False(RunwayCrossingDetector.IsOnRunway(junction.Position, rect));
            Assert.False(RunwayCrossingDetector.IsOnRunway(south.Position, rect));
            Assert.False(RunwayCrossingDetector.IsOnRunway(north.Position, rect));
            Assert.True(CrossTrackFt(rect, escape) > ruleFt);
            Assert.True(CrossTrackFt(rect, escape) < (paintFt - AirportGroundLayout.HoldingDistanceToleranceFt));

            Dictionary<(RunwayIdentifier Runway, string Taxiway), double> paint = RunwayCrossingDetector.PaintFtByTaxiway([
                new SnappedBar(bar, rect, CrossTrackFt(rect, bar), PaintedTaxiway),
            ]);
            return new EscapeBetweenTheRuleAndPaintCeilings(
                layout,
                rect,
                escape,
                new FallbackBar(bar, junction, rect, GroundNodeType.TaxiwayIntersection, Split: null, ruleFt),
                paint
            );
        }
    }

    /// <summary>
    /// The angle term never seats a bar where the taxiway to it touches another runway's holding area: on every committed
    /// layout, for a runway's bar that sits more than <see cref="AirportGroundLayout.HoldingDistanceToleranceFt"/> beyond
    /// its S (the term moved it), neither the bar nor the taxiway path from its S out to it touches another runway's
    /// holding area (<see cref="RunwayCrossingDetector.TouchesHoldingArea"/>); a touch inside S does not count. The path
    /// is every node reached from the bar by steps toward the runway's centerline, cut where it reaches S. And walking
    /// from such a bar alongside another runway toward that runway, that runway's own bar comes before its pavement.
    /// </summary>
    [Fact]
    public void EveryCommittedLayout_NoAngledBarSitsInsideAnotherRunwaysHoldingArea()
    {
        var violations = new List<string>();
        var inversions = new List<string>();
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
                inversions.AddRange(AngledBarsBeyondOtherRunwaysBars(airport, layout, name, rect, runways));
            }
        }

        AssertMatchesBaseline("Angled bars inside another runway's holding area", KnownAngledBarViolations, violations);
        AssertMatchesBaseline("Angled bars beyond another runway's own bar", KnownAngledBarInversions, inversions);
    }

    /// <summary>
    /// Fails unless <paramref name="seen"/> holds exactly the lines of <paramref name="baseline"/>, duplicates counted; the
    /// message lists only the difference: each line seen but not in the baseline, and each baseline line no longer seen.
    /// </summary>
    private static void AssertMatchesBaseline(string title, IEnumerable<string> baseline, IEnumerable<string> seen)
    {
        var remaining = baseline.GroupBy(line => line, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        List<string> unexpected = [];
        foreach (string line in seen.Order(StringComparer.Ordinal))
        {
            if (remaining.TryGetValue(line, out int count) && (count > 0))
            {
                remaining[line] = count - 1;
            }
            else
            {
                unexpected.Add(line);
            }
        }

        List<string> gone =
        [
            .. remaining.Where(kv => kv.Value > 0).SelectMany(kv => Enumerable.Repeat(kv.Key, kv.Value)).Order(StringComparer.Ordinal),
        ];
        string nl = Environment.NewLine;
        Assert.True(
            (unexpected.Count == 0) && (gone.Count == 0),
            $"{title}:{nl}Unexpected:{nl}{string.Join(nl, unexpected)}{nl}No longer seen:{nl}{string.Join(nl, gone)}"
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
            List<(LatLon From, LatLon To)> beyondS = StepsBeyondS(path, rect, baseFt);
            foreach ((string otherName, RunwayRectangle other) in runways.Where(entry => !entry.Rect.CombinedId.Equals(rect.CombinedId)))
            {
                bool barTouches = RunwayCrossingDetector.TouchesHoldingArea(bar.Position, other);
                if (barTouches || beyondS.Any(step => RunwayCrossingDetector.SegmentTouchesHoldingArea(step.From, step.To, other)))
                {
                    string steps = string.Join("→", path.Select(step => step.To.Id));
                    CultureInfo inv = CultureInfo.InvariantCulture;
                    yield return string.Create(inv, $"{airport} #{bar.Id}: bar of {runwayName} at {crossFt:F1} ft (S {baseFt:F1}) is ")
                        + string.Create(inv, $"{CrossTrackFt(other, bar):F1} ft from {otherName}; its path [{steps}] touches its ")
                        + string.Create(inv, $"{other.HoldShortNm * FeetPerNm:F1} ft holding area");
                }
            }
        }
    }

    /// <summary>
    /// The part of <paramref name="path"/> (steps from the bar inward, each nearer the runway's centerline) that lies
    /// beyond the runway's S (<paramref name="baseFt"/>): every step whose start is beyond S, the last one cut where it
    /// reaches S.
    /// </summary>
    private static List<(LatLon From, LatLon To)> StepsBeyondS(List<(GroundNode From, GroundNode To)> path, RunwayRectangle rect, double baseFt)
    {
        var steps = new List<(LatLon From, LatLon To)>();
        foreach ((GroundNode from, GroundNode to) in path)
        {
            double fromFt = CrossTrackFt(rect, from);
            double toFt = CrossTrackFt(rect, to);
            if (fromFt <= baseFt)
            {
                break;
            }

            if (toFt >= baseFt)
            {
                steps.Add((from.Position, to.Position));
                continue;
            }

            double fraction = (fromFt - baseFt) / (fromFt - toFt);
            LatLon atS = new(
                from.Position.Lat + (fraction * (to.Position.Lat - from.Position.Lat)),
                from.Position.Lon + (fraction * (to.Position.Lon - from.Position.Lon))
            );
            steps.Add((from.Position, atS));
            break;
        }

        return steps;
    }

    /// <summary>
    /// The angled bars of a runway that sit alongside another runway's length (within
    /// <see cref="RunwayCrossingDetector.RunwayTolerance"/> of either end) and within its angled holding area (its S plus
    /// half the taxiway width proxy plus 5 ft), and from which the walk along the bar's taxiway toward that runway reaches
    /// its pavement without passing one of its bars: the bar order is inverted there, an aircraft holding short of the
    /// first runway stands beyond the second runway's hold line.
    /// </summary>
    private static IEnumerable<string> AngledBarsBeyondOtherRunwaysBars(
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

            foreach ((string otherName, RunwayRectangle other) in runways.Where(entry => !entry.Rect.CombinedId.Equals(rect.CombinedId)))
            {
                double proxyFt = HoldShortStandoff.TaxiwayWidthProxyFt(RunwayCrossingDetector.RunwayWidthFt(other));
                double ceilingFt = (other.HoldShortNm * FeetPerNm) + (proxyFt / 2.0) + RunwayCrossingDetector.OtherRunwayAreaMarginFt;
                if (IsAlongside(other, bar) && (CrossTrackFt(other, bar) <= ceilingFt) && (PavementReachedWithoutBar(bar, other) is { } reached))
                {
                    CultureInfo inv = CultureInfo.InvariantCulture;
                    yield return string.Create(inv, $"{airport} #{bar.Id}: bar of {runwayName} at {crossFt:F1} ft (S {baseFt:F1}) reaches ")
                        + string.Create(inv, $"{otherName}'s pavement at #{reached.Id} before any of its bars ({CrossTrackFt(other, bar):F1} ft ")
                        + string.Create(inv, $"from it, S {other.HoldShortNm * FeetPerNm:F1})");
                }
            }
        }
    }

    /// <summary>
    /// Whether <paramref name="node"/> lies alongside the runway's length, within
    /// <see cref="RunwayCrossingDetector.RunwayTolerance"/> of either end (the along-track test of
    /// <see cref="RunwayCrossingDetector.TouchesHoldingArea"/>).
    /// </summary>
    private static bool IsAlongside(RunwayRectangle rect, GroundNode node)
    {
        double alongNm = GeoMath.AlongTrackDistanceNm(node.Position, new LatLon(rect.RefLat, rect.RefLon), rect.TrueHeading);
        return (alongNm >= -RunwayCrossingDetector.RunwayTolerance) && (alongNm <= rect.LengthNm + RunwayCrossingDetector.RunwayTolerance);
    }

    /// <summary>
    /// The first node on <paramref name="other"/>'s pavement that the walk from <paramref name="bar"/> along its own
    /// taxiway reaches, each step to the neighbour nearest that runway's centerline, without passing a bar of that runway;
    /// null when the walk meets one of its bars first or stops short of its pavement.
    /// </summary>
    private static GroundNode? PavementReachedWithoutBar(GroundNode bar, RunwayRectangle other)
    {
        string[] taxiways = [.. bar.Edges.OfType<GroundEdge>().Where(edge => !edge.IsRunwayCenterline).Select(edge => edge.TaxiwayName).Distinct()];
        GroundNode node = bar;
        while (true)
        {
            GroundNode current = node;
            double crossFt = CrossTrackFt(other, current);
            GroundNode? next = current
                .Edges.Where(edge => !edge.IsRunwayCenterline && taxiways.Any(edge.MatchesTaxiway))
                .Select(edge => edge.OtherNode(current))
                .Where(candidate => CrossTrackFt(other, candidate) < crossFt)
                .MinBy(candidate => CrossTrackFt(other, candidate));
            if (next is null)
            {
                return null;
            }

            if (RunwayCrossingDetector.IsOnRunway(next.Position, other))
            {
                return next;
            }

            bool isOtherBar = (next.Type == GroundNodeType.RunwayHoldShort) && (next.RunwayId is { } id) && id.Equals(other.CombinedId);
            if (isOtherBar)
            {
                return null;
            }

            node = next;
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
    /// gap verified in the real layout data, citing the issue that tracks it (the HKY entries cite YAAT-60). The block
    /// under the YAAT-1915 comment is an untriaged baseline taken when every vNAS taxi-graph layout was committed, not
    /// verified gaps.
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
        // Found when every vNAS taxi-graph layout was committed; untriaged (data quirk vs detector gap), tracked by YAAT-1915:
        "ABQ 3 - 21: F2/F2 - RAMP",
        "ABQ 3 - 21: F4/F4 - RAMP",
        "ABQ 3 - 21: F6/F6 - RAMP",
        "ABQ 3 - 21: H",
        "ABQ 3 - 21: RAMP",
        "ABQ 8 - 26: M3/A8 - M3/M3 - A8",
        "AGC 13 - 31: A3/A - A3",
        "AGC 13 - 31: A3/B - A3",
        "AGS 17 - 35: A1/A",
        "AVL 17 - 35: B",
        "AVL 17 - 35: B1",
        "BFL 30L - 12R: H/H - G",
        "BJC 3 - 21: A/A - B/A - R4",
        "BJC 3 - 21: B - RWY12L/30R",
        "BJC 3 - 21: B - RWY12R/30L",
        "BJC 3 - 21: B2/B - B2",
        "BJC 3 - 21: B2/B - B2/B2 - RAMP",
        "BJC 3 - 21: D/D - B",
        "BJC 3 - 21: RAMP/B - RAMP",
        "BJC 3 - 21: RAMP/B - RAMP",
        "BJC 3 - 21: RAMP/B2 - RAMP",
        "BOS 4R - 22L: T/T - RWY15R/33L",
        "BUF 14 - 32: D2",
        "BUF 14 - 32: D3",
        "CYS 13 - 31: C",
        "DAY 36 - 18: V/C - V",
        "DPA 33 - 15: A",
        "DPA 33 - 15: H/E - H",
        "FCM 28L - 10R: B - RWY36/18",
        "FCM 28L - 10R: RAMP",
        "FCM 28L - 10R: RAMP",
        "FCM 28L - 10R: RAMP",
        "FCM 28L - 10R: RAMP",
        "FHB 4 - 22: A",
        "FHB 4 - 22: A/A - B",
        "FMN 5 - 23: B1",
        "FMN 5 - 23: F/G - F",
        "FMN 5 - 23: K",
        "FMN 5 - 23: L",
        "FMN 5 - 23: M",
        "FMN 7 - 25: B/A - B",
        "FMN 7 - 25: C/C - RWY05/23",
        "FMN 7 - 25: H/A - H",
        "FMN 7 - 25: J/A - J",
        "FTW 17-35: RAMP",
        "FTW 17-35: RAMP",
        "HIO 13R - 31L: A6/A - A6",
        "HIO 13R - 31L: A9/A",
        "HNL 4L - 22R: D/D - RWY04R/22L",
        "HNL 4L - 22R: F/F - RWY04R/22L",
        "HNL 8L - 26R: S/S - A",
        "HNL 8L - 26R: S/Z - S",
        "HOU 4 - 22: Z",
        "HXD 3 - 21: A",
        "HXD 3 - 21: A",
        "HXD 3 - 21: F",
        "HXD 3 - 21: F",
        "ISP 33L-15R: RAMP",
        "ITO 3 - 21: C - RWY08/26",
        "ITO 3 - 21: C - RWY08/26",
        "ITO 3 - 21: I",
        "ITO 3 - 21: I/C - I",
        "ITO 3 - 21: J",
        "ITO 3 - 21: J/C - J",
        "ITO 3 - 21: RAMP/C - RAMP",
        "ITO 3 - 21: RAMP/C - RAMP",
        "JAX 14 - 32: M2/M2 - RAMP",
        "JFK 4R - 22L: FA",
        "JNU 8 - 26: RAMP",
        "JNU 8 - 26: RAMP",
        "JNU 8C - 26C: E/RWY08/26:link/RAMP",
        "JNU 8C - 26C: RAMP/RWY08/26 - RAMP",
        "LAF 10 - 28: C4/C",
        "LAF 10 - 28: C4/C",
        "LAF 5 - 23: B",
        "LAS 1L - 19R: F",
        "LAS 1L - 19R: F/F - H",
        "LAS 1L - 19R: F/F - H",
        "LAS 1L - 19R: H",
        "LAS 1L - 19R: H/E - H",
        "LAS 1L - 19R: H/F",
        "LAS 1L - 19R: H/F - H",
        "LAS 1L - 19R: H/F - H",
        "LAS 1R - 19L: Y",
        "LGA 4 - 22: AA",
        "LZU 7 - 25: ZHP/RAMP/ZHP - RAMP",
        "MDW 22L - 4R: Y",
        "MDW 22L - 4R: Y/Y1",
        "MDW 22L - 4R: Y/Y1",
        "MDW 31R - 13L: E/E3",
        "MKE 19R - 1L: E/F - E",
        "MKE 19R - 1L: F/E",
        "MKE 25L - 7R: A5/A",
        "MKE 25R - 7L: Z",
        "MSP 4 - 22: R3/R3 - RAMP",
        "OKC 35L - 17R: B",
        "OPF 12 - 30: N/T/N - N1",
        "OPF 12 - 30: N1",
        "OPF 12 - 30: N1/T - N1",
        "OPF 12 - 30: T/N",
        "OPF 12 - 30: Y/Y - Y5",
        "OPF 12 - 30: Y5/Y - Y5",
        "ORD 22L - 4R: N/N - V",
        "PAE 16L - 34R: D/D - G",
        "PAE 16L - 34R: D/D - G",
        "PAE 16L - 34R: L/L - G",
        "PAE 16L - 34R: RAMP/G - RAMP",
        "PAE 16L - 34R: RAMP/G - RAMP/G1 - RAMP",
        "PAE 16L - 34R: RAMP/G1 - RAMP",
        "PDK 34 - 16: B - RWY21R/03L",
        "PDK 34 - 16: C/D - C/C - D",
        "PDK 34 - 16: D - RWY21R/03L",
        "PDK 34 - 16: K/K - B",
        "PDK 34 - 16: RAMP/B - RAMP",
        "PDK 34 - 16: RAMP/B - RAMP",
        "PDK 34 - 16: RAMP/D - RAMP",
        "PDK 34 - 16: RAMP/D - RAMP",
        "PDK 34 - 16: RAMP/D - RAMP",
        "PHX 8 - 26: B/S - B",
        "PHX 8 - 26: S/S - B",
        "PRC 12 - 30: E",
        "PRC 12 - 30: F",
        "PRC 12 - 30: F",
        "PRC 3R - 21L: C",
        "PRC 3R - 21L: D",
        "PVD 16 - 34: T",
        "PWK 12 - 30: E/E - RWY06/24",
        "PWK 6 - 24: D",
        "PWK 6 - 24: L4 - RWY16/34",
        "RNT 16 - 34: RAMP/A - RAMP",
        "RNT 16 - 34: RAMP/A - RAMP",
        "RNT 16 - 34: RAMP/A - RAMP",
        "RNT 16 - 34: RAMP/A - RAMP",
        "RNT 16 - 34: RAMP/A - RAMP",
        "RNT 16 - 34: RAMP/A - RAMP",
        "RNT 16 - 34: RAMP/A - RAMP",
        "RNT 16 - 34: RAMP/A3 - RAMP",
        "RNT 16 - 34: RAMP/A3 - RAMP",
        "RNT 16 - 34: RAMP/A4 - RAMP",
        "ROC 04 - 22: A/A - RUNUP_A",
        "ROC 04 - 22: A/A - RUNUP_A",
        "ROC 04 - 22: A/RUNUP_A",
        "ROC 04 - 22: D",
        "ROC 07 - 25: G/F - G",
        "ROC 07 - 25: M_LINK1",
        "ROC 10 - 28: B_LINK1",
        "ROC 10 - 28: D_LINK1",
        "ROC 10 - 28: H_LINK1",
        "ROC 10 - 28: H_LINK2",
        "ROC 10 - 28: H_LINK2",
        "SAN 9 - 27: B/B1",
        "SAT 13R - 31L: Z",
        "SAT 4 - 22: N",
        "SIG 9 - 27: BG",
        "SIG 9 - 27: RAMP/SG - RAMP",
        "SIG 9 - 27: SG/SG - RAMP",
        "SJU 10 - 28: A",
        "SJU 10 - 28: B/H - B/A - B/B - RAMP",
        "SJU 10 - 28: C/H - C/C - RAMP",
        "SJU 10 - 28: J/RAMP",
        "SJU 10 - 28: L/H - L",
        "SJU 10 - 28: M/H - M",
        "SJU 10 - 28: N3/H - N3/N3 - RAMP",
        "SJU 10 - 28: P/H - P",
        "SJU 10 - 28: RAMP",
        "SJU 10 - 28: RAMP",
        "SJU 10 - 28: RAMP",
        "SJU 10 - 28: RAMP",
        "SJU 10 - 28: RAMP",
        "SJU 10 - 28: RAMP",
        "SJU 10 - 28: RAMP",
        "SJU 10 - 28: RAMP/A9 - RAMP",
        "SJU 10 - 28: RAMP/H - RAMP",
        "SJU 10 - 28: RAMP/H - RAMP",
        "SJU 10 - 28: RAMP/H - RAMP",
        "SJU 10 - 28: RAMP/H - RAMP",
        "SJU 10 - 28: RAMP/H - RAMP",
        "SJU 10 - 28: RAMP/H - RAMP",
        "SJU 10 - 28: RAMP/H - RAMP",
        "SJU 10 - 28: RAMP/H - RAMP",
        "SJU 10 - 28: RAMP/N3 - RAMP",
        "SJU 10 - 28: RAMP/N3 - RAMP",
        "SLC 14 - 32: L",
        "SLC 14 - 32: L1",
        "TCL 12 - 30: D6",
        "TCL 4 - 22: A/A5",
        "TCL 4 - 22: A1/F",
        "TCL 4 - 22: B/B5 - B",
        "TPA 1L - 19R: V",
        "TTD 7 - 25: RAMP",
        "TTD 7 - 25: RAMP/A - RAMP",
        "TTD 7 - 25: RAMP/A - RAMP",
        "TTD 7 - 25: RAMP/A - RAMP",
        "TTD 7 - 25: RAMP/A - RAMP",
        "TTD 7 - 25: RAMP/B - RAMP",
        "TTD 7 - 25: RAMP/B - RAMP",
        "TUS 12 - 30: D/A - D",
        "TUS 12 - 30: D/D - D3",
        "TYS 5L - 23R: B8/B",
        "TYS 5R - 23L: A",
        "TYS 5R - 23L: A/A4 - A",
        "TYS 5R - 23L: A4/A4 - A",
        "UAO 17 - 35: A/A - A1",
        "UAO 17 - 35: A/A1",
        "VNY 9 - 27: B/B1",
    ];

    /// <summary>
    /// The angled bars the committed layouts have inside another runway's holding area, each tracked by the issue its comment
    /// names. A fix that moves one removes it; any other violation fails the sweep.
    /// </summary>
    private static readonly string[] KnownAngledBarViolations = [];

    /// <summary>
    /// The angled bars the committed layouts have beyond another runway's own bar (an aircraft holding short of the first
    /// runway stands past the second runway's hold line). These predate the clamp of the angle term at the other runway's
    /// holding area and are tracked as their own item. The list is empty: no committed layout has one, and any inversion
    /// fails the sweep.
    /// </summary>
    private static readonly string[] KnownAngledBarInversions = [];

    private static string DescribeTaxiways(GroundNode node) =>
        string.Join("/", node.Edges.Where(edge => !edge.IsRunwayCenterline).Select(edge => edge.TaxiwayName).Distinct());

    /// <summary>The nodes beyond the runway's holding distance that a walk out from its pavement reaches without a bar.</summary>
    private static List<GroundNode> FindUnbarredEscapes(AirportGroundLayout layout, GroundRunway runway) =>
        FindUnbarredEscapes(layout, RunwayCrossingDetector.BuildRunwayRectangle(runway));

    private static List<GroundNode> FindUnbarredEscapes(AirportGroundLayout layout, RunwayRectangle rect)
    {
        // An angled taxiway's bar sits out to S + W_twy / 2, the angle term's ceiling. A bar seated on the painted marking may sit
        // farther out still, so a step along a taxiway is judged against the farthest painted bar of the runway on that same
        // taxiway, by the generator's own rule (RunwayCrossingDetector.EscapeCeilingFt); a far bar on another taxiway never
        // raises it. A painted bar's taxiway is the one the generator keys it on (RunwayHoldBars.BarTaxiwayName: the bar's own
        // straight taxiway, a fillet arc never naming it), not every name at the bar.
        double ruleFt = RunwayCrossingDetector.RuleCeilingFt(rect);
        List<GroundNode> paintBars = [.. layout.Nodes.Values.Where(node => IsBarOf(node, rect) && (node.PaintSegmentIndex is not null))];
        double holdingFt = Math.Max(ruleFt, paintBars.Select(node => CrossTrackFt(rect, node)).DefaultIfEmpty(0.0).Max());
        Dictionary<(RunwayIdentifier Runway, string Taxiway), double> paintByTaxiway = RunwayCrossingDetector.PaintFtByTaxiway(
            paintBars.SelectMany(bar =>
                (RunwayHoldBars.BarTaxiwayName(bar) is { } taxiway) ? new[] { new SnappedBar(bar, rect, CrossTrackFt(rect, bar), taxiway) } : []
            )
        );
        double EscapeFt(IGroundEdge step) =>
            RunwayCrossingDetector.EscapeCeilingFt(rect.CombinedId, RunwayCrossingDetector.StepTaxiways(step), paintByTaxiway, ruleFt)
            + AirportGroundLayout.HoldingDistanceToleranceFt;
        var start = layout.Nodes.Values.Where(node => RunwayCrossingDetector.IsOnRunway(node.Position, rect)).ToList();
        var visited = new HashSet<int>(start.Select(node => node.Id));
        var escaped = new HashSet<int>();
        var queue = new Queue<GroundNode>(start);
        var escapes = new List<GroundNode>();
        while (queue.TryDequeue(out GroundNode? node))
        {
            foreach (IGroundEdge edge in node.Edges.Where(edge => !edge.IsRunwayCenterline))
            {
                GroundNode next = edge.OtherNode(node);
                if (IsBarOf(next, rect) || IsBeyondRunwayEnds(rect, next, holdingFt))
                {
                    continue;
                }

                // A node is judged against the ceiling of the taxiway of every edge it is reached along, not only the first: a
                // step along a painted taxiway raises the ceiling for that edge alone, so both verdicts must be seen.
                if (CrossTrackFt(rect, next) >= EscapeFt(edge))
                {
                    if (escaped.Add(next.Id))
                    {
                        escapes.Add(next);
                    }

                    continue;
                }

                if (visited.Add(next.Id))
                {
                    queue.Enqueue(next);
                }
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
