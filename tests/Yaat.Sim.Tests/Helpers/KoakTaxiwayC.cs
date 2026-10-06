using Xunit;
using Yaat.Sim.Data.Airport;

namespace Yaat.Sim.Tests.Helpers;

/// <summary>
/// The KOAK taxiway C edge the mid-edge taxi tests stand on: the long straight C edge west of H, as a pose at no node
/// (issue #880 — the "aircraft on C's centreline west of H" fixture).
/// </summary>
public static class KoakTaxiwayC
{
    /// <summary>From this long (ft): the edge west of H spans it, so a mid-edge pose is clear of both its ends.</summary>
    private const double MinLongEdgeFt = 1000.0;

    /// <summary>
    /// KOAK's long straight C edge west of H: <c>TowardH</c>, its end at the C/H tangent cut, and <c>Behind</c>, its far
    /// end away from B. An aircraft spawned between the two facing <c>TowardH</c> is on C's centreline west of H, with no
    /// node within the at-node tolerance.
    /// </summary>
    public static (GroundNode TowardH, GroundNode Behind) LongEdgeWestOfH(AirportGroundLayout layout)
    {
        GroundNode junctionCH = Assert.IsType<GroundNode>(layout.FindIntersectionNode("C", "H"));
        GroundNode junctionCB = Assert.IsType<GroundNode>(layout.FindIntersectionNode("C", "B"));
        double awayFromB = (GeoMath.BearingTo(junctionCH.Position, junctionCB.Position) + 180.0) % 360.0;
        GroundNode towardH = NextAlongC(junctionCH, awayFromB);
        GroundNode behind = NextAlongC(towardH, GeoMath.BearingTo(junctionCH.Position, towardH.Position));
        double edgeFt = GeoMath.DistanceNm(towardH.Position, behind.Position) * GeoMath.FeetPerNm;
        Assert.True(
            edgeFt > MinLongEdgeFt,
            $"C edge {towardH.Id}-{behind.Id} (C/H {junctionCH.Id}, C/B {junctionCB.Id}) is {edgeFt:F0} ft, expected a long edge"
        );
        return (towardH, behind);
    }

    /// <summary>The node <paramref name="node"/>'s C edge leads to, nearest the bearing <paramref name="bearingDeg"/>.</summary>
    private static GroundNode NextAlongC(GroundNode node, double bearingDeg) =>
        node
            .Edges.Where(e => (e is GroundEdge) && e.MatchesTaxiway("C"))
            .Select(e => e.OtherNode(node))
            .OrderBy(other => GeoMath.AbsBearingDifference(GeoMath.BearingTo(node.Position, other.Position), bearingDeg))
            .First();
}
