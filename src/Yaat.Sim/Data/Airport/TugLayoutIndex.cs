using System.Runtime.CompilerServices;

namespace Yaat.Sim.Data.Airport;

/// <summary>
/// The layout facts the tug planner's checks read on every plan, gathered once per layout: every edge in
/// <see cref="AirportGroundLayout.AllEdges"/> order with its names and whether it touches a runway holding position, the
/// holding positions, the named parking stands that carry a heading, and each edge's movement-area name under a
/// classification. Nothing here depends on a plan's flat frame: each plan still lays the edges out from its own start, so
/// every figure it computes is the one it computed before. Built once and only read after, so every plan at the airport
/// shares it from any thread.
///
/// <para>A layout is assumed frozen once loaded: every build path ends in <see cref="AirportGroundLayout.RebuildAdjacencyLists"/>
/// and nothing edits the graph after. The only guard is a count check: a layout whose node, edge or arc count has changed
/// since is indexed again, but an edit that keeps the counts (a node's type or position changed in place) is not
/// caught and is served the stale index.</para>
/// </summary>
internal sealed class TugLayoutIndex
{
    private static readonly ConditionalWeakTable<AirportGroundLayout, TugLayoutIndex> Cache = [];

    private readonly AirportGroundLayout _layout;
    private readonly int _nodeCount;
    private readonly int _edgeCount;
    private readonly int _arcCount;
    private readonly ConditionalWeakTable<MovementAreaClassification, string?[]> _movementAreaNames = [];

    private TugLayoutIndex(AirportGroundLayout layout)
    {
        _layout = layout;
        _nodeCount = layout.Nodes.Count;
        _edgeCount = layout.Edges.Count;
        _arcCount = layout.Arcs.Count;
        Edges = [.. layout.AllEdges];
        EdgeNames = new string[Edges.Length][];
        TouchesHoldShort = new bool[Edges.Length];
        var holdShortEdges = new List<int>();
        for (int i = 0; i < Edges.Length; i++)
        {
            IGroundEdge edge = Edges[i];
            EdgeNames[i] = RampLaneReposition.EdgeNames(edge);
            TouchesHoldShort[i] = (edge.Nodes[0].Type == GroundNodeType.RunwayHoldShort) || (edge.Nodes[1].Type == GroundNodeType.RunwayHoldShort);
            if (TouchesHoldShort[i])
            {
                holdShortEdges.Add(i);
            }
        }

        HoldShortEdges = [.. holdShortEdges];
        HoldShorts = [.. layout.Nodes.Values.Where(n => n.Type == GroundNodeType.RunwayHoldShort).Select(n => n.Position)];
        Stands =
        [
            .. layout
                .Nodes.Values.Where(n => (n.Type == GroundNodeType.Parking) && (n.Name is not null) && (n.TrueHeading is not null))
                .OrderBy(n => n.Id),
        ];
    }

    /// <summary>Every edge of the layout, straight edges then arcs, as <see cref="AirportGroundLayout.AllEdges"/> lists them.</summary>
    internal IGroundEdge[] Edges { get; }

    /// <summary>Each edge's names (<see cref="RampLaneReposition.EdgeNames"/>), by edge index.</summary>
    internal string[][] EdgeNames { get; }

    /// <summary>Whether each edge has a runway holding-position node at either end, by edge index.</summary>
    internal bool[] TouchesHoldShort { get; }

    /// <summary>The indices of the edges touching a runway holding position, in edge order.</summary>
    internal int[] HoldShortEdges { get; }

    /// <summary>Every runway holding-position node's position, in the layout's node order.</summary>
    internal LatLon[] HoldShorts { get; }

    /// <summary>The parking nodes with a name and a heading, by node id.</summary>
    internal GroundNode[] Stands { get; }

    /// <summary>The index of <paramref name="layout"/>, built on first use and again when the layout has grown or shrunk since.</summary>
    /// <param name="layout">The layout.</param>
    /// <returns>Its index.</returns>
    internal static TugLayoutIndex For(AirportGroundLayout layout)
    {
        TugLayoutIndex index = Cache.GetValue(layout, static l => new TugLayoutIndex(l));
        if (index.Describes(layout))
        {
            return index;
        }

        var rebuilt = new TugLayoutIndex(layout);
        Cache.AddOrUpdate(layout, rebuilt);
        return rebuilt;
    }

    /// <summary>
    /// Each edge's movement-area name under <paramref name="classification"/>
    /// (<see cref="PavementClassifier.MovementAreaName"/>), by edge index.
    /// </summary>
    /// <param name="classification">The movement-area classification.</param>
    /// <returns>The names; null for a ramp edge.</returns>
    internal string?[] MovementAreaNames(MovementAreaClassification classification) =>
        _movementAreaNames.TryGetValue(classification, out string?[]? names)
            ? names
            : _movementAreaNames.GetValue(classification, BuildMovementAreaNames);

    private string?[] BuildMovementAreaNames(MovementAreaClassification classification)
    {
        var pavement = new PavementClassifier(_layout, classification);
        string?[] names = new string?[Edges.Length];
        for (int i = 0; i < Edges.Length; i++)
        {
            names[i] = pavement.MovementAreaName(Edges[i]);
        }

        return names;
    }

    private bool Describes(AirportGroundLayout layout) =>
        (layout.Nodes.Count == _nodeCount) && (layout.Edges.Count == _edgeCount) && (layout.Arcs.Count == _arcCount);
}
