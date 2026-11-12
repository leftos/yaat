using System.Globalization;
using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Data.Airport;

/// <summary>
/// The sidecar <c>spotEdges</c> chains in the layout build: each chain's points resolve to layout nodes (a spot, the junction
/// of two taxiways, a taxiway vertex near a point, or a node at a point) and consecutive points are joined by one straight
/// edge named after the chain. An entry with a point the layout cannot resolve, or may not use, is warned about and adds
/// nothing.
/// </summary>
public class SpotEdgeBuildTests
{
    /// <summary>
    /// Taxiway A runs north along -122.200 with vertices at 37.700, 37.705 and 37.710; B ends on A's middle vertex; C lies
    /// apart from both; D bows west from A's first vertex to its last, sharing both; spots $1 and $2 and stand P1 lie east
    /// of A; helipad H1 lies west of C.
    /// </summary>
    private const string GeoJson = """
        {
          "type": "FeatureCollection",
          "features": [
            {
              "type": "Feature",
              "properties": { "type": "taxiway", "name": "A", "circular": false },
              "geometry": { "type": "LineString", "coordinates": [ [ -122.200, 37.700 ], [ -122.200, 37.705 ], [ -122.200, 37.710 ] ] }
            },
            {
              "type": "Feature",
              "properties": { "type": "taxiway", "name": "B", "circular": false },
              "geometry": { "type": "LineString", "coordinates": [ [ -122.205, 37.705 ], [ -122.200, 37.705 ] ] }
            },
            {
              "type": "Feature",
              "properties": { "type": "taxiway", "name": "C", "circular": false },
              "geometry": { "type": "LineString", "coordinates": [ [ -122.210, 37.720 ], [ -122.210, 37.725 ] ] }
            },
            {
              "type": "Feature",
              "properties": { "type": "taxiway", "name": "D", "circular": false },
              "geometry": { "type": "LineString", "coordinates": [ [ -122.200, 37.700 ], [ -122.206, 37.7055 ], [ -122.200, 37.710 ] ] }
            },
            {
              "type": "Feature",
              "properties": { "type": "spot", "name": "$1" },
              "geometry": { "type": "Point", "coordinates": [ -122.199, 37.701 ] }
            },
            {
              "type": "Feature",
              "properties": { "type": "spot", "name": "$2" },
              "geometry": { "type": "Point", "coordinates": [ -122.198, 37.702 ] }
            },
            {
              "type": "Feature",
              "properties": { "type": "parking", "name": "P1", "heading": 0 },
              "geometry": { "type": "Point", "coordinates": [ -122.197, 37.703 ] }
            },
            {
              "type": "Feature",
              "properties": { "type": "helipad", "name": "H1", "heading": 0 },
              "geometry": { "type": "Point", "coordinates": [ -122.212, 37.7225 ] }
            }
          ]
        }
        """;

    private static readonly Lazy<string?> SfoGeoJson = new(() =>
    {
        string path = Path.Combine("TestData", "sfo.geojson");
        return File.Exists(path) ? File.ReadAllText(path) : null;
    });

    [Fact]
    public void SpotChain_AddsOneRampEdgePerPair_BetweenTheResolvedNodes()
    {
        AirportGroundLayout layout = Parse(new SpotEdgeChain("RAMP", [Spot("1"), Spot("2"), Near("A", 37.7049, -122.2001)], null));

        GroundNode spot1 = NodeAt(layout, 37.701, -122.199);
        GroundNode spot2 = NodeAt(layout, 37.702, -122.198);
        GroundNode aVertex = NodeAt(layout, 37.705, -122.200);
        List<GroundEdge> added = SpotEdges(layout);
        List<(int Low, int High)> expected = [Pair(spot1, spot2), Pair(spot2, aVertex)];
        Assert.Equal(expected, added.Select(e => Pair(e.Nodes[0], e.Nodes[1])).Order());
        Assert.All(
            added,
            e =>
            {
                Assert.Equal("RAMP", e.TaxiwayName);
                Assert.True(e.IsRamp);
                Assert.Equal(GeoMath.DistanceNm(e.Nodes[0].Position, e.Nodes[1].Position), e.DistanceNm);
            }
        );
    }

    [Fact]
    public void Junction_ResolvesToTheSharedNode()
    {
        AirportGroundLayout layout = Parse(new SpotEdgeChain("RAMP", [Junction("A", "B"), Spot("1")], null));

        GroundEdge edge = Assert.Single(SpotEdges(layout));
        Assert.Equal(Pair(NodeAt(layout, 37.705, -122.200), NodeAt(layout, 37.701, -122.199)), Pair(edge.Nodes[0], edge.Nodes[1]));
    }

    [Fact]
    public void Junction_WithNoSharedNode_SkipsTheWholeEntry()
    {
        var capture = WarningLogCapture.Install();

        AirportGroundLayout layout = Parse(new SpotEdgeChain("RAMP", [Spot("2"), Spot("1"), Junction("A", "C")], null));

        Assert.Empty(SpotEdges(layout));
        AssertWarned(capture, "TST", "RAMP", "names junction A/C, where the two taxiways share no node");
    }

    [Fact]
    public void Junction_WithSeveralSharedNodes_SkipsTheWholeEntry()
    {
        var capture = WarningLogCapture.Install();

        AirportGroundLayout layout = Parse(new SpotEdgeChain("RAMP", [Spot("1"), Junction("A", "D")], null));

        Assert.Empty(SpotEdges(layout));
        AssertWarned(capture, "TST", "RAMP", "names junction A/D, where the two taxiways share 2 nodes");
    }

    [Fact]
    public void TaxiwayNear_BeyondTheCap_SkipsTheWholeEntry()
    {
        var capture = WarningLogCapture.Install();

        AirportGroundLayout layout = Parse(new SpotEdgeChain("RAMP", [Spot("1"), Near("A", 37.705, -122.190)], null));

        Assert.Empty(SpotEdges(layout));
        AssertWarned(capture, "TST", "RAMP", "finds no vertex of taxiway A within 0.15 nm of (37.705000, -122.190000)");
    }

    [Fact]
    public void Point_MintsANewNode_WithEveryExistingIdUnchanged()
    {
        AirportGroundLayout baseline = Parse();

        AirportGroundLayout layout = Parse(new SpotEdgeChain("RAMP", [Spot("1"), Point(37.7035, -122.1985), Spot("2")], null));

        Assert.All(baseline.Nodes.Values, n => Assert.Equal(n.Position, layout.Nodes[n.Id].Position));
        GroundNode minted = Assert.Single(layout.Nodes.Values, n => !baseline.Nodes.ContainsKey(n.Id));
        Assert.True(minted.Id > baseline.Nodes.Keys.Max());
        Assert.Equal(new LatLon(37.7035, -122.1985), minted.Position);
        Assert.Equal(GroundNodeType.TaxiwayIntersection, minted.Type);
        Assert.Equal(2, SpotEdges(layout).Count(e => e.HasNode(minted.Id)));
    }

    /// <summary>A point within the snap tolerance of a taxiway node is that node: no node is minted.</summary>
    [Fact]
    public void Point_OnATaxiwayNode_ReusesIt()
    {
        AirportGroundLayout baseline = Parse();

        AirportGroundLayout layout = Parse(new SpotEdgeChain("RAMP", [Spot("1"), Point(37.70001, -122.20001)], null));

        Assert.Equal(baseline.Nodes.Count, layout.Nodes.Count);
        GroundEdge edge = Assert.Single(SpotEdges(layout));
        Assert.Equal(Pair(NodeAt(layout, 37.701, -122.199), NodeAt(layout, 37.700, -122.200)), Pair(edge.Nodes[0], edge.Nodes[1]));
    }

    /// <summary>
    /// A point on a stand would mint a node on top of it, since stands are not in the snap index: refused, nothing added.
    /// </summary>
    [Fact]
    public void Point_OnAStand_SkipsTheWholeEntry()
    {
        AirportGroundLayout baseline = Parse();
        var capture = WarningLogCapture.Install();

        AirportGroundLayout layout = Parse(new SpotEdgeChain("RAMP", [Spot("1"), Point(37.70301, -122.197)], null));

        Assert.Empty(SpotEdges(layout));
        Assert.Equal(baseline.Nodes.Count, layout.Nodes.Count);
        int stand = layout.FindParkingByName("P1")!.Id;
        AssertWarned(capture, "TST", "RAMP", $"resolves point (37.703010, -122.197000) to node {stand}, a parking node");
    }

    /// <summary>
    /// A point on a helipad would mint a node on top of it, since helipads are not in the snap index: refused, nothing added.
    /// </summary>
    [Fact]
    public void Point_OnAHelipad_SkipsTheWholeEntry()
    {
        AirportGroundLayout baseline = Parse();
        var capture = WarningLogCapture.Install();

        AirportGroundLayout layout = Parse(new SpotEdgeChain("RAMP", [Spot("1"), Point(37.72251, -122.212)], null));

        Assert.Empty(SpotEdges(layout));
        Assert.Equal(baseline.Nodes.Count, layout.Nodes.Count);
        int helipad = Assert.Single(layout.Nodes.Values, n => n.Type == GroundNodeType.Helipad).Id;
        AssertWarned(capture, "TST", "RAMP", $"resolves point (37.722510, -122.212000) to node {helipad}, a helipad node");
    }

    /// <summary>Two consecutive points on one node would be a zero-length edge: refused, nothing added.</summary>
    [Fact]
    public void ConsecutivePointsOnOneNode_SkipTheWholeEntry()
    {
        var capture = WarningLogCapture.Install();

        AirportGroundLayout layout = Parse(new SpotEdgeChain("RAMP", [Spot("2"), Spot("1"), Point(37.70101, -122.199)], null));

        Assert.Empty(SpotEdges(layout));
        AssertWarned(capture, "TST", "RAMP", "joins point 1 to point 2 at the same node");
    }

    /// <summary>A pair already joined by an edge of the chain's name gets no second edge.</summary>
    [Fact]
    public void PairAlreadyJoinedByThatName_AddsNoSecondEdge()
    {
        AirportGroundLayout layout = Parse(
            new SpotEdgeChain("RAMP", [Spot("1"), Spot("2")], null),
            new SpotEdgeChain("RAMP", [Spot("2"), Spot("1")], null)
        );

        GroundEdge edge = Assert.Single(SpotEdges(layout));
        Assert.Equal(Pair(NodeAt(layout, 37.701, -122.199), NodeAt(layout, 37.702, -122.198)), Pair(edge.Nodes[0], edge.Nodes[1]));
    }

    [Fact]
    public void UnknownSpot_LeavesTheLayoutIdenticalToNoEntry()
    {
        AirportGroundLayout baseline = Parse();
        var capture = WarningLogCapture.Install();

        AirportGroundLayout layout = Parse(new SpotEdgeChain("RAMP", [Spot("1"), Point(37.7035, -122.1985), Spot("9")], null));

        Assert.Equal(NodeSet(baseline), NodeSet(layout));
        Assert.Equal(EdgeSet(baseline), EdgeSet(layout));
        AssertWarned(capture, "TST", "RAMP", "names spot 9, which the layout does not have");
    }

    [Fact]
    public void NamedLane_KeepsItsName_AndIsNotRamp()
    {
        AirportGroundLayout layout = Parse(new SpotEdgeChain("L1", [Spot("1"), Spot("2")], null));

        GroundEdge edge = Assert.Single(SpotEdges(layout));
        Assert.Equal("L1", edge.TaxiwayName);
        Assert.False(edge.IsRamp);
    }

    /// <summary>A taxiway vertex nearest the point that is a runway hold-short node is refused: the entry adds nothing.</summary>
    [Fact]
    public void ChainNeverResolvesToAHoldShortNode()
    {
        if (SfoGeoJson.Value is not { } geoJson)
        {
            return;
        }

        AirportGroundLayout baseline = ParseSfo(geoJson);
        GroundEdge onHoldShort = baseline.Edges.First(e =>
            !e.IsRamp
            && !e.IsRunwayCenterline
            && !e.IsRunwayCrossingLink
            && e.Nodes.Any(n => n.Type == GroundNodeType.RunwayHoldShort)
            && e.Nodes.All(n => n.Type is not GroundNodeType.Parking and not GroundNodeType.Helipad)
        );
        GroundNode holdShort = onHoldShort.Nodes.First(n => n.Type == GroundNodeType.RunwayHoldShort);
        LatLon beside = new(holdShort.Position.Lat + 0.0005, holdShort.Position.Lon);
        var capture = WarningLogCapture.Install();

        AirportGroundLayout layout = ParseSfo(
            geoJson,
            new SpotEdgeChain(
                "RAMP",
                [Near(onHoldShort.TaxiwayName, holdShort.Position.Lat, holdShort.Position.Lon), Point(beside.Lat, beside.Lon)],
                null
            )
        );

        Assert.Empty(SpotEdges(layout));
        Assert.Equal(baseline.Nodes.Count, layout.Nodes.Count);
        AssertWarned(
            capture,
            "SFO",
            "RAMP",
            $"resolves taxiway {onHoldShort.TaxiwayName} near {Format(holdShort.Position)} to node {holdShort.Id}, a runway hold-short node"
        );
    }

    /// <summary>A point placed on a runway hold-short node snaps to it and is refused.</summary>
    [Fact]
    public void Point_OnAHoldShortNode_SkipsTheWholeEntry()
    {
        if (SfoGeoJson.Value is not { } geoJson)
        {
            return;
        }

        AirportGroundLayout baseline = ParseSfo(geoJson);
        GroundNode holdShort = Isolated(baseline, n => n.Type == GroundNodeType.RunwayHoldShort);

        AssertPointRefused(geoJson, baseline, holdShort, "a runway hold-short node");
    }

    /// <summary>A point placed on a runway centreline node snaps to it and is refused.</summary>
    [Fact]
    public void Point_OnARunwayCentrelineNode_SkipsTheWholeEntry()
    {
        if (SfoGeoJson.Value is not { } geoJson)
        {
            return;
        }

        AirportGroundLayout baseline = ParseSfo(geoJson);
        HashSet<int> centreline = [.. baseline.Edges.Where(e => e.IsRunwayCenterline).SelectMany(e => e.Nodes).Select(n => n.Id)];
        GroundNode onCentreline = Isolated(
            baseline,
            n => centreline.Contains(n.Id) && (n.Type is not GroundNodeType.RunwayHoldShort and not GroundNodeType.Parking)
        );

        AssertPointRefused(geoJson, baseline, onCentreline, "a runway centreline node");
    }

    private static void AssertPointRefused(string geoJson, AirportGroundLayout baseline, GroundNode target, string reason)
    {
        LatLon beside = new(target.Position.Lat + 0.0005, target.Position.Lon);
        var capture = WarningLogCapture.Install();

        AirportGroundLayout layout = ParseSfo(
            geoJson,
            new SpotEdgeChain("RAMP", [Point(target.Position.Lat, target.Position.Lon), Point(beside.Lat, beside.Lon)], null)
        );

        Assert.Empty(SpotEdges(layout));
        Assert.Equal(baseline.Nodes.Count, layout.Nodes.Count);
        AssertWarned(capture, "SFO", "RAMP", $"resolves point {Format(target.Position)} to node {target.Id}, {reason}");
    }

    /// <summary>The first node matching <paramref name="match"/> with no other node within the parser's snap tolerance.</summary>
    private static GroundNode Isolated(AirportGroundLayout layout, Func<GroundNode, bool> match) =>
        layout.Nodes.Values.First(n =>
            match(n)
            && !layout.Nodes.Values.Any(o =>
                (o.Id != n.Id)
                && (Math.Abs(o.Position.Lat - n.Position.Lat) <= GeoJsonParser.SnapToleranceDeg)
                && (Math.Abs(o.Position.Lon - n.Position.Lon) <= GeoJsonParser.SnapToleranceDeg)
            )
        );

    private static AirportGroundLayout Parse(params SpotEdgeChain[] chains) =>
        GeoJsonParser.Parse("TST", GeoJson, null, new LayoutInputs([], VideoMap: null, SpotEdges: chains), FilletMode.None);

    private static AirportGroundLayout ParseSfo(string geoJson, params SpotEdgeChain[] chains) =>
        GeoJsonParser.Parse("SFO", geoJson, null, new LayoutInputs([], VideoMap: null, SpotEdges: chains), FilletMode.None);

    private static SpotEdgePoint Spot(string name) => new(SpotEdgePointKind.Spot, name, null, null, null, null);

    private static SpotEdgePoint Junction(string a, string b) => new(SpotEdgePointKind.Junction, null, a, b, null, null);

    private static SpotEdgePoint Near(string taxiway, double lat, double lon) =>
        new(SpotEdgePointKind.TaxiwayNear, null, null, null, taxiway, new LatLon(lat, lon));

    private static SpotEdgePoint Point(double lat, double lon) => new(SpotEdgePointKind.Point, null, null, null, null, new LatLon(lat, lon));

    private static string Format(LatLon position) => string.Create(CultureInfo.InvariantCulture, $"({position.Lat:F6}, {position.Lon:F6})");

    private static GroundNode NodeAt(AirportGroundLayout layout, double lat, double lon) =>
        Assert.Single(layout.Nodes.Values, n => (Math.Abs(n.Position.Lat - lat) < 1e-7) && (Math.Abs(n.Position.Lon - lon) < 1e-7));

    private static List<GroundEdge> SpotEdges(AirportGroundLayout layout) => [.. layout.Edges.Where(e => e.Origin == SpotEdgeBuilder.Origin)];

    /// <summary>An edge's two node ids, lower first.</summary>
    private static (int Low, int High) Pair(GroundNode a, GroundNode b) => a.Id < b.Id ? (a.Id, b.Id) : (b.Id, a.Id);

    private static List<(int Id, LatLon Position, GroundNodeType Type)> NodeSet(AirportGroundLayout layout) =>
        [.. layout.Nodes.Values.Select(n => (n.Id, n.Position, n.Type)).OrderBy(n => n.Id)];

    private static List<string> EdgeSet(AirportGroundLayout layout) =>
        [.. layout.Edges.Select(e => $"{Pair(e.Nodes[0], e.Nodes[1])} {e.TaxiwayName} {e.Origin}").Order(StringComparer.Ordinal)];

    /// <summary>Asserts the layout build logged exactly the warning for <paramref name="chain"/> with <paramref name="problem"/>.</summary>
    private static void AssertWarned(WarningLogCapture capture, string airport, string chain, string problem) =>
        Assert.Contains($"{airport}: sidecar spotEdges entry {chain} {problem}; ignored", capture.Warnings);
}
