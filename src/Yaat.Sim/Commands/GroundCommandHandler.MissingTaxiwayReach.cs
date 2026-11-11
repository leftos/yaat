using Microsoft.Extensions.Logging;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Pathfinding;

namespace Yaat.Sim.Commands;

/// <summary>
/// The missing-taxiway fallback's reach test: the movement-area taxiways a stand, spot or helipad can be reached over
/// from the destination itself, so a candidate X that cannot reach it is never resolved. Each candidate is a full route
/// resolution on the shared tick thread, and nearly all of them fail.
/// </summary>
public static partial class GroundCommandHandler
{
    /// <summary>
    /// The movement-area taxiways that reach a clearance's destination from the graph, and whether the clearance's own
    /// taxiways already lie on that reach. A destination the layout does not carry has none.
    /// </summary>
    /// <param name="Destination">The destination node's name, for the log.</param>
    /// <param name="Reachable">Every movement-area taxiway an edge on the flood reaches.</param>
    /// <param name="PathTouches">Whether the clearance's path or a hold-short's taxiway lies on <paramref name="Reachable"/>.</param>
    private sealed record MissingTaxiwayReach(string Destination, IReadOnlySet<string> Reachable, bool PathTouches)
    {
        /// <summary>
        /// Whether the candidate <paramref name="taxiway"/> is worth resolving: it can reach the destination, or the
        /// cleared path already lies on that reach, so every candidate can.
        /// </summary>
        internal bool Admits(string taxiway) => PathTouches || Reachable.Contains(taxiway);
    }

    /// <summary>What one flood walks: the layout, the destination's own name (never movement area), and its classification.</summary>
    private sealed record ReachWorld(AirportGroundLayout Layout, IReadOnlySet<string> Own, MovementAreaClassification Classification);

    /// <summary>
    /// The reach set for a clearance to <paramref name="destinationNode"/>, or null where the fallback keeps every
    /// candidate: a runway destination and a destination node the layout does not carry.
    /// </summary>
    private static MissingTaxiwayReach? MissingTaxiwayReachFor(AirportGroundLayout layout, TaxiCommand taxi, GroundNode? destinationNode)
    {
        if ((taxi.DestinationRunway is not null) || (destinationNode is not { Name: { Length: > 0 } name } destination))
        {
            return null;
        }

        var own = new HashSet<string>([name], StringComparer.OrdinalIgnoreCase);
        var world = new ReachWorld(layout, own, MovementAreaClassification.For(layout));

        // Pass one crosses only edges no movement-area name blocks; pass two may also cross the taxiways pass one reached
        // and the airport's one-way lanes, which the expander may imply uncleared as the connector into a ramp
        // (SegmentExpander.TryExtendViaOneWayLane).
        var none = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        HashSet<string> leading = Flood(world, destination, none);
        var passable = new HashSet<string>(leading, StringComparer.OrdinalIgnoreCase);
        passable.UnionWith(OneWayResolver.GetOneWayLaneTaxiways(layout));
        HashSet<string> reachable = Flood(world, destination, passable);
        return new MissingTaxiwayReach(name, reachable, PathTouchesReach(taxi, reachable));
    }

    /// <summary>
    /// The candidates <paramref name="reach"/> admits, logging once the destination, the reach's size, whether the
    /// cleared path touches it, and the candidates skipped.
    /// </summary>
    private static List<string> AdmittedMissingTaxiways(List<string> candidates, MissingTaxiwayReach reach, string callsign)
    {
        List<string> admitted = [.. candidates.Where(reach.Admits)];
        List<string> skipped = [.. candidates.Where(candidate => !reach.Admits(candidate))];
        Log.LogDebug(
            "[TryTaxi] {Callsign}: missing-taxiway reach to {Destination} names {Count} taxiways; cleared path touches it: {PathTouches}; "
                + "skipped [{Skipped}]",
            callsign,
            reach.Destination,
            reach.Reachable.Count,
            reach.PathTouches,
            string.Join(" ", skipped)
        );
        return admitted;
    }

    /// <summary>
    /// Every movement-area taxiway named on an edge reachable from <paramref name="start"/>, walking undirected over every
    /// edge no name in <paramref name="passable"/> blocks.
    /// </summary>
    private static HashSet<string> Flood(ReachWorld world, GroundNode start, IReadOnlySet<string> passable)
    {
        var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<int> { start.Id };
        var queue = new Queue<GroundNode>();
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            GroundNode node = queue.Dequeue();
            foreach (IGroundEdge edge in node.Edges)
            {
                foreach (string name in SegmentExpander.EdgeNames(edge))
                {
                    if (IsMovementAreaName(name, world))
                    {
                        touched.Add(name);
                    }
                }

                GroundNode other = edge.OtherNode(node);
                if (Crossable(edge, world, passable) && seen.Add(other.Id))
                {
                    queue.Enqueue(other);
                }
            }
        }

        return touched;
    }

    /// <summary>A movement-area name under the layout's classification, the destination's own name excepted.</summary>
    private static bool IsMovementAreaName(string name, ReachWorld world) =>
        SegmentExpander.IsUnclearedMovementAreaName(name, world.Own, world.Classification);

    /// <summary>
    /// Whether <paramref name="name"/> stops a flood at its edge: a movement-area name the passable set leaves out. A
    /// combined junction name (<c>"A - B"</c>) is never a name the router resolves, so it never blocks.
    /// </summary>
    private static bool Blocked(string name, ReachWorld world, IReadOnlySet<string> passable) =>
        !name.Contains(" - ") && IsMovementAreaName(name, world) && !passable.Contains(name);

    /// <summary>Whether any name <paramref name="edge"/> can be driven as is unblocked.</summary>
    private static bool Crossable(IGroundEdge edge, ReachWorld world, IReadOnlySet<string> passable) =>
        RouteNames(edge).Any(name => !Blocked(name, world, passable));

    /// <summary>
    /// The names <see cref="RouteCostFunction.ResolveTaxiwayName"/> can give <paramref name="edge"/>: a junction arc's two
    /// taxiways, else the edge's own name.
    /// </summary>
    private static IEnumerable<string> RouteNames(IGroundEdge edge) =>
        edge is GroundArc { TaxiwayNames.Length: 2 } arc ? arc.TaxiwayNames : [edge.TaxiwayName];

    /// <summary>Whether a taxiway the clearance names — its path, or a hold-short's taxiway or target — lies on the reach.</summary>
    private static bool PathTouchesReach(TaxiCommand taxi, IReadOnlySet<string> reachable) =>
        taxi.Path.Any(reachable.Contains) || taxi.HoldShorts.Any(hold => reachable.Contains(hold.OnTaxiway ?? hold.Target));
}
