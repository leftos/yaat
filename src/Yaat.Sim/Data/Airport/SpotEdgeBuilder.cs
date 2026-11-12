using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Data.Airport;

/// <summary>
/// Adds an airport's sidecar spot-edge chains (<see cref="LayoutInputs.SpotEdges"/>) to a layout under construction: each
/// chain's points resolve to layout nodes and consecutive points are joined by one straight edge named after the chain.
/// Runs after the stand connectors and before the adjacency lists are built, so it reads <see cref="AirportGroundLayout.Edges"/>
/// only. An entry with a point that does not resolve, that resolves to a runway hold-short, parking or helipad node or a
/// runway centreline node, or with two consecutive points on one node is warned about and adds nothing.
/// </summary>
internal static class SpotEdgeBuilder
{
    /// <summary>The <see cref="GroundEdge.Origin"/> and <see cref="GroundNode.Origin"/> of everything a chain adds.</summary>
    internal const string Origin = "Sidecar:spot-edge";

    private static readonly ILogger Log = SimLog.CreateLogger("SpotEdgeBuilder");

    /// <summary>
    /// Adds each chain of <paramref name="chains"/> that resolves whole to <paramref name="layout"/>. Nodes a <c>point</c>
    /// mints get ids from <paramref name="nextNodeId"/>, after every node the layout already holds.
    /// </summary>
    /// <param name="layout">The layout under construction, its adjacency lists not yet built.</param>
    /// <param name="chains">The airport's spot-edge chains.</param>
    /// <param name="airportId">The layout's airport id, for the log lines.</param>
    /// <param name="coordIndex">The parser's snap index; a <c>point</c> reuses a node within its tolerance.</param>
    /// <param name="nextNodeId">The next free node id.</param>
    internal static void Apply(
        AirportGroundLayout layout,
        IReadOnlyList<SpotEdgeChain> chains,
        string airportId,
        CoordinateIndex coordIndex,
        ref int nextNodeId
    )
    {
        if (chains.Count == 0)
        {
            return;
        }

        var context = new BuildContext(
            layout,
            [.. layout.Edges.Where(e => e.IsRunwayCenterline).SelectMany(e => e.Nodes).Select(n => n.Id)],
            coordIndex
        );
        foreach (SpotEdgeChain chain in chains)
        {
            List<ResolvedPoint>? points = ResolveChain(context, chain, out string problem);
            if (points is null)
            {
                Log.LogWarning("{Airport}: sidecar spotEdges entry {Chain} {Problem}; ignored", airportId, chain.Name, problem);
                continue;
            }

            List<GroundNode> nodes = [];
            foreach (ResolvedPoint point in points)
            {
                nodes.Add(point.Node ?? MintNode(context, point.Position, ref nextNodeId));
            }

            AddEdges(layout, chain.Name, nodes);
        }
    }

    /// <summary>The layout under construction, its runway centreline node ids and the parser's snap index.</summary>
    private sealed record BuildContext(AirportGroundLayout Layout, HashSet<int> CentrelineNodeIds, CoordinateIndex Index);

    /// <summary>A resolved chain point: an existing node, or (with a null node) the position of a node to mint.</summary>
    private readonly record struct ResolvedPoint(GroundNode? Node, LatLon Position);

    /// <summary>Every point of <paramref name="chain"/> resolved, or null with <paramref name="problem"/> set.</summary>
    private static List<ResolvedPoint>? ResolveChain(BuildContext context, SpotEdgeChain chain, out string problem)
    {
        var points = new List<ResolvedPoint>(chain.Path.Count);
        for (int i = 0; i < chain.Path.Count; i++)
        {
            ResolvedPoint? point = Resolve(context, chain.Path[i], out problem);
            if (point is not { } resolved)
            {
                return null;
            }

            if ((i > 0) && IsSameNode(points[i - 1], resolved))
            {
                problem = $"joins point {i - 1} to point {i} at the same node";
                return null;
            }

            points.Add(resolved);
        }

        problem = string.Empty;
        return points;
    }

    private static ResolvedPoint? Resolve(BuildContext context, SpotEdgePoint point, out string problem)
    {
        if (point.Kind == SpotEdgePointKind.Point)
        {
            // Stands and helipads are not in the snap index, so a point on one is found by its own search and refused.
            LatLon at = point.Position!.Value;
            GroundNode? existing = StandOrHelipadAt(context.Layout, at) ?? ExistingNodeAt(context, at);
            if (existing is null)
            {
                problem = string.Empty;
                return new ResolvedPoint(null, at);
            }

            return AcceptOrNull(context, point, existing, out problem);
        }

        GroundNode? node = point.Kind switch
        {
            SpotEdgePointKind.Spot => ResolveSpot(context.Layout, point, out problem),
            SpotEdgePointKind.Junction => ResolveJunction(context.Layout, point, out problem),
            SpotEdgePointKind.TaxiwayNear => ResolveTaxiwayNear(context.Layout, point, out problem),
            _ => throw new UnreachableException($"spot-edge point kind {point.Kind}"),
        };
        return node is null ? null : AcceptOrNull(context, point, node, out problem);
    }

    /// <summary>The point resolved to <paramref name="node"/>, or null with <paramref name="problem"/> set when a chain may not use it.</summary>
    private static ResolvedPoint? AcceptOrNull(BuildContext context, SpotEdgePoint point, GroundNode node, out string problem)
    {
        string? reason = ForbiddenReason(context, node);
        if (reason is null)
        {
            problem = string.Empty;
            return new ResolvedPoint(node, node.Position);
        }

        problem = $"resolves {Describe(point)} to node {node.Id}, {reason}";
        return null;
    }

    /// <summary>A parking or helipad node within the snap tolerance of <paramref name="position"/>, or null.</summary>
    private static GroundNode? StandOrHelipadAt(AirportGroundLayout layout, LatLon position) =>
        layout.Nodes.Values.FirstOrDefault(n =>
            (n.Type is GroundNodeType.Parking or GroundNodeType.Helipad) && IsWithinSnapTolerance(n.Position, position)
        );

    private static bool IsWithinSnapTolerance(LatLon a, LatLon b) =>
        (Math.Abs(a.Lat - b.Lat) <= GeoJsonParser.SnapToleranceDeg) && (Math.Abs(a.Lon - b.Lon) <= GeoJsonParser.SnapToleranceDeg);

    /// <summary>The one spot node whose name, less a leading <c>$</c>, is the point's spot (case-insensitive).</summary>
    private static GroundNode? ResolveSpot(AirportGroundLayout layout, SpotEdgePoint point, out string problem)
    {
        List<GroundNode> matches =
        [
            .. layout.Nodes.Values.Where(n =>
                (n.Type == GroundNodeType.Spot)
                && (n.Name is { } name)
                && string.Equals(name.StartsWith('$') ? name[1..] : name, point.Spot, StringComparison.OrdinalIgnoreCase)
            ),
        ];
        problem = matches.Count switch
        {
            1 => string.Empty,
            0 => $"names spot {point.Spot}, which the layout does not have",
            _ => $"names spot {point.Spot}, which matches {matches.Count} spot nodes",
        };
        return matches.Count == 1 ? matches[0] : null;
    }

    /// <summary>The one node both junction taxiways' connectable edges share.</summary>
    private static GroundNode? ResolveJunction(AirportGroundLayout layout, SpotEdgePoint point, out string problem)
    {
        HashSet<GroundNode> shared = ConnectableNodesOf(layout, point.JunctionA!);
        shared.IntersectWith(ConnectableNodesOf(layout, point.JunctionB!));
        problem = shared.Count switch
        {
            1 => string.Empty,
            0 => $"names junction {point.JunctionA}/{point.JunctionB}, where the two taxiways share no node",
            _ => $"names junction {point.JunctionA}/{point.JunctionB}, where the two taxiways share {shared.Count} nodes",
        };
        return shared.Count == 1 ? shared.First() : null;
    }

    /// <summary>The vertex of the point's taxiway nearest its position, within the stand connector cap.</summary>
    private static GroundNode? ResolveTaxiwayNear(AirportGroundLayout layout, SpotEdgePoint point, out string problem)
    {
        LatLon near = point.Position!.Value;
        GroundNode? vertex = ConnectableNodesOf(layout, point.Taxiway!).MinBy(n => GeoMath.DistanceNm(n.Position, near));
        if ((vertex is null) || (GeoMath.DistanceNm(vertex.Position, near) > GeoJsonParser.ParkingConnectMaxNm))
        {
            problem = $"finds no vertex of taxiway {point.Taxiway} within {GeoJsonParser.ParkingConnectMaxNm} nm of {Format(near)}";
            return null;
        }

        problem = string.Empty;
        return vertex;
    }

    /// <summary>The nodes of every edge of <paramref name="taxiway"/> a connector may join (<see cref="GeoJsonParser.CanConnectToEdge"/>).</summary>
    private static HashSet<GroundNode> ConnectableNodesOf(AirportGroundLayout layout, string taxiway) =>
        [.. layout.Edges.Where(e => GeoJsonParser.CanConnectToEdge(e) && e.MatchesTaxiway(taxiway)).SelectMany(e => e.Nodes)];

    /// <summary>Why a chain may not use <paramref name="node"/>, or null when it may.</summary>
    private static string? ForbiddenReason(BuildContext context, GroundNode node) =>
        node.Type switch
        {
            GroundNodeType.RunwayHoldShort => "a runway hold-short node",
            GroundNodeType.Parking => "a parking node",
            GroundNodeType.Helipad => "a helipad node",
            _ => context.CentrelineNodeIds.Contains(node.Id) ? "a runway centreline node" : null,
        };

    private static GroundNode? ExistingNodeAt(BuildContext context, LatLon position) =>
        ((context.Index.FindNearest(position) is { } id) && context.Layout.Nodes.TryGetValue(id, out GroundNode? node)) ? node : null;

    private static bool IsSameNode(ResolvedPoint a, ResolvedPoint b) =>
        ((a.Node is not null) || (b.Node is not null)) ? ReferenceEquals(a.Node, b.Node) : IsWithinSnapTolerance(a.Position, b.Position);

    /// <summary>A node at <paramref name="position"/>: one an earlier point of the chain minted there, else a new one.</summary>
    private static GroundNode MintNode(BuildContext context, LatLon position, ref int nextNodeId)
    {
        if (ExistingNodeAt(context, position) is { } existing)
        {
            return existing;
        }

        var node = new GroundNode
        {
            Id = nextNodeId++,
            Position = position,
            Type = GroundNodeType.TaxiwayIntersection,
            Origin = Origin,
        };
        context.Layout.Nodes[node.Id] = node;
        context.Index.Add(position, node.Id);
        return node;
    }

    /// <summary>One straight edge per consecutive pair, unless an edge of <paramref name="name"/> already joins the pair.</summary>
    private static void AddEdges(AirportGroundLayout layout, string name, List<GroundNode> nodes)
    {
        for (int i = 1; i < nodes.Count; i++)
        {
            GroundNode a = nodes[i - 1];
            GroundNode b = nodes[i];
            if (layout.Edges.Any(e => e.MatchesTaxiway(name) && e.HasNode(a.Id) && e.HasNode(b.Id)))
            {
                continue;
            }

            layout.Edges.Add(
                new GroundEdge
                {
                    Nodes = [a, b],
                    TaxiwayName = name,
                    DistanceNm = GeoMath.DistanceNm(a.Position, b.Position),
                    Origin = Origin,
                }
            );
        }
    }

    private static string Describe(SpotEdgePoint point) =>
        point.Kind switch
        {
            SpotEdgePointKind.Spot => $"spot {point.Spot}",
            SpotEdgePointKind.Junction => $"junction {point.JunctionA}/{point.JunctionB}",
            SpotEdgePointKind.TaxiwayNear => $"taxiway {point.Taxiway} near {Format(point.Position!.Value)}",
            SpotEdgePointKind.Point => $"point {Format(point.Position!.Value)}",
            _ => throw new UnreachableException($"spot-edge point kind {point.Kind}"),
        };

    private static string Format(LatLon position) => string.Create(CultureInfo.InvariantCulture, $"({position.Lat:F6}, {position.Lon:F6})");
}
