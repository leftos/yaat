using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Data.Airport.Pathfinding;

/// <summary>
/// Resolved blocked-turn data for one concrete layout. A blocked turn forbids passing from one taxiway
/// arm onto another through an intersection apex, in both directions:
/// <list type="bullet">
/// <item><see cref="ForbiddenTurns"/> — directed <c>(prev, apex, next)</c> triples for the straight pivot
/// through a surviving apex node (a 2-node edge block there would over-block straight-through / other-arm
/// traffic, so the pivot is keyed on the full turn).</item>
/// <item><see cref="ForbiddenArcMoves"/> — directed 2-node moves over the fillet corner arc that smooths
/// the same turn (it bypasses the apex, so the turn-triple cannot catch it).</item>
/// <item><see cref="HiddenArcPairs"/> — the corner arcs Ground View must not draw (unordered endpoint
/// pairs). Same arcs as <see cref="ForbiddenArcMoves"/> — never routed, never drawn.</item>
/// </list>
/// </summary>
public sealed record BlockedTurnResult(
    IReadOnlySet<(int Prev, int Apex, int Next)> ForbiddenTurns,
    IReadOnlySet<(int From, int To)> ForbiddenArcMoves,
    IReadOnlySet<(int A, int B)> HiddenArcPairs
)
{
    public static readonly BlockedTurnResult Empty = new(new HashSet<(int, int, int)>(), new HashSet<(int, int)>(), new HashSet<(int, int)>());

    /// <summary>True when the arc between <paramref name="a"/> and <paramref name="b"/> is hidden (either endpoint order).</summary>
    public bool IsHiddenArc(int a, int b) => HiddenArcPairs.Contains((a, b)) || HiddenArcPairs.Contains((b, a));
}

/// <summary>
/// Resolves per-airport <see cref="BlockedTurn"/>s (authored as ordered coordinate polylines) against a
/// concrete <see cref="AirportGroundLayout"/>. Snaps the polyline to nodes, traces the connected node span
/// (<see cref="PolylineSnapper"/>), then at each interior node where the path turns onto a different
/// taxiway emits a forbidden turn-triple and identifies the fillet corner arc that smooths that turn (by
/// matching arc taxiway names + endpoint bearings from the apex). Mirrors <see cref="OneWayResolver"/>:
/// resolved sets are cached per layout instance.
/// </summary>
public static class BlockedTurnResolver
{
    private static readonly ILogger Log = SimLog.CreateLogger("BlockedTurnResolver");

    /// <summary>Deflection below this (deg) is a collinear continuation, not a turn (matches the fillet collinear threshold).</summary>
    private const double CornerDeflectionThresholdDeg = 15.0;

    /// <summary>Max bearing error (deg) when matching a corner arc's endpoint to the apex→arm direction.</summary>
    private const double ArcBearingToleranceDeg = 35.0;

    private static readonly ConditionalWeakTable<AirportGroundLayout, BlockedTurnResult> Cache = new();

    /// <summary>
    /// Blocked-turn data for <paramref name="layout"/>, cached. Reads the airport's turns from the global
    /// <see cref="NavigationDatabase"/>; empty when no database is initialized or the airport has none.
    /// </summary>
    public static BlockedTurnResult GetBlocked(AirportGroundLayout layout) => Cache.GetValue(layout, BuildForLayout);

    private static BlockedTurnResult BuildForLayout(AirportGroundLayout layout)
    {
        var db = NavigationDatabase.InstanceOrNull;
        IReadOnlyList<BlockedTurn> turns = db?.AirportSidecars.GetBlockedTurns(layout.AirportId) ?? [];
        return Resolve(layout, turns);
    }

    /// <summary>Pure resolution of <paramref name="turns"/> against <paramref name="layout"/>. Unit-testable without a database.</summary>
    public static BlockedTurnResult Resolve(AirportGroundLayout layout, IReadOnlyList<BlockedTurn> turns)
    {
        if (turns.Count == 0)
        {
            return BlockedTurnResult.Empty;
        }

        var forbiddenTurns = new HashSet<(int, int, int)>();
        var forbiddenArcMoves = new HashSet<(int, int)>();
        var hiddenArcs = new HashSet<(int, int)>();

        foreach (var turn in turns)
        {
            ResolveTurn(layout, turn, forbiddenTurns, forbiddenArcMoves, hiddenArcs);
        }

        return new BlockedTurnResult(forbiddenTurns, forbiddenArcMoves, hiddenArcs);
    }

    private static void ResolveTurn(
        AirportGroundLayout layout,
        BlockedTurn turn,
        HashSet<(int, int, int)> forbiddenTurns,
        HashSet<(int, int)> forbiddenArcMoves,
        HashSet<(int, int)> hiddenArcs
    )
    {
        var snapped = PolylineSnapper.Snap(layout, turn.Path, "Blocked-turn", Log);
        if (snapped is null)
        {
            return;
        }

        var seq = BuildNodeSequence(layout, snapped);
        if (seq is null || seq.Count < 3)
        {
            return;
        }

        // A traced edge that is itself an arc is a corner arc directly on the path (a removed-junction
        // apex, where no surviving node carries the pivot). Block + hide it.
        for (int i = 0; i + 1 < seq.Count; i++)
        {
            if (FindEdge(layout, seq[i], seq[i + 1]) is GroundArc)
            {
                AddArc(forbiddenArcMoves, hiddenArcs, seq[i], seq[i + 1]);
            }
        }

        // Interior nodes where the path turns onto a different taxiway are apex pivots. Block the pivot
        // turn-triple and the fillet corner arc that smooths the same turn.
        for (int i = 1; i + 1 < seq.Count; i++)
        {
            int prev = seq[i - 1];
            int apex = seq[i];
            int next = seq[i + 1];
            if (!IsCorner(layout, prev, apex, next))
            {
                continue;
            }

            forbiddenTurns.Add((prev, apex, next));
            forbiddenTurns.Add((next, apex, prev));

            var arc = FindCornerArc(layout, apex, prev, next);
            if (arc is not null)
            {
                AddArc(forbiddenArcMoves, hiddenArcs, arc.Nodes[0].Id, arc.Nodes[1].Id);
            }
            else
            {
                Log.LogDebug(
                    "Blocked-turn at {Airport}: no fillet arc matched the corner ({Prev},{Apex},{Next})",
                    layout.AirportId,
                    prev,
                    apex,
                    next
                );
            }
        }
    }

    /// <summary>Trace the full connected node-id sequence across the snapped waypoints; null when a segment cannot be spanned.</summary>
    private static List<int>? BuildNodeSequence(AirportGroundLayout layout, List<GroundNode> snapped)
    {
        var seq = new List<int> { snapped[0].Id };
        for (int i = 0; i + 1 < snapped.Count; i++)
        {
            var span = PolylineSnapper.BuildSpan(layout, snapped[i], snapped[i + 1]);
            if (span is null)
            {
                Log.LogWarning(
                    "Blocked-turn at {Airport}: nodes {A}->{B} are not connected and share no taxiway; skipping turn",
                    layout.AirportId,
                    snapped[i].Id,
                    snapped[i + 1].Id
                );
                return null;
            }

            foreach (var (_, to) in span)
            {
                if (seq[^1] != to)
                {
                    seq.Add(to);
                }
            }
        }

        return seq;
    }

    /// <summary>A corner is an interior node where the arms belong to different taxiways and the path deflects past the collinear threshold.</summary>
    private static bool IsCorner(AirportGroundLayout layout, int prev, int apex, int next)
    {
        if (
            !layout.Nodes.TryGetValue(prev, out var prevNode)
            || !layout.Nodes.TryGetValue(apex, out var apexNode)
            || !layout.Nodes.TryGetValue(next, out var nextNode)
        )
        {
            return false;
        }

        var armPrev = FindEdge(layout, apex, prev);
        var armNext = FindEdge(layout, apex, next);
        if (armPrev is null || armNext is null || armPrev.SharesTaxiway(armNext))
        {
            return false;
        }

        double bPrev = GeoMath.BearingTo(apexNode.Position, prevNode.Position);
        double bNext = GeoMath.BearingTo(apexNode.Position, nextNode.Position);
        double deflection = AngleDiff((bPrev + 180.0) % 360.0, bNext);
        return deflection > CornerDeflectionThresholdDeg;
    }

    /// <summary>
    /// The fillet corner arc smoothing the apex turn: an arc that bridges both arm taxiways and whose two
    /// endpoints bear (from the apex) toward the prev arm and the next arm respectively. Returns the
    /// best-aligned such arc, or null when none is within tolerance.
    /// </summary>
    private static GroundArc? FindCornerArc(AirportGroundLayout layout, int apex, int prev, int next)
    {
        var apexNode = layout.Nodes[apex];
        var prevNode = layout.Nodes[prev];
        var nextNode = layout.Nodes[next];
        double bPrev = GeoMath.BearingTo(apexNode.Position, prevNode.Position);
        double bNext = GeoMath.BearingTo(apexNode.Position, nextNode.Position);

        var armPrev = FindEdge(layout, apex, prev);
        var armNext = FindEdge(layout, apex, next);
        if (armPrev is null || armNext is null)
        {
            return null;
        }

        GroundArc? best = null;
        double bestScore = double.MaxValue;
        foreach (var arc in layout.Arcs)
        {
            if (!ArcBridgesArms(arc, armPrev, armNext))
            {
                continue;
            }

            double b0 = GeoMath.BearingTo(apexNode.Position, arc.Nodes[0].Position);
            double b1 = GeoMath.BearingTo(apexNode.Position, arc.Nodes[1].Position);

            // Either endpoint may face either arm; take the better of the two pairings.
            double pairingA = Math.Max(AngleDiff(b0, bPrev), AngleDiff(b1, bNext));
            double pairingB = Math.Max(AngleDiff(b0, bNext), AngleDiff(b1, bPrev));
            double worst = Math.Min(pairingA, pairingB);
            if (worst <= ArcBearingToleranceDeg && worst < bestScore)
            {
                bestScore = worst;
                best = arc;
            }
        }

        return best;
    }

    private static bool ArcBridgesArms(GroundArc arc, IGroundEdge armPrev, IGroundEdge armNext) =>
        ArmNames(armPrev).Any(arc.MatchesTaxiway) && ArmNames(armNext).Any(arc.MatchesTaxiway);

    private static IEnumerable<string> ArmNames(IGroundEdge edge) => edge is GroundArc arc ? arc.TaxiwayNames : [edge.TaxiwayName];

    private static IGroundEdge? FindEdge(AirportGroundLayout layout, int a, int b) =>
        layout.Nodes.TryGetValue(a, out var node) ? node.Edges.FirstOrDefault(e => e.HasNode(b)) : null;

    private static void AddArc(HashSet<(int, int)> moves, HashSet<(int, int)> hidden, int a, int b)
    {
        moves.Add((a, b));
        moves.Add((b, a));
        hidden.Add((a, b));
    }

    private static double AngleDiff(double a, double b)
    {
        double d = Math.Abs(a - b) % 360.0;
        return d > 180.0 ? 360.0 - d : d;
    }
}
