using Yaat.Sim.Data.Airport;

namespace Yaat.Sim.Tests.Helpers;

/// <summary>
/// Finds a ground-graph node by what it is (a runway's hold-short bar on a taxiway, the junction of two taxiways, a
/// parking stand, a point a given distance along a taxiway) instead of by its node id. Ids come from a running counter
/// that every parse pass draws from, so they shift whenever the layout's inputs change; a fixture that names a node by
/// meaning keeps meaning the same node. Each lookup returns exactly one node, or throws naming the airport and the query
/// when none matches or, without a tie-breaker, more than one does.
/// </summary>
/// <remarks>
/// A node is on a taxiway when one of its edges carries exactly that name, so a fillet arc joining two taxiways
/// (<c>"C - J"</c>) puts its ends on neither. A runway centreline is named <c>RWY{CombinedId}</c>, as in <c>"RWY15/33"</c>.
/// </remarks>
internal static class GroundNodeLookup
{
    /// <summary>The only hold-short node of <paramref name="runway"/> on <paramref name="taxiway"/>.</summary>
    public static GroundNode HoldShortBar(AirportGroundLayout layout, string runway, string taxiway) =>
        Single(layout, Bars(layout, runway, taxiway), $"hold-short bar of runway {runway} on taxiway {taxiway}");

    /// <summary>
    /// The hold-short node of <paramref name="runway"/> on <paramref name="taxiway"/> nearest <paramref name="nearestTo"/>:
    /// a taxiway that crosses the runway has one each side.
    /// </summary>
    public static GroundNode HoldShortBar(AirportGroundLayout layout, string runway, string taxiway, GroundNode nearestTo) =>
        Nearest(layout, Bars(layout, runway, taxiway), nearestTo, $"hold-short bar of runway {runway} on taxiway {taxiway}");

    /// <summary>The only node with an edge on <paramref name="taxiwayA"/> and an edge on <paramref name="taxiwayB"/>.</summary>
    public static GroundNode Junction(AirportGroundLayout layout, string taxiwayA, string taxiwayB) =>
        Single(layout, Junctions(layout, taxiwayA, taxiwayB), $"junction of taxiways {taxiwayA} and {taxiwayB}");

    /// <summary>
    /// The node with an edge on <paramref name="taxiwayA"/> and an edge on <paramref name="taxiwayB"/> nearest
    /// <paramref name="nearestTo"/>, for two taxiways that meet more than once.
    /// </summary>
    public static GroundNode Junction(AirportGroundLayout layout, string taxiwayA, string taxiwayB, GroundNode nearestTo) =>
        Nearest(layout, Junctions(layout, taxiwayA, taxiwayB), nearestTo, $"junction of taxiways {taxiwayA} and {taxiwayB}");

    /// <summary>The parking (stand) node named <paramref name="name"/>.</summary>
    public static GroundNode Parking(AirportGroundLayout layout, string name)
    {
        List<GroundNode> stands =
        [
            .. layout.Nodes.Values.Where(node =>
                (node.Type == GroundNodeType.Parking) && string.Equals(node.Name, name, StringComparison.OrdinalIgnoreCase)
            ),
        ];
        return stands.Count switch
        {
            0 => throw new InvalidOperationException($"{layout.AirportId}: no parking {name}"),
            1 => stands[0],
            _ => throw new InvalidOperationException($"{layout.AirportId}: {stands.Count} parking nodes are named {name} ({Ids(stands)})"),
        };
    }

    /// <summary>
    /// The first node on <paramref name="taxiway"/> at or past <paramref name="distanceFt"/> along it from
    /// <paramref name="from"/>: the walk leaves by the taxiway edge pointing most nearly at <paramref name="toward"/>, then
    /// goes straight on at every node. A distance of zero returns <paramref name="from"/>. Throws when <paramref name="from"/>
    /// has no edge on the taxiway, or none within 90° of <paramref name="toward"/>, and when the walk ends short of
    /// <paramref name="distanceFt"/> because the taxiway ends or its next edge turns 90° or more.
    /// </summary>
    public static GroundNode OnTaxiway(AirportGroundLayout layout, string taxiway, GroundNode from, double distanceFt, GroundNode toward)
    {
        double towardDeg = GeoMath.BearingTo(from.Position, toward.Position);
        (IGroundEdge? edge, double offDeg) = StraightestEdge(from, null, taxiway, towardDeg);
        if (edge is null)
        {
            throw new InvalidOperationException($"{layout.AirportId}: node #{from.Id} has no edge on taxiway {taxiway}");
        }

        if (offDeg >= 90.0)
        {
            throw new InvalidOperationException(
                $"{layout.AirportId}: no edge on taxiway {taxiway} at node #{from.Id} leads toward #{toward.Id} (best is {offDeg:F0}° off)"
            );
        }

        GroundNode at = from;
        double walkedFt = 0.0;
        while ((walkedFt < distanceFt) && (edge is not null))
        {
            GroundNode next = edge.OtherNode(at);
            double headingDeg = GeoMath.BearingTo(at.Position, next.Position);
            (at, walkedFt) = (next, walkedFt + (edge.DistanceNm * GeoMath.FeetPerNm));
            (IGroundEdge? onward, double onwardOffDeg) = StraightestEdge(at, edge, taxiway, headingDeg);
            edge = (onwardOffDeg < 90.0) ? onward : null;
        }

        if (walkedFt < distanceFt)
        {
            throw new InvalidOperationException(
                $"{layout.AirportId}: taxiway {taxiway} from #{from.Id} toward #{toward.Id} ends {walkedFt:F0} ft along, short of {distanceFt:F0} ft"
            );
        }

        return at;
    }

    private static List<GroundNode> Bars(AirportGroundLayout layout, string runway, string taxiway) =>
        TestLayoutNodes.RunwayHoldShortsOnTaxiway(layout, runway, taxiway);

    private static List<GroundNode> Junctions(AirportGroundLayout layout, string taxiwayA, string taxiwayB) =>
        [.. layout.Nodes.Values.Where(node => IsOn(node, taxiwayA) && IsOn(node, taxiwayB))];

    private static (IGroundEdge? Edge, double OffDeg) StraightestEdge(GroundNode at, IGroundEdge? arrivedBy, string taxiway, double headingDeg)
    {
        IGroundEdge? best = null;
        double bestOffDeg = double.MaxValue;
        foreach (IGroundEdge edge in at.Edges)
        {
            if (ReferenceEquals(edge, arrivedBy) || !IsOnTaxiway(edge, taxiway))
            {
                continue;
            }

            double offDeg = GeoMath.AbsBearingDifference(headingDeg, GeoMath.BearingTo(at.Position, edge.OtherNode(at).Position));
            if (offDeg < bestOffDeg)
            {
                (best, bestOffDeg) = (edge, offDeg);
            }
        }

        return (best, bestOffDeg);
    }

    private static bool IsOn(GroundNode node, string taxiway) => node.Edges.Any(edge => IsOnTaxiway(edge, taxiway));

    private static bool IsOnTaxiway(IGroundEdge edge, string taxiway) => string.Equals(edge.TaxiwayName, taxiway, StringComparison.OrdinalIgnoreCase);

    private static string Ids(List<GroundNode> nodes) => string.Join(", ", nodes.Select(node => $"#{node.Id}"));

    private static GroundNode Single(AirportGroundLayout layout, List<GroundNode> matches, string query) =>
        matches.Count switch
        {
            0 => throw new InvalidOperationException($"{layout.AirportId}: no {query}"),
            1 => matches[0],
            _ => throw new InvalidOperationException(
                $"{layout.AirportId}: {matches.Count} nodes match {query} ({Ids(matches)}); pick one with the nearestTo overload"
            ),
        };

    private static GroundNode Nearest(AirportGroundLayout layout, List<GroundNode> matches, GroundNode nearestTo, string query) =>
        (matches.Count == 0)
            ? throw new InvalidOperationException($"{layout.AirportId}: no {query}")
            : matches.MinBy(node => GeoMath.DistanceNm(nearestTo.Position, node.Position))!;
}
