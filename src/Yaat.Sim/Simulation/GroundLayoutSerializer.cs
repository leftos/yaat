using System.Text.Json;
using Yaat.Sim.Data.Airport;

namespace Yaat.Sim.Simulation;

/// <summary>
/// The one place an <see cref="AirportGroundLayout"/> is turned into bytes and back. The recording archive and the
/// precompute store both store a layout this way, so the two formats cannot drift apart.
/// </summary>
public static class GroundLayoutSerializer
{
    /// <summary>
    /// The serializer options every stored layout is written and read with: the recording options plus fields, since a
    /// layout's coordinates are value tuples, whose items are fields.
    /// </summary>
    public static JsonSerializerOptions Options { get; } = new(RecordingJsonOptions.Default) { IncludeFields = true };

    public static byte[] Serialize(AirportGroundLayout layout) => JsonSerializer.SerializeToUtf8Bytes(layout, Options);

    public static AirportGroundLayout Deserialize(Stream json, string airportId)
    {
        AirportGroundLayout layout =
            JsonSerializer.Deserialize<AirportGroundLayout>(json, Options)
            ?? throw new InvalidDataException($"Failed to deserialize the ground layout for {airportId}.");
        Repair(layout);
        return layout;
    }

    /// <summary>
    /// Restores what serialization leaves out: the node adjacency lists, over edges re-linked to the layout's own nodes.
    /// </summary>
    public static void Repair(AirportGroundLayout layout)
    {
        RelinkEdgeNodes(layout);
        layout.RebuildAdjacencyLists();
    }

    /// <summary>
    /// The serializer writes each edge's end nodes in full, so a read edge holds copies of them; points every edge at the
    /// layout's own node objects, whose adjacency lists the pathfinder and the arc taxiway-name resolution walk.
    /// </summary>
    private static void RelinkEdgeNodes(AirportGroundLayout layout)
    {
        foreach (IGroundEdge edge in layout.AllEdges)
        {
            for (int i = 0; i < edge.Nodes.Length; i++)
            {
                edge.Nodes[i] = layout.Nodes.TryGetValue(edge.Nodes[i].Id, out GroundNode? node)
                    ? node
                    : throw new InvalidDataException($"Layout {layout.AirportId} has an edge to node {edge.Nodes[i].Id}, which it lacks.");
            }
        }
    }
}
