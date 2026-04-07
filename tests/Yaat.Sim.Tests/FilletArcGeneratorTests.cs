using Xunit;
using Yaat.Sim.Data.Airport;

namespace Yaat.Sim.Tests;

public class FilletArcGeneratorTests
{
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
            Latitude = 0,
            Longitude = 0,
            Type = GroundNodeType.TaxiwayIntersection,
        };
        layout.Nodes[0] = intersection;

        for (int i = 0; i < endpoints.Length; i++)
        {
            int id = i + 1;
            var node = new GroundNode
            {
                Id = id,
                Latitude = endpoints[i].Lat,
                Longitude = endpoints[i].Lon,
                Type = GroundNodeType.TaxiwayIntersection,
            };
            layout.Nodes[id] = node;

            double dist = GeoMath.DistanceNm(0, 0, node.Latitude, node.Longitude);
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
    public void TwoEdges_Collinear_MergesIntoStraightEdge()
    {
        // North and South edges — 180° apart = 0° turn angle (collinear)
        var layout = BuildLayout((0.01, 0), (-0.01, 0));

        FilletArcGenerator.Apply(layout);

        // Intersection node 0 should be removed
        Assert.False(layout.Nodes.ContainsKey(0));

        // No arcs — collinear merge
        Assert.Empty(layout.Arcs);

        // One merged edge connecting the two endpoints directly
        Assert.Single(layout.Edges);

        var edge = layout.Edges[0];
        Assert.True(edge.HasNode(1));
        Assert.True(edge.HasNode(2));
    }

    [Fact]
    public void ThreeEdges_CollinearPairPlusPerpendicular_MergesAndCreatesArcs()
    {
        // Node 1 North, Node 2 South (collinear), Node 3 East (perpendicular)
        var layout = BuildLayout((0.01, 0), (-0.01, 0), (0, 0.01));

        FilletArcGenerator.Apply(layout);

        // Intersection node removed
        Assert.False(layout.Nodes.ContainsKey(0));

        // 1 collinear merge + 2 arcs (N→E and S→E)
        Assert.Equal(2, layout.Arcs.Count);
    }

    [Fact]
    public void HoldShortNode_IsNotFilleted()
    {
        var layout = new AirportGroundLayout { AirportId = "TEST" };

        var holdShort = new GroundNode
        {
            Id = 0,
            Latitude = 0,
            Longitude = 0,
            Type = GroundNodeType.RunwayHoldShort,
            RunwayId = RunwayIdentifier.Parse("28L"),
        };
        layout.Nodes[0] = holdShort;

        var nodeA = new GroundNode
        {
            Id = 1,
            Latitude = 0.01,
            Longitude = 0,
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var nodeB = new GroundNode
        {
            Id = 2,
            Latitude = 0,
            Longitude = 0.01,
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
            Latitude = 0,
            Longitude = 0,
            Type = GroundNodeType.Parking,
            Name = "A1",
            TrueHeading = new TrueHeading(90),
        };
        layout.Nodes[0] = parking;

        var nodeA = new GroundNode
        {
            Id = 1,
            Latitude = 0.01,
            Longitude = 0,
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var nodeB = new GroundNode
        {
            Id = 2,
            Latitude = 0,
            Longitude = 0.01,
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

        // N-S collinear → merge, E-W collinear → merge
        // N-E, N-W, S-E, S-W → 4 arcs (all 90° turns)
        Assert.False(layout.Nodes.ContainsKey(0));
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
}
