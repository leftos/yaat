using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// YAAT-1263: at SFO (S1-SFO-3 Ground Control 28RT) UAL2237 was cleared <c>TAXI GL L LF F 28L</c> at t=2081 and told
/// <c>GIVEWAY UAL926</c> at t=2098, with UAL926 (<c>TAXI A F 28L</c>, <c>CROSS 1L 1R</c>) about 5,000 ft away crossing at
/// 90 degrees toward F. The hold released at once, UAL2237 entered F first, and UAL926 ended stopped behind it. The give-way
/// must hold UAL2237 short of the node where the routes meet until UAL926's tail has cleared it (7110.65 3-7-2.a).
///
/// <para>
/// Until then UAL2237's nose keeps UAL926's half-span plus the wingtip buffer off UAL926's inbound path only, and UAL2237 never
/// passes the node ahead of UAL926's tail. Both leave the node along F, so it is a merge: a merge hands over to in-trail
/// spacing on the shared taxiway, and the edge out of the node is UAL2237's own route, which an aircraft falling in behind
/// must approach before the leader's tail has cleared the node.
/// </para>
/// </summary>
public class SfoGiveWayCrossingTests(ITestOutputHelper output)
{
    private const string BundlePath = "TestData/sfo-gc-28rt-recording.yaat-bug-report-bundle.zip";
    private const string HeldCallsign = "UAL2237";
    private const string TrafficCallsign = "UAL926";

    /// <summary>Points sampled along a fillet arc for the traffic's path.</summary>
    private const int ArcSamples = 32;

    [Fact]
    public void UAL2237_GiveWayToUAL926OnF_WaitsUntilUAL926HasPassed()
    {
        var groundData = RecordingGroundData.VersionPinned(BundlePath, "UAL2237's GIVEWAY to UAL926 crossing toward F");
        if (groundData.GetLayout("SFO") is null)
        {
            return;
        }

        TestVnasData.EnsureInitialized();
        RecordingArchive? archive = RecordingLoader.OpenArchive(BundlePath);
        Assert.NotNull(archive);

        using (archive)
        {
            var engine = new SimulationEngine(groundData, PushPlanStore.CreateShipped());
            engine.Replay(archive.ToBaseSessionRecording(), 2098);

            AircraftState held = Assert.IsType<AircraftState>(engine.FindAircraft(HeldCallsign));
            AircraftState traffic = Assert.IsType<AircraftState>(engine.FindAircraft(TrafficCallsign));
            Assert.True(held.Ground.Hold?.IsGiveWayFor(TrafficCallsign) == true, "test setup: UAL2237 gives way to UAL926 at t=2098");

            GroundConflictDetector.GiveWayJunction? junction = GroundConflictDetector.GiveWayJunctionOf(held, traffic);
            Assert.True(junction is { TrafficPast: false }, "test setup: the routes should meet at a node UAL926 has still to clear");
            int nodeId = junction.Value.NodeId;
            double requiredTailPastFt = junction.Value.RequiredPastFt;
            double trafficHalfLengthFt = AircraftLength.ResolveFt(traffic.AircraftType) / 2.0;
            double heldHalfLengthFt = AircraftLength.ResolveFt(held.AircraftType) / 2.0;
            double noseRequiredFt = HalfSpanFt(traffic.AircraftType) + GroundOutlineSweep.WingtipBufferFt;
            List<LatLon> trafficPath = InboundPathToNode(RouteOf(traffic), nodeId);
            output.WriteLine(
                $"shared node {nodeId}, UAL926's tail must be {requiredTailPastFt:F0} ft past it, UAL2237's nose {noseRequiredFt:F0} ft off its inbound path"
            );

            double heldPastFt = double.NaN;
            double trafficPastFt = double.NaN;
            for (int t = 2099; t <= 2400; t++)
            {
                engine.TickOneSecond();
                heldPastFt = CentrePastFt(held, nodeId);
                trafficPastFt = CentrePastFt(traffic, nodeId);
                double trafficTailPastFt = trafficPastFt - trafficHalfLengthFt;
                LatLon heldNose = GeoMath.ProjectPoint(held.Position, held.TrueHeading, heldHalfLengthFt / GeoMath.FeetPerNm);
                double noseOffPathFt = DistanceToPathFt(trafficPath, heldNose);
                if (t % 10 == 0)
                {
                    output.WriteLine(
                        $"t={t} UAL2237 hold={held.Ground.Hold?.Kind.ToString() ?? "-"} gs={held.GroundSpeed:F1} past={heldPastFt:F0}ft "
                            + $"nose off path={noseOffPathFt:F0}ft | UAL926 gs={traffic.GroundSpeed:F1} tail past={trafficTailPastFt:F0}ft"
                    );
                }

                Assert.True(
                    (heldPastFt < 0) || (trafficTailPastFt >= requiredTailPastFt),
                    $"t={t}: UAL2237 is {heldPastFt:F0} ft past node {nodeId} while UAL926's tail is only {trafficTailPastFt:F0} ft past it "
                        + $"(needs {requiredTailPastFt:F0} ft)"
                );
                Assert.True(
                    (trafficTailPastFt >= requiredTailPastFt) || (noseOffPathFt >= noseRequiredFt),
                    $"t={t}: UAL2237's nose is {noseOffPathFt:F0} ft off UAL926's inbound path to node {nodeId} (needs {noseRequiredFt:F0} ft) "
                        + $"while UAL926's tail is only {trafficTailPastFt:F0} ft past it"
                );
                Assert.True(
                    (trafficTailPastFt >= requiredTailPastFt) || (heldPastFt < 0) || (heldPastFt < trafficTailPastFt),
                    $"t={t}: UAL2237 is {heldPastFt:F0} ft past node {nodeId}, ahead of UAL926's tail at {trafficTailPastFt:F0} ft"
                );
            }

            Assert.True(
                trafficPastFt > heldPastFt,
                $"at t=2400 UAL926 is {trafficPastFt:F0} ft past node {nodeId}, behind UAL2237 at {heldPastFt:F0} ft"
            );
        }
    }

    private static TaxiRoute RouteOf(AircraftState aircraft)
    {
        TaxiRoute? route = FollowingPhase.DrivenRouteOf(aircraft) ?? aircraft.Ground.AssignedTaxiRoute;
        Assert.True(route is not null, $"{aircraft.Callsign} has no taxi route");
        return route;
    }

    /// <summary>
    /// How far (ft) the aircraft's centre is past <paramref name="nodeId"/> along its taxi route, negative while short of it,
    /// measured here from the route's geometry: on the segment into the node, the signed along-track distance from the node
    /// along that segment's chord; on an earlier segment, minus the straight distance to the node; past it, the chords of the
    /// segments from the node to the current one plus the signed along-track distance along the current one's chord.
    /// </summary>
    private static double CentrePastFt(AircraftState aircraft, int nodeId)
    {
        TaxiRoute route = RouteOf(aircraft);
        int intoNode = route.Segments.FindIndex(s => s.ToNodeId == nodeId);
        Assert.True(intoNode >= 0, $"{aircraft.Callsign}'s route never passes node {nodeId}");
        int current = Math.Clamp(route.CurrentSegmentIndex, 0, route.Segments.Count - 1);
        LatLon node = route.Segments[intoNode].Edge.ToNode.Position;
        if (current < intoNode)
        {
            return -GeoMath.DistanceNm(aircraft.Position, node) * GeoMath.FeetPerNm;
        }

        if (current == intoNode)
        {
            return AlongChordFt(aircraft.Position, route.Segments[intoNode].Edge.FromNode.Position, node, node);
        }

        double pastFt = 0;
        for (int i = intoNode + 1; i < current; i++)
        {
            pastFt += GeoMath.DistanceNm(route.Segments[i].Edge.FromNode.Position, route.Segments[i].Edge.ToNode.Position) * GeoMath.FeetPerNm;
        }

        DirectionalEdge edge = route.Segments[current].Edge;
        return pastFt + AlongChordFt(aircraft.Position, edge.FromNode.Position, edge.ToNode.Position, edge.FromNode.Position);
    }

    /// <summary>The signed distance (ft) of <paramref name="point"/> past <paramref name="origin"/> along the chord <paramref name="from"/> → <paramref name="to"/>.</summary>
    private static double AlongChordFt(LatLon point, LatLon from, LatLon to, LatLon origin) =>
        GeoMath.AlongTrackDistanceNm(point, origin, new TrueHeading(GeoMath.BearingTo(from, to))) * GeoMath.FeetPerNm;

    /// <summary>
    /// The pavement of <paramref name="route"/>'s edges from its current segment up to and into <paramref name="nodeId"/>, as one
    /// polyline: the traffic's inbound path, without the edge out of the node.
    /// </summary>
    private static List<LatLon> InboundPathToNode(TaxiRoute route, int nodeId)
    {
        int intoNode = route.Segments.FindIndex(s => s.ToNodeId == nodeId);
        Assert.True(intoNode >= 0, $"the traffic's route never passes node {nodeId}");
        int first = Math.Clamp(route.CurrentSegmentIndex, 0, intoNode);
        List<LatLon> path = [.. PavementPoints(route.Segments[first].Edge)];
        for (int i = first + 1; i <= intoNode; i++)
        {
            path.AddRange(PavementPoints(route.Segments[i].Edge).Skip(1));
        }

        return path;
    }

    /// <summary>An edge's pavement from its from-node to its to-node: a fillet arc sampled off its Bézier, a straight edge through its intermediate points.</summary>
    private static List<LatLon> PavementPoints(DirectionalEdge edge)
    {
        if (edge.Edge is GroundArc arc)
        {
            CubicBezier curve = arc.ToBezier();
            bool forward = arc.Nodes[0].Id == edge.FromNodeId;
            return
            [
                .. Enumerable
                    .Range(0, ArcSamples + 1)
                    .Select(i => curve.Evaluate(forward ? (double)i / ArcSamples : 1.0 - ((double)i / ArcSamples)))
                    .Select(p => new LatLon(p.Lat, p.Lon)),
            ];
        }

        if (edge.Edge is GroundEdge { IntermediatePoints.Count: > 0 } straight)
        {
            List<LatLon> points =
            [
                straight.Nodes[0].Position,
                .. straight.IntermediatePoints.Select(p => new LatLon(p.Lat, p.Lon)),
                straight.Nodes[1].Position,
            ];
            if (straight.Nodes[0].Id != edge.FromNodeId)
            {
                points.Reverse();
            }

            return points;
        }

        return [edge.FromNode.Position, edge.ToNode.Position];
    }

    /// <summary>The closest (ft) <paramref name="point"/> comes to the polyline <paramref name="path"/>.</summary>
    private static double DistanceToPathFt(List<LatLon> path, LatLon point)
    {
        double best = double.MaxValue;
        for (int i = 1; i < path.Count; i++)
        {
            best = Math.Min(best, GeoMath.DistanceToSegmentFt(point, path[i - 1], path[i]));
        }

        return best;
    }

    private static double HalfSpanFt(string aircraftType) =>
        (FaaAircraftDatabase.Get(aircraftType)?.WingspanFt ?? AircraftFootprint.ResolveWingspanFt(aircraftType)) / 2.0;
}
