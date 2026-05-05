using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;

namespace Yaat.Sim.Tests;

public class FilletArcGeneratorTests
{
    private readonly ITestOutputHelper _output;

    public FilletArcGeneratorTests(ITestOutputHelper output)
    {
        _output = output;
        SimLog.InitializeForTest(LoggerFactory.Create(b => b.AddXUnit(output).SetMinimumLevel(LogLevel.Debug)));
    }

    /// <summary>
    /// Build a simple layout with a single intersection node and the given edge endpoints.
    /// The intersection node is at (0, 0) and edges connect to nodes at the given lat/lon positions.
    /// </summary>
    private static AirportGroundLayout BuildLayout(params (double Lat, double Lon)[] endpoints)
    {
        var layout = new AirportGroundLayout { AirportId = "TEST" };

        var intersection = new GroundNode
        {
            Id = 0,
            Position = new LatLon(0, 0),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        layout.Nodes[0] = intersection;

        for (int i = 0; i < endpoints.Length; i++)
        {
            int id = i + 1;
            var node = new GroundNode
            {
                Id = id,
                Position = new LatLon(endpoints[i].Lat, endpoints[i].Lon),
                Type = GroundNodeType.TaxiwayIntersection,
            };
            layout.Nodes[id] = node;

            double dist = GeoMath.DistanceNm(LatLon.Zero, node.Position);
            var edge = new GroundEdge
            {
                Nodes = [intersection, node],
                TaxiwayName = $"T{id}",
                DistanceNm = dist,
            };
            layout.Edges.Add(edge);
        }

        layout.RebuildAdjacencyLists();
        return layout;
    }

    [Fact]
    public void TwoEdges_90Degrees_CreatesOneArc()
    {
        // North and East edges forming a 90° turn at the origin
        // Node 1 at ~0.01° north, Node 2 at ~0.01° east
        var layout = BuildLayout((0.01, 0), (0, 0.01));

        FilletArcGenerator.Apply(layout);

        // Intersection node 0 should be removed
        Assert.False(layout.Nodes.ContainsKey(0));

        // Should have exactly 1 arc
        Assert.Single(layout.Arcs);

        var arc = layout.Arcs[0];
        Assert.True(arc.MinRadiusOfCurvatureFt > 0);
        Assert.True(arc.DistanceNm > 0);

        // Two shortened edges remain (from original endpoints to tangent points)
        Assert.Equal(2, layout.Edges.Count);

        // Two new tangent-point nodes created
        Assert.Equal(4, layout.Nodes.Count); // 2 original + 2 tangent
    }

    [Fact]
    public void TwoEdges_Collinear_PreservesIntersectionNode()
    {
        // North and South edges — 180° apart = 0° turn angle (collinear)
        var layout = BuildLayout((0.01, 0), (-0.01, 0));

        FilletArcGenerator.Apply(layout);

        // Intersection node preserved (collinear pair creates preserve stubs)
        Assert.True(layout.Nodes.ContainsKey(0));

        // No arcs — collinear pair, no turn
        Assert.Empty(layout.Arcs);

        // Two collinear preserve stubs: 0→1 and 0→2
        Assert.Equal(2, layout.Edges.Count);
        Assert.Contains(layout.Edges, e => e.HasNode(0) && e.HasNode(1));
        Assert.Contains(layout.Edges, e => e.HasNode(0) && e.HasNode(2));
    }

    [Fact]
    public void ThreeEdges_CollinearPairPlusPerpendicular_PreservesNodeAndCreatesArcs()
    {
        // Node 1 North, Node 2 South (collinear), Node 3 East (perpendicular)
        var layout = BuildLayout((0.01, 0), (-0.01, 0), (0, 0.01));

        FilletArcGenerator.Apply(layout);

        // Intersection node preserved (collinear pair)
        Assert.True(layout.Nodes.ContainsKey(0));

        // 2 arcs (N→E and S→E)
        Assert.Equal(2, layout.Arcs.Count);
    }

    [Fact]
    public void HoldShortNode_IsNotFilleted()
    {
        var layout = new AirportGroundLayout { AirportId = "TEST" };

        var holdShort = new GroundNode
        {
            Id = 0,
            Position = new LatLon(0, 0),
            Type = GroundNodeType.RunwayHoldShort,
            RunwayId = RunwayIdentifier.Parse("28L"),
        };
        layout.Nodes[0] = holdShort;

        var nodeA = new GroundNode
        {
            Id = 1,
            Position = new LatLon(0.01, 0),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var nodeB = new GroundNode
        {
            Id = 2,
            Position = new LatLon(0, 0.01),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        layout.Nodes[1] = nodeA;
        layout.Nodes[2] = nodeB;

        layout.Edges.Add(
            new GroundEdge
            {
                Nodes = [holdShort, nodeA],
                TaxiwayName = "A",
                DistanceNm = GeoMath.DistanceNm(0, 0, 0.01, 0),
            }
        );
        layout.Edges.Add(
            new GroundEdge
            {
                Nodes = [holdShort, nodeB],
                TaxiwayName = "B",
                DistanceNm = GeoMath.DistanceNm(0, 0, 0, 0.01),
            }
        );
        layout.RebuildAdjacencyLists();

        FilletArcGenerator.Apply(layout);

        // Hold-short node should still exist
        Assert.True(layout.Nodes.ContainsKey(0));
        Assert.Empty(layout.Arcs);
    }

    [Fact]
    public void ParkingNode_IsNotFilleted()
    {
        var layout = new AirportGroundLayout { AirportId = "TEST" };

        var parking = new GroundNode
        {
            Id = 0,
            Position = new LatLon(0, 0),
            Type = GroundNodeType.Parking,
            Name = "A1",
            TrueHeading = new TrueHeading(90),
        };
        layout.Nodes[0] = parking;

        var nodeA = new GroundNode
        {
            Id = 1,
            Position = new LatLon(0.01, 0),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var nodeB = new GroundNode
        {
            Id = 2,
            Position = new LatLon(0, 0.01),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        layout.Nodes[1] = nodeA;
        layout.Nodes[2] = nodeB;

        layout.Edges.Add(
            new GroundEdge
            {
                Nodes = [parking, nodeA],
                TaxiwayName = "A",
                DistanceNm = GeoMath.DistanceNm(0, 0, 0.01, 0),
            }
        );
        layout.Edges.Add(
            new GroundEdge
            {
                Nodes = [parking, nodeB],
                TaxiwayName = "B",
                DistanceNm = GeoMath.DistanceNm(0, 0, 0, 0.01),
            }
        );
        layout.RebuildAdjacencyLists();

        FilletArcGenerator.Apply(layout);

        // Parking node should still exist
        Assert.True(layout.Nodes.ContainsKey(0));
        Assert.Empty(layout.Arcs);
    }

    [Fact]
    public void ShortEdge_ReducesRadiusToFit()
    {
        // Short edge: 50ft. At 90° turn, max radius = 50ft / tan(45°) = 50ft.
        // Should still create an arc, just with a smaller radius.
        double shortDistNm = 50.0 / GeoMath.FeetPerNm;
        var (shortLat, shortLon) = GeoMath.ProjectPointRaw(0, 0, 0, shortDistNm); // North

        var layout = BuildLayout((shortLat, shortLon), (0, 0.01));

        FilletArcGenerator.Apply(layout);

        // Arc should be created with reduced curvature (fits the short edge)
        Assert.Single(layout.Arcs);
        Assert.True(
            layout.Arcs[0].MinRadiusOfCurvatureFt <= 55,
            $"MinRadius {layout.Arcs[0].MinRadiusOfCurvatureFt:F1}ft should be ≤55ft (clamped to short edge)"
        );

        // Intersection node should be removed
        Assert.False(layout.Nodes.ContainsKey(0));
    }

    [Fact]
    public void FourWayIntersection_Creates4Arcs2Merges()
    {
        // N, S, E, W forming a + intersection
        var layout = BuildLayout((0.01, 0), (-0.01, 0), (0, 0.01), (0, -0.01));

        FilletArcGenerator.Apply(layout);

        // N-S collinear, E-W collinear → intersection preserved
        // N-E, N-W, S-E, S-W → 4 arcs (all 90° turns)
        Assert.True(layout.Nodes.ContainsKey(0));
        Assert.Equal(4, layout.Arcs.Count);
    }

    [Fact]
    public void BezierControlPoints_AreOnInsideOfTurn()
    {
        // North and East edges: the inside of the turn is in the NE quadrant.
        // P1 and P2 should be between the two edges (NE of the tangent points).
        var layout = BuildLayout((0.01, 0), (0, 0.01));

        FilletArcGenerator.Apply(layout);

        var arc = layout.Arcs[0];

        // P1 is along the north edge toward intersection → should have lon near 0, lat < node[0]
        // P2 is along the east edge toward intersection → should have lat near 0, lon < node[1]
        // Both control points should be inside the turn (toward the NE quadrant relative to their endpoints)
        Assert.True(arc.MinRadiusOfCurvatureFt > 0, "MinRadiusOfCurvatureFt should be positive");

        // The midpoint of the bezier should be in the NE quadrant (inside the turn)
        var bezier = arc.ToBezier();
        var (midLat, midLon) = bezier.Evaluate(0.5);
        Assert.True(midLat > 0, $"Bezier midpoint lat {midLat} should be positive (inside turn)");
        Assert.True(midLon > 0, $"Bezier midpoint lon {midLon} should be positive (inside turn)");
    }

    [Fact]
    public void BezierArc_TangentDirectionsMatchEdgeBearings()
    {
        // North and East edges at the origin: 90° turn.
        // Tangent at P0 (on north edge) should point roughly south (toward intersection).
        // Tangent at P3 (on east edge) should point roughly west (toward intersection).
        var layout = BuildLayout((0.01, 0), (0, 0.01));

        FilletArcGenerator.Apply(layout);

        var arc = layout.Arcs[0];
        var bezier = arc.ToBezier();

        double tangentAtP0 = bezier.TangentBearing(0.0);
        double tangentAtP3 = bezier.TangentBearing(1.0);

        // P0 is on the north edge: tangent should be roughly 180° (south, toward intersection)
        // or roughly 0° (north, away from intersection) — depends on which node is P0.
        // The key constraint: the two tangents should be roughly 90° apart.
        double angleBetween = GeoMath.AbsBearingDifference(tangentAtP0, tangentAtP3);
        Assert.InRange(angleBetween, 80, 100); // Should be ~90° for a right-angle turn
    }

    [Fact]
    public void OAK_FilletGeneration_ProducesArcsAndPreservesGraph()
    {
        string path = Path.Combine("TestData", "oak.geojson");
        if (!File.Exists(path))
        {
            return; // Silently skip if test data absent
        }

        // GeoJsonParser.Parse now auto-applies fillets
        var layout = GeoJsonParser.Parse("OAK", File.ReadAllText(path), null);

        // Should have created arcs
        Assert.True(layout.Arcs.Count > 0, "Expected arcs to be generated for OAK");

        // Graph should still be connected — every node should have at least 1 edge
        foreach (var node in layout.Nodes.Values)
        {
            Assert.True(node.Edges.Count > 0, $"Node {node.Id} ({node.Type}) has no edges after filleting");
        }
    }

    [Theory]
    [InlineData("OAK")]
    [InlineData("SFO")]
    [InlineData("SJC")]
    [InlineData("FAT")]
    [InlineData("HWD")]
    [InlineData("MER")]
    [InlineData("RNO")]
    public void Airport_FilletProducesCleanGraph(string airportId)
    {
        string path = Path.Combine("TestData", $"{airportId.ToLowerInvariant()}.geojson");
        if (!File.Exists(path))
        {
            return;
        }

        var layout = GeoJsonParser.Parse(airportId, File.ReadAllText(path), null);

        // Skip airports with no taxiway geometry (runway-only layouts)
        if (layout.Nodes.Count == 0)
        {
            return;
        }

        // No degenerate edges: no edge between two TaxiwayIntersection nodes shorter than 1ft
        var degenerateEdges = new List<string>();
        foreach (var edge in layout.Edges)
        {
            var n0 = layout.Nodes[edge.Nodes[0].Id];
            var n1 = layout.Nodes[edge.Nodes[1].Id];
            if ((n0.Type == GroundNodeType.TaxiwayIntersection) && (n1.Type == GroundNodeType.TaxiwayIntersection))
            {
                double actualDistFt = GeoMath.DistanceNm(n0.Position, n1.Position) * GeoMath.FeetPerNm;
                if (actualDistFt < 1.0)
                {
                    degenerateEdges.Add($"{edge.TaxiwayName} ({n0.Id}->{n1.Id}): {actualDistFt:F1}ft");
                }
            }
        }

        Assert.True(degenerateEdges.Count == 0, $"{airportId}: {degenerateEdges.Count} degenerate edges:\n{string.Join("\n", degenerateEdges)}");

        // No coincident node pairs: no two TaxiwayIntersection nodes within 5ft
        double thresholdNm = 5.0 / GeoMath.FeetPerNm;
        var intersectionNodes = layout.Nodes.Values.Where(n => n.Type == GroundNodeType.TaxiwayIntersection).ToList();
        var coincidentPairs = new List<string>();
        for (int i = 0; i < intersectionNodes.Count; i++)
        {
            for (int j = i + 1; j < intersectionNodes.Count; j++)
            {
                double dist = GeoMath.DistanceNm(intersectionNodes[i].Position, intersectionNodes[j].Position);
                if (dist <= thresholdNm)
                {
                    coincidentPairs.Add($"({intersectionNodes[i].Id}, {intersectionNodes[j].Id}): {dist * GeoMath.FeetPerNm:F1}ft");
                }
            }
        }

        Assert.True(coincidentPairs.Count == 0, $"{airportId}: {coincidentPairs.Count} coincident node pairs:\n{string.Join("\n", coincidentPairs)}");

        // Graph connectivity: BFS from most-connected node reaches ≥ 90% of all nodes
        var startNode = layout.Nodes.Values.OrderByDescending(n => n.Edges.Count).First();
        var visited = new HashSet<int> { startNode.Id };
        var queue = new Queue<int>();
        queue.Enqueue(startNode.Id);
        while (queue.Count > 0)
        {
            int nodeId = queue.Dequeue();
            if (!layout.Nodes.TryGetValue(nodeId, out var node))
            {
                continue;
            }

            foreach (var edge in node.Edges)
            {
                int otherId = edge.OtherNodeId(nodeId);
                if (visited.Add(otherId))
                {
                    queue.Enqueue(otherId);
                }
            }
        }

        double reachPct = (double)visited.Count / layout.Nodes.Count;
        Assert.True(reachPct >= 0.9, $"{airportId}: only {visited.Count}/{layout.Nodes.Count} ({reachPct:P0}) nodes reachable");

        // All arcs valid: positive MinRadiusOfCurvatureFt and DistanceNm
        foreach (var arc in layout.Arcs)
        {
            Assert.True(
                arc.MinRadiusOfCurvatureFt > 0,
                $"{airportId}: arc {arc.Nodes[0].Id}->{arc.Nodes[1].Id} has MinRadius={arc.MinRadiusOfCurvatureFt:F1}ft"
            );
            Assert.True(arc.DistanceNm > 0, $"{airportId}: arc {arc.Nodes[0].Id}->{arc.Nodes[1].Id} has DistanceNm={arc.DistanceNm}");
        }

        // Every non-spot/helipad node has at least one edge (spots/helipads may be too
        // far from taxiways to connect)
        foreach (var node in layout.Nodes.Values)
        {
            if ((node.Type != GroundNodeType.Spot) && (node.Type != GroundNodeType.Helipad))
            {
                Assert.True(node.Edges.Count > 0, $"{airportId}: node {node.Id} ({node.Type}) has no edges");
            }
        }
    }

    /// <summary>
    /// Bug repro: when an intersection has a manual-arc chain on one side and several
    /// branches at different angles on the others, multiple per-pair tangent placements
    /// can land in <em>different</em> chain edges. Phase D1 only splits the SplitEdge of
    /// the farthest tangent — nearer chain tangents end up with an arc but no chain-side
    /// edge, so RescueOrphanedTangentNodes has to invent a connection.
    ///
    /// A correctly-handled scenario should need zero rescue-orphan edges: every tangent
    /// gets its chain edge split so the chain walks through it naturally.
    /// </summary>
    [Fact]
    public void MultipleTangentsInManualArcChain_NoRescueOrphansNeeded()
    {
        var layout = new AirportGroundLayout { AirportId = "TEST" };

        // Intersection at origin
        var x = new GroundNode
        {
            Id = 0,
            Position = new LatLon(0, 0),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        layout.Nodes[0] = x;

        // Chain north: X -> S1 -> S2 -> S3 -> F, all on taxiway "T1"
        // Edge lengths chosen so two different tangent distances land in different
        // chain edges:
        //   pair (T1, T-east) at 90° -> tangent ~75 ft -> in S1->S2
        //   pair (T1, T-northeast) at 135° -> tangent capped at 150 ft -> in S2->S3
        var s1 = MakeNorthNode(1, 40);
        var s2 = MakeNorthNode(2, 40 + 60);
        var s3 = MakeNorthNode(3, 40 + 60 + 80);
        var f = MakeNorthNode(4, 40 + 60 + 80 + 60);
        layout.Nodes[1] = s1;
        layout.Nodes[2] = s2;
        layout.Nodes[3] = s3;
        layout.Nodes[4] = f;

        AddEdge(layout, x, s1, "T1");
        AddEdge(layout, s1, s2, "T1");
        AddEdge(layout, s2, s3, "T1");
        AddEdge(layout, s3, f, "T1");

        // East branch (200 ft east) — produces 90° turn with chain
        var e = MakeProjectedNode(5, 90, 200);
        layout.Nodes[5] = e;
        AddEdge(layout, x, e, "T2");

        // Northeast branch (200 ft NE) — produces ~135° turn with chain (sharper)
        var ne = MakeProjectedNode(6, 45, 200);
        layout.Nodes[6] = ne;
        AddEdge(layout, x, ne, "T3");

        layout.RebuildAdjacencyLists();

        FilletArcGenerator.Apply(layout);

        var rescueEdges = layout.Edges.Where(e => e.Origin?.Contains("rescue-orphan") == true).ToList();
        Assert.True(
            rescueEdges.Count == 0,
            $"Expected no rescue-orphan edges; got {rescueEdges.Count}: "
                + string.Join(", ", rescueEdges.Select(e => $"{e.TaxiwayName}({e.Nodes[0].Id}↔{e.Nodes[1].Id}) {e.Origin}"))
        );

        // All tangent nodes should have at least one straight edge connecting them
        // to the graph, not just an arc. (Without straight edges, the rescue pass
        // would have papered over a real connectivity gap.)
        var tangentNodes = layout.Nodes.Values.Where(n => n.Origin?.StartsWith("Fillet:tangent-node") == true).ToList();
        Assert.NotEmpty(tangentNodes);
        foreach (var tan in tangentNodes)
        {
            bool hasStraightEdge = layout.Edges.Any(edge => edge.HasNode(tan.Id));
            Assert.True(hasStraightEdge, $"Tangent node #{tan.Id} ({tan.Origin}) has no straight edges");
        }
    }

    private static GroundNode MakeNorthNode(int id, double distFt) => MakeProjectedNode(id, 0, distFt);

    private static GroundNode MakeProjectedNode(int id, double bearingDeg, double distFt)
    {
        var (lat, lon) = GeoMath.ProjectPointRaw(0, 0, bearingDeg, distFt / GeoMath.FeetPerNm);
        return new GroundNode
        {
            Id = id,
            Position = new LatLon(lat, lon),
            Type = GroundNodeType.TaxiwayIntersection,
        };
    }

    private static void AddEdge(AirportGroundLayout layout, GroundNode a, GroundNode b, string twy) =>
        layout.Edges.Add(
            new GroundEdge
            {
                Nodes = [a, b],
                TaxiwayName = twy,
                DistanceNm = GeoMath.DistanceNm(a.Position, b.Position),
            }
        );
}
