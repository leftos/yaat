using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Testing;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests;

public class RunwayCrossingDetectorTests
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
        RunwayRectangle rect = RunwayCrossingDetector.BuildRunwayRectangle(rwy, 150.0, RunwayIdentifier.Parse("4/22"));

        // Midpoint of the runway is on the centerline
        double midLat = (rwy.Coords[0].Lat + rwy.Coords[1].Lat) / 2.0;
        double midLon = (rwy.Coords[0].Lon + rwy.Coords[1].Lon) / 2.0;

        Assert.True(RunwayCrossingDetector.IsOnRunway(midLat, midLon, rect));
    }

    [Fact]
    public void IsOnRunway_DiagonalRunway_PointFarOffCenterline_ReturnsFalse()
    {
        GeoJsonParser.RunwayFeature rwy = DiagonalRunway();
        RunwayRectangle rect = RunwayCrossingDetector.BuildRunwayRectangle(rwy, 150.0, RunwayIdentifier.Parse("4/22"));

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
        RunwayRectangle rect = RunwayCrossingDetector.BuildRunwayRectangle(rwy, 150.0, RunwayIdentifier.Parse("4/22"));

        // Project past the far end
        (double beyondLat, double beyondLon) = GeoMath.ProjectPoint(rwy.Coords[1].Lat, rwy.Coords[1].Lon, new TrueHeading(45.0), 0.1);

        Assert.False(RunwayCrossingDetector.IsOnRunway(beyondLat, beyondLon, rect));
    }

    [Fact]
    public void IsOnRunway_NorthSouthRunway_PointSlightlyOffCenter_ReturnsTrue()
    {
        GeoJsonParser.RunwayFeature rwy = NorthSouthRunway();
        RunwayRectangle rect = RunwayCrossingDetector.BuildRunwayRectangle(rwy, 150.0, RunwayIdentifier.Parse("18/36"));

        // 50ft east of centerline (within 75ft half-width)
        double midLat = (rwy.Coords[0].Lat + rwy.Coords[1].Lat) / 2.0;
        (double offLat, double offLon) = GeoMath.ProjectPoint(midLat, rwy.Coords[0].Lon, new TrueHeading(90.0), 50.0 / FeetPerNm);

        Assert.True(RunwayCrossingDetector.IsOnRunway(offLat, offLon, rect));
    }

    // -------------------------------------------------------------------------
    // BuildRunwayRectangle — width-based hold-short distance
    // -------------------------------------------------------------------------

    // FAA AC 150/5300-13B Table 3-2 — hold-short distance from centerline by ADG (proxied by width).
    [Theory]
    [InlineData(60.0, 125.0)] // < 75     → 125 ft  (ADG I/II)
    [InlineData(74.0, 125.0)]
    [InlineData(75.0, 150.0)] // 75-99    → 150 ft  (ADG II/III)
    [InlineData(99.0, 150.0)]
    [InlineData(100.0, 200.0)] // 100-149  → 200 ft  (ADG III)
    [InlineData(149.0, 200.0)]
    [InlineData(150.0, 250.0)] // 150-199  → 250 ft  (ADG IV/V)
    [InlineData(199.0, 250.0)]
    [InlineData(200.0, 280.0)] // >= 200   → 280 ft  (ADG V/VI / CAT III)
    [InlineData(250.0, 280.0)]
    public void BuildRunwayRectangle_HoldShortDistance_MatchesWidthCategory(double widthFt, double expectedHoldShortFt)
    {
        GeoJsonParser.RunwayFeature rwy = NorthSouthRunway();
        RunwayRectangle rect = RunwayCrossingDetector.BuildRunwayRectangle(rwy, widthFt, RunwayIdentifier.Parse("18/36"));

        double actualHoldShortFt = rect.HoldShortNm * FeetPerNm;
        Assert.Equal(expectedHoldShortFt, actualHoldShortFt, precision: 0);
    }

    [Fact]
    public void BuildRunwayRectangle_HalfWidth_DerivedFromWidthFt()
    {
        GeoJsonParser.RunwayFeature rwy = NorthSouthRunway();
        RunwayRectangle rect = RunwayCrossingDetector.BuildRunwayRectangle(rwy, 200.0, RunwayIdentifier.Parse("18/36"));

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

        RunwayCrossingDetector.DetectRunwayCrossings(rwy, layout, coordIndex, ref nextNodeId, null);

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

        RunwayCrossingDetector.DetectRunwayCrossings(rwy, layout, coordIndex, ref nextNodeId, null);

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

        RunwayCrossingDetector.DetectRunwayCrossings(rwy, layout, coordIndex, ref nextNodeId, null);

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

        RunwayCrossingDetector.DetectRunwayCrossings(rwy, layout, coordIndex, ref nextNodeId, null);

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

        RunwayCrossingDetector.DetectRunwayCrossings(rwy, layout, coordIndex, ref nextNodeId, null);

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

        RunwayCrossingDetector.DetectRunwayCrossings(rwy, layout, coordIndex, ref nextNodeId, null);

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

        RunwayCrossingDetector.DetectRunwayCrossings(rwy, layout, coordIndex, ref nextNodeId, null);

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

        RunwayCrossingDetector.DetectRunwayCrossings(rwy, layout, coordIndex, ref nextNodeId, null);

        Assert.Equal(100, nextNodeId); // no new nodes
        Assert.Single(layout.Edges); // original edge untouched
    }

    // -------------------------------------------------------------------------
    // Real-world SFO E/28L hold-short placement
    // -------------------------------------------------------------------------

    /// <summary>
    /// SFO 28L (200 ft wide) E exit hold-short is ~260 ft from centerline per FAA
    /// AC 150/5300-13B Table 3-2 (CAT III/ADG V-VI). Loads the real sfo.geojson,
    /// finds the hold-short node on taxiway E for runway 28L, and verifies its
    /// cross-track distance from 28L centerline is within ±20 ft of 260 ft.
    /// </summary>
    [Fact]
    public void DetectRunwayCrossings_Sfo_TaxiwayE_AtRunway28L_HoldShortAt260Ft()
    {
        TestVnasData.EnsureInitialized();
        string path = Path.Combine("TestData", "sfo.geojson");
        if (!File.Exists(path))
        {
            return; // silently skip if TestData missing
        }

        AirportGroundLayout layout = GeoJsonParser.Parse("KSFO", File.ReadAllText(path), "KSFO");

        var combinedId = RunwayIdentifier.Parse("10R/28L");
        GroundRunway rwy = layout.Runways.Single(r => RunwayIdentifier.Parse(r.Name).Equals(combinedId));
        RunwayRectangle rect = RunwayCrossingDetector.BuildRunwayRectangle(rwy);

        // Hold-shorts on taxiway E for 28L: HS node must be connected to at least
        // one non-RWY edge whose taxiway name is "E".
        var eHsOnRunway28L = layout
            .Nodes.Values.Where(n => n.Type == GroundNodeType.RunwayHoldShort && n.RunwayId.HasValue && n.RunwayId.Value.Equals(combinedId))
            .Where(n => layout.Edges.Any(e => !e.IsRunwayCenterline && e.MatchesTaxiway("E") && e.HasNode(n.Id)))
            .ToList();

        Assert.NotEmpty(eHsOnRunway28L);

        // Take the HS node closest to the actual E/28L crossing by picking the
        // one with the smallest along-track variance — any one should do, since
        // E only crosses 28L once.
        GroundNode hs = eHsOnRunway28L.First();
        double crossTrackFt =
            Math.Abs(GeoMath.SignedCrossTrackDistanceNm(hs.Position, new LatLon(rect.RefLat, rect.RefLon), rect.TrueHeading)) * FeetPerNm;

        // Real-world measurement: 260 ft from 28L centerline to E hold-short bar.
        // Tolerance 240-285 ft: FAA Table 3-2 gives 280 ft for 200 ft wide CAT III
        // runways (ADG V/VI); 240 lower bound allows for ADG V rounding.
        Assert.InRange(crossTrackFt, 240.0, 285.0);
    }

    // -------------------------------------------------------------------------
    // Angle-independent hold-short placement (OAK 30/12 W-exits)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Every runway holding-position node sits at the same perpendicular (cross-track)
    /// distance from the runway centerline regardless of the exit taxiway's intersection
    /// angle — the AC 150/5300-13B RSA-boundary standoff is runway-parallel by construction.
    /// At OAK, the acute high-speed exits (W3/W4/W5, ~27-32 deg) must land at the same
    /// standoff as the right-angle exits (W1/W2/W6/W7, ~85-90 deg), not closer to the runway.
    /// Loads the real oak.geojson and asserts every 30/12 hold-short is within 6 ft of the
    /// runway's ideal <see cref="RunwayRectangle.HoldShortNm"/> — asserted against the computed
    /// ideal (not a hard-coded value) so it survives any future width-bucket change.
    /// </summary>
    [Fact]
    public void DetectRunwayCrossings_Oak_Runway30_12_HoldShortsAreAngleIndependent()
    {
        TestVnasData.EnsureInitialized();
        string path = Path.Combine("TestData", "oak.geojson");
        if (!File.Exists(path))
        {
            return; // silently skip if TestData missing
        }

        AirportGroundLayout layout = GeoJsonParser.Parse("KOAK", File.ReadAllText(path), "KOAK");

        var combinedId = RunwayIdentifier.Parse("30/12");
        GroundRunway rwy = layout.Runways.Single(r => RunwayIdentifier.Parse(r.Name).Equals(combinedId));
        RunwayRectangle rect = RunwayCrossingDetector.BuildRunwayRectangle(rwy);
        double idealFt = rect.HoldShortNm * FeetPerNm;

        var holdShorts = layout
            .Nodes.Values.Where(n => n.Type == GroundNodeType.RunwayHoldShort && n.RunwayId.HasValue && n.RunwayId.Value.Equals(combinedId))
            .ToList();

        Assert.NotEmpty(holdShorts);

        foreach (GroundNode? hs in holdShorts)
        {
            double crossTrackFt =
                Math.Abs(GeoMath.SignedCrossTrackDistanceNm(hs.Position, new LatLon(rect.RefLat, rect.RefLon), rect.TrueHeading)) * FeetPerNm;
            Assert.True(
                Math.Abs(crossTrackFt - idealFt) <= 6.0,
                $"Hold-short node {hs.Id} on 30/12 is {crossTrackFt:F1} ft from centerline; ideal is {idealFt:F1} ft (±6 ft)."
            );
        }
    }

    // -------------------------------------------------------------------------
    // Authoritative GeoJSON holdShortDistance (per-runway) overrides the width heuristic
    // -------------------------------------------------------------------------

    /// <summary>
    /// The vNAS airport map carries an explicit per-runway <c>holdShortDistance</c> (ft from
    /// centerline) for some runways. When present it is the authoritative standoff and must be used
    /// verbatim; when absent the width-based FAA Table 3-2 heuristic is the fallback. At OAK, 28R/10L
    /// authors 225 ft (heuristic would give 250) while 30/12 authors none (falls back to 250).
    /// </summary>
    [Fact]
    public void BuildRunwayRectangle_Oak_UsesAuthoredHoldShortDistance_ElseWidthFallback()
    {
        TestVnasData.EnsureInitialized();
        string path = Path.Combine("TestData", "oak.geojson");
        if (!File.Exists(path))
        {
            return; // silently skip if TestData missing
        }

        AirportGroundLayout layout = GeoJsonParser.Parse("KOAK", File.ReadAllText(path), "KOAK");

        GroundRunway rwy28R = layout.Runways.Single(r => RunwayIdentifier.Parse(r.Name).Equals(RunwayIdentifier.Parse("28R/10L")));
        double hs28R = RunwayCrossingDetector.BuildRunwayRectangle(rwy28R).HoldShortNm * FeetPerNm;
        Assert.Equal(225.0, hs28R, precision: 0); // authored value, not the 250 ft width heuristic

        GroundRunway rwy30 = layout.Runways.Single(r => RunwayIdentifier.Parse(r.Name).Equals(RunwayIdentifier.Parse("30/12")));
        double hs30 = RunwayCrossingDetector.BuildRunwayRectangle(rwy30).HoldShortNm * FeetPerNm;
        Assert.Equal(250.0, hs30, precision: 0); // no authored value → width fallback
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
    /// The unbarred escapes the committed layouts already had before junction fallback bars could be dropped (no dropped
    /// bar adds one). Each is a bar-placement gap of its own; the list only shrinks, and a fix that closes one removes it.
    /// </summary>
    private static readonly string[] KnownUnbarredEscapes =
    [
        "atl 9R - 27L: SJ/SJ - SJ2",
        "atl 9R - 27L: T",
        "atl 9R - 27L: R12",
        "atl 9R - 27L: R6",
        "atl 9R - 27L: R10",
        "atl 8R - 26L: B4/E3",
        "atl 8R - 26L: B2/E1",
        "atl 8R - 26L: B2",
        "atl 8L - 26R: V/V - H",
        "atl 8L - 26R: A7/A7 - A",
        "atl 8L - 26R: B",
        "atl 8L - 26R: A",
        "atl 8L - 26R: A5/A5 - A",
        "atl 8L - 26R: B2/B - B2",
        "atl 8L - 26R: A3/A3 - A",
        "atl 8L - 26R: B",
        "atl 8L - 26R: A",
        "aus 18R - 36L: V",
        "fll 10L - 28R: A1/A",
        "fll 10L - 28R: B1/B",
        "fll 10L - 28R: B12/B",
        "fll 10L - 28R: A/A8",
        "fll 10R - 28L: J",
        "fll 10R - 28L: J12/J",
        "fll 10R - 28L: J4",
        "lax 6R - 24L: E6/E",
        "mia 8R - 26L: N13/M/M11",
        "mia 8R - 26L: M1L/M1L - Q1",
        "mia 8R - 26L: N/Q/Q1/M1L",
        "mia 8R - 26L: P/M1/M/PAD",
        "mia 8R - 26L: M/PAD - M",
        "mia 12 - 30: Q1/PAD - Q1",
        "mia 9 - 27: V/U - V/V - U",
    ];

    private static string DescribeTaxiways(GroundNode node) =>
        string.Join("/", node.Edges.Where(edge => !edge.IsRunwayCenterline).Select(edge => edge.TaxiwayName).Distinct());

    /// <summary>The nodes beyond the runway's holding distance that a walk out from its pavement reaches without a bar.</summary>
    private static List<GroundNode> FindUnbarredEscapes(AirportGroundLayout layout, GroundRunway runway)
    {
        RunwayRectangle rect = RunwayCrossingDetector.BuildRunwayRectangle(runway);
        double holdingFt = rect.HoldShortNm * FeetPerNm;
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
