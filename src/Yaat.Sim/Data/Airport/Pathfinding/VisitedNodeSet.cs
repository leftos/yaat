namespace Yaat.Sim.Data.Airport.Pathfinding;

/// <summary>
/// Immutable set of node ids visited along one <see cref="PartialRoute"/>. Backed by a sorted
/// <c>int[]</c>: <see cref="Add"/> copies the array (routes are short, so a copy is a few hundred
/// bytes at most) and <see cref="Contains"/> is a binary search. Replaces the per-route
/// <c>ImmutableHashSet&lt;int&gt;</c>, whose tree nodes were the pathfinder's dominant allocation.
/// </summary>
public readonly struct VisitedNodeSet
{
    private readonly int[]? _sorted;

    private VisitedNodeSet(int[] sorted) => _sorted = sorted;

    /// <summary>A set containing only <paramref name="nodeId"/>.</summary>
    public static VisitedNodeSet Single(int nodeId) => new([nodeId]);

    public int Count => _sorted?.Length ?? 0;

    public bool Contains(int nodeId) => _sorted is not null && Array.BinarySearch(_sorted, nodeId) >= 0;

    /// <summary>Returns a set with <paramref name="nodeId"/> added; returns this set unchanged if already present.</summary>
    public VisitedNodeSet Add(int nodeId)
    {
        if (_sorted is null)
        {
            return Single(nodeId);
        }

        int index = Array.BinarySearch(_sorted, nodeId);
        if (index >= 0)
        {
            return this;
        }

        int insertAt = ~index;
        var next = new int[_sorted.Length + 1];
        Array.Copy(_sorted, 0, next, 0, insertAt);
        next[insertAt] = nodeId;
        Array.Copy(_sorted, insertAt, next, insertAt + 1, _sorted.Length - insertAt);
        return new VisitedNodeSet(next);
    }
}
