using System.Collections.Concurrent;
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
/// A constraint whose <see cref="OneWayConstraint.ExemptWakeClasses"/> holds the aircraft's wake class is
/// skipped for that aircraft.
/// </summary>
public static class OneWayResolver
{
    private static readonly ILogger Log = SimLog.CreateLogger("OneWayResolver");

    // Resolved sets are cached per layout instance and, within it, per wake class: a re-downloaded map produces a new
    // layout object (cache miss → re-resolve against the new node ids), and the old entry is collected with it.
    private static readonly ConditionalWeakTable<
        AirportGroundLayout,
        ConcurrentDictionary<WakeTurbulenceData.WakeClass, HashSet<(int, int)>>
    > Cache = [];

    private static readonly ConditionalWeakTable<AirportGroundLayout, HashSet<(int, int)>> UnexemptedCache = [];

    private static readonly ConditionalWeakTable<AirportGroundLayout, HashSet<string>> LaneCache = [];

    /// <summary>
    /// The wake class a search with no aircraft (the runway exit walk) resolves the constraints for: Large, the class most
    /// traffic is, so a constraint that exempts Large — a supers-only closure — does not bind it, and one that binds every
    /// aircraft, or every aircraft but supers, does.
    /// </summary>
    public const WakeTurbulenceData.WakeClass AircraftlessWakeClass = WakeTurbulenceData.WakeClass.Large;

    /// <summary>
    /// Forbidden directed moves for <paramref name="layout"/> with every constraint binding, whatever its exemptions: the
    /// span of every one-way lane, for <see cref="GetOneWayLaneTaxiways"/>. Cached; empty when the airport has no one-way data.
    /// </summary>
    private static IReadOnlySet<(int From, int To)> GetForbiddenMovesIgnoringExemptions(AirportGroundLayout layout) =>
        UnexemptedCache.GetValue(layout, BuildUnexempted);

    /// <summary>
    /// Forbidden directed moves for an aircraft of <paramref name="wakeClass"/> on <paramref name="layout"/>, cached.
    /// Reads the airport's constraints from the global <see cref="NavigationDatabase"/>. Empty when no database is
    /// initialized or the airport has no one-way data.
    /// </summary>
    public static IReadOnlySet<(int From, int To)> GetForbiddenMoves(AirportGroundLayout layout, WakeTurbulenceData.WakeClass wakeClass) =>
        Cache.GetValue(layout, static _ => new()).GetOrAdd(wakeClass, static (wc, l) => Resolve(l, ConstraintsFor(l), wc), layout);

    /// <summary>
    /// The taxiway names carried by any edge on any one-way constraint span of <paramref name="layout"/>, whatever the
    /// constraint's direction or exemptions: the airport's one-way lanes. Cached per layout; empty when the airport has
    /// no one-way data.
    /// </summary>
    public static IReadOnlySet<string> GetOneWayLaneTaxiways(AirportGroundLayout layout) => LaneCache.GetValue(layout, BuildLaneTaxiways);

    private static IReadOnlyList<OneWayConstraint> ConstraintsFor(AirportGroundLayout layout) =>
        NavigationDatabase.InstanceOrNull?.AirportSidecars.GetOneWayConstraints(layout.AirportId) ?? [];

    private static HashSet<(int, int)> BuildUnexempted(AirportGroundLayout layout)
    {
        var moves = new HashSet<(int, int)>();
        foreach (OneWayConstraint constraint in ConstraintsFor(layout))
        {
            ResolveConstraint(layout, constraint, moves);
        }

        return moves;
    }

    private static HashSet<string> BuildLaneTaxiways(AirportGroundLayout layout)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach ((int from, int to) in GetForbiddenMovesIgnoringExemptions(layout))
        {
            if (!layout.Nodes.TryGetValue(from, out GroundNode? node))
            {
                continue;
            }

            foreach (IGroundEdge edge in node.Edges.Where(e => e.HasNode(to)))
            {
                names.UnionWith(SegmentExpander.EdgeNames(edge));
            }
        }

        return names;
    }

    /// <summary>
    /// Pure resolution of <paramref name="constraints"/> against <paramref name="layout"/> into the directed moves
    /// forbidden to an aircraft of <paramref name="wakeClass"/>. Unit-testable without a <see cref="NavigationDatabase"/>.
    /// </summary>
    public static HashSet<(int From, int To)> Resolve(
        AirportGroundLayout layout,
        IReadOnlyList<OneWayConstraint> constraints,
        WakeTurbulenceData.WakeClass wakeClass
    )
    {
        var forbidden = new HashSet<(int, int)>();
        foreach (OneWayConstraint constraint in constraints)
        {
            if (constraint.ExemptWakeClasses.Contains(wakeClass))
            {
                continue;
            }

            ResolveConstraint(layout, constraint, forbidden);
        }

        return forbidden;
    }

    private static void ResolveConstraint(AirportGroundLayout layout, OneWayConstraint constraint, HashSet<(int, int)> forbidden)
    {
        List<GroundNode>? nodes = PolylineSnapper.Snap(layout, constraint.Path, "One-way", Log);
        if (nodes is null)
        {
            return;
        }

        for (int i = 0; i + 1 < nodes.Count; i++)
        {
            List<(int From, int To)>? span = PolylineSnapper.BuildSpan(layout, nodes[i], nodes[i + 1]);
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

            foreach ((int from, int to) in span)
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
