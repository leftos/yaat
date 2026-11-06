using Yaat.Sim.Data.Airport;

namespace Yaat.Sim.Tests.Helpers;

/// <summary>
/// KOAK taxiway J's approach to the 28R/10L crossing, by meaning: the entry-side hold-short bar on J, the far-side bar across
/// the runway at the C/J junction, and the node a crossing is approached from.
/// </summary>
internal static class OakJCrossing
{
    /// <summary>The far-side 28R hold-short bar on J: the one nearest the C/J junction, across the runway.</summary>
    public static GroundNode FarBarOnJ(AirportGroundLayout layout) =>
        GroundNodeLookup.HoldShortBar(layout, "28R", "J", GroundNodeLookup.Junction(layout, "C", "J"));

    /// <summary>The entry-side 28R hold-short bar on J: of the two, the one farther from the C/J junction.</summary>
    public static GroundNode EntryBarOnJ(AirportGroundLayout layout)
    {
        LatLon cj = GroundNodeLookup.Junction(layout, "C", "J").Position;
        return TestLayoutNodes.RunwayHoldShortsOnTaxiway(layout, "28R", "J").MaxBy(node => GeoMath.DistanceNm(node.Position, cj))!;
    }

    /// <summary>
    /// The J node the crossing is approached from: two J edges back from the entry bar, where the high-speed exit geometry starts.
    /// </summary>
    public static GroundNode ApproachNode(AirportGroundLayout layout)
    {
        GroundNode bar = EntryBarOnJ(layout);
        GroundNode first = NextAlongJ(bar, GroundNodeLookup.Junction(layout, "C", "J"));
        return NextAlongJ(first, bar);
    }

    /// <summary>The J node one edge along from <paramref name="from"/>, on the far side from <paramref name="awayFrom"/>.</summary>
    private static GroundNode NextAlongJ(GroundNode from, GroundNode awayFrom) =>
        from
            .Edges.OfType<GroundEdge>()
            .Where(edge => edge.MatchesTaxiway("J"))
            .Select(edge => edge.OtherNode(from))
            .MaxBy(node => GeoMath.DistanceNm(node.Position, awayFrom.Position))!;
}
