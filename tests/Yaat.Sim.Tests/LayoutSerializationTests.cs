using System.Text.Json;
using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests;

/// <summary>
/// Verifies that AirportGroundLayout survives JSON round-trip with all
/// runtime-critical properties intact. The static cache in TestAirportGroundData
/// relies on this for correctness.
/// </summary>
public class LayoutSerializationTests
{
    [Fact]
    public void OAK_RoundTrip_PreservesGraphStructure()
    {
        string path = "TestData/oak.geojson";
        if (!File.Exists(path))
        {
            return;
        }

        var original = GeoJsonParser.Parse("OAK", File.ReadAllText(path), null);
        var json = JsonSerializer.SerializeToUtf8Bytes(original);
        var restored = JsonSerializer.Deserialize<AirportGroundLayout>(json)!;

        // Fix node references (edges reference deserialized copies, not dictionary instances)
        foreach (var edge in restored.AllEdges)
        {
            for (int i = 0; i < edge.Nodes.Length; i++)
            {
                edge.Nodes[i] = restored.Nodes[edge.Nodes[i].Id];
            }
        }

        restored.RebuildAdjacencyLists();

        // Node counts
        Assert.Equal(original.Nodes.Count, restored.Nodes.Count);
        Assert.Equal(original.Edges.Count, restored.Edges.Count);
        Assert.Equal(original.Arcs.Count, restored.Arcs.Count);
        Assert.Equal(original.Runways.Count, restored.Runways.Count);

        // Node properties
        foreach (var (id, origNode) in original.Nodes)
        {
            Assert.True(restored.Nodes.ContainsKey(id), $"Node {id} missing after round-trip");
            var restNode = restored.Nodes[id];
            Assert.Equal(origNode.Position.Lat, restNode.Position.Lat);
            Assert.Equal(origNode.Position.Lon, restNode.Position.Lon);
            Assert.Equal(origNode.Type, restNode.Type);
            Assert.Equal(origNode.Name, restNode.Name);
            Assert.Equal(origNode.RunwayId?.ToString(), restNode.RunwayId?.ToString());
        }

        // Edge properties
        for (int i = 0; i < original.Edges.Count; i++)
        {
            var origEdge = original.Edges[i];
            var restEdge = restored.Edges[i];
            Assert.Equal(origEdge.Nodes[0].Id, restEdge.Nodes[0].Id);
            Assert.Equal(origEdge.Nodes[1].Id, restEdge.Nodes[1].Id);
            Assert.Equal(origEdge.TaxiwayName, restEdge.TaxiwayName);
            Assert.Equal(origEdge.DistanceNm, restEdge.DistanceNm, 6);
        }

        // Arc properties
        for (int i = 0; i < original.Arcs.Count; i++)
        {
            var origArc = original.Arcs[i];
            var restArc = restored.Arcs[i];
            Assert.Equal(origArc.Nodes[0].Id, restArc.Nodes[0].Id);
            Assert.Equal(origArc.Nodes[1].Id, restArc.Nodes[1].Id);
            Assert.Equal(origArc.P1Lat, restArc.P1Lat, 10);
            Assert.Equal(origArc.P1Lon, restArc.P1Lon, 10);
            Assert.Equal(origArc.P2Lat, restArc.P2Lat, 10);
            Assert.Equal(origArc.P2Lon, restArc.P2Lon, 10);
            Assert.Equal(origArc.MinRadiusOfCurvatureFt, restArc.MinRadiusOfCurvatureFt, 4);
            Assert.Equal(origArc.DistanceNm, restArc.DistanceNm, 6);
            Assert.Equal(origArc.TaxiwayNames, restArc.TaxiwayNames);
        }

        // Adjacency: every node should have the same edge count
        foreach (var (id, origNode) in original.Nodes)
        {
            var restNode = restored.Nodes[id];
            Assert.True(
                origNode.Edges.Count == restNode.Edges.Count,
                $"Node {id} adjacency count mismatch: {origNode.Edges.Count} vs {restNode.Edges.Count}"
            );
        }

        // Node identity: edge.Nodes[i] must be the same instance as Nodes[id]
        foreach (var edge in restored.AllEdges)
        {
            Assert.Same(restored.Nodes[edge.Nodes[0].Id], edge.Nodes[0]);
            Assert.Same(restored.Nodes[edge.Nodes[1].Id], edge.Nodes[1]);
        }
    }
}
