using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Pathfinding;
using Yaat.Sim.Phases.Ground;

namespace Yaat.Sim.Tests.Helpers;

/// <summary>
/// Structural assertions on resolved <see cref="TaxiRoute"/>s that hold at every airport and under every
/// cost preference. A route that walks tangent-cut → junction centre → tangent-cut over two straight edges
/// where the fillet generator already joined those two cuts with a <see cref="GroundArc"/> is a square
/// pivot at a painted corner. For a bend sharper than <see cref="GroundNavigator.EntryAlignmentThresholdDeg"/>
/// the navigator rounds that pivot at the nose-wheel radius near walking pace instead of playing the
/// fillet, and re-acquires the outgoing centerline with a visible swing (the OAK U/W corner in the S2-OAK-2
/// bundle); the arc is admissible whenever the pivot is — its end tangents are the two straights' bearings —
/// so there is no legitimate reason to prefer the pivot (a facility-blocked turn forbids the pivot triple and
/// the arc move alike; only a one-way constraint traced through the fillet's endpoints could forbid the arc
/// alone, and none of the covered airports has one). A gentler bend is rounded by pure pursuit at
/// taxi speed without leaving the line, so cutting it straight is not flagged, and neither is a pivot past a
/// fillet tighter than <see cref="GeometricAdmissibility.MinSteerableArcRadiusFt"/>, which no aircraft can
/// track and the pathfinder never admits.
/// </summary>
internal static class RouteGeometryAsserts
{
    public static void AssertNoSquarePivotWhereFilletExists(TaxiRoute route, string context)
    {
        var violations = FindSquarePivotsWhereFilletExists(route);
        Assert.True(
            violations.Count == 0,
            $"{context}: route pivots square through a junction the fillet generator rounded — {string.Join("; ", violations)}"
        );
    }

    /// <summary>
    /// Every consecutive straight pair (a→b, b→c) whose corner node b is neither a hold-short stop nor
    /// the route end, where an arc on node a ends at c. Empty for a clean route.
    /// </summary>
    public static List<string> FindSquarePivotsWhereFilletExists(TaxiRoute route)
    {
        var stops = route.HoldShortPoints.Select(h => h.NodeId).ToHashSet();
        var violations = new List<string>();
        for (int i = 0; i + 1 < route.Segments.Count; i++)
        {
            var into = route.Segments[i].Edge;
            var outOf = route.Segments[i + 1].Edge;
            if (into.Edge is GroundArc || outOf.Edge is GroundArc || into.ToNodeId != outOf.FromNodeId)
            {
                continue;
            }

            var a = into.FromNode;
            int b = into.ToNodeId;
            int c = outOf.ToNodeId;
            if (stops.Contains(b) || a.Id == c)
            {
                continue;
            }

            double bendDeg = GeoMath.AbsBearingDifference(into.ArrivalBearing, outOf.DepartureBearing);
            var arc = a.Edges.OfType<GroundArc>().FirstOrDefault(e => e.OtherNodeId(a.Id) == c);
            if (
                arc is null
                || (arc.MinRadiusOfCurvatureFt < GeometricAdmissibility.MinSteerableArcRadiusFt)
                || (bendDeg <= GroundNavigator.EntryAlignmentThresholdDeg)
            )
            {
                continue;
            }

            violations.Add(
                $"seg[{i}] {a.Id}->{b}->{c} ({route.Segments[i].TaxiwayName} -> {route.Segments[i + 1].TaxiwayName}, {bendDeg:F0}° bend) "
                    + $"while arc [{string.Join(",", arc.TaxiwayNames)}] joins {a.Id}<->{c} "
                    + $"({arc.DistanceNm * GeoMath.FeetPerNm:F0} ft, r={arc.MinRadiusOfCurvatureFt:F0} ft, {arc.MaxSafeSpeedKts(AircraftCategory.Jet):F1} kt)"
            );
        }

        return violations;
    }
}
