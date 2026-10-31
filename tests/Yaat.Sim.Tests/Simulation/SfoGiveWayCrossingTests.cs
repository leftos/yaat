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
                TaxiRoute preview = Assert.IsType<TaxiRoute>(Preview(engine, held, deferral, layout));
                GroundConflictDetector.GiveWayJunction? junction = GroundConflictDetector.GiveWayJunctionOf(held, traffic, preview);
                Assert.True(junction is { TrafficPast: false }, "test setup: the previewed route should meet UAL926 at a node it has still to clear");
                int nodeId = junction.Value.NodeId;
                double requiredTailPastFt = junction.Value.RequiredPastFt;
                double trafficHalfLengthFt = AircraftLength.ResolveFt(traffic.AircraftType) / 2.0;

                int? firedAt = null;
                for (int t = FirstBandSecond + 1; t <= 2400; t++)
                {
                    engine.TickOneSecond();
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
                Assert.Null(Preview(engine, held, deferral, layout));

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

                Assert.IsType<TaxiRoute>(Preview(engine, held, deferral, layout));

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
                TaxiRoute route = Assert.IsType<TaxiRoute>(Preview(engine, held, deferral, layout));
                Assert.True(
                    GroundConflictDetector.GiveWayTrafficHeadOn(held, traffic, route),
                    "test setup: UAL926 should run head-on along the route"
                );

                int secondsDecidedByHeadOn = 0;
                int? firedAt = null;
                for (int t = FirstBandSecond + 1; (t <= 2400) && (firedAt is null); t++)
                {
                    bool nothingElseHolds = LeftToTheHeadOnRule(held, traffic, route);
                    bool inBand = InCrossingBand(held, traffic);
                    engine.TickOneSecond();
                    bool headOn = GroundConflictDetector.GiveWayTrafficHeadOn(held, traffic, route);
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
                    Preview(engine, held, taxi, layout) is { } route
                    && (GroundConflictDetector.GiveWayJunctionClear(GroundConflictDetector.GiveWayJunctionOf(held, traffic, route)) == true);

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
                TaxiRoute route = Assert.IsType<TaxiRoute>(Preview(engine, held, deferral, layout));
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
                    if (nothingElseHolds && GroundConflictDetector.GiveWayTrafficHeadOn(held, traffic, route))
                    {
                        secondsDecidedByHeadOn++;
                        Assert.False(block.TriggerMet, $"t={t}: the queued block passed with UAL926 running head-on along its route");
                    }
                }

                Assert.True(secondsDecidedByHeadOn > 0, "test setup: the head-on rule alone should hold the block in the band");
            }
        );
    }

    private const int BeforeTaxiClearance = 2080;

    /// <summary>
    /// The first second after <see cref="BeforeTaxiClearance"/> the pair is 60-120 degrees apart: UAL926 turning, UAL2237 held short at 118.
    /// </summary>
    private const int FirstBandSecond = 2082;
    private const string DeferredTaxi = "GIVEWAY UAL926 TAXI GL L LF F 28L";

    /// <summary>A TAXI onto F that runs back along UAL926's route past where it stands at <see cref="FirstBandSecond"/>.</summary>
    private const string HeadOnTaxi = "TAXI GL L F A #1144";

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

    private static TaxiRoute? Preview(SimulationEngine engine, AircraftState held, DeferredDispatch deferral, AirportGroundLayout layout) =>
        Preview(engine, held, deferral.Payload.Blocks[0].Commands.OfType<TaxiCommand>().First(), layout);

    private static TaxiRoute? Preview(SimulationEngine engine, AircraftState held, TaxiCommand taxi, AirportGroundLayout layout)
    {
        GroundCommandHandler.TaxiDispatch dispatch = TestDispatch.EmptyTaxi with { ListAircraft = () => engine.World.GetSnapshot() };
        return GroundCommandHandler.ResolveTaxiRoute(held, taxi, layout, dispatch);
    }

    private static TaxiCommand TaxiOf(string command) =>
        Assert.IsType<TaxiCommand>(Assert.Single(Assert.Single(CommandParser.ParseCompound(command).Value!.Blocks).Commands));

    /// <summary>Whether no shared node decides the pair on <paramref name="route"/>, so only the head-on rule can hold it.</summary>
    private static bool LeftToTheHeadOnRule(AircraftState held, AircraftState traffic, TaxiRoute route) =>
        GroundConflictDetector.GiveWayJunctionClear(GroundConflictDetector.GiveWayJunctionOf(held, traffic, route)) is null;

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
