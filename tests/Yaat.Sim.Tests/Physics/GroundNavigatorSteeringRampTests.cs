using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Simulation.GroundTaxi;

namespace Yaat.Sim.Tests.Physics;

/// <summary>
/// The ground navigator's free steering under the ramp turn model: on a straight it publishes its pure-pursuit steer as
/// the target heading and physics swings the nose through the ramped yaw rate (built, held and eased out, through zero
/// before a reversal), a closed-form arc hands its own yaw rate on to the straight after it, and inside the last
/// look-ahead window of a stop target the steer holds the segment bearing. Under the instant model the navigator still
/// writes the heading itself. Every case drives real OAK taxiways.
/// </summary>
public class GroundNavigatorSteeringRampTests(ITestOutputHelper output)
{
    private const double Dt = 0.25;
    private const string Type = "B738";
    private const double MinStraightFt = 900.0;

    private static readonly double RateStep =
        CategoryPerformance.GroundTurnRate(AircraftCategory.Jet) / CategoryPerformance.GroundSteerRampSeconds(AircraftCategory.Jet) * Dt;

    // T1: a 10 deg heading error at 15 kt ramps in: the rate rises from zero by at most one rate step a tick, and the
    // nose never turns faster than the ground steer ceiling.
    [Fact]
    public void Straight_Ramp_HeadingErrorRampsInWithinTheCeiling()
    {
        Rig rig = StraightRig(TurnModel.Ramp, new StraightStart(20, 0, 10, 15, Type, AircraftCategory.Jet));
        double ceiling = CategoryPerformance.GroundSteerRateCeilingDegPerSec(AircraftCategory.Jet, Type, 15, null);
        double previousRate = 0;
        for (int i = 0; i < 40; i++)
        {
            double before = rig.Aircraft.TrueHeading.Degrees;
            rig.Step(15);
            double turned = Math.Abs(GeoMath.SignedBearingDifference(before, rig.Aircraft.TrueHeading.Degrees));
            Assert.True(turned <= (ceiling * Dt) + 1e-9, $"tick {i}: turned {turned:F4} deg, ceiling {ceiling * Dt:F4}");
            double rate = Assert.NotNull(rig.Aircraft.AppliedTurnRateDegPerSec);
            Assert.True(Math.Abs(rate - previousRate) <= RateStep + 1e-9, $"tick {i}: rate {previousRate:F4} -> {rate:F4}");
            previousRate = rate;
        }
    }

    // T2: offset to the left of the line, the steer is first to the right and then, closing on the line, to the left: the
    // applied rate passes through zero, never jumping from one sign to the other by more than one rate step.
    [Fact]
    public void Reversal_Ramp_RatePassesThroughZero()
    {
        Rig rig = StraightRig(TurnModel.Ramp, new StraightStart(20, -15, 0, 15, Type, AircraftCategory.Jet));
        double previousRate = 0;
        bool sawRight = false;
        bool sawLeft = false;
        for (int i = 0; i < 120; i++)
        {
            rig.Step(15);
            double rate = Assert.NotNull(rig.Aircraft.AppliedTurnRateDegPerSec);
            Assert.True(Math.Abs(rate - previousRate) <= RateStep + 1e-9, $"tick {i}: rate {previousRate:F4} -> {rate:F4}");
            sawRight |= rate > RateStep;
            sawLeft |= sawRight && (rate < -RateStep);
            previousRate = rate;
        }

        Assert.True(sawRight && sawLeft, $"expected a right then a left yaw (right {sawRight}, left {sawLeft})");
    }

    // T3: under the instant model the navigator still writes the heading at the instant v/R rate and no rate is applied.
    [Fact]
    public void Straight_Instant_WritesTodaysHeadings()
    {
        Rig rig = StraightRig(TurnModel.Instant, new StraightStart(20, 0, 10, 15, Type, AircraftCategory.Jet));
        TrueHeading h0 = rig.Aircraft.TrueHeading;
        double maxTurn = CategoryPerformance.GroundYawRateAtSpeed(AircraftCategory.Jet, Type, 15) * Dt;
        Assert.True(maxTurn < 9.0, $"the first tick must not reach the steer ({maxTurn:F2} deg)");

        rig.Step(15);

        Assert.Equal(GeoMath.TurnHeadingToward(h0, rig.SegmentBearingDeg, maxTurn).Degrees, rig.Aircraft.TrueHeading.Degrees, 1e-9);
        for (int i = 0; i < 40; i++)
        {
            Assert.Null(rig.Aircraft.AppliedTurnRateDegPerSec);
            rig.Step(15);
        }
    }

    // T4: a real OAK fillet hands its closed-form yaw rate on to the straight after it: once the straight is set up the
    // applied rate is within one rate step of the arc's last-tick rate, and while it bleeds off the nose is never pulled
    // back toward the arc's exit tangent.
    [Fact]
    public void ArcHandOff_Ramp_CarriesTheArcYawRateIntoTheStraight()
    {
        (TaxiRoute route, LatLon start, double entryDeg) = ArcThenStraight();
        var rig = Rig.Create(TurnModel.Ramp, route, new RigStart(start, entryDeg, 10, Type, AircraftCategory.Jet), cleared: true);
        double arcRate = double.NaN;
        bool handedOff = false;
        for (int i = 0; (i < 2000) && !handedOff; i++)
        {
            NavigatorResult result = rig.Nav.Tick(rig.Ctx, isLastSegment: false, _ => true);
            arcRate = rig.Aircraft.AppliedTurnRateDegPerSec ?? 0;
            handedOff = result == NavigatorResult.ArrivedAtNode;
            if (handedOff)
            {
                route.CurrentSegmentIndex++;
                rig.Nav.SetupSegment(route, rig.Ctx, _ => true);
                PhysicsSteeringOntoTheNextSegment(rig, route);
            }
            else
            {
                rig.Physics();
            }
        }

        Assert.True(handedOff, "the arc never completed");
        Assert.True(Math.Abs(arcRate) > RateStep, $"the arc's last-tick rate {arcRate:F4} must be above one rate step");
        double handOffRate = Assert.NotNull(rig.Aircraft.AppliedTurnRateDegPerSec);
        output.WriteLine($"arc last-tick rate {arcRate:F4}, after hand-off {handOffRate:F4}, step {RateStep:F4}");
        Assert.True(Math.Abs(handOffRate - arcRate) <= RateStep + 1e-9, $"rate {arcRate:F4} -> {handOffRate:F4} at the hand-off");
        AssertNoPullBack(rig, Math.Sign(arcRate), handOffRate);
    }

    // A straight-to-straight node transition keeps the applied rate: it is never reset to null for a tick.
    [Fact]
    public void StraightToStraight_Ramp_KeepsTheAppliedRateAcrossTheNode()
    {
        (TaxiRoute route, LatLon start, double bearingDeg) = StraightThenStraight();
        var rig = Rig.Create(TurnModel.Ramp, route, new RigStart(start, bearingDeg, 10, Type, AircraftCategory.Jet), cleared: true);
        double previousRate = 0;
        bool crossed = false;
        for (int i = 0; (i < 400) && !crossed; i++)
        {
            NavigatorResult result = rig.Nav.Tick(rig.Ctx, isLastSegment: false, _ => true);
            crossed = result == NavigatorResult.ArrivedAtNode;
            if (crossed)
            {
                route.CurrentSegmentIndex++;
                rig.Nav.SetupSegment(route, rig.Ctx, _ => true);
                PhysicsSteeringOntoTheNextSegment(rig, route);
            }
            else
            {
                rig.Physics();
            }
            double rate = Assert.NotNull(rig.Aircraft.AppliedTurnRateDegPerSec);
            Assert.True(Math.Abs(rate - previousRate) <= RateStep + 1e-9, $"tick {i}: rate {previousRate:F4} -> {rate:F4}");
            previousRate = rate;
        }

        Assert.True(crossed, "never reached the node");
        for (int i = 0; i < 8; i++)
        {
            rig.Step(null);
            Assert.NotNull(rig.Aircraft.AppliedTurnRateDegPerSec);
        }
    }

    // T5: 30 ft off a real straight, heading parallel: one overshoot is inherent in the nose-wheel's ramp lag, but it is at
    // most a quarter of the initial offset, the offset never crosses back past the starting side by more than 1 ft, and it
    // is within 1 ft past 400 ft along-track. Under the ramp for a jet at 10 and 20 kt and a piston at 10 kt, and, as the
    // control, under the instant model.
    [Theory]
    [InlineData(TurnModel.Ramp, "B738", AircraftCategory.Jet, 10.0)]
    [InlineData(TurnModel.Ramp, "B738", AircraftCategory.Jet, 20.0)]
    [InlineData(TurnModel.Ramp, "C172", AircraftCategory.Piston, 10.0)]
    [InlineData(TurnModel.Instant, "B738", AircraftCategory.Jet, 10.0)]
    public void Convergence_ThirtyFeetOff_SettlesOnTheLine(TurnModel model, string type, AircraftCategory cat, double speedKts)
    {
        const double initialOffsetFt = 30.0;
        Rig rig = StraightRig(model, new StraightStart(20, -initialOffsetFt, 0, speedKts, type, cat));
        double farSideFt = 0;
        double recrossFt = 0;
        double worstLateFt = 0;
        var trace = new List<string>();
        for (int i = 0; (i < 2000) && (rig.AlongFt() < 600); i++)
        {
            rig.Step(speedKts);
            double crossFt = rig.CrossFt();
            farSideFt = Math.Max(farSideFt, crossFt);
            recrossFt = farSideFt > 0 ? Math.Max(recrossFt, -crossFt) : recrossFt;
            worstLateFt = rig.AlongFt() >= 400 ? Math.Max(worstLateFt, Math.Abs(crossFt)) : worstLateFt;
            if ((i % (int)Math.Round(80 / speedKts)) == 0)
            {
                trace.Add($"{rig.AlongFt():F0}:{crossFt:F2}");
            }
        }

        string label = $"{model} {type} {speedKts} kt";
        output.WriteLine(
            $"{label}: far side {farSideFt:F2} ft, recross {recrossFt:F2} ft, past 400 ft {worstLateFt:F2} ft; {string.Join(" ", trace)}"
        );
        Assert.True(farSideFt <= 0.25 * initialOffsetFt, $"{label}: overshot to the far side by {farSideFt:F2} ft");
        Assert.True(recrossFt <= 1.0, $"{label}: crossed back over the line by {recrossFt:F2} ft");
        Assert.True(worstLateFt <= 1.0, $"{label}: {worstLateFt:F2} ft off the line past 400 ft along-track");
    }

    // T6: inside the last look-ahead window of a hold-short stop the published steer is the segment bearing, the aircraft
    // stops on the node within the final arrival threshold, and once stopped no rate is applied.
    [Fact]
    public void StopTarget_Ramp_HoldsTheSegmentBearingAndStopsOnTheNode()
    {
        (GroundEdge edge, GroundNode from, GroundNode to) = LongStraight();
        double lengthFt = edge.DistanceNm * GeoMath.FeetPerNm;
        var route = new TaxiRoute
        {
            Segments = [new TaxiRouteSegment { Edge = edge.Directed(from, to), TaxiwayName = edge.TaxiwayName }],
            HoldShortPoints = [new HoldShortPoint { NodeId = to.Id, Reason = HoldShortReason.ExplicitHoldShort }],
        };
        double segBearing = GeoMath.BearingTo(from.Position, to.Position);
        LatLon start = Offset(from.Position, segBearing, lengthFt - 400, 2);
        var rig = Rig.Create(TurnModel.Ramp, route, new RigStart(start, segBearing, 10, Type, AircraftCategory.Jet), cleared: false);
        double windowFt = CategoryPerformance.MainGearTurnRadiusFt(AircraftCategory.Jet, Type);
        int windowTicks = 0;
        bool arrived = false;
        for (int i = 0; (i < 2000) && !arrived; i++)
        {
            arrived = rig.Nav.Tick(rig.Ctx, isLastSegment: false, _ => false) == NavigatorResult.ArrivedAtNode;
            if (!arrived && ((lengthFt - rig.AlongFt()) <= windowFt))
            {
                windowTicks++;
                Assert.Equal(segBearing, Assert.NotNull(rig.Ctx.Targets.TargetTrueHeading).Degrees, 1e-9);
            }

            rig.Physics();
        }

        double stopFt = GeoMath.DistanceNm(rig.Aircraft.Position, to.Position) * GeoMath.FeetPerNm;
        output.WriteLine($"stop {stopFt:F2} ft from the node after {windowTicks} window ticks");
        Assert.True(arrived && (windowTicks > 0), $"arrived {arrived}, window ticks {windowTicks}");
        Assert.True(stopFt <= (GroundNavigator.FinalNodeArrivalThresholdNm * GeoMath.FeetPerNm) + 0.1, $"stopped {stopFt:F2} ft off");
        rig.Ctx.Targets.TargetSpeed = 0;
        for (int i = 0; (i < 80) && (rig.Aircraft.GroundSpeed >= FlightPhysics.StationaryGroundSpeedKts); i++)
        {
            rig.Physics();
        }

        rig.Physics();
        Assert.Null(rig.Aircraft.AppliedTurnRateDegPerSec);
    }

    // A Ramp aircraft forced to circle on a straight (physics keeps turning it right) reaches the orbit limit: the guard
    // counts the turn physics makes between navigator ticks, not only the navigator's own heading writes.
    [Fact]
    public void ForcedCircle_Ramp_ReachesTheOrbitLimit()
    {
        Assert.True(GroundNavigator.ThrowOnOrbit, "the test module sets ThrowOnOrbit");
        Rig rig = StraightRig(TurnModel.Ramp, new StraightStart(400, 0, 0, 5, Type, AircraftCategory.Jet));
        InvalidOperationException orbit = Assert.Throws<InvalidOperationException>(() =>
        {
            for (int i = 0; i < 2000; i++)
            {
                rig.Aircraft.IndicatedAirspeed = 5;
                rig.Nav.Tick(rig.Ctx, isLastSegment: true, _ => true);
                rig.Ctx.Targets.TargetTrueHeading = new TrueHeading(rig.Aircraft.TrueHeading.Degrees + 90);
                rig.Aircraft.IndicatedAirspeed = 5;
                rig.Physics();
            }
        });
        Assert.Contains("orbit", orbit.Message, StringComparison.Ordinal);
    }

    // A Ramp turn about on real OAK geometry (N152SP on D): on the tick its reversal completes, the applied rate does not
    // change sign, so the creep turn does not pull the nose back toward the reversal's tangent.
    [Fact]
    public void TurnAboutReversalEnd_Ramp_KeepsTheYawSense()
    {
        SessionRecording? recording = RecordingLoader.Load("TestData/n152sp-ifr-cto-vfr-modifier-recording.yaat-bug-report-bundle.zip");
        if (recording is null)
        {
            output.WriteLine("Skipped: recording not available");
            return;
        }

        TestVnasData.EnsureInitialized();
        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped());
        engine.Replay(recording, 725);
        AircraftState ac = engine.FindAircraft("N152SP") ?? throw new InvalidOperationException("N152SP is not in the replay");
        ac.TrueHeading = new TrueHeading(132.42087186571268);
        ac.Position = GeoMath.ProjectPoint(new LatLon(37.72923805852486, -122.21368729789909), ac.TrueHeading, -13.0 / GeoMath.FeetPerNm);
        Assert.IsType<SimScenarioState>(engine.Scenario).TurnModel = TurnModel.Ramp;

        bool wasPlaying = false;
        bool completed = false;
        double rateBefore = 0;
        for (int sub = 0; (sub < 120 * SimulationEngine.PhysicsSubTickRate) && !completed; sub++)
        {
            rateBefore = ac.AppliedTurnRateDegPerSec ?? 0;
            engine.ReplayOneSubTick();
            bool playing = GroundNavigatorArcRestoreTests
                .PlaybackObjects(JsonSerializer.SerializeToNode(engine.CaptureSnapshot(), RecordingJsonOptions.Default))
                .Any(p => (bool?)p[nameof(GroundNavigatorPlaybackDto.TurnAboutReversalPlaying)] == true);
            completed = wasPlaying && !playing;
            wasPlaying = playing;
        }

        double rateAfter = ac.AppliedTurnRateDegPerSec ?? 0;
        output.WriteLine($"reversal completed {completed}: rate {rateBefore:F4} -> {rateAfter:F4}");
        Assert.True(completed, "the reversal never played and completed under the ramp");
        Assert.True((rateBefore * rateAfter) >= 0, $"the applied rate changed sign on the completion tick: {rateBefore:F4} -> {rateAfter:F4}");
    }

    // A fillet entered with a heading error: the closed-form rate is not recorded on the entry tick's pose write, and the
    // rate recorded never exceeds the ground steer ceiling.
    [Fact]
    public void FilletEnteredOffHeading_Ramp_RecordedRateWithinTheCeiling()
    {
        (TaxiRoute route, LatLon start, double entryDeg) = ArcThenStraight();
        var rig = Rig.Create(TurnModel.Ramp, route, new RigStart(start, entryDeg + 15, 10, Type, AircraftCategory.Jet), cleared: true);
        bool arrived = false;
        for (int i = 0; (i < 2000) && !arrived; i++)
        {
            arrived = rig.Nav.Tick(rig.Ctx, isLastSegment: false, _ => true) == NavigatorResult.ArrivedAtNode;
            double rate = rig.Aircraft.AppliedTurnRateDegPerSec ?? 0;
            double gs = rig.Aircraft.GroundSpeed;
            double ceiling = CategoryPerformance.GroundSteerRateCeilingDegPerSec(AircraftCategory.Jet, Type, gs, rig.Ctx.Targets.TurnRateOverride);
            Assert.True(Math.Abs(rate) <= ceiling + 1e-9, $"tick {i}: recorded {rate:F4} deg/s above the ceiling {ceiling:F4} at {gs:F1} kt");
            rig.Physics();
        }

        Assert.True(arrived, "the fillet never completed");
    }

    // A leftover preferred turn direction opposite a 2 deg correction: a ground steer never goes the long way round.
    [Fact]
    public void LeftoverPreferredTurnDirection_Ramp_TurnsTheShortWay()
    {
        Rig rig = StraightRig(TurnModel.Ramp, new StraightStart(20, 0, 2, 10, Type, AircraftCategory.Jet));
        rig.Ctx.Targets.PreferredTurnDirection = TurnDirection.Right;
        for (int i = 0; i < 80; i++)
        {
            rig.Step(10);
            double off = GeoMath.AbsBearingDifference(rig.Aircraft.TrueHeading.Degrees, rig.SegmentBearingDeg);
            Assert.True(off <= 2.5, $"tick {i}: heading {off:F2} deg off the line's bearing; it went the long way");
        }
    }

    // A real short shared-arm straight between two fillets (OAK or SFO): under the ramp, with the look-ahead raised to the
    // ramp lag, the aircraft completes both corners without orbiting.
    [Fact]
    public void ShortSharedArm_Ramp_CompletesBothCornersWithoutOrbit()
    {
        (TaxiRoute route, LatLon start, double entryDeg, string where) = ShortSharedArm();
        var rig = Rig.Create(TurnModel.Ramp, route, new RigStart(start, entryDeg, 10, Type, AircraftCategory.Jet), cleared: true);
        bool done = false;
        for (int i = 0; (i < 4000) && !done; i++)
        {
            bool last = route.CurrentSegmentIndex == (route.Segments.Count - 1);
            if (rig.Nav.Tick(rig.Ctx, isLastSegment: last, _ => true) == NavigatorResult.ArrivedAtNode)
            {
                route.CurrentSegmentIndex++;
                done = route.CurrentSegmentIndex >= route.Segments.Count;
                if (!done)
                {
                    rig.Nav.SetupSegment(route, rig.Ctx, _ => true);
                }
            }

            rig.Physics();
        }

        output.WriteLine($"{where}: completed {done}");
        Assert.True(done, $"{where}: the route never completed");
    }

    // An entry-alignment arc ending into its deferred straight (segment k): its end-of-arc nudge aims at segment k+1's
    // bearing, which is not the straight that takes over, so on the hand-off tick the target is the heading the arc left,
    // not k+1's bearing, and the nose does not move toward k+1 that tick.
    [Fact]
    public void EntryAlignmentHandOff_Ramp_DropsTheNextSegmentNudge()
    {
        (TaxiRoute route, GroundEdge first, double nextBrg) = StraightThenBentStraight();
        double inDeg = GeoMath.BearingTo(first.Nodes[0].Position, first.Nodes[1].Position);
        LatLon start = Offset(first.Nodes[0].Position, inDeg, 100, 0);
        var rig = Rig.Create(TurnModel.Ramp, route, new RigStart(start, inDeg + 90, 10, Type, AircraftCategory.Jet), cleared: true);
        Assert.IsType<PathPrimitiveSlowTurn>(rig.Nav.CurrentPrimitive);
        bool handedOff = false;
        for (int i = 0; (i < 2000) && !handedOff; i++)
        {
            bool aligning = rig.Nav.CurrentPrimitive is PathPrimitiveSlowTurn;
            NavigatorResult result = rig.Nav.Tick(rig.Ctx, isLastSegment: false, _ => true);
            handedOff = aligning && (result == NavigatorResult.Navigating) && (rig.Nav.CurrentPrimitive is PathPrimitiveStraight);
            if (!handedOff)
            {
                rig.Physics();
            }
        }

        Assert.True(handedOff, "the entry-alignment arc never handed over to its straight");
        double heading = rig.Aircraft.TrueHeading.Degrees;
        double target = Assert.NotNull(rig.Ctx.Targets.TargetTrueHeading).Degrees;
        output.WriteLine($"hand-off: heading {heading:F4}, target {target:F4}, k+1 bearing {nextBrg:F4}");
        Assert.True(GeoMath.AbsBearingDifference(target, heading) < 1e-9, $"target {target:F4} is not the heading {heading:F4}");
        Assert.True(GeoMath.AbsBearingDifference(target, nextBrg) > 1e-6, $"target {target:F4} is segment k+1's bearing");
        rig.Physics();
        double moved = GeoMath.SignedBearingDifference(heading, rig.Aircraft.TrueHeading.Degrees);
        Assert.True(Math.Abs(moved) < 1e-9, $"the nose moved {moved:F4} deg toward k+1's bearing on the hand-off tick");
    }

    // YAAT-2751: a short collinear straight then a 25-32 deg bend, under the ramp at 15 kt: the look-ahead walks past the short
    // straight and round the bend, so after the bend the aircraft is within 5 ft of the line.
    [Fact]
    public void ShortCollinearEdgeThenThirtyDegreeBend_Ramp_WalkedAimTakesTheBend_CrossTrackAfterTheBendWithinFiveFeet()
    {
        (TaxiRoute route, LatLon start, double inDeg, string where) = RequireChain(ShortCollinearThenBend());
        var rig = Rig.Create(TurnModel.Ramp, route, new RigStart(start, inDeg, 15, Type, AircraftCategory.Jet), cleared: true);
        List<TickSample> trace = Drive(rig, route, 4000, dropWalk: false);

        Assert.True(route.CurrentSegmentIndex >= route.Segments.Count, $"{where}: the route never completed");
        double afterFt = MaxOffsetPastNodeFt(trace, route.Segments[2]);
        output.WriteLine($"{where}: {RouteText(route)}; max cross-track after the bend {afterFt:F2} ft");
        Assert.True(afterFt <= 5.0, $"{where}: {afterFt:F2} ft off the line after the bend");
    }

    // YAAT-2751: a straight into a fillet, under the ramp at 15 kt: the walked aim follows the arc, so on the arc the aircraft
    // is within 5 ft of it.
    [Fact]
    public void Fillet_Ramp_AimStaysOnTheArc_CrossTrackOnTheArcWithinFiveFeet()
    {
        (TaxiRoute route, LatLon start, double inDeg, string where) = RequireChain(StraightIntoFillet());
        var rig = Rig.Create(TurnModel.Ramp, route, new RigStart(start, inDeg, 15, Type, AircraftCategory.Jet), cleared: true);
        List<TickSample> trace = Drive(rig, route, 4000, dropWalk: false);

        Assert.True(route.CurrentSegmentIndex >= route.Segments.Count, $"{where}: the route never completed");
        DirectionalEdge arcEdge = route.Segments[1].Edge;
        GroundArc arc = Assert.IsType<GroundArc>(arcEdge.Edge);
        double onArcFt = trace.Where(s => s.SegmentIndex == 1).Select(s => arc.Locate(arcEdge.FromNode, s.After).OffFt).DefaultIfEmpty(0).Max();
        output.WriteLine($"{where}: {RouteText(route)}; max cross-track on the arc {onArcFt:F2} ft");
        Assert.True(trace.Any(s => s.SegmentIndex == 1), $"{where}: no tick on the arc");
        Assert.True(onArcFt <= 5.0, $"{where}: {onArcFt:F2} ft off the arc");
    }

    // YAAT-2751: an unfilleted 35-40 deg bend, under the ramp at 15 kt: the nose turns one way through the node (no swing
    // toward it and back) until it reaches the next bearing, and after the bend the aircraft is within 10 ft of the line.
    [Fact]
    public void ThirtyFiveToFortyDegreeBend_Ramp_NoSwingAtTheNode_NoOrbitNoOvershoot()
    {
        (TaxiRoute route, LatLon start, double inDeg, string where) = RequireChain(
            FindStraightChain(FilletMode.Standard, new ChainLeg(60, 1e9, 35, 40))
        );
        var rig = Rig.Create(TurnModel.Ramp, route, new RigStart(start, inDeg, 15, Type, AircraftCategory.Jet), cleared: true);
        List<TickSample> trace = Drive(rig, route, 4000, dropWalk: false);

        Assert.True(route.CurrentSegmentIndex >= route.Segments.Count, $"{where}: the route never completed");
        DirectionalEdge post = route.Segments[1].Edge;
        double afterFt = MaxOffsetPastNodeFt(trace, route.Segments[1]);
        double reversalDeg = MaxHeadingReversalDeg(trace, post.FromNode.Position, inDeg, post.DepartureBearing);
        output.WriteLine($"{where}: {RouteText(route)}; after the bend {afterFt:F2} ft, heading reversal {reversalDeg:F4} deg");
        Assert.True(reversalDeg <= 1e-6, $"{where}: the heading swung back {reversalDeg:F4} deg through the node");
        Assert.True(afterFt <= 10.0, $"{where}: {afterFt:F2} ft off the line after the bend");
    }

    // YAAT-2751: an unfilleted 28-30 deg kink (no fillet pass), under the ramp at 15 kt, where the past-node aim and the lowered
    // entry-alignment gate both apply: no orbit (the module throws on one) and no overshoot: after the kink the aircraft is at
    // most 10 ft to the outside of the turn (the TDG 3 edge safety margin). The cut inside the corner is reported only.
    [Fact]
    public void UnfilletedKinkTwentyEightToThirty_Ramp_NeitherOrbitsNorOvershoots()
    {
        (TaxiRoute route, LatLon start, double inDeg, string where) = RequireChain(
            FindStraightChain(FilletMode.None, new ChainLeg(60, 1e9, 28.0001, 30))
        );
        Assert.True(GroundNavigator.ThrowOnOrbit, "the test module sets ThrowOnOrbit");
        var rig = Rig.Create(TurnModel.Ramp, route, new RigStart(start, inDeg, 15, Type, AircraftCategory.Jet), cleared: true);
        List<TickSample> trace = Drive(rig, route, 4000, dropWalk: false);

        Assert.True(route.CurrentSegmentIndex >= route.Segments.Count, $"{where}: the route never completed");
        DirectionalEdge post = route.Segments[1].Edge;
        double postFt = post.DistanceNm * GeoMath.FeetPerNm;
        double insideSign = Math.Sign(GeoMath.SignedBearingDifference(route.Segments[0].Edge.DepartureBearing, post.DepartureBearing));
        double outsideFt = 0;
        double insideFt = 0;
        foreach (TickSample sample in trace)
        {
            (double along, double cross) = LineFrame(post.FromNode.Position, post.DepartureBearing, sample.After);
            if ((along >= 0) && (along <= postFt))
            {
                insideFt = Math.Max(insideFt, cross * insideSign);
                outsideFt = Math.Max(outsideFt, -cross * insideSign);
            }
        }

        output.WriteLine($"{where}: {RouteText(route)}; after the kink {outsideFt:F2} ft outside, {insideFt:F2} ft inside the turn");
        Assert.True(outsideFt <= 10.0, $"{where}: overshot {outsideFt:F2} ft to the outside of the turn after the kink");
    }

    // YAAT-2751: under the instant model the walk changes nothing: through the short-straight-then-bend fixture the heading
    // trace is the same, bit for bit, as the trace flown with the walk cleared after every set-up. Under the ramp, as the
    // control, clearing it does change the trace.
    [Fact]
    public void Instant_PastNodeAimUnchanged()
    {
        static string Digest(TurnModel model, bool dropWalk)
        {
            (TaxiRoute route, LatLon start, double inDeg, string _) = RequireChain(ShortCollinearThenBend());
            var rig = Rig.Create(model, route, new RigStart(start, inDeg, 15, Type, AircraftCategory.Jet), cleared: true);
            return HeadingDigest(Drive(rig, route, 4000, dropWalk));
        }

        string instant = Digest(TurnModel.Instant, dropWalk: false);
        string ramp = Digest(TurnModel.Ramp, dropWalk: false);
        string rampWithoutWalk = Digest(TurnModel.Ramp, dropWalk: true);
        output.WriteLine($"instant {instant}, ramp {ramp}, ramp without the walk {rampWithoutWalk}");
        Assert.Equal(Digest(TurnModel.Instant, dropWalk: true), instant);
        Assert.NotEqual(rampWithoutWalk, ramp);
    }

    // YAAT-2751: an uncleared bar at the far end of the next segment, within the look-ahead's reach: the walked aim never
    // passes its painted stop (to 0.1 ft, the gap between GeoMath.ProjectPointRaw's and GeoMath.DistanceNm's measures over
    // the leg), and rests there once the reach is past it; cleared, the walk goes on past the node.
    [Fact]
    public void UnclearedHoldShortWithinReach_Ramp_AimNeverPassesThePaintedStop()
    {
        (TaxiRoute route, LatLon start, double inDeg, string where) = RequireChain(
            FindStraightChain(FilletMode.Standard, new ChainLeg(40, 150, 0, 20), new ChainLeg(20, 1e9, 0, 20))
        );
        DirectionalEdge barEdge = route.Segments[1].Edge;
        double barEdgeFt = GeoMath.DistanceNm(barEdge.FromNode.Position, barEdge.ToNode.Position) * GeoMath.FeetPerNm;
        LatLon stop = GeoMath.ProjectPointRaw(barEdge.FromNode.Position, barEdge.DepartureBearing, (barEdgeFt - 15) / GeoMath.FeetPerNm);
        var bar = new HoldShortPoint
        {
            NodeId = barEdge.ToNodeId,
            Reason = HoldShortReason.ExplicitHoldShort,
            Latitude = stop.Lat,
            Longitude = stop.Lon,
        };
        route.HoldShortPoints.Add(bar);
        var rig = Rig.Create(TurnModel.Ramp, route, new RigStart(start, inDeg, 15, Type, AircraftCategory.Jet), cleared: true);
        IReadOnlyList<GroundNavigator.RouteWalkLeg> legs = rig.Nav.RouteWalkLegs ?? throw new InvalidOperationException("no walk legs");
        bool Uncleared(int id) => id != bar.NodeId;
        double paintedFt = LineFrame(barEdge.FromNode.Position, barEdge.DepartureBearing, stop).AlongFt;
        output.WriteLine($"{where}: {RouteText(route)}; painted stop {paintedFt:F2} ft along the bar's segment");

        for (double leftoverFt = 0; leftoverFt <= barEdgeFt + 200; leftoverFt += 1)
        {
            (double along, double _) = LineFrame(
                barEdge.FromNode.Position,
                barEdge.DepartureBearing,
                GroundNavigator.PointAlongRoute(legs, leftoverFt, Uncleared)
            );
            Assert.True(
                along <= paintedFt + 0.1,
                $"{where}: leftover {leftoverFt:F0} ft walks {along:F2} ft, past the painted stop at {paintedFt:F2} ft"
            );
        }

        double restFt = GeoMath.DistanceNm(GroundNavigator.PointAlongRoute(legs, barEdgeFt + 200, Uncleared), stop) * GeoMath.FeetPerNm;
        Assert.True(restFt <= 0.5, $"{where}: the clamped aim rests {restFt:F2} ft from the painted stop");
        DirectionalEdge beyond = route.Segments[2].Edge;
        (double beyondAlong, double beyondCross) = LineFrame(
            beyond.FromNode.Position,
            beyond.DepartureBearing,
            GroundNavigator.PointAlongRoute(legs, barEdgeFt + 10, _ => true)
        );
        Assert.True(
            (Math.Abs(beyondAlong - 10) <= 0.1) && (Math.Abs(beyondCross) <= 0.1),
            $"{where}: cleared, the walk stops at {beyondAlong:F2} ft"
        );
    }

    // YAAT-2751: the route ends within the look-ahead's reach: the walked aim runs along the last segment and stops on its
    // far node.
    [Fact]
    public void RouteEndWithinReach_Ramp_AimClampedAtTheLastNode()
    {
        (TaxiRoute route, LatLon start, double inDeg, string where) = RequireChain(
            FindStraightChain(FilletMode.Standard, new ChainLeg(20, 1e9, 0, 20))
        );
        var rig = Rig.Create(TurnModel.Ramp, route, new RigStart(start, inDeg, 15, Type, AircraftCategory.Jet), cleared: true);
        IReadOnlyList<GroundNavigator.RouteWalkLeg> legs = rig.Nav.RouteWalkLegs ?? throw new InvalidOperationException("no walk legs");
        DirectionalEdge last = route.Segments[1].Edge;
        double lastFt = last.DistanceNm * GeoMath.FeetPerNm;
        output.WriteLine($"{where}: {RouteText(route)}");

        (double halfAlong, double halfCross) = LineFrame(
            last.FromNode.Position,
            last.DepartureBearing,
            GroundNavigator.PointAlongRoute(legs, lastFt / 2, _ => true)
        );
        Assert.True(
            (Math.Abs(halfAlong - (lastFt / 2)) <= 0.1) && (Math.Abs(halfCross) <= 0.1),
            $"{where}: half way walks to {halfAlong:F2}/{halfCross:F2} ft"
        );
        double endFt = GeoMath.DistanceNm(GroundNavigator.PointAlongRoute(legs, lastFt + 100, _ => true), last.ToNode.Position) * GeoMath.FeetPerNm;
        Assert.True(endFt <= 1e-6, $"{where}: past the route end the aim is {endFt:F6} ft from the last node");
    }

    // YAAT-2751: a corner sharper than 45 deg at the target: the steer past the node is null, so the final window
    // keeps the bearing to the node; the heading trace is the one flown with the walk cleared after every set-up.
    [Fact]
    public void CornerAboveFortyFive_Ramp_AimsAtTheNodeAsToday()
    {
        (TaxiRoute route, LatLon start, double inDeg, string where) = RequireChain(
            FindStraightChain(FilletMode.Standard, new ChainLeg(60, 1e9, 50, 120))
        );
        var rig = Rig.Create(TurnModel.Ramp, route, new RigStart(start, inDeg, 15, Type, AircraftCategory.Jet), cleared: true);
        double segBearingDeg = route.Segments[0].Edge.DepartureBearing;
        foreach (double leftoverFt in new[] { 1.0, 10.0, 50.0, 100.0 })
        {
            Assert.Null(rig.Nav.PastNodeSteerDeg(rig.Ctx, segBearingDeg, leftoverFt / GeoMath.FeetPerNm, _ => true));
        }

        output.WriteLine($"{where}: {RouteText(route)}");
        AssertSameHeadings(where, RampTrace(route, start, inDeg, dropWalk: false), RampTrace(route, start, inDeg, dropWalk: true));
    }

    // YAAT-2751: an aircraft on an aimed line over a fillet whose line meets the next straight more than 45 deg off (the
    // fillet's own exit tangent runs on into it): the walk's first corner is judged against the line flown, so the steer past
    // the node is null, and the heading trace is the one flown with the walk cleared after every set-up.
    [Fact]
    public void AimedLineOverFillet_FlownLineMeetsTheNextStraightAboveFortyFive_Ramp_KeepsTodaysAim()
    {
        (Rig rig, TaxiRoute route, string where) = AimedLineRig();
        Assert.IsType<PathPrimitiveStraight>(rig.Nav.CurrentPrimitive);
        double segBearingDeg = GeoMath.BearingTo(rig.From, route.Segments[0].Edge.ToNode.Position);
        foreach (double leftoverFt in new[] { 1.0, 10.0, 50.0, 100.0 })
        {
            Assert.Null(rig.Nav.PastNodeSteerDeg(rig.Ctx, segBearingDeg, leftoverFt / GeoMath.FeetPerNm, _ => true));
        }

        output.WriteLine($"{where}: {RouteText(route)}");
        (Rig other, TaxiRoute otherRoute, string _) = AimedLineRig();
        AssertSameHeadings(where, Drive(rig, route, 4000, dropWalk: false), Drive(other, otherRoute, 4000, dropWalk: true));
    }

    // YAAT-2751: ticked at 30 kt toward a bar at the far end of the next segment, uncleared and then cleared mid-segment: while it is
    // uncleared the steer is the bearing to its painted stop; once cleared, the same straight's steer walks on past the bar's
    // node, so TickStraight reads the live hold-short state each tick.
    [Fact]
    public void HoldShortClearedMidSegment_Ramp_AimStopsAtThePaintedStopThenWalksPast()
    {
        const double SpeedKts = 30.0;
        (TaxiRoute route, LatLon start, double inDeg, string where) = RequireChain(
            FindStraightChain(FilletMode.Standard, new ChainLeg(25, 35, 0, 20), new ChainLeg(40, 1e9, 10, 40))
        );
        DirectionalEdge first = route.Segments[0].Edge;
        DirectionalEdge barEdge = route.Segments[1].Edge;
        double barEdgeFt = GeoMath.DistanceNm(barEdge.FromNode.Position, barEdge.ToNode.Position) * GeoMath.FeetPerNm;
        LatLon stop = GeoMath.ProjectPointRaw(barEdge.FromNode.Position, barEdge.DepartureBearing, (barEdgeFt - 10) / GeoMath.FeetPerNm);
        var bar = new HoldShortPoint
        {
            NodeId = barEdge.ToNodeId,
            Reason = HoldShortReason.ExplicitHoldShort,
            Latitude = stop.Lat,
            Longitude = stop.Lon,
        };
        route.HoldShortPoints.Add(bar);
        var rig = Rig.Create(TurnModel.Ramp, route, new RigStart(start, inDeg, SpeedKts, Type, AircraftCategory.Jet), cleared: true);
        IReadOnlyList<GroundNavigator.RouteWalkLeg> legs = rig.Nav.RouteWalkLegs ?? throw new InvalidOperationException("no walk legs");
        LatLon clamped = GroundNavigator.PointAlongRoute(legs, 1e9, id => id != bar.NodeId);
        double firstFt = GeoMath.DistanceNm(first.FromNode.Position, first.ToNode.Position) * GeoMath.FeetPerNm;
        double lookAheadFt = SpeedKts * GeoMath.FeetPerNm / 3600.0 * CategoryPerformance.GroundSteerRampSeconds(AircraftCategory.Jet);
        bool barCleared = false;
        int unclearedTicks = 0;
        int clearedTicks = 0;
        for (int i = 0; i < 2000; i++)
        {
            rig.Aircraft.IndicatedAirspeed = SpeedKts;
            LatLon pos = rig.Aircraft.Position;
            double leftoverFt = lookAheadFt - (firstFt - LineFrame(first.FromNode.Position, first.DepartureBearing, pos).AlongFt);
            bool toNodeClear = (GeoMath.DistanceNm(pos, first.ToNode.Position) * GeoMath.FeetPerNm) > 50;
            bool clearedNow = barCleared;
            if (rig.Nav.Tick(rig.Ctx, isLastSegment: false, id => clearedNow || (id != bar.NodeId)) == NavigatorResult.ArrivedAtNode)
            {
                break;
            }

            double target = Assert.NotNull(rig.Ctx.Targets.TargetTrueHeading).Degrees;
            if (toNodeClear && !clearedNow && (leftoverFt >= barEdgeFt - 9))
            {
                Assert.True(GeoMath.AbsBearingDifference(target, GeoMath.BearingTo(pos, clamped)) < 1e-9, $"tick {i}: uncleared, not at the stop");
                unclearedTicks++;
                barCleared = true;
            }
            else if (toNodeClear && clearedNow && (leftoverFt >= barEdgeFt + 5))
            {
                double walkedDeg = GeoMath.BearingTo(pos, GroundNavigator.PointAlongRoute(legs, leftoverFt, _ => true));
                double stopDeg = GeoMath.BearingTo(pos, clamped);
                Assert.True(GeoMath.AbsBearingDifference(target, walkedDeg) < 0.05, $"tick {i}: cleared, {target:F4} is not the walk {walkedDeg:F4}");
                Assert.True(GeoMath.AbsBearingDifference(target, stopDeg) > 0.2, $"tick {i}: cleared, still aimed at the stop {stopDeg:F4}");
                clearedTicks++;
            }

            rig.Aircraft.IndicatedAirspeed = SpeedKts;
            rig.Physics();
        }

        output.WriteLine($"{where}: {RouteText(route)}; {unclearedTicks} uncleared and {clearedTicks} cleared window ticks");
        Assert.True((unclearedTicks > 0) && (clearedTicks > 0), $"{where}: uncleared ticks {unclearedTicks}, cleared ticks {clearedTicks}");
    }

    // YAAT-2751: the walk legs are not snapshotted: a navigator restored mid-segment from a round-tripped snapshot has none
    // until its set-up rebuilds them from the route, and from there flies the same headings, bit for bit, as the run that was
    // never interrupted.
    [Fact]
    public void WalkLegs_RestoredMidSegment_RebuiltBySetUp_ContinuesIdentically()
    {
        (TaxiRoute liveRoute, LatLon start, double inDeg, string where) = RequireChain(ShortCollinearThenBend());
        (TaxiRoute otherRoute, LatLon _, double _, string _) = RequireChain(ShortCollinearThenBend());
        var rigStart = new RigStart(start, inDeg, 15, Type, AircraftCategory.Jet);
        var live = Rig.Create(TurnModel.Ramp, liveRoute, rigStart, cleared: true);
        var other = Rig.Create(TurnModel.Ramp, otherRoute, rigStart, cleared: true);
        Drive(live, liveRoute, 12, dropWalk: false);
        Drive(other, otherRoute, 12, dropWalk: false);
        Assert.NotNull(other.Nav.RouteWalkLegs);

        string json = JsonSerializer.Serialize(other.Nav.ToSnapshot(), RecordingJsonOptions.Default);
        GroundNavigatorDto read = Assert.IsType<GroundNavigatorDto>(
            JsonSerializer.Deserialize<GroundNavigatorDto>(json, RecordingJsonOptions.Default)
        );
        var restored = GroundNavigator.FromSnapshot(read);
        Assert.Null(restored.RouteWalkLegs);
        Assert.Equal(json, JsonSerializer.Serialize(restored.ToSnapshot(), RecordingJsonOptions.Default));
        restored.SetupSegment(otherRoute, other.Ctx, _ => true);
        Assert.NotNull(restored.RouteWalkLegs);
        var resumed = new Rig
        {
            Aircraft = other.Aircraft,
            Ctx = other.Ctx,
            Nav = restored,
            Options = other.Options,
            From = other.From,
            SegmentBearingDeg = other.SegmentBearingDeg,
            Cleared = true,
        };

        List<TickSample> liveTrace = Drive(live, liveRoute, 4000, dropWalk: false);
        output.WriteLine($"{where}: {RouteText(liveRoute)}; {liveTrace.Count} ticks after the restore");
        AssertSameHeadings(where, liveTrace, Drive(resumed, otherRoute, 4000, dropWalk: false));
    }

    // YAAT-2751: a virtual first segment entered off its heading by more than the entry-alignment gate, so the alignment arc
    // plays and its completion re-anchors the line on the aircraft: snapshotted right after that and restored, the navigator
    // flies the same headings, bit for bit, as the run that was never interrupted, for lines 35-44 deg off the next straight
    // on either side and either turn sense.
    [Fact]
    public void VirtualFirstSegment_RestoredAfterTheAlignmentArc_ContinuesIdentically()
    {
        int checkedCases = 0;
        foreach (double offDeg in new[] { -44.0, -40.0, -35.0, 35.0, 40.0, 44.0 })
        {
            foreach (double turnDeg in new[] { -90.0, 90.0 })
            {
                (Rig live, TaxiRoute liveRoute, string where) = VirtualEntryRig(offDeg, turnDeg);
                (Rig other, TaxiRoute otherRoute, string _) = VirtualEntryRig(offDeg, turnDeg);
                string label = $"{where}: line {offDeg} deg off, heading {turnDeg} deg off it";
                Assert.IsType<PathPrimitiveSlowTurn>(live.Nav.CurrentPrimitive);
                for (int i = 0; (i < 400) && (live.Nav.CurrentPrimitive is PathPrimitiveSlowTurn); i++)
                {
                    Drive(live, liveRoute, 1, dropWalk: false);
                    Drive(other, otherRoute, 1, dropWalk: false);
                }

                Assert.True(live.Nav.CurrentPrimitive is PathPrimitiveStraight, $"{label}: the alignment arc never handed over to a straight");
                string json = JsonSerializer.Serialize(other.Nav.ToSnapshot(), RecordingJsonOptions.Default);
                var restored = GroundNavigator.FromSnapshot(
                    Assert.IsType<GroundNavigatorDto>(JsonSerializer.Deserialize<GroundNavigatorDto>(json, RecordingJsonOptions.Default))
                );
                restored.SetupSegment(otherRoute, other.Ctx, _ => true);
                var resumed = new Rig
                {
                    Aircraft = other.Aircraft,
                    Ctx = other.Ctx,
                    Nav = restored,
                    Options = other.Options,
                    From = other.From,
                    SegmentBearingDeg = other.SegmentBearingDeg,
                    Cleared = true,
                };
                AssertSameHeadings(label, Drive(live, liveRoute, 4000, dropWalk: false), Drive(resumed, otherRoute, 4000, dropWalk: false));
                checkedCases++;
            }
        }

        output.WriteLine($"{checkedCases} cases continued identically");
    }

    /// <summary>
    /// Two plain straights on OAK then SFO, the first at least 200 ft and the second at least 60 ft, meeting at a node with a
    /// 3-40 deg deflection: the route and the first edge, and the second's departure bearing.
    /// </summary>
    private static (TaxiRoute Route, GroundEdge First, double NextBrg) StraightThenBentStraight()
    {
        foreach (string airport in new[] { "OAK", "SFO" })
        {
            AirportGroundLayout? layout = new TestAirportGroundData(FilletMode.Standard).GetLayout(airport);
            List<GroundEdge> plain = layout is null ? [] : [.. layout.Edges.Where(IsPlainTaxiway).OrderBy(e => e.Nodes[0].Id)];
            foreach (GroundEdge first in plain.Where(e => (e.DistanceNm * GeoMath.FeetPerNm) >= 200))
            {
                GroundNode node = first.Nodes[1];
                double inDeg = GeoMath.BearingTo(first.Nodes[0].Position, node.Position);
                GroundEdge? second = plain.FirstOrDefault(e =>
                    (e != first)
                    && e.HasNode(node.Id)
                    && ((e.DistanceNm * GeoMath.FeetPerNm) >= 60)
                    && (DeflectionDeg(inDeg, node, e) is >= 3 and <= 40)
                );
                if (second is null)
                {
                    continue;
                }

                DirectionalEdge next = second.Directed(node, second.OtherNode(node));
                var route = new TaxiRoute
                {
                    Segments =
                    [
                        new TaxiRouteSegment { Edge = first.Directed(first.Nodes[0], node), TaxiwayName = first.TaxiwayName },
                        new TaxiRouteSegment { Edge = next, TaxiwayName = second.TaxiwayName },
                    ],
                    HoldShortPoints = [],
                };
                return (route, first, next.DepartureBearing);
            }
        }

        Assert.Fail("no pair of plain straights meets at a 3-40 deg bend on OAK or SFO");
        throw new InvalidOperationException();
    }

    /// <summary>
    /// On the tick a straight takes over: the target is the next segment's bearing the nudge published, and physics moves
    /// the nose toward it that same tick.
    /// </summary>
    private static void PhysicsSteeringOntoTheNextSegment(Rig rig, TaxiRoute route)
    {
        double nextBrg = route.Segments[route.CurrentSegmentIndex].Edge.DepartureBearing;
        double target = Assert.NotNull(rig.Ctx.Targets.TargetTrueHeading).Degrees;
        Assert.True(GeoMath.AbsBearingDifference(target, nextBrg) < 1e-9, $"target {target:F4} is not the next bearing {nextBrg:F4}");
        double before = rig.Aircraft.TrueHeading.Degrees;
        double errDeg = GeoMath.SignedBearingDifference(before, nextBrg);
        rig.Physics();
        double moved = GeoMath.SignedBearingDifference(before, rig.Aircraft.TrueHeading.Degrees);
        Assert.True((Math.Abs(errDeg) < 1e-9) || ((moved * errDeg) > 0), $"error {errDeg:F4} deg, the nose moved {moved:F4} deg");
    }

    /// <summary>
    /// A plain straight of 30-70 ft whose two ends are fillets tangent to it (within 5 deg), on OAK then SFO: the route is
    /// the first fillet, the straight and the second fillet, starting on the first fillet's start point.
    /// </summary>
    private static (TaxiRoute Route, LatLon Start, double EntryDeg, string Where) ShortSharedArm()
    {
        foreach (string airport in new[] { "OAK", "SFO" })
        {
            AirportGroundLayout? layout = new TestAirportGroundData(FilletMode.Standard).GetLayout(airport);
            if ((layout is not null) && (ShortSharedArmOn(layout) is { } found))
            {
                return (found.Route, found.Start, found.EntryDeg, airport);
            }
        }

        Assert.Fail("no short shared-arm straight between two fillets on OAK or SFO");
        throw new InvalidOperationException();
    }

    private static (TaxiRoute Route, LatLon Start, double EntryDeg)? ShortSharedArmOn(AirportGroundLayout layout)
    {
        IEnumerable<GroundEdge> arms = layout.Edges.Where(e => IsPlainTaxiway(e) && (e.DistanceNm * GeoMath.FeetPerNm) is >= 30 and <= 70);
        foreach (GroundEdge arm in arms.OrderBy(e => e.Nodes[0].Id))
        {
            GroundNode a = arm.Nodes[0];
            GroundNode b = arm.Nodes[1];
            double armDeg = GeoMath.BearingTo(a.Position, b.Position);
            TaxiRouteSegment? into = TangentArc(layout, a, armDeg, exits: true);
            TaxiRouteSegment? outOf = TangentArc(layout, b, armDeg, exits: false);
            if ((into is null) || (outOf is null))
            {
                continue;
            }

            var route = new TaxiRoute
            {
                Segments = [into, new TaxiRouteSegment { Edge = arm.Directed(a, b), TaxiwayName = arm.TaxiwayName }, outOf],
                HoldShortPoints = [],
            };
            var bez = (PathPrimitiveBezier)PathPrimitiveBuilder.FromSegment(into);
            return (route, into.Edge.FromNode.Position, bez.Curve.TangentBearing(0.0));
        }

        return null;
    }

    /// <summary>
    /// A fillet at <paramref name="node"/> tangent to <paramref name="armDeg"/> there: ending on the node (<paramref name="exits"/>)
    /// or starting from it.
    /// </summary>
    private static TaxiRouteSegment? TangentArc(AirportGroundLayout layout, GroundNode node, double armDeg, bool exits)
    {
        foreach (GroundArc arc in layout.Arcs.Where(c => c.HasNode(node.Id) && (c.Nodes[0].Id != c.Nodes[1].Id) && (c.MinRadiusOfCurvatureFt >= 40)))
        {
            GroundNode other = arc.Nodes[0].Id == node.Id ? arc.Nodes[1] : arc.Nodes[0];
            (GroundNode from, GroundNode to) = exits ? (other, node) : (node, other);
            var seg = new TaxiRouteSegment { Edge = arc.Directed(from, to), TaxiwayName = arc.TaxiwayName };
            var bez = (PathPrimitiveBezier)PathPrimitiveBuilder.FromSegment(seg);
            double tangentDeg = bez.Curve.TangentBearing(exits ? 1.0 : 0.0);
            if (GeoMath.AbsBearingDifference(tangentDeg, armDeg) <= 5.0)
            {
                return seg;
            }
        }

        return null;
    }

    private void AssertNoPullBack(Rig rig, int arcSign, double handOffRate)
    {
        double rate = handOffRate;
        for (int i = 0; (i < 80) && ((rate * arcSign) > 0); i++)
        {
            double before = rig.Aircraft.TrueHeading.Degrees;
            rig.Step(null);
            rate = rig.Aircraft.AppliedTurnRateDegPerSec ?? 0;
            if ((rate * arcSign) <= 0)
            {
                break; // the yaw has passed through zero: from here the straight's own steer turns the nose
            }

            double moved = GeoMath.SignedBearingDifference(before, rig.Aircraft.TrueHeading.Degrees) * arcSign;
            Assert.True(moved >= -1e-9, $"tick {i}: the nose was pulled back {moved:F4} deg toward the arc's exit tangent");
        }

        output.WriteLine($"bled to {rate:F4}");
    }

    private static Rig StraightRig(TurnModel model, StraightStart s)
    {
        (GroundEdge edge, GroundNode from, GroundNode to) = LongStraight();
        var route = new TaxiRoute
        {
            Segments = [new TaxiRouteSegment { Edge = edge.Directed(from, to), TaxiwayName = edge.TaxiwayName }],
            HoldShortPoints = [],
        };
        double segBearing = GeoMath.BearingTo(from.Position, to.Position);
        LatLon start = Offset(from.Position, segBearing, s.AlongFt, s.OffsetFt);
        var rigStart = new RigStart(start, segBearing + s.HeadingErrDeg, s.SpeedKts, s.AircraftType, s.Category);
        return Rig.Create(model, route, rigStart, cleared: true);
    }

    /// <summary>A point <paramref name="alongFt"/> along <paramref name="bearingDeg"/>, then <paramref name="rightFt"/> to its right.</summary>
    private static LatLon Offset(LatLon origin, double bearingDeg, double alongFt, double rightFt)
    {
        (double lat, double lon) = GeoMath.ProjectPoint(origin, new TrueHeading(bearingDeg), alongFt / GeoMath.FeetPerNm);
        (double oLat, double oLon) = GeoMath.ProjectPoint(new LatLon(lat, lon), new TrueHeading(bearingDeg + 90), rightFt / GeoMath.FeetPerNm);
        return new LatLon(oLat, oLon);
    }

    private static AirportGroundLayout Oak()
    {
        TestVnasData.EnsureInitialized();
        AirportGroundLayout? layout = new TestAirportGroundData(FilletMode.Standard).GetLayout("OAK");
        Assert.NotNull(layout);
        return layout;
    }

    private static bool IsPlainTaxiway(GroundEdge e) =>
        (e.IntermediatePoints.Count == 0) && !e.IsRunwayCenterline && !e.IsRunwayCrossingLink && !e.IsRamp && (e.Nodes[0].Id != e.Nodes[1].Id);

    /// <summary>The longest plain OAK taxiway straight (no shape points), at least <see cref="MinStraightFt"/>.</summary>
    private static (GroundEdge Edge, GroundNode From, GroundNode To) LongStraight()
    {
        GroundEdge edge = Oak().Edges.Where(IsPlainTaxiway).OrderByDescending(e => e.DistanceNm).ThenBy(e => e.Nodes[0].Id).First();
        Assert.True(edge.DistanceNm * GeoMath.FeetPerNm >= MinStraightFt, $"longest OAK straight is {edge.DistanceNm * GeoMath.FeetPerNm:F0} ft");
        return (edge, edge.Nodes[0], edge.Nodes[1]);
    }

    /// <summary>
    /// An OAK fillet followed by a plain straight leaving its to-node within 2 deg of the fillet's exit tangent; the
    /// aircraft starts on the fillet's start point on its entry tangent.
    /// </summary>
    private static (TaxiRoute Route, LatLon Start, double EntryDeg) ArcThenStraight()
    {
        AirportGroundLayout layout = Oak();
        foreach (
            GroundArc arc in layout.Arcs.Where(a => (a.Nodes[0].Id != a.Nodes[1].Id) && (a.MinRadiusOfCurvatureFt >= 40)).OrderBy(a => a.Nodes[0].Id)
        )
        {
            foreach ((GroundNode from, GroundNode to) in new[] { (arc.Nodes[0], arc.Nodes[1]), (arc.Nodes[1], arc.Nodes[0]) })
            {
                var arcSeg = new TaxiRouteSegment { Edge = arc.Directed(from, to), TaxiwayName = arc.TaxiwayName };
                var bez = (PathPrimitiveBezier)PathPrimitiveBuilder.FromSegment(arcSeg);
                double exitDeg = bez.Curve.TangentBearing(1.0);
                GroundEdge? next = layout
                    .Edges.Where(e => IsPlainTaxiway(e) && e.HasNode(to.Id) && ((e.DistanceNm * GeoMath.FeetPerNm) >= 300))
                    .FirstOrDefault(e => GeoMath.AbsBearingDifference(exitDeg, GeoMath.BearingTo(to.Position, e.OtherNode(to).Position)) <= 2.0);
                if (next is null)
                {
                    continue;
                }

                var route = new TaxiRoute
                {
                    Segments = [arcSeg, new TaxiRouteSegment { Edge = next.Directed(to, next.OtherNode(to)), TaxiwayName = next.TaxiwayName }],
                    HoldShortPoints = [],
                };
                return (route, from.Position, bez.Curve.TangentBearing(0.0));
            }
        }

        Assert.Fail("no OAK fillet hands on to a plain straight");
        throw new InvalidOperationException();
    }

    /// <summary>
    /// Two plain OAK straights, each at least 250 ft, meeting at a node deflecting at most 20 deg; the aircraft starts on
    /// the first, 200 ft short of the node.
    /// </summary>
    private static (TaxiRoute Route, LatLon Start, double BearingDeg) StraightThenStraight()
    {
        List<GroundEdge> plain =
        [
            .. Oak().Edges.Where(e => IsPlainTaxiway(e) && ((e.DistanceNm * GeoMath.FeetPerNm) >= 250)).OrderBy(e => e.Nodes[0].Id),
        ];
        foreach (GroundEdge first in plain)
        {
            GroundNode from = first.Nodes[0];
            GroundNode node = first.Nodes[1];
            double inDeg = GeoMath.BearingTo(from.Position, node.Position);
            GroundEdge? second = plain.FirstOrDefault(e => (e != first) && e.HasNode(node.Id) && (DeflectionDeg(inDeg, node, e) <= 20.0));
            if (second is null)
            {
                continue;
            }

            var route = new TaxiRoute
            {
                Segments =
                [
                    new TaxiRouteSegment { Edge = first.Directed(from, node), TaxiwayName = first.TaxiwayName },
                    new TaxiRouteSegment { Edge = second.Directed(node, second.OtherNode(node)), TaxiwayName = second.TaxiwayName },
                ],
                HoldShortPoints = [],
            };
            double lengthFt = first.DistanceNm * GeoMath.FeetPerNm;
            return (route, Offset(from.Position, inDeg, lengthFt - 200, 0), inDeg);
        }

        Assert.Fail("no pair of OAK straights meets at a shallow node");
        throw new InvalidOperationException();
    }

    private static double DeflectionDeg(double inDeg, GroundNode node, GroundEdge next) =>
        GeoMath.AbsBearingDifference(inDeg, GeoMath.BearingTo(node.Position, next.OtherNode(node).Position));

    /// <summary>One navigator tick and the physics tick after it: the segment, the result, the poses and the published target.</summary>
    private readonly record struct TickSample(
        int SegmentIndex,
        NavigatorResult Result,
        LatLon Before,
        LatLon After,
        double HeadingDeg,
        double? TargetDeg
    );

    /// <summary>A further straight of a chain: its length range (ft) and its bend range (deg) from the straight before it.</summary>
    private readonly record struct ChainLeg(double MinFt, double MaxFt, double MinBendDeg, double MaxBendDeg);

    /// <summary>
    /// Drive the rig along every segment of <paramref name="route"/>, advancing and setting up each, cleared throughout; with
    /// <paramref name="dropWalk"/>, the walk past the node is cleared after every set-up.
    /// </summary>
    private static List<TickSample> Drive(Rig rig, TaxiRoute route, int maxTicks, bool dropWalk)
    {
        var trace = new List<TickSample>();
        if (dropWalk)
        {
            rig.Nav.RouteWalkLegs = null;
        }

        for (int i = 0; (i < maxTicks) && (route.CurrentSegmentIndex < route.Segments.Count); i++)
        {
            int seg = route.CurrentSegmentIndex;
            LatLon before = rig.Aircraft.Position;
            bool last = seg == (route.Segments.Count - 1);
            NavigatorResult result = rig.Nav.Tick(rig.Ctx, isLastSegment: last, _ => rig.Cleared);
            double? target = rig.Ctx.Targets.TargetTrueHeading?.Degrees;
            if (result == NavigatorResult.ArrivedAtNode)
            {
                route.CurrentSegmentIndex++;
                if (route.CurrentSegmentIndex < route.Segments.Count)
                {
                    rig.Nav.SetupSegment(route, rig.Ctx, _ => rig.Cleared);
                    if (dropWalk)
                    {
                        rig.Nav.RouteWalkLegs = null;
                    }
                }
            }

            rig.Physics();
            trace.Add(new TickSample(seg, result, before, rig.Aircraft.Position, rig.Aircraft.TrueHeading.Degrees, target));
        }

        return trace;
    }

    /// <summary>Along-track and right-positive cross-track (ft) of <paramref name="p"/> against the line from <paramref name="origin"/>.</summary>
    private static (double AlongFt, double CrossFt) LineFrame(LatLon origin, double bearingDeg, LatLon p)
    {
        double distFt = GeoMath.DistanceNm(origin, p) * GeoMath.FeetPerNm;
        double rel = GeoMath.SignedBearingDifference(bearingDeg, GeoMath.BearingTo(origin, p)) * Math.PI / 180.0;
        return (distFt * Math.Cos(rel), distFt * Math.Sin(rel));
    }

    /// <summary>The largest cross-track (ft) from straight <paramref name="seg"/> of every pose that lies along it, past its start.</summary>
    private static double MaxOffsetPastNodeFt(List<TickSample> trace, TaxiRouteSegment seg)
    {
        DirectionalEdge edge = seg.Edge;
        double lengthFt = edge.DistanceNm * GeoMath.FeetPerNm;
        double worst = 0;
        foreach (TickSample s in trace)
        {
            (double along, double cross) = LineFrame(edge.FromNode.Position, edge.DepartureBearing, s.After);
            if ((along >= 0) && (along <= lengthFt))
            {
                worst = Math.Max(worst, Math.Abs(cross));
            }
        }

        return worst;
    }

    /// <summary>
    /// The largest turn (deg) against the bend's sense between consecutive ticks, from 150 ft before <paramref name="node"/>
    /// until the heading first reaches <paramref name="outDeg"/>.
    /// </summary>
    private static double MaxHeadingReversalDeg(List<TickSample> trace, LatLon node, double inDeg, double outDeg)
    {
        double sense = Math.Sign(GeoMath.SignedBearingDifference(inDeg, outDeg));
        double worst = 0;
        double? previous = null;
        foreach (TickSample s in trace)
        {
            if ((previous is null) && ((GeoMath.DistanceNm(s.After, node) * GeoMath.FeetPerNm) > 150))
            {
                continue;
            }

            if (previous is { } prev)
            {
                worst = Math.Max(worst, -GeoMath.SignedBearingDifference(prev, s.HeadingDeg) * sense);
            }

            if ((GeoMath.SignedBearingDifference(s.HeadingDeg, outDeg) * sense) <= 0)
            {
                break;
            }

            previous = s.HeadingDeg;
        }

        return worst;
    }

    private static string HeadingDigest(List<TickSample> trace)
    {
        byte[] bytes = [.. trace.SelectMany(s => BitConverter.GetBytes(s.HeadingDeg))];
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))[..16];
    }

    private static string RouteText(TaxiRoute route) =>
        string.Join(
            " ",
            route.Segments.Select(s => $"{s.FromNodeId}-{s.ToNodeId}({s.Edge.DistanceNm * GeoMath.FeetPerNm:F0}ft,{s.Edge.DepartureBearing:F1})")
        );

    private static (TaxiRoute Route, LatLon Start, double InDeg, string Where) RequireChain((TaxiRoute, LatLon, double, string)? found)
    {
        Assert.True(found is not null, "the selector found no matching geometry on OAK or SFO");
        return found.Value;
    }

    private static TaxiRouteSegment Seg(GroundEdge edge, GroundNode from) =>
        new() { Edge = edge.Directed(from, edge.OtherNode(from)), TaxiwayName = edge.TaxiwayName };

    /// <summary>A plain straight of at most 50 ft continuing within 3 deg of the one before it, then a 25-32 deg bend.</summary>
    private static (TaxiRoute, LatLon, double, string)? ShortCollinearThenBend() =>
        FindStraightChain(FilletMode.Standard, new ChainLeg(5, 50, 0, 3), new ChainLeg(60, 1e9, 25, 32));

    /// <summary>
    /// A chain of plain straights on OAK then SFO: a first of at least 100 ft, then one straight per <paramref name="legs"/>
    /// entry; the aircraft starts on the first, 200 ft short of its end or at its start, on its line.
    /// </summary>
    private static (TaxiRoute, LatLon, double, string)? FindStraightChain(FilletMode mode, params ChainLeg[] legs)
    {
        TestVnasData.EnsureInitialized();
        foreach (string airport in new[] { "OAK", "SFO" })
        {
            AirportGroundLayout? layout = new TestAirportGroundData(mode).GetLayout(airport);
            List<GroundEdge> plain = layout is null
                ? []
                : [.. layout.Edges.Where(IsPlainTaxiway).OrderBy(e => e.Nodes[0].Id).ThenBy(e => e.Nodes[1].Id)];
            ILookup<int, GroundEdge> byNode = plain.SelectMany(e => new[] { (e.Nodes[0].Id, e), (e.Nodes[1].Id, e) }).ToLookup(p => p.Id, p => p.e);
            foreach (GroundEdge first in plain.Where(e => (e.DistanceNm * GeoMath.FeetPerNm) >= 100))
            {
                foreach (GroundNode from in first.Nodes)
                {
                    if (Extend(byNode, [Seg(first, from)], legs) is not { } chain)
                    {
                        continue;
                    }

                    double inDeg = chain[0].Edge.DepartureBearing;
                    double lengthFt = first.DistanceNm * GeoMath.FeetPerNm;
                    var route = new TaxiRoute { Segments = chain, HoldShortPoints = [] };
                    return (route, Offset(from.Position, inDeg, Math.Max(0, lengthFt - 200), 0), inDeg, airport);
                }
            }
        }

        return null;
    }

    private static List<TaxiRouteSegment>? Extend(ILookup<int, GroundEdge> byNode, List<TaxiRouteSegment> chain, ChainLeg[] legs)
    {
        if (chain.Count > legs.Length)
        {
            return chain;
        }

        ChainLeg leg = legs[chain.Count - 1];
        DirectionalEdge last = chain[^1].Edge;
        GroundNode node = last.ToNode;
        foreach (GroundEdge e in byNode[node.Id].Where(e => chain.All(c => c.Edge.Edge != e)))
        {
            double lengthFt = e.DistanceNm * GeoMath.FeetPerNm;
            double bendDeg = DeflectionDeg(last.ArrivalBearing, node, e);
            if ((lengthFt < leg.MinFt) || (lengthFt > leg.MaxFt) || (bendDeg < leg.MinBendDeg) || (bendDeg > leg.MaxBendDeg))
            {
                continue;
            }

            if (Extend(byNode, [.. chain, Seg(e, node)], legs) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// A plain straight of at least 250 ft into a fillet tangent to it (within 5 deg), then a plain straight of at least 100 ft
    /// leaving within 5 deg of the fillet's exit tangent, on OAK then SFO; the aircraft starts 200 ft short of the fillet.
    /// </summary>
    private static (TaxiRoute, LatLon, double, string)? StraightIntoFillet()
    {
        TestVnasData.EnsureInitialized();
        foreach (string airport in new[] { "OAK", "SFO" })
        {
            AirportGroundLayout? layout = new TestAirportGroundData(FilletMode.Standard).GetLayout(airport);
            if (layout is null)
            {
                continue;
            }

            List<GroundEdge> plain = [.. layout.Edges.Where(IsPlainTaxiway).OrderBy(e => e.Nodes[0].Id).ThenBy(e => e.Nodes[1].Id)];
            foreach (GroundEdge arm in plain.Where(e => (e.DistanceNm * GeoMath.FeetPerNm) >= 250))
            {
                foreach (GroundNode from in arm.Nodes)
                {
                    TaxiRouteSegment armSeg = Seg(arm, from);
                    double armDeg = armSeg.Edge.DepartureBearing;
                    if (TangentArc(layout, armSeg.Edge.ToNode, armDeg, exits: false) is not { } arcSeg)
                    {
                        continue;
                    }

                    var bez = (PathPrimitiveBezier)PathPrimitiveBuilder.FromSegment(arcSeg);
                    double exitDeg = bez.Curve.TangentBearing(1.0);
                    GroundNode end = arcSeg.Edge.ToNode;
                    GroundEdge? exit = plain.FirstOrDefault(e =>
                        e.HasNode(end.Id) && ((e.DistanceNm * GeoMath.FeetPerNm) >= 100) && (DeflectionDeg(exitDeg, end, e) <= 5.0)
                    );
                    if (exit is null)
                    {
                        continue;
                    }

                    var route = new TaxiRoute { Segments = [armSeg, arcSeg, Seg(exit, end)], HoldShortPoints = [] };
                    double lengthFt = arm.DistanceNm * GeoMath.FeetPerNm;
                    return (route, Offset(from.Position, armDeg, lengthFt - 200, 0), armDeg, airport);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// A virtual first segment 150 ft long onto the shared node of two plain straights, its line <paramref name="offDeg"/> off
    /// the second straight's bearing, then that straight; the aircraft stands on the virtual node headed
    /// <paramref name="turnDeg"/> off the line.
    /// </summary>
    private static (Rig Rig, TaxiRoute Route, string Where) VirtualEntryRig(double offDeg, double turnDeg)
    {
        (TaxiRoute chain, LatLon _, double _, string where) = RequireChain(FindStraightChain(FilletMode.Standard, new ChainLeg(150, 1e9, 0, 20)));
        TaxiRouteSegment next = chain.Segments[1];
        GroundNode node = next.Edge.FromNode;
        double lineDeg = (next.Edge.DepartureBearing + offDeg + 360.0) % 360.0;
        LatLon from = GeoMath.ProjectPointRaw(node.Position, (lineDeg + 180.0) % 360.0, 150.0 / GeoMath.FeetPerNm);
        GroundNode virtualFrom = VirtualNode.Create(from.Lat, from.Lon);
        var route = new TaxiRoute { Segments = [VirtualNode.CreateSegment(virtualFrom, node, next.TaxiwayName), next], HoldShortPoints = [] };
        var rig = Rig.Create(
            TurnModel.Ramp,
            route,
            new RigStart(from, (lineDeg + turnDeg + 360.0) % 360.0, 10, Type, AircraftCategory.Jet),
            cleared: true
        );
        return (rig, route, where);
    }

    /// <summary>A fresh rig on a copy of <paramref name="route"/> under the ramp, driven to its end.</summary>
    private static List<TickSample> RampTrace(TaxiRoute route, LatLon start, double inDeg, bool dropWalk)
    {
        var copy = new TaxiRoute { Segments = [.. route.Segments], HoldShortPoints = [] };
        var rig = Rig.Create(TurnModel.Ramp, copy, new RigStart(start, inDeg, 15, Type, AircraftCategory.Jet), cleared: true);
        return Drive(rig, copy, 4000, dropWalk);
    }

    private static void AssertSameHeadings(string where, List<TickSample> expected, List<TickSample> actual)
    {
        Assert.True(expected.Count == actual.Count, $"{where}: {expected.Count} ticks, then {actual.Count}");
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.True(
                BitConverter.DoubleToInt64Bits(expected[i].HeadingDeg) == BitConverter.DoubleToInt64Bits(actual[i].HeadingDeg),
                $"{where}: tick {i}: {expected[i].HeadingDeg:R}, then {actual[i].HeadingDeg:R}"
            );
        }
    }

    /// <summary>
    /// The fillet and the straight after it from <see cref="StraightIntoFillet"/>, flown as an aimed line over the fillet
    /// (a restored navigator's set-up rebuilds it): the line runs 150 ft into the fillet's far end 60 deg off the straight.
    /// </summary>
    private static (Rig Rig, TaxiRoute Route, string Where) AimedLineRig()
    {
        (TaxiRoute full, LatLon _, double _, string where) = RequireChain(StraightIntoFillet());
        TaxiRouteSegment fillet = full.Segments[1];
        var route = new TaxiRoute { Segments = [fillet, full.Segments[2]], HoldShortPoints = [] };
        GroundNode end = fillet.Edge.ToNode;
        double lineDeg = (full.Segments[2].Edge.DepartureBearing + 60.0) % 360.0;
        LatLon anchor = GeoMath.ProjectPointRaw(end.Position, (lineDeg + 180.0) % 360.0, 150.0 / GeoMath.FeetPerNm);
        var rig = Rig.Create(TurnModel.Ramp, route, new RigStart(anchor, lineDeg, 15, Type, AircraftCategory.Jet), cleared: true);
        var nav = GroundNavigator.FromSnapshot(
            new GroundNavigatorDto
            {
                TargetNodeId = end.Id,
                TargetLat = end.Position.Lat,
                TargetLon = end.Position.Lon,
                SegmentFromLat = anchor.Lat,
                SegmentFromLon = anchor.Lon,
                PrevDistToTarget = double.MaxValue,
                MaxSpeedKts = 15.0,
                OnAimedLineOverFillet = true,
                AimedLineFilletFromNodeId = fillet.FromNodeId,
            }
        );
        nav.SetupSegment(route, rig.Ctx, _ => true);
        var aimed = new Rig
        {
            Aircraft = rig.Aircraft,
            Ctx = rig.Ctx,
            Nav = nav,
            Options = rig.Options,
            From = anchor,
            SegmentBearingDeg = lineDeg,
            Cleared = true,
        };
        return (aimed, route, where);
    }

    /// <summary>Where a rig's aircraft starts: its position, heading and speed, and its type and category.</summary>
    private readonly record struct RigStart(LatLon Position, double HeadingDeg, double SpeedKts, string AircraftType, AircraftCategory Category);

    /// <summary>A start on the long straight: feet along it and right of it, the heading error, the speed, type and category.</summary>
    private readonly record struct StraightStart(
        double AlongFt,
        double OffsetFt,
        double HeadingErrDeg,
        double SpeedKts,
        string AircraftType,
        AircraftCategory Category
    );

    /// <summary>A navigator on a route and a ground aircraft driven by it, one phase tick then one physics tick.</summary>
    private sealed class Rig
    {
        public required AircraftState Aircraft { get; init; }
        public required PhaseContext Ctx { get; init; }
        public required GroundNavigator Nav { get; init; }
        public required PhysicsTickOptions Options { get; init; }
        public required LatLon From { get; init; }
        public required double SegmentBearingDeg { get; init; }
        public required bool Cleared { get; init; }

        public static Rig Create(TurnModel model, TaxiRoute route, RigStart start, bool cleared)
        {
            var aircraft = new AircraftState
            {
                Callsign = "NAVRMP",
                AircraftType = start.AircraftType,
                Position = start.Position,
                TrueHeading = new TrueHeading(start.HeadingDeg),
                TrueTrack = new TrueHeading(start.HeadingDeg),
                IndicatedAirspeed = start.SpeedKts,
                Altitude = 9,
                IsOnGround = true,
            };
            var ctx = new PhaseContext
            {
                ActiveRunways = ActiveRunways.Empty,
                Aircraft = aircraft,
                Targets = aircraft.Targets,
                Category = start.Category,
                DeltaSeconds = Dt,
                Runway = null,
                FieldElevation = 0,
                GroundLayout = null,
                Logger = NullLogger.Instance,
                TurnModel = model,
            };
            var nav = new GroundNavigator { MaxSpeedKts = 15.0 };
            nav.SetupSegment(route, ctx, _ => cleared);
            DirectionalEdge edge = route.Segments[0].Edge;
            return new Rig
            {
                Aircraft = aircraft,
                Ctx = ctx,
                Nav = nav,
                Options = PhysicsTickOptions.Default with { TurnModel = model },
                From = edge.FromNode.Position,
                SegmentBearingDeg = GeoMath.BearingTo(edge.FromNode.Position, edge.ToNode.Position),
                Cleared = cleared,
            };
        }

        /// <summary>One phase tick and one physics tick; a given speed is pinned before each.</summary>
        public NavigatorResult Step(double? pinnedSpeedKts)
        {
            Pin(pinnedSpeedKts);
            NavigatorResult result = Nav.Tick(Ctx, isLastSegment: true, _ => Cleared);
            Pin(pinnedSpeedKts);
            Physics();
            return result;
        }

        public void Physics() => FlightPhysics.Update(Aircraft, Dt, aircraftLookup: null, weather: null, simTimeSeconds: 0, Options);

        /// <summary>Signed offset from the first segment's line, ft, positive right.</summary>
        public double CrossFt() =>
            GeoMath.SignedCrossTrackDistanceNm(Aircraft.Position, From, new TrueHeading(SegmentBearingDeg)) * GeoMath.FeetPerNm;

        /// <summary>Along-track distance from the first segment's start, ft.</summary>
        public double AlongFt()
        {
            double distFt = GeoMath.DistanceNm(From, Aircraft.Position) * GeoMath.FeetPerNm;
            double crossFt = CrossFt();
            return Math.Sqrt(Math.Max(0, (distFt * distFt) - (crossFt * crossFt)));
        }

        private void Pin(double? speedKts)
        {
            if (speedKts is { } kts)
            {
                Aircraft.IndicatedAirspeed = kts;
            }
        }
    }
}
