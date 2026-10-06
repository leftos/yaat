using Yaat.Sim.Data.Airport;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim;

/// <summary>
/// One straight taxi edge in a <see cref="TaxiEdgeTrail"/>: its end node ids (<c>Nodes[0]</c>, <c>Nodes[1]</c>) and its
/// length (ft), read from the layout when it was recorded.
/// </summary>
public readonly record struct TaxiTrailEdge(int NodeA, int NodeB, double LengthFt)
{
    /// <summary>Whether this is <paramref name="edge"/>, whichever way round its end nodes are named.</summary>
    public bool Is(GroundEdge edge) =>
        ((edge.Nodes[0].Id == NodeA) && (edge.Nodes[1].Id == NodeB)) || ((edge.Nodes[0].Id == NodeB) && (edge.Nodes[1].Id == NodeA));

    /// <summary>
    /// The straight edge of <paramref name="layout"/> joining <see cref="NodeA"/> and <see cref="NodeB"/>, or null when it
    /// has none.
    /// </summary>
    public GroundEdge? Resolve(AirportGroundLayout layout)
    {
        if (!layout.Nodes.TryGetValue(NodeA, out GroundNode? nodeA))
        {
            return null;
        }

        foreach (IGroundEdge edge in nodeA.Edges)
        {
            if ((edge is GroundEdge straight) && Is(straight))
            {
                return straight;
            }
        }

        return null;
    }
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
    /// Append <paramref name="edge"/> unless it is already the newest, then drop the oldest edges while the edges after the
    /// oldest exceed <see cref="CapFt"/>.
    /// </summary>
    public void Record(GroundEdge edge)
    {
        if ((Newest is { } newest) && newest.Is(edge))
        {
            return;
        }

        _edges.Add(new TaxiTrailEdge(edge.Nodes[0].Id, edge.Nodes[1].Id, edge.DistanceNm * GeoMath.FeetPerNm));
        while ((_edges.Count > 1) && ((TotalLengthFt - _edges[0].LengthFt) > CapFt))
        {
            _edges.RemoveAt(0);
        }
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
                }),
            ];

    public static TaxiEdgeTrail FromSnapshot(List<TaxiTrailEdgeDto>? dto)
    {
        var trail = new TaxiEdgeTrail();
        foreach (TaxiTrailEdgeDto edge in dto ?? [])
        {
            trail._edges.Add(new TaxiTrailEdge(edge.NodeA, edge.NodeB, edge.LengthFt));
        }

        return trail;
    }
}
