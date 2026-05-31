using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests;

/// <summary>
/// Systemic guard for V2 fillet arc playback. <c>GroundNavigatorV2</c> compiles every
/// <see cref="GroundArc"/> into a <see cref="PathPrimitiveBezier"/> and plays it by arc-length, so
/// playback must terminate on the segment's to-node. The earlier <see cref="PathPrimitiveArc"/>
/// reinterpreted the fillet as a circle of the Bézier's <em>minimum</em> radius of curvature; for
/// wide sweeping runway/taxiway fillets the apex curvature is far tighter than the endpoint-connecting
/// radius, so the circle ended tens of feet short of the to-node (the N9225L OAK 28R→G corner under-
/// shot 56 ft). This sweeps every arc on the committed test airports and asserts the Bézier playback
/// lands on each arc's to-node, in both traversal directions.
/// </summary>
public class GroundArcBezierPlaybackGuardTests(ITestOutputHelper output)
{
    // Arc-length integration step error is sub-foot; a 2 ft tolerance is comfortably above it and far
    // below the tens-of-feet undershoot the circle approximation produced.
    private const double MaxPlaybackEndErrorFt = 2.0;

    // Degenerate arcs (sub-floor radius) are filtered out of the graph elsewhere; skip any that slip through.
    private const double MinUsableRadiusFt = 5.0;

    [Theory]
    [InlineData("OAK")]
    [InlineData("SFO")]
    [InlineData("FLL")]
    public void EveryV2Arc_BezierPlayback_EndsOnToNode(string airport)
    {
        var layout = new TestAirportGroundData(FilletMode.V2).GetLayout(airport);
        if (layout is null)
        {
            return; // test data absent — skip silently (offline convention)
        }

        int arcsChecked = 0;
        int circleWouldUndershoot = 0;
        double worstBezierErrFt = 0;
        string worstArc = "";

        foreach (var arc in layout.Arcs)
        {
            if (arc.MinRadiusOfCurvatureFt < MinUsableRadiusFt || arc.Nodes[0].Id == arc.Nodes[1].Id)
            {
                continue;
            }

            foreach (bool reversed in new[] { false, true })
            {
                var (from, to) = reversed ? (arc.Nodes[1], arc.Nodes[0]) : (arc.Nodes[0], arc.Nodes[1]);
                var segment = new TaxiRouteSegment { Edge = arc.Directed(from, to), TaxiwayName = arc.TaxiwayName };

                var bez = Assert.IsType<PathPrimitiveBezier>(PathPrimitiveBuilder.FromSegmentV2(segment));
                arcsChecked++;

                double bezErrFt = PlaybackEndErrorFt(bez, to);
                if (bezErrFt > worstBezierErrFt)
                {
                    worstBezierErrFt = bezErrFt;
                    worstArc = $"{arc.TaxiwayName} {from.Id}->{to.Id} r={arc.MinRadiusOfCurvatureFt:F0}ft";
                }

                // Informational: how often the retired circle approximation would have missed the node.
                if (CircleEndErrorFt(PathPrimitiveBuilder.FromSegment(segment), to) > 5.0)
                {
                    circleWouldUndershoot++;
                }

                Assert.True(
                    bezErrFt <= MaxPlaybackEndErrorFt,
                    $"{airport}: arc {arc.TaxiwayName} {from.Id}->{to.Id} (r={arc.MinRadiusOfCurvatureFt:F0}ft) Bézier playback "
                        + $"ended {bezErrFt:F1}ft from its to-node — playback is not terminating on the node."
                );
            }
        }

        output.WriteLine(
            $"{airport}: {arcsChecked} arc traversals; worst Bézier end-error {worstBezierErrFt:F2}ft ({worstArc}); "
                + $"the retired circle approximation would have undershot >5ft on {circleWouldUndershoot} of them."
        );
        Assert.True(arcsChecked > 0, $"{airport}: no V2 arcs found to check");
    }

    private static double PlaybackEndErrorFt(PathPrimitiveBezier bez, GroundNode toNode)
    {
        double t = 0.0;
        const double stepFt = 1.0;
        for (int i = 0; (i < 200000) && (t < 1.0); i++)
        {
            double speedFt = bez.Curve.DerivativeMagnitudeFt(t);
            t = speedFt > 1e-6 ? Math.Min(1.0, t + (stepFt / speedFt)) : 1.0;
        }
        var (lat, lon) = bez.Curve.Evaluate(Math.Min(t, 1.0));
        return GeoMath.DistanceNm(lat, lon, toNode.Position.Lat, toNode.Position.Lon) * GeoMath.FeetPerNm;
    }

    private static double CircleEndErrorFt(PathPrimitive circlePrim, GroundNode toNode)
    {
        if (circlePrim is not PathPrimitiveArc arc)
        {
            return 0;
        }

        double endBearing = arc.StartBearingFromCenterDeg + (arc.RightTurn ? arc.SweepDeg : -arc.SweepDeg);
        var (lat, lon) = GeoMath.ProjectPoint(arc.CenterLat, arc.CenterLon, new TrueHeading(endBearing), arc.RadiusNm);
        return GeoMath.DistanceNm(lat, lon, toNode.Position.Lat, toNode.Position.Lon) * GeoMath.FeetPerNm;
    }
}
