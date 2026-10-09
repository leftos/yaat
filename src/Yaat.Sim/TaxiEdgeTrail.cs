using Yaat.Sim.Data.Airport;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim;

/// <summary>
/// One straight taxi edge in a <see cref="TaxiEdgeTrail"/>: its end node ids (<c>Nodes[0]</c>, <c>Nodes[1]</c>) and its
/// length (ft), read from the layout when it was recorded, and <paramref name="EntryNodeId"/>, the end node the aircraft entered
/// it from (<see cref="TaxiEdgeTrail.EntryNodeOf"/>).
/// </summary>
public readonly record struct TaxiTrailEdge(int NodeA, int NodeB, double LengthFt, int EntryNodeId)
{
    /// <summary>Whether this is <paramref name="edge"/>, whichever way round its end nodes are named.</summary>
    public bool Is(GroundEdge edge) =>
        ((edge.Nodes[0].Id == NodeA) && (edge.Nodes[1].Id == NodeB)) || ((edge.Nodes[0].Id == NodeB) && (edge.Nodes[1].Id == NodeA));

    /// <summary>Whether <paramref name="nodeId"/> is one of this edge's end nodes.</summary>
    public bool Touches(int nodeId) => (nodeId == NodeA) || (nodeId == NodeB);

    /// <summary>
    /// The straight edge of <paramref name="layout"/> joining <see cref="NodeA"/> and <see cref="NodeB"/>, or null when it
    /// has none.
    /// </summary>
    public GroundEdge? Resolve(AirportGroundLayout layout) => TaxiEdgeLocator.StraightEdgeJoining(layout, NodeA, NodeB);
}

/// <summary>
/// The straight taxi edges a ground aircraft has driven, oldest first, consecutive repeats collapsed: written once per
/// sim-second by <see cref="Simulation.SimulationEngine.TickTaxiEdgeTrails"/> for every moving ground aircraft not rolling
/// along a runway, from the edge <see cref="TaxiEdgeLocator"/> finds it on. Kept to about <see cref="CapFt"/> of taxi
/// distance: the oldest edge is dropped while the edges after it alone exceed the cap. A new taxi clearance leaves it as it is;
/// it is emptied by a warp (<c>WARP</c>, <c>WARPG</c>) and when the aircraft leaves the ground.
/// </summary>
public sealed class TaxiEdgeTrail
{
    /// <summary>How much taxi distance (ft) the trail keeps behind the oldest edge it holds.</summary>
    public const double CapFt = 3000.0;

    private readonly List<TaxiTrailEdge> _edges = [];

    /// <summary>The edges driven, oldest first.</summary>
    public IReadOnlyList<TaxiTrailEdge> Edges => _edges;

    /// <summary>The summed length (ft) of <see cref="Edges"/>, added up oldest first.</summary>
    public double TotalLengthFt
    {
        get
        {
            double totalFt = 0.0;
            foreach (TaxiTrailEdge edge in _edges)
            {
                totalFt += edge.LengthFt;
            }

            return totalFt;
        }
    }

    /// <summary>The edge driven most recently, or null when the trail is empty.</summary>
    public TaxiTrailEdge? Newest => _edges.Count > 0 ? _edges[^1] : null;

    /// <summary>
    /// Append <paramref name="edge"/>, entered from <paramref name="entryNode"/>, unless it is already the newest (which keeps
    /// the entry node it was first recorded with), then drop the oldest edges while the edges after the oldest exceed
    /// <see cref="CapFt"/>.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="entryNode"/> is not an end node of <paramref name="edge"/>.</exception>
    public void Record(GroundEdge edge, GroundNode entryNode)
    {
        if ((entryNode.Id != edge.Nodes[0].Id) && (entryNode.Id != edge.Nodes[1].Id))
        {
            throw new ArgumentException(
                $"Entry node #{entryNode.Id} is not an end of the edge #{edge.Nodes[0].Id}-#{edge.Nodes[1].Id} being recorded; pass one of its ends.",
                nameof(entryNode)
            );
        }

        if ((Newest is { } newest) && newest.Is(edge))
        {
            return;
        }

        _edges.Add(new TaxiTrailEdge(edge.Nodes[0].Id, edge.Nodes[1].Id, edge.DistanceNm * GeoMath.FeetPerNm, entryNode.Id));
        while ((_edges.Count > 1) && ((TotalLengthFt - _edges[0].LengthFt) > CapFt))
        {
            _edges.RemoveAt(0);
        }
    }

    /// <summary>
    /// The end node of <paramref name="edge"/> that <paramref name="aircraft"/>, on it, entered it from, to record it in the
    /// aircraft's own trail with: the node it shares with that trail's newest edge, the edge the aircraft has just driven off.
    /// Only when the two share no node — the first edge, an edge past a fillet arc, or one reached over an edge driven between two
    /// records — is it read from the aircraft's movement: the end behind its track (its pushback track on a tug, else its heading),
    /// read against the edge's own direction. Mid-turn at a sharp junction the heading alone would name the far end.
    /// </summary>
    public static GroundNode EntryNodeOf(GroundEdge edge, AircraftState aircraft)
    {
        if (
            (aircraft.Ground.TaxiEdgeTrail.Newest is { } newest)
            && !newest.Is(edge)
            && (Array.Find(edge.Nodes, node => newest.Touches(node.Id)) is { } shared)
        )
        {
            return shared;
        }

        double firstToSecondDeg = GeoMath.BearingTo(edge.Nodes[0].Position, edge.Nodes[1].Position);
        return TracksWithin90Of(aircraft, firstToSecondDeg) ? edge.Nodes[0] : edge.Nodes[1];
    }

    /// <summary>
    /// Whether <paramref name="aircraft"/>'s track — its pushback track on a tug, else its heading — is within 90° of
    /// <paramref name="bearingDeg"/>: it moves that way rather than away from it.
    /// </summary>
    public static bool TracksWithin90Of(AircraftState aircraft, double bearingDeg)
    {
        double trackDeg = aircraft.Ground.PushbackTrueHeading?.Degrees ?? aircraft.TrueHeading.Degrees;
        return GeoMath.AbsBearingDifference(trackDeg, bearingDeg) < 90.0;
    }

    public void Clear() => _edges.Clear();

    /// <summary>The trail for a snapshot, or null when it is empty so a snapshot with no trail is written as before.</summary>
    public List<TaxiTrailEdgeDto>? ToSnapshot() =>
        _edges.Count == 0
            ? null
            :
            [
                .. _edges.Select(static edge => new TaxiTrailEdgeDto
                {
                    NodeA = edge.NodeA,
                    NodeB = edge.NodeB,
                    LengthFt = edge.LengthFt,
                    EntryNodeId = edge.EntryNodeId,
                }),
            ];

    /// <summary>The trail a snapshot holds; empty when it holds none.</summary>
    /// <exception cref="InvalidDataException">An edge's entry node is neither of its end nodes (a snapshot written without it loads 0).</exception>
    public static TaxiEdgeTrail FromSnapshot(List<TaxiTrailEdgeDto>? dto)
    {
        var trail = new TaxiEdgeTrail();
        foreach (TaxiTrailEdgeDto edge in dto ?? [])
        {
            if ((edge.EntryNodeId != edge.NodeA) && (edge.EntryNodeId != edge.NodeB))
            {
                throw new InvalidDataException(
                    $"Snapshot taxi trail edge #{edge.NodeA}-#{edge.NodeB} has entry node #{edge.EntryNodeId}, which is neither of its ends; "
                        + "the snapshot is damaged or was written without the edge's entry node."
                );
            }

            trail._edges.Add(new TaxiTrailEdge(edge.NodeA, edge.NodeB, edge.LengthFt, edge.EntryNodeId));
        }

        return trail;
    }
}
