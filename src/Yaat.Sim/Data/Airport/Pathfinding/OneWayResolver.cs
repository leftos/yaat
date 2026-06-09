using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Data.Airport.Pathfinding;

/// <summary>
/// Resolves per-airport <see cref="OneWayConstraint"/>s (authored as ordered coordinate polylines)
/// against a concrete <see cref="AirportGroundLayout"/> into a set of FORBIDDEN directed node moves
/// <c>(fromId, toId)</c>. A search gates a candidate edge traversal by a single O(1) membership test;
/// it works identically for straight edges and arcs because it keys on endpoint node ids.
///
/// Each constraint's <see cref="OneWayConstraint.Path"/> defines the allowed travel direction (first →
/// last). The reverse of each edge along the resolved span is forbidden; when
/// <see cref="OneWayConstraint.BlockBoth"/> is set, the forward direction is forbidden too (a closed
/// segment / forbidden turn). Consecutive waypoints may sit on different taxiways (a transition across a
/// junction) or far apart on the same taxiway (the span between them is filled by a taxiway-restricted BFS).
/// </summary>
public static class OneWayResolver
{
    private static readonly ILogger Log = SimLog.CreateLogger("OneWayResolver");

    // Resolved sets are cached per layout instance: a re-downloaded map produces a new layout object
    // (cache miss → re-resolve against the new node ids), and the old entry is collected with it.
    private static readonly ConditionalWeakTable<AirportGroundLayout, HashSet<(int, int)>> Cache = new();

    /// <summary>
    /// Forbidden directed moves for <paramref name="layout"/>, cached. Reads the airport's constraints
    /// from the global <see cref="NavigationDatabase"/>. Empty when no database is initialized or the
    /// airport has no one-way data.
    /// </summary>
    public static IReadOnlySet<(int From, int To)> GetForbiddenMoves(AirportGroundLayout layout) => Cache.GetValue(layout, BuildForLayout);

    private static HashSet<(int, int)> BuildForLayout(AirportGroundLayout layout)
    {
        var db = NavigationDatabase.InstanceOrNull;
        IReadOnlyList<OneWayConstraint> constraints = db?.AirportSidecars.GetOneWayConstraints(layout.AirportId) ?? [];
        return Resolve(layout, constraints);
    }

    /// <summary>
    /// Pure resolution of <paramref name="constraints"/> against <paramref name="layout"/> into forbidden
    /// directed moves. Unit-testable without a <see cref="NavigationDatabase"/>.
    /// </summary>
    public static HashSet<(int From, int To)> Resolve(AirportGroundLayout layout, IReadOnlyList<OneWayConstraint> constraints)
    {
        var forbidden = new HashSet<(int, int)>();
        foreach (var constraint in constraints)
        {
            ResolveConstraint(layout, constraint, forbidden);
        }

        return forbidden;
    }

    private static void ResolveConstraint(AirportGroundLayout layout, OneWayConstraint constraint, HashSet<(int, int)> forbidden)
    {
        var nodes = PolylineSnapper.Snap(layout, constraint.Path, "One-way", Log);
        if (nodes is null)
        {
            return;
        }

        for (int i = 0; i + 1 < nodes.Count; i++)
        {
            var span = PolylineSnapper.BuildSpan(layout, nodes[i], nodes[i + 1]);
            if (span is null)
            {
                Log.LogWarning(
                    "One-way at {Airport}: nodes {A}->{B} are not directly connected and share no taxiway; skipping segment",
                    layout.AirportId,
                    nodes[i].Id,
                    nodes[i + 1].Id
                );
                continue;
            }

            foreach (var (from, to) in span)
            {
                forbidden.Add((to, from));
                if (constraint.BlockBoth)
                {
                    forbidden.Add((from, to));
                }
            }
        }
    }
}
