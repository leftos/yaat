using Xunit;
using Yaat.Sim.Data.Airport;

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
            Latitude = lat,
            Longitude = lon,
            Type = type,
        };
    }

    private static GroundEdge MakeEdge(int from, int to, string taxiway, double dist = 0.1)
    {
        return new GroundEdge
        {
            FromNodeId = from,
            ToNodeId = to,
            TaxiwayName = taxiway,
            DistanceNm = dist,
        };
    }

    private static void WireEdge(AirportGroundLayout layout, GroundEdge edge)
    {
        layout.Edges.Add(edge);
        layout.Nodes[edge.FromNodeId].Edges.Add(edge);
        layout.Nodes[edge.ToNodeId].Edges.Add(edge);
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

    [Theory]
    [InlineData(60.0, 105.0)] // halfWidth 30 + 75 buffer
    [InlineData(74.0, 112.0)] // halfWidth 37 + 75 buffer
    [InlineData(100.0, 125.0)] // halfWidth 50 + 75 buffer
    [InlineData(150.0, 150.0)] // halfWidth 75 + 75 buffer
    [InlineData(200.0, 175.0)] // halfWidth 100 + 75 buffer
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

        var edge = MakeEdge(1, 2, "A", GeoMath.DistanceNm(onNode.Latitude, onNode.Longitude, offNode.Latitude, offNode.Longitude));
        WireEdge(layout, edge);

        int nextNodeId = 100;
        var coordIndex = new CoordinateIndex(0.0001);
        coordIndex.Add(onNode.Latitude, onNode.Longitude, 1);
        coordIndex.Add(offNode.Latitude, offNode.Longitude, 2);

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
        WireEdge(layout, MakeEdge(1, 2, "A", GeoMath.DistanceNm(onNode.Latitude, onNode.Longitude, offNode.Latitude, offNode.Longitude)));

        int nextNodeId = 100;
        var coordIndex = new CoordinateIndex(0.0001);
        coordIndex.Add(onNode.Latitude, onNode.Longitude, 1);
        coordIndex.Add(offNode.Latitude, offNode.Longitude, 2);

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

        // For 150ft-wide runway (default), hold-short distance = 75 + 75 = 150ft from centerline
        double holdShortFt = 150.0;

        double midLat = (rwy.Coords[0].Lat + rwy.Coords[1].Lat) / 2.0;
        var onNode = MakeNode(1, midLat, rwy.Coords[0].Lon);

        // Place the off-node within 50ft of the ideal hold-short distance (at 140ft, 10ft short)
        var (offLat, offLon) = GeoMath.ProjectPoint(midLat, rwy.Coords[0].Lon, new TrueHeading(90.0), (holdShortFt - 10.0) / FeetPerNm);
        var offNode = MakeNode(2, offLat, offLon);

        layout.Nodes[1] = onNode;
        layout.Nodes[2] = offNode;
        WireEdge(layout, MakeEdge(1, 2, "A", GeoMath.DistanceNm(onNode.Latitude, onNode.Longitude, offNode.Latitude, offNode.Longitude)));

        int nextNodeId = 100;
        var coordIndex = new CoordinateIndex(0.0001);
        coordIndex.Add(onNode.Latitude, onNode.Longitude, 1);
        coordIndex.Add(offNode.Latitude, offNode.Longitude, 2);

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

        // Place off-node at 600ft from centerline (450ft away from ideal 150ft HS point — well beyond 50ft reuse)
        var (offLat, offLon) = GeoMath.ProjectPoint(midLat, rwy.Coords[0].Lon, new TrueHeading(90.0), 600.0 / FeetPerNm);
        var offNode = MakeNode(2, offLat, offLon);

        layout.Nodes[1] = onNode;
        layout.Nodes[2] = offNode;
        WireEdge(layout, MakeEdge(1, 2, "A", GeoMath.DistanceNm(onNode.Latitude, onNode.Longitude, offNode.Latitude, offNode.Longitude)));

        int nextNodeId = 100;
        var coordIndex = new CoordinateIndex(0.0001);
        coordIndex.Add(onNode.Latitude, onNode.Longitude, 1);
        coordIndex.Add(offNode.Latitude, offNode.Longitude, 2);

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

        // Off-node at 800ft east — HS should be interpolated at ~150ft
        var (offLat, offLon) = GeoMath.ProjectPoint(midLat, rwy.Coords[0].Lon, new TrueHeading(90.0), 800.0 / FeetPerNm);
        var offNode = MakeNode(2, offLat, offLon);

        layout.Nodes[1] = onNode;
        layout.Nodes[2] = offNode;
        WireEdge(layout, MakeEdge(1, 2, "A", GeoMath.DistanceNm(onNode.Latitude, onNode.Longitude, offNode.Latitude, offNode.Longitude)));

        int nextNodeId = 100;
        var coordIndex = new CoordinateIndex(0.0001);
        coordIndex.Add(onNode.Latitude, onNode.Longitude, 1);
        coordIndex.Add(offNode.Latitude, offNode.Longitude, 2);

        RunwayCrossingDetector.DetectRunwayCrossings(rwy, layout, coordIndex, ref nextNodeId, null);

        var hsNode = layout.Nodes[100];
        // HS node should be between on-node and off-node (latitude should be same since E-W edge,
        // longitude should be between the two)
        double hsDistFromCenter = GeoMath.DistanceNm(midLat, rwy.Coords[0].Lon, hsNode.Latitude, hsNode.Longitude) * FeetPerNm;
        // Should be near the hold-short distance (150ft for 150ft-wide runway)
        Assert.InRange(hsDistFromCenter, 100.0, 200.0);
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
        WireEdge(layout, MakeEdge(1, 2, "RWY18/36", 0.1));

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
        WireEdge(layout, MakeEdge(1, 2, "A", 0.05));

        int nextNodeId = 100;
        var coordIndex = new CoordinateIndex(0.0001);

        RunwayCrossingDetector.DetectRunwayCrossings(rwy, layout, coordIndex, ref nextNodeId, null);

        Assert.Equal(100, nextNodeId); // no new nodes
        Assert.Single(layout.Edges); // original edge untouched
    }
}
