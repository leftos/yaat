using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Pathfinding;

/// <summary>
/// GitHub issue #236 follow-up — the pathfinder must fly the existing smooth fillet corner
/// arc at a lane-change transition, not the square junction-node pivot.
///
/// <para>
/// At SFO, taxiways A and B are parallel (~236 ft apart), joined by the ~perpendicular short
/// connector F1. A clearance <c>TAXI A F1 B</c> (the reporter's <c>TAXI A F1 B M1 1L</c>) is a
/// lane change. The fillet generator emits a smooth <c>[A,F1]</c> corner arc for the A→F1 turn
/// (radius ~72 ft) and a <c>[B,F1]</c> arc for the F1→B turn. The F1→B corner already routes over
/// its arc (from the F1 tangent-cut node, B is only reachable via the arc), but before the fix the
/// A→F1 turn routed through the <b>square pivot at the A/F1 junction node</b> because the
/// <c>[A,F1]</c> membership arc carried the <see cref="RouteCostFunction.MembershipJunctionArcContinuationCostNm"/>
/// (0.5 nm) penalty — even though that arc is the <i>intended</i> turn between the two cleared
/// taxiways, not a spurious turn-off. That square pivot is the "full turn to align with F1" the
/// reporter described.
/// </para>
///
/// <para>
/// The fix (transition-arc exemption in <c>SegmentExpander.LocalSearchToJunction</c>) lets the
/// A→F1 turn use its <c>[A,F1]</c> arc. Requirement ① is unaffected — its guard only flags
/// membership arcs flanked by the <i>same</i> single-name taxiway on both sides (a turn-off-and-return);
/// the A→F1 arc is flanked by A then F1 (different), so it is not a Req① diversion.
/// </para>
/// </summary>
public class Issue236LaneChangeArcTests(ITestOutputHelper output)
{
    private static AirportGroundLayout? Layout() => new TestAirportGroundData(FilletMode.Standard).GetLayout("SFO");

    [Fact]
    public void Sfo_TaxiAF1B_UsesAF1CornerArc_NotJunctionPivot()
    {
        var layout = Layout();
        if (layout is null)
        {
            output.WriteLine("sfo.geojson not found — skipping");
            return;
        }

        // Start on taxiway A a short distance NORTH of the A/F1 junction, so the aircraft
        // approaches southbound and turns left (east) onto F1 — the reporter's direction.
        const double StartLat = 37.617800;
        const double StartLon = -122.379400;
        var startNode = layout
            .Nodes.Values.Where(n => n.Edges.Any(e => e.MatchesTaxiway("A")))
            .OrderBy(n => GeoMath.DistanceNm(StartLat, StartLon, n.Position.Lat, n.Position.Lon))
            .First();
        output.WriteLine($"Start: #{startNode.Id} at ({startNode.Position.Lat:F6}, {startNode.Position.Lon:F6})");

        var route = TaxiPathfinder.ResolveExplicitPath(
            layout,
            startNode.Id,
            ["A", "F1", "B"],
            out string? failReason,
            new ExplicitPathOptions { AirportId = "SFO", DiagnosticLog = msg => output.WriteLine(msg) },
            AircraftCategory.Jet
        );

        Assert.NotNull(route);
        Assert.Null(failReason);

        output.WriteLine($"Route: {route.Segments.Count} segments");
        foreach (var seg in route.Segments)
        {
            bool arc = seg.Edge.Edge is GroundArc;
            output.WriteLine($"  {seg.TaxiwayName, -10} #{seg.FromNodeId} -> #{seg.ToNodeId} {(arc ? "[arc]" : "")}");
        }

        // The A→F1 turn must be flown over the [A,F1] fillet corner arc, not a square pivot
        // through the junction node. Detect the arc by membership: a GroundArc naming both A and F1.
        bool usesAF1Arc = route.Segments.Any(s =>
            s.Edge.Edge is GroundArc a
            && a.TaxiwayNames.Any(n => n.Equals("A", StringComparison.OrdinalIgnoreCase))
            && a.TaxiwayNames.Any(n => n.Equals("F1", StringComparison.OrdinalIgnoreCase))
        );

        Assert.True(
            usesAF1Arc,
            "TAXI A F1 B should turn A->F1 over the smooth [A,F1] fillet corner arc, but the resolved "
                + "route pivots square through the A/F1 junction node instead (no [A,F1] arc segment). "
                + "The membership-continuation penalty is being misapplied to the intended lane-change turn."
        );
    }
}
