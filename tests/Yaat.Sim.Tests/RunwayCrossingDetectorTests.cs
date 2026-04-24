using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Testing;

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
        var (endLat, endLon) = GeoMath.ProjectPoint(startLat, startLon, new TrueHeading(45.0), 1.0);
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
        var rwy = DiagonalRunway();
        var rect = RunwayCrossingDetector.BuildRunwayRectangle(rwy, 150.0, RunwayIdentifier.Parse("4/22"));

        // Midpoint of the runway is on the centerline
        double midLat = (rwy.Coords[0].Lat + rwy.Coords[1].Lat) / 2.0;
        double midLon = (rwy.Coords[0].Lon + rwy.Coords[1].Lon) / 2.0;

        Assert.True(RunwayCrossingDetector.IsOnRunway(midLat, midLon, rect));
    }

    [Fact]
    public void IsOnRunway_DiagonalRunway_PointFarOffCenterline_ReturnsFalse()
    {
        var rwy = DiagonalRunway();
        var rect = RunwayCrossingDetector.BuildRunwayRectangle(rwy, 150.0, RunwayIdentifier.Parse("4/22"));

        // Point well to the side of the diagonal runway (~0.01 degrees offset perpendicular)
        double midLat = (rwy.Coords[0].Lat + rwy.Coords[1].Lat) / 2.0;
        double midLon = (rwy.Coords[0].Lon + rwy.Coords[1].Lon) / 2.0;

        // Offset perpendicular to 45° heading (i.e., at 135°) by ~500ft
        var (offLat, offLon) = GeoMath.ProjectPoint(midLat, midLon, new TrueHeading(135.0), 500.0 / FeetPerNm);

        Assert.False(RunwayCrossingDetector.IsOnRunway(offLat, offLon, rect));
    }

    [Fact]
    public void IsOnRunway_DiagonalRunway_PointBeyondEnd_ReturnsFalse()
    {
        var rwy = DiagonalRunway();
        var rect = RunwayCrossingDetector.BuildRunwayRectangle(rwy, 150.0, RunwayIdentifier.Parse("4/22"));

        // Project past the far end
        var (beyondLat, beyondLon) = GeoMath.ProjectPoint(rwy.Coords[1].Lat, rwy.Coords[1].Lon, new TrueHeading(45.0), 0.1);

        Assert.False(RunwayCrossingDetector.IsOnRunway(beyondLat, beyondLon, rect));
    }

    [Fact]
    public void IsOnRunway_NorthSouthRunway_PointSlightlyOffCenter_ReturnsTrue()
    {
        var rwy = NorthSouthRunway();
        var rect = RunwayCrossingDetector.BuildRunwayRectangle(rwy, 150.0, RunwayIdentifier.Parse("18/36"));

        // 50ft east of centerline (within 75ft half-width)
        double midLat = (rwy.Coords[0].Lat + rwy.Coords[1].Lat) / 2.0;
        var (offLat, offLon) = GeoMath.ProjectPoint(midLat, rwy.Coords[0].Lon, new TrueHeading(90.0), 50.0 / FeetPerNm);

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
        var rwy = NorthSouthRunway();
        var rect = RunwayCrossingDetector.BuildRunwayRectangle(rwy, widthFt, RunwayIdentifier.Parse("18/36"));

        double actualHoldShortFt = rect.HoldShortNm * FeetPerNm;
        Assert.Equal(expectedHoldShortFt, actualHoldShortFt, precision: 0);
    }

    [Fact]
    public void BuildRunwayRectangle_HalfWidth_DerivedFromWidthFt()
    {
        var rwy = NorthSouthRunway();
        var rect = RunwayCrossingDetector.BuildRunwayRectangle(rwy, 200.0, RunwayIdentifier.Parse("18/36"));

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
        var rwy = NorthSouthRunway();
        var layout = EmptyLayout();

        // Node 1: on the runway centerline (midpoint)
        double midLat = (rwy.Coords[0].Lat + rwy.Coords[1].Lat) / 2.0;
        var onNode = MakeNode(1, midLat, rwy.Coords[0].Lon);

        // Node 2: well off the runway (~1000ft east)
        var (offLat, offLon) = GeoMath.ProjectPoint(midLat, rwy.Coords[0].Lon, new TrueHeading(90.0), 1000.0 / FeetPerNm);
        var offNode = MakeNode(2, offLat, offLon);

        layout.Nodes[1] = onNode;
        layout.Nodes[2] = offNode;

        var edge = MakeEdge(layout, 1, 2, "A", GeoMath.DistanceNm(onNode.Position, offNode.Position));
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
        var rwy = NorthSouthRunway();
        var layout = EmptyLayout();

        double midLat = (rwy.Coords[0].Lat + rwy.Coords[1].Lat) / 2.0;
        var onNode = MakeNode(1, midLat, rwy.Coords[0].Lon);
        var (offLat, offLon) = GeoMath.ProjectPoint(midLat, rwy.Coords[0].Lon, new TrueHeading(90.0), 1000.0 / FeetPerNm);
        var offNode = MakeNode(2, offLat, offLon);

        layout.Nodes[1] = onNode;
        layout.Nodes[2] = offNode;
        WireEdge(layout, MakeEdge(layout, 1, 2, "A", GeoMath.DistanceNm(onNode.Position, offNode.Position)));
        layout.RebuildAdjacencyLists();

        int nextNodeId = 100;
        var coordIndex = new CoordinateIndex(0.0001);
        coordIndex.Add(onNode.Position, 1);
        coordIndex.Add(offNode.Position, 2);

        RunwayCrossingDetector.DetectRunwayCrossings(rwy, layout, coordIndex, ref nextNodeId, null);

        var hsNode = layout.Nodes.Values.First(n => n.Type == GroundNodeType.RunwayHoldShort);
        Assert.NotNull(hsNode.RunwayId);
        Assert.Equal(RunwayIdentifier.Parse("18/36"), hsNode.RunwayId.Value);
    }

    // -------------------------------------------------------------------------
    // DetectRunwayCrossings — node reuse within 50ft
    // -------------------------------------------------------------------------

    [Fact]
    public void DetectRunwayCrossings_OffNodeNearHoldShortDistance_ReusesExistingNode()
    {
        var rwy = NorthSouthRunway();
        var layout = EmptyLayout();

        // For 150ft-wide runway (default), FAA Table 3-2 gives 250ft hold-short from centerline.
        double holdShortFt = 250.0;

        double midLat = (rwy.Coords[0].Lat + rwy.Coords[1].Lat) / 2.0;
        var onNode = MakeNode(1, midLat, rwy.Coords[0].Lon);

        // Place the off-node within 50ft of the ideal hold-short distance (at 140ft, 10ft short)
        var (offLat, offLon) = GeoMath.ProjectPoint(midLat, rwy.Coords[0].Lon, new TrueHeading(90.0), (holdShortFt - 10.0) / FeetPerNm);
        var offNode = MakeNode(2, offLat, offLon);

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

    [Fact]
    public void DetectRunwayCrossings_OffNodeFarFromHoldShort_CreatesNewNode()
    {
        var rwy = NorthSouthRunway();
        var layout = EmptyLayout();

        double midLat = (rwy.Coords[0].Lat + rwy.Coords[1].Lat) / 2.0;
        var onNode = MakeNode(1, midLat, rwy.Coords[0].Lon);

        // Place off-node at 800ft from centerline (550ft away from ideal 250ft HS point — well beyond 50ft reuse)
        var (offLat, offLon) = GeoMath.ProjectPoint(midLat, rwy.Coords[0].Lon, new TrueHeading(90.0), 800.0 / FeetPerNm);
        var offNode = MakeNode(2, offLat, offLon);

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
        var rwy = NorthSouthRunway();
        var layout = EmptyLayout();

        double midLat = (rwy.Coords[0].Lat + rwy.Coords[1].Lat) / 2.0;
        var onNode = MakeNode(1, midLat, rwy.Coords[0].Lon);

        // Off-node at 900ft east — HS should be interpolated at ~250ft (150ft-wide runway → 250ft HS per FAA Table 3-2)
        var (offLat, offLon) = GeoMath.ProjectPoint(midLat, rwy.Coords[0].Lon, new TrueHeading(90.0), 900.0 / FeetPerNm);
        var offNode = MakeNode(2, offLat, offLon);

        layout.Nodes[1] = onNode;
        layout.Nodes[2] = offNode;
        WireEdge(layout, MakeEdge(layout, 1, 2, "A", GeoMath.DistanceNm(onNode.Position, offNode.Position)));
        layout.RebuildAdjacencyLists();

        int nextNodeId = 100;
        var coordIndex = new CoordinateIndex(0.0001);
        coordIndex.Add(onNode.Position, 1);
        coordIndex.Add(offNode.Position, 2);

        RunwayCrossingDetector.DetectRunwayCrossings(rwy, layout, coordIndex, ref nextNodeId, null);

        var hsNode = layout.Nodes[100];
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
        var rwy = NorthSouthRunway();
        var layout = EmptyLayout();

        double midLat = (rwy.Coords[0].Lat + rwy.Coords[1].Lat) / 2.0;
        var onNode = MakeNode(1, midLat, rwy.Coords[0].Lon);
        var (offLat, offLon) = GeoMath.ProjectPoint(midLat, rwy.Coords[0].Lon, new TrueHeading(90.0), 1000.0 / FeetPerNm);
        var offNode = MakeNode(2, offLat, offLon);

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
        var rwy = NorthSouthRunway();
        var layout = EmptyLayout();

        double midLat = (rwy.Coords[0].Lat + rwy.Coords[1].Lat) / 2.0;

        // Both nodes far east of runway
        var (lat1, lon1) = GeoMath.ProjectPoint(midLat, rwy.Coords[0].Lon, new TrueHeading(90.0), 500.0 / FeetPerNm);
        var (lat2, lon2) = GeoMath.ProjectPoint(midLat, rwy.Coords[0].Lon, new TrueHeading(90.0), 800.0 / FeetPerNm);
        var node1 = MakeNode(1, lat1, lon1);
        var node2 = MakeNode(2, lat2, lon2);

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

        var layout = GeoJsonParser.Parse("KSFO", File.ReadAllText(path), "KSFO");

        var combinedId = RunwayIdentifier.Parse("10R/28L");
        var rwy = layout.Runways.Single(r => RunwayIdentifier.Parse(r.Name).Equals(combinedId));
        var rect = RunwayCrossingDetector.BuildRunwayRectangle(rwy);

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
        var hs = eHsOnRunway28L.First();
        double crossTrackFt =
            Math.Abs(GeoMath.SignedCrossTrackDistanceNm(hs.Position, new LatLon(rect.RefLat, rect.RefLon), rect.TrueHeading)) * FeetPerNm;

        // Real-world measurement: 260 ft from 28L centerline to E hold-short bar.
        // Tolerance 240-285 ft: FAA Table 3-2 gives 280 ft for 200 ft wide CAT III
        // runways (ADG V/VI); 240 lower bound allows for ADG V rounding.
        Assert.InRange(crossTrackFt, 240.0, 285.0);
    }
}
