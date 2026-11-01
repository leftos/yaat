using System.Text.Json;
using Xunit;
using Yaat.Sim.Commands;
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

    /// <summary>
    /// YAAT-1266: the same clearance as one deferred <c>GIVEWAY UAL926 TAXI GL L LF F 28L</c> issued at t=2082. UAL2237 is held short with
    /// no route to it yet and its heading is 60-120 degrees off UAL926's, where the geometry alone would release it at once.
    /// </summary>
    [Fact]
    public void DeferredGiveWayTaxi_CrossingTraffic_WaitsForTheTailPastTheSharedNode()
    {
        WithEngineInBand(
            (engine, layout) =>
            {
                AircraftState held = Assert.IsType<AircraftState>(engine.FindAircraft(HeldCallsign));
                AircraftState traffic = Assert.IsType<AircraftState>(engine.FindAircraft(TrafficCallsign));
                output.WriteLine(
                    $"t={FirstBandSecond} UAL2237 heading {held.TrueHeading.Degrees:F1}, UAL926 heading {traffic.TrueHeading.Degrees:F1}, "
                        + $"phase {held.Phases?.CurrentPhase?.GetType().Name}"
                );
                Assert.True(InCrossingBand(held, traffic), "test setup: the pair must be in the 60-120 degree band");

                DeferredDispatch deferral = Defer(engine, held, DeferredTaxi);
                TaxiRoute preview = Assert.IsType<TaxiRoute>(Preview(engine, held, deferral));
                GroundConflictDetector.GiveWayJunction? junction = GroundConflictDetector.GiveWayJunctionOf(held, traffic, preview);
                Assert.True(junction is { TrafficPast: false }, "test setup: the previewed route should meet UAL926 at a node it has still to clear");
                int nodeId = junction.Value.NodeId;
                double requiredTailPastFt = junction.Value.RequiredPastFt;
                double trafficHalfLengthFt = AircraftLength.ResolveFt(traffic.AircraftType) / 2.0;

                var trigger = new BlockTrigger { Type = BlockTriggerType.GiveWay, TargetCallsign = TrafficCallsign };
                int secondsGeometryAloneWouldRelease = 0;
                int? firedAt = null;
                for (int t = FirstBandSecond + 1; t <= 2400; t++)
                {
                    engine.TickOneSecond();
                    if (
                        (firedAt is null)
                        && HasDeferral(held)
                        && FlightPhysics.IsGiveWayMet(held, trigger, engine.FindAircraft)
                        && (Preview(engine, held, deferral) is { } route)
                        && (GroundConflictDetector.GiveWayRouteVerdictOf(held, traffic, route) is { Clear: false })
                    )
                    {
                        secondsGeometryAloneWouldRelease++;
                    }

                    double trafficTailPastFt = CentrePastFt(traffic, nodeId) - trafficHalfLengthFt;
                    if ((firedAt is null) && !HasDeferral(held))
                    {
                        firedAt = t;
                        output.WriteLine($"t={t} deferral fired with UAL926's tail {trafficTailPastFt:F0} ft past node {nodeId}");
                        Assert.True(
                            trafficTailPastFt >= requiredTailPastFt,
                            $"t={t}: the deferral fired with UAL926's tail {trafficTailPastFt:F0} ft past node {nodeId} "
                                + $"(needs {requiredTailPastFt:F0} ft)"
                        );
                    }

                    if (firedAt is not null)
                    {
                        double heldPastFt = CentrePastFt(held, nodeId);
                        Assert.True(
                            (heldPastFt < 0) || (heldPastFt < trafficTailPastFt),
                            $"t={t}: UAL2237 is {heldPastFt:F0} ft past node {nodeId}, ahead of UAL926's tail at {trafficTailPastFt:F0} ft"
                        );
                    }
                }

                Assert.True(firedAt is not null, $"the deferral never fired, so UAL2237 never taxied (UAL926 gs {traffic.GroundSpeed:F1})");
                Assert.True(
                    secondsGeometryAloneWouldRelease > 0,
                    "test setup: the geometry alone should have released the deferral while the shared node held it"
                );
            }
        );
    }

    /// <summary>
    /// A TAXI that resolves to no route leaves nothing to judge: the geometry still releases it, and the TAXI refuses when it fires.
    /// </summary>
    [Fact]
    public void DeferredGiveWay_PreviewFails_KeepsTheGeometry()
    {
        WithEngineInBand(
            (engine, layout) =>
            {
                AircraftState held = Assert.IsType<AircraftState>(engine.FindAircraft(HeldCallsign));
                AircraftState traffic = Assert.IsType<AircraftState>(engine.FindAircraft(TrafficCallsign));
                Assert.True(InCrossingBand(held, traffic), "test setup: the pair must be in the 60-120 degree band");

                DeferredDispatch deferral = Defer(engine, held, "GIVEWAY UAL926 TAXI GL #999999 28L");
                Assert.Null(Preview(engine, held, deferral));

                engine.TickOneSecond();

                Assert.False(HasDeferral(held), "the geometry releases a deferral whose route fails to build");
            }
        );
    }

    [Fact]
    public void DeferredGiveWay_NonTaxiPayload_KeepsTheGeometry()
    {
        WithEngineInBand(
            (engine, _) =>
            {
                AircraftState held = Assert.IsType<AircraftState>(engine.FindAircraft(HeldCallsign));
                AircraftState traffic = Assert.IsType<AircraftState>(engine.FindAircraft(TrafficCallsign));
                Assert.True(InCrossingBand(held, traffic), "test setup: the pair must be in the 60-120 degree band");

                Defer(engine, held, "GIVEWAY UAL926 RWY 28L");

                engine.TickOneSecond();

                Assert.False(HasDeferral(held), "a payload with no TAXI has no route to judge, so the geometry releases it");
            }
        );
    }

    /// <summary>The preview the deferred form judges changes nothing on the aircraft.</summary>
    [Fact]
    public void DeferredGiveWay_Preview_LeavesTheAircraftSnapshotUnchanged()
    {
        WithEngineInBand(
            (engine, layout) =>
            {
                AircraftState held = Assert.IsType<AircraftState>(engine.FindAircraft(HeldCallsign));
                DeferredDispatch deferral = Defer(engine, held, DeferredTaxi);
                string before = JsonSerializer.Serialize(held.ToSnapshot());

                Assert.IsType<TaxiRoute>(Preview(engine, held, deferral));

                Assert.Equal(before, JsonSerializer.Serialize(held.ToSnapshot()));
            }
        );
    }

    /// <summary>
    /// A GiveWay block queued behind a TAXI has no TAXI of its own to preview, so the route UAL2237 is driving decides: in the band the
    /// routes still meet at a node UAL926 has to clear, so the block waits. The TAXI holds short of F, so UAL2237 is still short of the
    /// node when UAL926 clears it and the block passes: a queued block stops nothing, and run on it would enter F first.
    /// </summary>
    [Fact]
    public void QueuedGiveWayBlock_DrivingARoute_WaitsForTheTail()
    {
        WithEngineInBand(
            (engine, _) =>
            {
                AircraftState held = Assert.IsType<AircraftState>(engine.FindAircraft(HeldCallsign));
                AircraftState traffic = Assert.IsType<AircraftState>(engine.FindAircraft(TrafficCallsign));
                CommandResult result = engine.SendCommand(HeldCallsign, "TAXI GL L LF F 28L HS F@LF; GIVEWAY UAL926 RWY 28L");
                Assert.True(result.Success, result.Message);
                CommandBlock block = Assert.Single(held.Queue.Blocks, b => b.Trigger?.Type == BlockTriggerType.GiveWay);

                int secondsWaited = 0;
                for (int t = FirstBandSecond + 1; t <= 2400; t++)
                {
                    bool mustWait = GroundConflictDetector.GiveWayJunctionOf(held, traffic) is { TrafficPast: false };
                    engine.TickOneSecond();
                    if (mustWait && (GroundConflictDetector.GiveWayJunctionOf(held, traffic) is { TrafficPast: false }))
                    {
                        secondsWaited++;
                        Assert.False(block.TriggerMet, $"t={t}: the queued block passed with UAL926 still short of clearing the shared node");
                    }
                }

                Assert.True(secondsWaited > 0, "test setup: the pair should spend time with UAL926 short of the node");
                Assert.True(block.TriggerMet, "the queued block never passed, though UAL926 cleared the shared node");
            }
        );
    }

    /// <summary>
    /// A route that turns onto UAL926's taxiway and runs back along it toward it: F then A west to node 1144, which UAL926 passes
    /// seconds after t=2082, with no side entry ahead of it. Past that node nothing decides by a junction, and in the band the geometry
    /// would release at once with UAL926 still coming the other way along the route. The hold lasts until it has left the route.
    /// </summary>
    [Fact]
    public void DeferredGiveWayTaxi_TrafficHeadOnAlongTheRoute_Holds()
    {
        WithEngineInBand(
            (engine, layout) =>
            {
                AircraftState held = Assert.IsType<AircraftState>(engine.FindAircraft(HeldCallsign));
                AircraftState traffic = Assert.IsType<AircraftState>(engine.FindAircraft(TrafficCallsign));
                DeferredDispatch deferral = Defer(engine, held, $"GIVEWAY UAL926 {HeadOnTaxi}");
                TaxiRoute route = Assert.IsType<TaxiRoute>(Preview(engine, held, deferral));
                Assert.True(HeldByTheHeadOnRule(held, traffic, route), "test setup: UAL926 should run head-on along the route");

                int secondsDecidedByHeadOn = 0;
                int? firedAt = null;
                for (int t = FirstBandSecond + 1; (t <= 2400) && (firedAt is null); t++)
                {
                    bool nothingElseHolds = LeftToTheHeadOnRule(held, traffic, route);
                    bool inBand = InCrossingBand(held, traffic);
                    engine.TickOneSecond();
                    bool headOn = HeldByTheHeadOnRule(held, traffic, route);
                    if (!HasDeferral(held))
                    {
                        firedAt = t;
                        Assert.False(headOn, $"t={t}: the deferral fired with UAL926 still running head-on along the route");
                    }
                    else if (nothingElseHolds && inBand)
                    {
                        secondsDecidedByHeadOn++;
                    }
                }

                output.WriteLine($"deferral fired at t={firedAt}, {secondsDecidedByHeadOn} s held in the band by the head-on rule alone");
                Assert.True(secondsDecidedByHeadOn > 0, "test setup: the head-on rule alone should hold it in the band");
                Assert.True(firedAt is not null, "the deferral never fired once UAL926 was past");
            }
        );
    }

    /// <summary>
    /// A cleared junction is final whatever the geometry says. <c>TAXI GL L A F 28L</c> meets UAL926 at a node on F; it is held there,
    /// stopped just past it, where UAL2237's heading puts it behind the abeam line of a same-direction pair, which the geometry holds.
    /// </summary>
    [Fact]
    public void DeferredGiveWay_JunctionCleared_FiresWhateverTheGeometry()
    {
        WithEngineInBand(
            (engine, layout) =>
            {
                AircraftState held = Assert.IsType<AircraftState>(engine.FindAircraft(HeldCallsign));
                AircraftState traffic = Assert.IsType<AircraftState>(engine.FindAircraft(TrafficCallsign));
                TaxiCommand taxi = TaxiOf("TAXI GL L A F 28L");
                bool Cleared() =>
                    (engine.PreviewDeferredTaxiRoute(held, taxi, isScenarioScripted: false) is { } route)
                    && (GroundConflictDetector.GiveWayRouteVerdictOf(held, traffic, route) is { Clear: true });

                for (int t = FirstBandSecond + 1; (t <= 2400) && !Cleared(); t++)
                {
                    engine.TickOneSecond();
                }

                Assert.True(Cleared(), "test setup: UAL926 should clear the shared node");
                CommandResult hold = engine.SendCommand(TrafficCallsign, "HOLD");
                Assert.True(hold.Success, hold.Message);
                for (int i = 0; (i < 60) && (traffic.GroundSpeed > 0); i++)
                {
                    engine.TickOneSecond();
                }

                var trigger = new BlockTrigger { Type = BlockTriggerType.GiveWay, TargetCallsign = TrafficCallsign };
                Assert.True(traffic.GroundSpeed <= 0, "test setup: UAL926 should have stopped");
                Assert.True(Cleared(), "test setup: UAL926 should still be past the shared node");
                Assert.False(FlightPhysics.IsGiveWayMet(held, trigger, engine.FindAircraft), "test setup: the geometry should say hold");

                Defer(engine, held, "GIVEWAY UAL926 TAXI GL L A F 28L");
                engine.TickOneSecond();

                Assert.False(HasDeferral(held), "the cleared junction should fire the deferral whatever the geometry says");
            }
        );
    }

    /// <summary>
    /// The TAXI need not lead its block: <c>RWY 28L, TAXI ...</c> is judged on the route, not released by the geometry in the band.
    /// </summary>
    [Fact]
    public void DeferredGiveWay_RwyFirstPayload_IsJudgedOnItsTaxiRoute()
    {
        WithEngineInBand(
            (engine, layout) =>
            {
                AircraftState held = Assert.IsType<AircraftState>(engine.FindAircraft(HeldCallsign));
                AircraftState traffic = Assert.IsType<AircraftState>(engine.FindAircraft(TrafficCallsign));
                Assert.True(InCrossingBand(held, traffic), "test setup: the pair must be in the 60-120 degree band");

                DeferredDispatch deferral = Defer(engine, held, "GIVEWAY UAL926 RWY 28L, TAXI GL L LF F 28L");
                Assert.Single(deferral.Payload.Blocks);
                TaxiRoute route = Assert.IsType<TaxiRoute>(Preview(engine, held, deferral));
                Assert.True(
                    GroundConflictDetector.GiveWayJunctionOf(held, traffic, route) is { TrafficPast: false },
                    "test setup: the route should meet UAL926 at a node it has still to clear"
                );

                engine.TickOneSecond();

                Assert.True(HasDeferral(held), "the TAXI behind the RWY should hold the deferral for the shared node");
            }
        );
    }

    /// <summary>The queued form of the head-on rule: the block waits on the route being driven while UAL926 comes the other way along it.</summary>
    [Fact]
    public void QueuedGiveWayBlock_HeadOnAlongItsRoute_Holds()
    {
        WithEngineInBand(
            (engine, _) =>
            {
                AircraftState held = Assert.IsType<AircraftState>(engine.FindAircraft(HeldCallsign));
                AircraftState traffic = Assert.IsType<AircraftState>(engine.FindAircraft(TrafficCallsign));
                CommandResult result = engine.SendCommand(HeldCallsign, $"{HeadOnTaxi}; GIVEWAY UAL926 RWY 28L");
                Assert.True(result.Success, result.Message);
                CommandBlock block = Assert.Single(held.Queue.Blocks, b => b.Trigger?.Type == BlockTriggerType.GiveWay);
                TaxiRoute route = RouteOf(held);

                int secondsDecidedByHeadOn = 0;
                for (int t = FirstBandSecond + 1; t <= FirstBandSecond + 60; t++)
                {
                    bool nothingElseHolds = LeftToTheHeadOnRule(held, traffic, route) && InCrossingBand(held, traffic);
                    engine.TickOneSecond();
                    if (nothingElseHolds && HeldByTheHeadOnRule(held, traffic, route))
                    {
                        secondsDecidedByHeadOn++;
                        Assert.False(block.TriggerMet, $"t={t}: the queued block passed with UAL926 running head-on along its route");
                    }
                }

                Assert.True(secondsDecidedByHeadOn > 0, "test setup: the head-on rule alone should hold the block in the band");
            }
        );
    }

    /// <summary>
    /// Held short of F at L, UAL2237 is told <c>GIVEWAY UAL926 TAXI F A #1144</c>: west along F into UAL926, coming east along F
    /// through node 278, the node UAL2237 starts the route on. The deferral waits until UAL926's tail has cleared that node, and
    /// UAL926 never has to brake for UAL2237.
    /// </summary>
    [Fact]
    public void DeferredGiveWayTaxi_HeldAtTheJunction_HeadOn_HoldsUntilTrafficTailPassesTheStartNode()
    {
        WithEngineInBand(
            (engine, _) =>
            {
                AircraftState held = Assert.IsType<AircraftState>(engine.FindAircraft(HeldCallsign));
                AircraftState traffic = Assert.IsType<AircraftState>(engine.FindAircraft(TrafficCallsign));
                int start = HoldShortOfFAtL(engine, held);
                Defer(engine, held, $"GIVEWAY UAL926 {HeadOnFromTheJunction}");

                AssertHeldUntilTheTailPassesTheStartNode(engine, held, traffic, start, () => HasDeferral(held));
            }
        );
    }

    /// <summary>The direct form of the same move: <c>TAXI F A #1144 GIVEWAY UAL926</c> issued held short of F at L.</summary>
    [Fact]
    public void DirectTaxiGiveWay_HeldAtTheJunction_HeadOn_HoldsUntilTrafficTailPasses()
    {
        WithEngineInBand(
            (engine, _) =>
            {
                AircraftState held = Assert.IsType<AircraftState>(engine.FindAircraft(HeldCallsign));
                AircraftState traffic = Assert.IsType<AircraftState>(engine.FindAircraft(TrafficCallsign));
                int start = HoldShortOfFAtL(engine, held);
                CommandResult result = engine.SendCommand(HeldCallsign, $"{HeadOnFromTheJunction} GIVEWAY UAL926");
                Assert.True(result.Success, result.Message);
                Assert.True(held.Ground.Hold?.IsGiveWayFor(TrafficCallsign) == true, "test setup: UAL2237 should give way to UAL926");

                AssertHeldUntilTheTailPassesTheStartNode(engine, held, traffic, start, () => held.Ground.Hold?.IsGiveWayFor(TrafficCallsign) == true);
            }
        );
    }

    /// <summary>
    /// The queued form of the same geometry: a GiveWay block behind a TAXI that holds UAL2237 short of F at L on a route that runs on
    /// west along F into UAL926. The block passes only once UAL926's tail has cleared node 278.
    /// </summary>
    [Fact]
    public void QueuedGiveWayBlock_HeldAtTheJunction_HeadOn_Holds()
    {
        WithEngineInBand(
            (engine, _) =>
            {
                AircraftState held = Assert.IsType<AircraftState>(engine.FindAircraft(HeldCallsign));
                AircraftState traffic = Assert.IsType<AircraftState>(engine.FindAircraft(TrafficCallsign));
                CommandResult result = engine.SendCommand(HeldCallsign, $"{MoveUpToTheJunction}; GIVEWAY UAL926 RWY 28L");
                Assert.True(result.Success, result.Message);
                CommandBlock block = Assert.Single(held.Queue.Blocks, b => b.Trigger?.Type == BlockTriggerType.GiveWay);
                double requiredTailPastFt = HalfSpanFt(held.AircraftType) + GroundOutlineSweep.WingtipBufferFt;
                double trafficHalfLengthFt = AircraftLength.ResolveFt(traffic.AircraftType) / 2.0;

                int? passedAt = null;
                for (int t = FirstBandSecond + 1; (t <= 2400) && (passedAt is null); t++)
                {
                    engine.TickOneSecond();
                    if (block.TriggerMet)
                    {
                        passedAt = t;
                        double tailPastFt = CentrePastFt(traffic, StartNodeId) - trafficHalfLengthFt;
                        output.WriteLine($"t={t} queued block passed with UAL926's tail {tailPastFt:F0} ft past node {StartNodeId}");
                        Assert.True(
                            tailPastFt >= requiredTailPastFt,
                            $"t={t}: the queued block passed with UAL926's tail {tailPastFt:F0} ft past node {StartNodeId} "
                                + $"(needs {requiredTailPastFt:F0} ft)"
                        );
                    }
                }

                Assert.True(passedAt is not null, "the queued block never passed, though UAL926 cleared node 278");
            }
        );
    }

    /// <summary>
    /// The queued form of <see cref="DeferredGiveWay_JunctionCleared_FiresWhateverTheGeometry"/>: UAL926 stopped just past the node
    /// where <c>TAXI GL L A F 28L</c> meets it, the geometry holding, a GiveWay block queued behind that TAXI passes on the cleared node.
    /// </summary>
    [Fact]
    public void QueuedGiveWayBlock_JunctionCleared_PassesWhateverTheGeometry()
    {
        WithEngineInBand(
            (engine, _) =>
            {
                AircraftState held = Assert.IsType<AircraftState>(engine.FindAircraft(HeldCallsign));
                AircraftState traffic = Assert.IsType<AircraftState>(engine.FindAircraft(TrafficCallsign));
                TaxiCommand taxi = TaxiOf("TAXI GL L A F 28L");
                bool Cleared() =>
                    (engine.PreviewDeferredTaxiRoute(held, taxi, isScenarioScripted: false) is { } route)
                    && (GroundConflictDetector.GiveWayRouteVerdictOf(held, traffic, route) is { Clear: true });

                for (int t = FirstBandSecond + 1; (t <= 2400) && !Cleared(); t++)
                {
                    engine.TickOneSecond();
                }

                Assert.True(Cleared(), "test setup: UAL926 should clear the shared node");
                CommandResult hold = engine.SendCommand(TrafficCallsign, "HOLD");
                Assert.True(hold.Success, hold.Message);
                for (int i = 0; (i < 60) && (traffic.GroundSpeed > 0); i++)
                {
                    engine.TickOneSecond();
                }

                var trigger = new BlockTrigger { Type = BlockTriggerType.GiveWay, TargetCallsign = TrafficCallsign };
                Assert.True(traffic.GroundSpeed <= 0, "test setup: UAL926 should have stopped");
                Assert.True(Cleared(), "test setup: UAL926 should still be past the shared node");
                Assert.False(FlightPhysics.IsGiveWayMet(held, trigger, engine.FindAircraft), "test setup: the geometry should say hold");

                CommandResult result = engine.SendCommand(HeldCallsign, "TAXI GL L A F 28L; GIVEWAY UAL926 RWY 28L");
                Assert.True(result.Success, result.Message);
                CommandBlock block = Assert.Single(held.Queue.Blocks, b => b.Trigger?.Type == BlockTriggerType.GiveWay);
                engine.TickOneSecond();

                Assert.False(FlightPhysics.IsGiveWayMet(held, trigger, engine.FindAircraft), "test setup: the geometry should still say hold");
                Assert.True(block.TriggerMet, "the cleared junction should pass the queued block whatever the geometry says");
            }
        );
    }

    /// <summary>
    /// The held route's nodes start at the node the aircraft stands on: a fresh route's first from-node. A route already begun has
    /// its current from-node behind the aircraft, and starts at that segment's to-node.
    /// </summary>
    [Fact]
    public void HeldRouteNodes_IncludeTheStartNode()
    {
        WithEngineInBand(
            (engine, layout) =>
            {
                AircraftState held = Assert.IsType<AircraftState>(engine.FindAircraft(HeldCallsign));
                AircraftState traffic = Assert.IsType<AircraftState>(engine.FindAircraft(TrafficCallsign));
                DeferredDispatch deferral = Defer(engine, held, $"GIVEWAY UAL926 {HeadOnTaxi}");
                TaxiRoute fresh = Assert.IsType<TaxiRoute>(Preview(engine, held, deferral));
                Dictionary<int, int> freshNodes = GroundConflictDetector.HeldRouteNodes(held, fresh);
                Assert.Contains(fresh.Segments[0].FromNodeId, freshNodes.Keys);
                Assert.All(fresh.Segments, s => Assert.Contains(s.ToNodeId, freshNodes.Keys));

                TaxiRoute driven = RouteOf(traffic);
                int current = driven.CurrentSegmentIndex;
                Assert.True(current > 0, "test setup: UAL926 should be past its route's first segment");
                Dictionary<int, int> drivenNodes = GroundConflictDetector.HeldRouteNodes(traffic, driven);
                Assert.DoesNotContain(driven.Segments[current].FromNodeId, drivenNodes.Keys);
                Assert.All(driven.Segments.Skip(current), s => Assert.Contains(s.ToNodeId, drivenNodes.Keys));
            }
        );
    }

    /// <summary>
    /// From the hold short of F at L, <see cref="HeadOnFromTheJunction"/> turns west onto F along a fillet arc that cuts the L/F
    /// intersection, node <see cref="StartNodeId"/>, which the route itself never runs to. The cut corner is a node of the held route.
    /// </summary>
    [Fact]
    public void HeldRouteNodes_IncludeTheCornerAnArcCuts()
    {
        WithEngineInBand(
            (engine, _) =>
            {
                AircraftState held = Assert.IsType<AircraftState>(engine.FindAircraft(HeldCallsign));
                HoldShortOfFAtL(engine, held);
                TaxiCommand taxi = TaxiOf(HeadOnFromTheJunction);
                TaxiRoute route = Assert.IsType<TaxiRoute>(engine.PreviewDeferredTaxiRoute(held, taxi, isScenarioScripted: false));
                Assert.DoesNotContain(route.Segments, s => (s.FromNodeId == StartNodeId) || (s.ToNodeId == StartNodeId));
                TaxiRouteSegment arc = Assert.Single(route.Segments, s => (s.Edge.Edge is GroundArc a) && (a.Corner()?.Node.Id == StartNodeId));

                Dictionary<int, int> nodes = GroundConflictDetector.HeldRouteNodes(held, route);

                Assert.True(nodes.TryGetValue(StartNodeId, out int enteredFrom), $"node {StartNodeId}, the corner the arc cuts, is missing");
                Assert.Equal(arc.FromNodeId, enteredFrom);
            }
        );
    }

    /// <summary>
    /// UAL2237 starts a route at node 279, where LF meets F, and runs back down LF: UAL926, coming east along F, crosses that node
    /// from the side. Once UAL2237's tail is past the node, traffic crossing it behind UAL2237 holds it no longer.
    ///
    /// <para>
    /// The route's first edge, 279→356, is 69 ft, so both rows are past it: they pin the release rule rather than reproduce the base
    /// fault (<see cref="GiveWay_TrafficCrossingTheStartNodeBehindALongFirstEdge_NeverThrows"/> does that).
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(200.0)]
    [InlineData(600.0)]
    public void GiveWay_TrafficCrossingBehindPastTheStartNode_Releases(double pastFt)
    {
        WithEngineInBand(
            (engine, _) =>
            {
                (AircraftState held, AircraftState traffic, TaxiRoute backDownLf) = CrossingBehindSetup(engine);
                PlaceAlong(held, backDownLf, pastFt);

                GroundConflictDetector.GiveWayRouteVerdict? verdict = GroundConflictDetector.GiveWayRouteVerdictOf(held, traffic, backDownLf);

                output.WriteLine($"{pastFt:F0} ft past node {MergeNodeId}: segment {backDownLf.CurrentSegmentIndex}, verdict {verdict}");
                Assert.False(verdict is { Clear: false }, $"{pastFt:F0} ft past node {MergeNodeId} UAL2237 still holds: {verdict}");
            }
        );
    }

    /// <summary>
    /// The non-example of <see cref="GiveWay_TrafficCrossingBehindPastTheStartNode_Releases"/>: standing on node 279, UAL2237
    /// holds.
    /// </summary>
    [Fact]
    public void GiveWay_TrafficCrossingTheStartNodeItStandsOn_Holds()
    {
        WithEngineInBand(
            (engine, _) =>
            {
                (AircraftState held, AircraftState traffic, TaxiRoute backDownLf) = CrossingBehindSetup(engine);
                PlaceAlong(held, backDownLf, 0);

                GroundConflictDetector.GiveWayRouteVerdict? verdict = GroundConflictDetector.GiveWayRouteVerdictOf(held, traffic, backDownLf);

                Assert.True(verdict is { Clear: false }, $"standing on node {MergeNodeId} UAL2237 should hold: {verdict}");
                Assert.Equal(MergeNodeId, verdict.Value.Junction.NodeId);
            }
        );
    }

    /// <summary>
    /// UAL2237 starts a route at node 279 and runs on east along F, UAL926 behind it on F coming through that node: either continuing
    /// along behind it, or turning off down LF there. While UAL2237 still occupies node 279 it holds; once its tail is past, it does not.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GiveWay_TrafficInTrailBehind_ReleasesOncePastItsStart(bool trafficTurnsOff)
    {
        WithEngineInBand(
            (engine, _) =>
            {
                AircraftState held = Assert.IsType<AircraftState>(engine.FindAircraft(HeldCallsign));
                AircraftState traffic = Assert.IsType<AircraftState>(engine.FindAircraft(TrafficCallsign));
                (TaxiRoute merge, int intoMerge) = MergeRoute(engine, held);
                TaxiRoute alongF = NewRoute([.. merge.Segments.Skip(intoMerge + 1)]);
                TaxiRoute trafficRoute = RouteOf(traffic);
                int trafficIntoMerge = trafficRoute.Segments.FindIndex(s => s.ToNodeId == MergeNodeId);
                Assert.True(trafficIntoMerge >= trafficRoute.CurrentSegmentIndex, "test setup: UAL926 should still be short of node 279");
                if (trafficTurnsOff)
                {
                    TaxiRoute turnsOff = NewRoute([
                        .. trafficRoute.Segments.Take(trafficIntoMerge + 1),
                        Reversed(merge.Segments[intoMerge]),
                        Reversed(merge.Segments[intoMerge - 1]),
                    ]);
                    turnsOff.CurrentSegmentIndex = trafficRoute.CurrentSegmentIndex;
                    traffic.Ground.AssignedTaxiRoute = turnsOff;
                    Assert.Same(turnsOff, FollowingPhase.DrivenRouteOf(traffic));
                }
                else
                {
                    Assert.Equal(alongF.Segments[0].ToNodeId, trafficRoute.Segments[trafficIntoMerge + 1].ToNodeId);
                }

                double halfLengthFt = AircraftLength.ResolveFt(held.AircraftType) / 2.0;
                foreach (double pastFt in new[] { 0.0, halfLengthFt - 10 })
                {
                    PlaceAlong(held, alongF, pastFt);
                    GroundConflictDetector.GiveWayRouteVerdict? occupying = GroundConflictDetector.GiveWayRouteVerdictOf(held, traffic, alongF);
                    output.WriteLine($"centre {pastFt:F0} ft past node {MergeNodeId}: verdict {occupying}");
                    Assert.True(occupying is { Clear: false }, $"centre {pastFt:F0} ft past node {MergeNodeId}, still on it, UAL2237 should hold");
                    Assert.Equal(MergeNodeId, occupying.Value.Junction.NodeId);
                }

                PlaceAlong(held, alongF, halfLengthFt + 10);
                GroundConflictDetector.GiveWayRouteVerdict? past = GroundConflictDetector.GiveWayRouteVerdictOf(held, traffic, alongF);
                output.WriteLine($"tail 10 ft past node {MergeNodeId}: verdict {past}");
                Assert.False(past is { Clear: false }, $"with its tail past node {MergeNodeId} UAL2237 still holds: {past}");
            }
        );
    }

    /// <summary>
    /// From the hold short of F at L, <see cref="HeadOnFromTheJunction"/> turns west onto F along a fillet arc that cuts node
    /// <see cref="StartNodeId"/>. UAL926, put on its own route's edges through that node the other way, westbound along F from east of
    /// it, crosses the corner. Wherever UAL2237 stands along its route, the give-way stop never throws, and short of the arc it stops
    /// for that corner no farther on than the arc's end.
    ///
    /// <para>
    /// This pins the behaviour rather than reproducing a fault: no staging on the base throws for a cut corner (340 SFO cut-corner
    /// cases swept).
    /// </para>
    /// </summary>
    [Fact]
    public void GiveWayStop_CrossingOnACutCorner_StopsAtTheArcEnd()
    {
        WithEngineInBand(
            (engine, _) =>
            {
                AircraftState held = Assert.IsType<AircraftState>(engine.FindAircraft(HeldCallsign));
                AircraftState traffic = Assert.IsType<AircraftState>(engine.FindAircraft(TrafficCallsign));
                HoldShortOfFAtL(engine, held);
                TaxiRoute route = Assert.IsType<TaxiRoute>(
                    engine.PreviewDeferredTaxiRoute(held, TaxiOf(HeadOnFromTheJunction), isScenarioScripted: false)
                );
                held.Ground.AssignedTaxiRoute = route;
                Assert.Same(route, FollowingPhase.DrivenRouteOf(held));
                int arcIndex = route.Segments.FindIndex(s => (s.Edge.Edge is GroundArc a) && (a.Corner()?.Node.Id == StartNodeId));
                double throughArcFt = route.Segments.Take(arcIndex + 1).Sum(s => ChordFt(s.Edge));
                TaxiRoute eastbound = RouteOf(traffic);
                int intoCorner = eastbound.Segments.FindIndex(s => s.ToNodeId == StartNodeId);
                Assert.True(intoCorner > 0, $"test setup: UAL926's route should pass node {StartNodeId}");
                TaxiRoute westbound = NewRoute([.. Enumerable.Range(intoCorner - 1, 4).Reverse().Select(k => Reversed(eastbound.Segments[k]))]);
                traffic.Ground.AssignedTaxiRoute = westbound;
                Assert.Same(westbound, FollowingPhase.DrivenRouteOf(traffic));
                PlaceAlong(traffic, westbound, 0);
                output.WriteLine($"UAL926 westbound {string.Join(" ", westbound.Segments.Select(s => $"{s.FromNodeId}>{s.ToNodeId}"))}");

                PlaceAlong(held, route, 0);
                (int NodeId, double ToStopFt)? atStart = GroundConflictDetector.GiveWayStop(held, traffic, out string? why);
                output.WriteLine($"at the route's start: stop {atStart} ({why}), junction {GroundConflictDetector.GiveWayJunctionOf(held, traffic)}");
                Assert.Equal(StartNodeId, atStart?.NodeId);
                Assert.True(
                    atStart?.ToStopFt <= throughArcFt,
                    $"from the route's start the stop is {atStart?.ToStopFt:F0} ft on, past the arc's end {throughArcFt:F0} ft on"
                );

                for (double pastFt = 0; pastFt <= throughArcFt + 300; pastFt += 10)
                {
                    PlaceAlong(held, route, pastFt);
                    (int NodeId, double ToStopFt)? stop = null;
                    Exception? thrown = Record.Exception(() => stop = GroundConflictDetector.GiveWayStop(held, traffic, out string? _));
                    output.WriteLine($"{pastFt:F0} ft along, segment {route.CurrentSegmentIndex}: stop {stop}");
                    Assert.True(thrown is null, $"{pastFt:F0} ft along the route: {thrown?.Message}");
                    Assert.True(
                        (stop is not { NodeId: StartNodeId } corner) || (corner.ToStopFt <= (throughArcFt - pastFt)),
                        $"{pastFt:F0} ft along the route the stop for node {StartNodeId} is {stop?.ToStopFt:F0} ft on, past the arc's end"
                    );
                }
            }
        );
    }

    /// <summary>
    /// UAL2237 on <see cref="CrossingBehindSetup"/>'s route back down LF from node 279, which UAL926, coming east along F, crosses
    /// from the side, asks for its give-way stop while it still occupies that start node: its centre on it, or half its length
    /// less 10 ft past it. The stop is node 279, where it stands: its centre is already inside the room UAL926's track needs.
    /// Standing 600 ft off F abeam the node instead, on the side away from LF and so short of the node along its route, outside
    /// that room, it still stops for the route's start node, its walk running straight to the node (the start-node branch of the
    /// give-way walk) and coming inside the room no farther on than the node. None of the three throws.
    /// </summary>
    [Fact]
    public void GiveWayStop_CrossingOnTheStartNode_StopsWithoutThrowing()
    {
        WithEngineInBand(
            (engine, _) =>
            {
                (AircraftState held, AircraftState traffic, TaxiRoute backDownLf) = CrossingBehindSetup(engine);
                held.Ground.AssignedTaxiRoute = backDownLf;
                Assert.Same(backDownLf, FollowingPhase.DrivenRouteOf(held));

                (int NodeId, double ToStopFt)? StopFrom(string where)
                {
                    (int NodeId, double ToStopFt)? stop = null;
                    Exception? thrown = Record.Exception(() => stop = GroundConflictDetector.GiveWayStop(held, traffic, out string? _));
                    output.WriteLine($"{where}: stop {stop}");
                    Assert.True(thrown is null, $"{where}: {thrown?.Message}");
                    return stop;
                }

                double halfLengthFt = AircraftLength.ResolveFt(held.AircraftType) / 2.0;
                foreach (double pastFt in new[] { 0.0, halfLengthFt - 10 })
                {
                    PlaceAlong(held, backDownLf, pastFt);
                    Assert.True(backDownLf.CurrentSegmentIndex == 0, $"test setup: {pastFt:F0} ft on should still be on the route's first edge");
                    string where = $"centre {pastFt:F0} ft past node {MergeNodeId}";
                    (int NodeId, double ToStopFt)? onIt = StopFrom(where);
                    Assert.True(onIt is { NodeId: MergeNodeId, ToStopFt: 0.0 }, $"{where}: UAL2237 should stop where it stands, stop {onIt}");
                }

                const double OffTheTrackFt = 600;
                LatLon node = backDownLf.Segments[0].Edge.FromNode.Position;
                double downLfDeg = GeoMath.BearingTo(node, backDownLf.Segments[0].Edge.ToNode.Position);
                DirectionalEdge alongF = RouteOf(traffic).Segments.First(s => s.ToNodeId == MergeNodeId).Edge;
                double sideDeg = (GeoMath.BearingTo(alongF.FromNode.Position, alongF.ToNode.Position) + 90) % 360;
                if (Math.Cos((sideDeg - downLfDeg) * Math.PI / 180) > 0)
                {
                    sideDeg = (sideDeg + 180) % 360;
                }

                held.Position = GeoMath.ProjectPoint(node, new TrueHeading(sideDeg), OffTheTrackFt / GeoMath.FeetPerNm);
                held.TrueHeading = new TrueHeading(GeoMath.BearingTo(held.Position, node));
                backDownLf.CurrentSegmentIndex = 0;
                string offF = $"centre {OffTheTrackFt:F0} ft off F abeam node {MergeNodeId}, away from LF";
                (int NodeId, double ToStopFt)? offIt = StopFrom(offF);
                Assert.True(
                    (offIt is { NodeId: MergeNodeId } stop) && (stop.ToStopFt > 0) && (stop.ToStopFt <= OffTheTrackFt),
                    $"{offF}: UAL2237 should stop on its way to the node, stop {offIt}"
                );
            }
        );
    }

    /// <summary>
    /// UAL2237, put on node 280 where N leaves F and cleared <c>TAXI N</c>, is 450 ft down N's 617 ft first edge, its tail well
    /// past node 280, when told to give way to UAL926, still about 7,000 ft back along F short of node 280. Traffic crossing that
    /// node behind it never stops the tick, and UAL2237 leaves the give-way and moves on within a few seconds.
    /// </summary>
    [Fact]
    public void GiveWay_TrafficCrossingTheStartNodeBehindALongFirstEdge_NeverThrows()
    {
        WithEngineInBand(
            (engine, layout) =>
            {
                AircraftState held = Assert.IsType<AircraftState>(engine.FindAircraft(HeldCallsign));
                AircraftState traffic = Assert.IsType<AircraftState>(engine.FindAircraft(TrafficCallsign));
                GroundNode start = layout.Nodes[NFromFNodeId];
                held.Position = start.Position;
                held.TrueHeading = new TrueHeading(GeoMath.BearingTo(start.Position, layout.Nodes[NFirstEdgeEndNodeId].Position));
                CommandResult taxi = engine.SendCommand(HeldCallsign, "TAXI N");
                Assert.True(taxi.Success, taxi.Message);
                TaxiRoute route = RouteOf(held);
                Assert.Equal((NFromFNodeId, NFirstEdgeEndNodeId), (route.Segments[0].FromNodeId, route.Segments[0].ToNodeId));
                TaxiRoute trafficRoute = RouteOf(traffic);
                Assert.True(
                    trafficRoute.Segments.FindIndex(s => s.ToNodeId == NFromFNodeId) >= trafficRoute.CurrentSegmentIndex,
                    $"test setup: UAL926 should still be short of node {NFromFNodeId}"
                );

                for (int t = 0; GeoMath.DistanceNm(start.Position, held.Position) * GeoMath.FeetPerNm < 450; t++)
                {
                    Assert.True(t < 60, "test setup: UAL2237 never got 450 ft down N");
                    engine.TickOneSecond();
                }

                Assert.Equal(0, route.CurrentSegmentIndex);
                CommandResult giveWay = engine.SendCommand(HeldCallsign, "GIVEWAY UAL926");
                Assert.True(giveWay.Success, giveWay.Message);
                Assert.True(held.Ground.Hold?.IsGiveWayFor(TrafficCallsign) == true, "test setup: UAL2237 should give way to UAL926");

                const int ReleaseWithinSeconds = 5;
                int? releasedAt = null;
                double speedAtReleaseKt = 0;
                for (int t = 1; t <= 30; t++)
                {
                    Exception? thrown = Record.Exception(engine.TickOneSecond);
                    output.WriteLine(
                        $"t+{t}: hold {held.Ground.Hold?.Kind.ToString() ?? "-"}, gs {held.GroundSpeed:F1}, segment {route.CurrentSegmentIndex}"
                    );
                    Assert.True(thrown is null, $"t+{t}: {thrown?.Message}");
                    if ((releasedAt is null) && (held.Ground.Hold?.IsGiveWayFor(TrafficCallsign) != true))
                    {
                        releasedAt = t;
                        speedAtReleaseKt = held.GroundSpeed;
                    }
                }

                Assert.True(releasedAt is <= ReleaseWithinSeconds, $"UAL2237 left the give-way at t+{releasedAt?.ToString() ?? "never"}");
                Assert.True(speedAtReleaseKt > 0, $"UAL2237 should be moving on down N when it leaves the give-way, gs {speedAtReleaseKt:F1}");
            }
        );
    }

    /// <summary>
    /// UAL2237, put on node 280 where N leaves F with <c>TAXI N</c> previewed from there, stands midway along one of that route's
    /// segments, no longer occupying the segment's from-node: the first edge, 280 to 715, its tail past node 280, or the edge after
    /// it. UAL926 is put on a route that comes back along N the other way, down that segment toward UAL2237, and on east along F.
    /// The head-on is decided at the segment's from-node: UAL2237 holds for UAL926 there until UAL926's tail has cleared that node
    /// by UAL2237's half-span plus the wingtip buffer, and is released once it has.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void GiveWay_HeadOnTrafficComingDownTheHeldAircraftsEdge_HoldsUntilItsTailClearsTheEdgesFromNode(int heldSegment)
    {
        WithEngineInBand(
            (engine, layout) =>
            {
                AircraftState held = Assert.IsType<AircraftState>(engine.FindAircraft(HeldCallsign));
                AircraftState traffic = Assert.IsType<AircraftState>(engine.FindAircraft(TrafficCallsign));
                GroundNode start = layout.Nodes[NFromFNodeId];
                held.Position = start.Position;
                held.TrueHeading = new TrueHeading(GeoMath.BearingTo(start.Position, layout.Nodes[NFirstEdgeEndNodeId].Position));
                TaxiRoute route = Assert.IsType<TaxiRoute>(engine.PreviewDeferredTaxiRoute(held, TaxiOf("TAXI N"), isScenarioScripted: false));
                Assert.Equal((NFromFNodeId, NFirstEdgeEndNodeId), (route.Segments[0].FromNodeId, route.Segments[0].ToNodeId));
                Assert.True(route.Segments.Count > heldSegment + 1, $"test setup: TAXI N should run on past its segment {heldSegment}");
                TaxiRouteSegment edge = route.Segments[heldSegment];
                PlaceAlong(held, route, route.Segments.Take(heldSegment).Sum(s => ChordFt(s.Edge)) + (ChordFt(edge.Edge) / 2));
                Assert.Equal(heldSegment, route.CurrentSegmentIndex);
                Assert.DoesNotContain(edge.FromNodeId, GroundConflictDetector.HeldRouteNodes(held, route).Keys);

                List<TaxiRouteSegment> alongF = [.. RouteOf(traffic).Segments.SkipWhile(s => s.FromNodeId != NFromFNodeId).Take(2)];
                Assert.True(alongF.Count > 0, $"test setup: UAL926's route should run on along F from node {NFromFNodeId}");
                TaxiRoute downTheEdge = NewRoute([.. route.Segments.Take(heldSegment + 2).Reverse().Select(Reversed), .. alongF]);
                traffic.Ground.AssignedTaxiRoute = downTheEdge;
                Assert.Same(downTheEdge, FollowingPhase.DrivenRouteOf(traffic));
                string trafficNodes = string.Join(" ", downTheEdge.Segments.Select(s => $"{s.FromNodeId}>{s.ToNodeId}"));
                output.WriteLine($"UAL2237 on {edge.FromNodeId}>{edge.ToNodeId}; UAL926 {trafficNodes}");

                double toFromNodeFt = downTheEdge.Segments.Take(2).Sum(s => ChordFt(s.Edge));
                double trafficHalfLengthFt = AircraftLength.ResolveFt(traffic.AircraftType) / 2.0;
                double requiredTailPastFt = HalfSpanFt(held.AircraftType) + GroundOutlineSweep.WingtipBufferFt;
                double clearsAtFt = toFromNodeFt + trafficHalfLengthFt + requiredTailPastFt;
                for (double alongFt = 0; alongFt <= clearsAtFt - 10; alongFt += 10)
                {
                    PlaceAlong(traffic, downTheEdge, alongFt);
                    GroundConflictDetector.GiveWayRouteVerdict? verdict = GroundConflictDetector.GiveWayRouteVerdictOf(held, traffic, route);
                    double tailPastFt = alongFt - toFromNodeFt - trafficHalfLengthFt;
                    Assert.True(
                        (verdict is { Clear: false } holding) && (holding.Junction.NodeId == edge.FromNodeId),
                        $"UAL926's tail {tailPastFt:F0} ft past node {edge.FromNodeId}: UAL2237 should hold for it there, verdict {verdict}"
                    );
                }

                PlaceAlong(traffic, downTheEdge, clearsAtFt + 10);
                GroundConflictDetector.GiveWayRouteVerdict? cleared = GroundConflictDetector.GiveWayRouteVerdictOf(held, traffic, route);
                string where = $"UAL926's tail {requiredTailPastFt + 10:F0} ft past node {edge.FromNodeId}";
                output.WriteLine($"{where}: verdict {cleared}");
                Assert.True(cleared is { Clear: true }, $"{where}: UAL2237 should be released, verdict {cleared}");
            }
        );
    }

    /// <summary>
    /// Head-on along F, each told to give way to the other, with no crossing from the higher callsign's side: UAL2237, put on node 283
    /// on F east of UAL926 and cleared <c>TAXI F A #1144</c> back along UAL926's route, and UAL926 eastbound. One of them goes within
    /// 120 s; the pair never deadlocks.
    /// </summary>
    [Fact]
    public void GiveWay_HeadOnMutualGiveWay_NoCrossingFromTheHigherCallsign_OneGoes()
    {
        WithEngineInBand(
            (engine, layout) =>
            {
                AircraftState held = Assert.IsType<AircraftState>(engine.FindAircraft(HeldCallsign));
                AircraftState traffic = Assert.IsType<AircraftState>(engine.FindAircraft(TrafficCallsign));
                GroundNode start = layout.Nodes[HeadOnStartNodeId];
                held.Position = start.Position;
                held.TrueHeading = new TrueHeading(GeoMath.BearingTo(start.Position, layout.Nodes[HeadOnStartNodeId - 1].Position));
                CommandResult taxi = engine.SendCommand(HeldCallsign, "TAXI F A #1144");
                Assert.True(taxi.Success, taxi.Message);
                Assert.Equal(HeadOnStartNodeId, RouteOf(held).Segments[0].FromNodeId);
                CommandResult heldGiveWay = engine.SendCommand(HeldCallsign, "GIVEWAY UAL926");
                Assert.True(heldGiveWay.Success, heldGiveWay.Message);
                CommandResult trafficGiveWay = engine.SendCommand(TrafficCallsign, "GIVEWAY UAL2237");
                Assert.True(trafficGiveWay.Success, trafficGiveWay.Message);
                Assert.True(
                    GroundConflictDetector.GiveWayJunctionOf(traffic, held) is null,
                    "test setup: UAL926's route should meet UAL2237's across it nowhere"
                );

                int? firstReleaseAt = null;
                for (int t = 1; (t <= 120) && (firstReleaseAt is null); t++)
                {
                    engine.TickOneSecond();
                    if ((held.Ground.Hold is null) || (traffic.Ground.Hold is null))
                    {
                        firstReleaseAt = t;
                    }
                }

                output.WriteLine(
                    $"first release at t+{firstReleaseAt?.ToString() ?? "none"}: UAL2237 hold {held.Ground.Hold?.Kind.ToString() ?? "-"}, "
                        + $"UAL926 hold {traffic.Ground.Hold?.Kind.ToString() ?? "-"}"
                );
                Assert.True(firstReleaseAt is not null, "both still give way to each other after 120 s");
                Assert.True(held.Ground.Hold is null, "UAL2237, sorting first, should go");
                Assert.True(traffic.Ground.Hold?.IsGiveWayFor(HeldCallsign) == true, "UAL926 should keep holding for UAL2237");
            }
        );
    }

    /// <summary>The node where N leaves F, which UAL926 passes eastbound along F.</summary>
    private const int NFromFNodeId = 280;

    /// <summary>The far end of N's first edge from <see cref="NFromFNodeId"/>, 617 ft along it.</summary>
    private const int NFirstEdgeEndNodeId = 715;

    /// <summary>A node on F east of UAL926, its route running on west to node 282.</summary>
    private const int HeadOnStartNodeId = 283;

    /// <summary>The node where UAL2237's <c>TAXI GL L LF F 28L</c> turns off LF onto F, which UAL926 passes eastbound along F.</summary>
    private const int MergeNodeId = 279;

    /// <summary>UAL2237's previewed <c>TAXI GL L LF F 28L</c>, and the index of its segment into <see cref="MergeNodeId"/>.</summary>
    private static (TaxiRoute Route, int IntoMerge) MergeRoute(SimulationEngine engine, AircraftState held)
    {
        TaxiRoute route = Assert.IsType<TaxiRoute>(engine.PreviewDeferredTaxiRoute(held, TaxiOf("TAXI GL L LF F 28L"), isScenarioScripted: false));
        int intoMerge = route.Segments.FindIndex(s => s.ToNodeId == MergeNodeId);
        Assert.True(intoMerge > 0, $"test setup: the route should run into node {MergeNodeId} along LF");
        return (route, intoMerge);
    }

    /// <summary>
    /// UAL2237, UAL926 and a route from <see cref="MergeNodeId"/> back down the way <see cref="MergeRoute"/> comes into it, at least
    /// 900 ft of it.
    /// </summary>
    private static (AircraftState Held, AircraftState Traffic, TaxiRoute BackDownLf) CrossingBehindSetup(SimulationEngine engine)
    {
        AircraftState held = Assert.IsType<AircraftState>(engine.FindAircraft(HeldCallsign));
        AircraftState traffic = Assert.IsType<AircraftState>(engine.FindAircraft(TrafficCallsign));
        (TaxiRoute merge, int intoMerge) = MergeRoute(engine, held);
        List<TaxiRouteSegment> back = [];
        for (int k = intoMerge; (k >= 0) && (back.Sum(s => ChordFt(s.Edge)) < 900); k--)
        {
            back.Add(Reversed(merge.Segments[k]));
        }

        TaxiRoute trafficRoute = RouteOf(traffic);
        Assert.True(
            trafficRoute.Segments.FindIndex(s => s.ToNodeId == MergeNodeId) >= trafficRoute.CurrentSegmentIndex,
            "test setup: UAL926 should still be short of node 279"
        );
        return (held, traffic, NewRoute(back));
    }

    private static TaxiRoute NewRoute(List<TaxiRouteSegment> segments) =>
        new()
        {
            Segments = segments,
            HoldShortPoints = [],
            CurrentSegmentIndex = 0,
        };

    private static TaxiRouteSegment Reversed(TaxiRouteSegment segment) =>
        new() { TaxiwayName = segment.TaxiwayName, Edge = segment.Edge.Edge.Directed(segment.Edge.ToNode, segment.Edge.FromNode) };

    private static double ChordFt(DirectionalEdge edge) => GeoMath.DistanceNm(edge.FromNode.Position, edge.ToNode.Position) * GeoMath.FeetPerNm;

    /// <summary>
    /// Puts <paramref name="aircraft"/> <paramref name="pastFt"/> along <paramref name="route"/>'s chords from its start node, facing
    /// along the chord it stands on, with the route's cursor on that segment.
    /// </summary>
    private static void PlaceAlong(AircraftState aircraft, TaxiRoute route, double pastFt)
    {
        double leftFt = pastFt;
        for (int i = 0; i < route.Segments.Count; i++)
        {
            DirectionalEdge edge = route.Segments[i].Edge;
            if ((leftFt <= ChordFt(edge)) || (i == route.Segments.Count - 1))
            {
                var heading = new TrueHeading(GeoMath.BearingTo(edge.FromNode.Position, edge.ToNode.Position));
                route.CurrentSegmentIndex = i;
                aircraft.Position = GeoMath.ProjectPoint(edge.FromNode.Position, heading, leftFt / GeoMath.FeetPerNm);
                aircraft.TrueHeading = heading;
                return;
            }

            leftFt -= ChordFt(edge);
        }
    }

    /// <summary>
    /// Ticks UAL2237 forward to F on <see cref="MoveUpToTheJunction"/> until it stands held short of F at L, short of
    /// <see cref="StartNodeId"/>; returns the second it stopped.
    /// </summary>
    private int HoldShortOfFAtL(SimulationEngine engine, AircraftState held)
    {
        CommandResult result = engine.SendCommand(HeldCallsign, MoveUpToTheJunction);
        Assert.True(result.Success, result.Message);
        bool moved = false;
        for (int t = FirstBandSecond + 1; t <= 2300; t++)
        {
            engine.TickOneSecond();
            moved |= held.GroundSpeed > 1;
            if (moved && (held.GroundSpeed <= 0))
            {
                double pastFt = CentrePastFt(held, StartNodeId);
                output.WriteLine($"t={t} UAL2237 stopped {-pastFt:F0} ft short of node {StartNodeId}, heading {held.TrueHeading.Degrees:F0}");
                Assert.True(
                    pastFt is < 0 and > -500,
                    $"test setup: UAL2237 stopped {pastFt:F0} ft past node {StartNodeId}, not held short of F at L"
                );
                return t;
            }
        }

        Assert.Fail("test setup: UAL2237 never stopped short of F at L");
        return 0;
    }

    /// <summary>
    /// Ticks from <paramref name="start"/> while <paramref name="waiting"/> says UAL2237 still gives way, and asserts it stops waiting
    /// only once UAL926's tail is past <see cref="StartNodeId"/> by YAAT-1263's crossing margin (UAL2237's half-span plus the wingtip
    /// buffer), and that UAL926, once rolling at 10 kt, never slows below it before then.
    /// </summary>
    private void AssertHeldUntilTheTailPassesTheStartNode(
        SimulationEngine engine,
        AircraftState held,
        AircraftState traffic,
        int start,
        Func<bool> waiting
    )
    {
        double requiredTailPastFt = HalfSpanFt(held.AircraftType) + GroundOutlineSweep.WingtipBufferFt;
        double trafficHalfLengthFt = AircraftLength.ResolveFt(traffic.AircraftType) / 2.0;
        const double RollingKt = 10;
        bool rolling = false;
        int? releasedAt = null;
        for (int t = start + 1; t <= 2400; t++)
        {
            engine.TickOneSecond();
            double tailPastFt = CentrePastFt(traffic, StartNodeId) - trafficHalfLengthFt;
            if ((releasedAt is null) && !waiting())
            {
                releasedAt = t;
                output.WriteLine($"t={t} released with UAL926's tail {tailPastFt:F0} ft past node {StartNodeId}");
                Assert.True(
                    tailPastFt >= requiredTailPastFt,
                    $"t={t}: released with UAL926's tail {tailPastFt:F0} ft past node {StartNodeId} (needs {requiredTailPastFt:F0} ft)"
                );
            }

            rolling |= traffic.GroundSpeed >= RollingKt;
            Assert.True(
                !rolling || (tailPastFt >= requiredTailPastFt) || (traffic.GroundSpeed >= RollingKt),
                $"t={t}: UAL926 slowed to {traffic.GroundSpeed:F1} kt with its tail {tailPastFt:F0} ft past node {StartNodeId}"
            );
        }

        Assert.True(releasedAt is not null, "UAL2237 was never released, though UAL926 cleared node 278");
    }

    private const int BeforeTaxiClearance = 2080;

    /// <summary>
    /// The first second after <see cref="BeforeTaxiClearance"/> the pair is 60-120 degrees apart: UAL926 turning, UAL2237 held short at 118.
    /// </summary>
    private const int FirstBandSecond = 2082;
    private const string DeferredTaxi = "GIVEWAY UAL926 TAXI GL L LF F 28L";

    /// <summary>A TAXI onto F that runs back along UAL926's route past where it stands at <see cref="FirstBandSecond"/>.</summary>
    private const string HeadOnTaxi = "TAXI GL L F A #1144";

    /// <summary>The same route, held short of F at L, where it turns west onto F at <see cref="StartNodeId"/>.</summary>
    private const string MoveUpToTheJunction = "TAXI GL L F A #1144 HS F@L";

    /// <summary>The rest of <see cref="MoveUpToTheJunction"/> from where it holds short: west along F into UAL926.</summary>
    private const string HeadOnFromTheJunction = "TAXI F A #1144";

    /// <summary>The node where L meets F, which UAL926 passes eastbound along F and UAL2237 starts the move up on.</summary>
    private const int StartNodeId = 278;

    /// <summary>
    /// Replays the recording to <see cref="BeforeTaxiClearance"/>, ticks to <see cref="FirstBandSecond"/> without the recording's later
    /// commands, so UAL2237 stays held short, and runs <paramref name="test"/> there with the archive open.
    /// </summary>
    private static void WithEngineInBand(Action<SimulationEngine, AirportGroundLayout> test)
    {
        var groundData = RecordingGroundData.VersionPinned(BundlePath, "UAL2237's GIVEWAY to UAL926 crossing toward F");
        if (groundData.GetLayout("SFO") is not { } layout)
        {
            return;
        }

        TestVnasData.EnsureInitialized();
        RecordingArchive? archive = RecordingLoader.OpenArchive(BundlePath);
        Assert.NotNull(archive);

        using (archive)
        {
            var engine = new SimulationEngine(groundData, PushPlanStore.CreateShipped());
            engine.Replay(archive.ToBaseSessionRecording(), BeforeTaxiClearance);
            for (int t = BeforeTaxiClearance; t < FirstBandSecond; t++)
            {
                engine.TickOneSecond();
            }

            test(engine, layout);
        }
    }

    private static bool InCrossingBand(AircraftState held, AircraftState traffic)
    {
        double apart = Math.Abs(held.TrueHeading.Degrees - traffic.TrueHeading.Degrees);
        return Math.Min(apart, 360 - apart) is >= 60 and <= 120;
    }

    private static bool HasDeferral(AircraftState held) => held.DeferredDispatches.Exists(d => d.GiveWayTarget == TrafficCallsign);

    private static DeferredDispatch Defer(SimulationEngine engine, AircraftState held, string command)
    {
        CommandResult result = engine.SendCommand(HeldCallsign, command);
        Assert.True(result.Success, result.Message);
        return Assert.Single(held.DeferredDispatches, d => d.GiveWayTarget == TrafficCallsign);
    }

    private static TaxiRoute? Preview(SimulationEngine engine, AircraftState held, DeferredDispatch deferral) =>
        engine.PreviewDeferredTaxiRoute(held, deferral.Payload.Blocks[0].Commands.OfType<TaxiCommand>().First(), deferral.IsScenarioScripted);

    private static TaxiCommand TaxiOf(string command) =>
        Assert.IsType<TaxiCommand>(Assert.Single(Assert.Single(CommandParser.ParseCompound(command).Value!.Blocks).Commands));

    /// <summary>
    /// Whether no crossing the traffic has still to clear decides the pair on <paramref name="route"/>, so only the head-on rule
    /// can hold it.
    /// </summary>
    private static bool LeftToTheHeadOnRule(AircraftState held, AircraftState traffic, TaxiRoute route) =>
        GroundConflictDetector.GiveWayJunctionOf(held, traffic, route) is null or { TrafficPast: true };

    /// <summary>Whether the routes hold the pair on <paramref name="route"/>: their verdict is not clear.</summary>
    private static bool HeldByTheHeadOnRule(AircraftState held, AircraftState traffic, TaxiRoute route) =>
        GroundConflictDetector.GiveWayRouteVerdictOf(held, traffic, route) is { Clear: false };

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

    /// <summary>
    /// The signed distance (ft) of <paramref name="point"/> past <paramref name="origin"/> along the chord <paramref name="from"/> →
    /// <paramref name="to"/>.
    /// </summary>
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

    /// <summary>
    /// An edge's pavement from its from-node to its to-node: a fillet arc sampled off its Bézier, a straight edge through its
    /// intermediate points.
    /// </summary>
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
