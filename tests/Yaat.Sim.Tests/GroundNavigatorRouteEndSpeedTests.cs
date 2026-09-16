using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests;

/// <summary>
/// The speed a taxi route plans to arrive at its FINAL node with. Every ordinary taxi ends in a stop — a
/// gate, a spot, a bar the aircraft is not cleared through — so <see cref="GroundNavigator.RouteEndSpeedKts"/>
/// defaults to 0. A taxi whose destination-runway bar has already been cleared by a stored takeoff clearance
/// is the exception: braking to 2 kt at the bar and re-accelerating at the category taxi rate cost N346G ~30 s
/// of its 82 s from clearance to takeoff roll, and there was never a stop to make — the clearance was in hand
/// before the aircraft got there.
///
/// Taxi speed is integrated by physics alone (see <see cref="StandstillAccelerationProfileTests"/>); these
/// tests drive the navigator with <see cref="FlightPhysics.Update"/> in the loop for that reason.
/// </summary>
[Collection("NavDbMutator")]
public class GroundNavigatorRouteEndSpeedTests(ITestOutputHelper output)
{
    /// <summary>The shipping sim runs four physics sub-ticks per second.</summary>
    private const double SubTick = 0.25;

    /// <summary>Node 835 — the 28R hold-short bar on SFO taxiway E.</summary>
    private static readonly LatLon BarPose = new(37.622192, -122.375736);

    /// <summary>On taxiway E, short of the 28R bar and pointing at it: the last straight of N346G's taxi.</summary>
    private static readonly LatLon ApproachPose = new(37.622442, -122.375395);

    private const double ApproachHeadingDeg = 225.3;

    /// <summary>Where N346G stood when it was cleared for takeoff — the head of the ramp lane onto taxiway E.</summary>
    private static readonly LatLon RouteStartPose = new(37.622831, -122.375626);

    /// <summary>Above this the aircraft is flowing through the bar rather than creeping up to it.</summary>
    private const double FlowingIasKts = 5.0;

    /// <summary>A Heavy: barred from a rolling takeoff by 7110.65 §3-9-6.c, so its line-up must end in a stop.</summary>
    private const string HeavyType = "B77W";

    /// <summary><see cref="TakeoffPhase.Name"/> — the phase whose arrival ends a run.</summary>
    private const string TakeoffPhaseName = "Takeoff";

    /// <summary>
    /// Speed (kts) below which the first second of a takeoff roll is one that began from a standstill. The
    /// engine's bare test tick is a whole second and the line-up's stop, the pre-satisfied LUAW and the first
    /// sub-tick of the roll all land inside one of them, so the standstill is read off the roll itself: the
    /// ground roll only ever accelerates (<see cref="GroundRollProfile"/> — a jet 1.0 kt/s off idle), so a
    /// sub-knot speed on the first second of the roll is an aircraft that was at rest a fraction of a second
    /// earlier. A rolling hand-off enters the roll an order of magnitude faster — 10 kt or more, which is what
    /// <see cref="TaxiingPhase_WithStoredTakeoffClearance_ReachesTheBarRolling"/> pins.
    /// </summary>
    private const double StandingStartIasKts = 1.5;

    /// <summary>
    /// Distance-to-bar (ft) at which the late-cancel test issues <c>CTOC</c>. Inside the ~42 ft a piston needs
    /// to brake 10 kt to a stop (<see cref="CategoryPerformance.TaxiDecelRate"/> = 2 kt/s), so the cancel lands
    /// in the worst window the flow-through creates.
    /// </summary>
    private const double LateCancelDistFt = 40.0;

    /// <summary>
    /// Slack (ft) allowed against the bar's own distance from the centerline before an aircraft counts as past
    /// the holding position — the same margin <c>SfoCtoIntersectionDepartureE2ETests</c> uses, covering the
    /// difference between the bar node and the aircraft's centroid.
    /// </summary>
    private const double PastBarToleranceFt = 5.0;

    // -------------------------------------------------------------------------
    // Navigator level
    // -------------------------------------------------------------------------

    /// <summary>
    /// The default: the navigator plans the route's last node as a stop and the aircraft arrives at rest.
    /// Every caller but the cleared-for-takeoff taxi wants this — a gate, a spot, the far side of a crossing.
    /// </summary>
    [Fact]
    public void RouteEndSpeedZero_ArrivesStopped()
    {
        if (!TryBuildApproachRoute(out var route))
        {
            return;
        }

        var arrival = DriveRoute(route, routeEndSpeedKts: 0);

        Assert.NotNull(arrival);
        Assert.True(arrival.IasKts < FlowingIasKts, $"the aircraft arrived at {arrival.IasKts:F2} kt — the route end must be a stop");
        Assert.True(
            arrival.CommandedKts < FlowingIasKts,
            $"the navigator was still commanding {arrival.CommandedKts:F2} kt at the route's last node — the route end must be a stop"
        );
        Assert.True(
            arrival.IasKts < arrival.PeakIasKts * 0.5,
            $"the aircraft arrived at {arrival.IasKts:F2} kt off a {arrival.PeakIasKts:F2} kt peak — it never braked for the route end"
        );
    }

    /// <summary>
    /// With a non-zero route-end speed the aircraft flows through the last node instead of braking to it: the
    /// speed the lineup takes over at, so the hand-off costs no re-acceleration.
    /// </summary>
    [Fact]
    public void RouteEndSpeedNonZero_PassesTheTerminalNodeRolling()
    {
        if (!TryBuildApproachRoute(out var route))
        {
            return;
        }

        double routeEnd = CategoryPerformance.TaxiCornerSpeed(AircraftCategory.Piston);
        var arrival = DriveRoute(route, routeEndSpeedKts: routeEnd);

        Assert.NotNull(arrival);
        Assert.True(
            arrival.IasKts > FlowingIasKts,
            $"the aircraft arrived at {arrival.IasKts:F2} kt with a {routeEnd:F1} kt route-end speed — it still braked to the node"
        );
    }

    /// <summary>
    /// A real multi-segment SFO route ending at the 28R bar node on taxiway E: the last stretch of N346G's taxi.
    /// </summary>
    private bool TryBuildApproachRoute(out TaxiRoute route)
    {
        route = null!;

        TestVnasData.EnsureInitialized();
        var layout = new TestAirportGroundData().GetLayout("SFO");
        if (layout is null)
        {
            output.WriteLine("SKIP: SFO ground layout not available");
            return false;
        }

        var start = NearestNode(layout, RouteStartPose);
        var bar = NearestNode(layout, BarPose);
        if (start is null || bar is null || start.Id == bar.Id)
        {
            output.WriteLine("SKIP: SFO layout has no taxiway E nodes at the expected positions");
            return false;
        }

        var found = TaxiPathfinder.FindRoute(layout, start.Id, bar.Id, AircraftCategory.Piston);
        if (found is null || found.Segments.Count < 2)
        {
            output.WriteLine($"SKIP: no multi-segment route from node {start.Id} to node {bar.Id} ({found?.Segments.Count ?? 0} segments)");
            return false;
        }

        output.WriteLine(
            $"route node {start.Id} -> node {bar.Id}: {found.Segments.Count} segments, "
                + $"{found.Segments.Sum(s => s.Edge.DistanceNm) * GeoMath.FeetPerNm:F0} ft"
        );
        route = found;
        return true;
    }

    private static GroundNode? NearestNode(AirportGroundLayout layout, LatLon pos)
    {
        GroundNode? best = null;
        double bestNm = double.MaxValue;
        foreach (var n in layout.Nodes.Values)
        {
            double d = GeoMath.DistanceNm(pos, n.Position);
            if (d < bestNm)
            {
                bestNm = d;
                best = n;
            }
        }

        return best;
    }

    /// <summary>
    /// What the navigator had done by the time it reached the route's last node: the speed it was commanding,
    /// the speed the aircraft was actually doing, and the peak it reached along the way. The navigator's
    /// arrival fires 1.8 ft out, where a braked-to-a-stop aircraft is still rolling at a walking pace — the
    /// standstill itself is imposed by <see cref="TaxiingPhase.OnEnd"/> — so the stop shows up as a near-zero
    /// commanded speed off a much higher peak, not as a zero IAS.
    /// </summary>
    private sealed record RouteEndArrival(double IasKts, double CommandedKts, double PeakIasKts);

    /// <summary>
    /// Drive <paramref name="route"/> through a navigator from its first node at taxi speed, advancing segments
    /// the way <see cref="TaxiingPhase"/> does. Returns the state on arrival at the last node, or null if it
    /// never got there.
    /// </summary>
    private RouteEndArrival? DriveRoute(TaxiRoute route, double routeEndSpeedKts)
    {
        var first = route.Segments[0];
        var aircraft = new AircraftState
        {
            Callsign = "NAVEND",
            AircraftType = "BE36",
            Position = first.Edge.FromNode.Position,
            TrueHeading = new TrueHeading(first.Edge.DepartureBearing),
            TrueTrack = new TrueHeading(first.Edge.DepartureBearing),
            IndicatedAirspeed = 0,
            IsOnGround = true,
        };
        var ctx = new PhaseContext
        {
            Aircraft = aircraft,
            Targets = aircraft.Targets,
            Category = AircraftCategory.Piston,
            DeltaSeconds = SubTick,
            Runway = null,
            FieldElevation = 0,
            GroundLayout = null,
            Logger = NullLogger.Instance,
        };

        var nav = new GroundNavigator { MaxSpeedKts = CategoryPerformance.TaxiSpeed(AircraftCategory.Piston), RouteEndSpeedKts = routeEndSpeedKts };
        nav.SetupSegment(route, ctx, _ => true);

        double peakIas = 0;
        const int maxSubTicks = (int)(600 / SubTick);
        for (int k = 0; k < maxSubTicks; k++)
        {
            bool isLastSegment = route.CurrentSegmentIndex + 1 >= route.Segments.Count;
            if (nav.Tick(ctx, isLastSegment, _ => true) == NavigatorResult.ArrivedAtNode)
            {
                if (isLastSegment)
                {
                    var arrival = new RouteEndArrival(aircraft.IndicatedAirspeed, ctx.Targets.TargetSpeed ?? 0, peakIas);
                    output.WriteLine(
                        $"RouteEndSpeedKts={routeEndSpeedKts:F1}: arrived at the last node at {arrival.IasKts:F2} kt "
                            + $"commanding {arrival.CommandedKts:F2} kt (peak {peakIas:F2} kt)"
                    );
                    return arrival;
                }

                route.CurrentSegmentIndex++;
                nav.SetupSegment(route, ctx, _ => true);
            }

            FlightPhysics.Update(aircraft, ctx.DeltaSeconds);
            peakIas = Math.Max(peakIas, aircraft.IndicatedAirspeed);
        }

        output.WriteLine($"RouteEndSpeedKts={routeEndSpeedKts:F1}: never reached the route's last node (peak {peakIas:F2} kt)");
        return null;
    }

    // -------------------------------------------------------------------------
    // TaxiingPhase arming
    // -------------------------------------------------------------------------

    /// <summary>
    /// A takeoff clearance stored during the taxi arms the flow-through: the aircraft reaches its
    /// destination-runway bar still rolling, because the bar it was told to stop at has been cleared.
    /// </summary>
    [Fact]
    public void TaxiingPhase_WithStoredTakeoffClearance_ReachesTheBarRolling()
    {
        var run = RunTaxiToBar(new TaxiRunSpec(AircraftType: "BE36", ClearForTakeoff: true, CancelAtDistToBarFt: null, MaxSeconds: 60));
        if (run is null)
        {
            return;
        }

        var arrival = run.LastTaxiing;
        Assert.NotNull(arrival);
        Assert.True(
            arrival.IasKts > FlowingIasKts,
            $"the aircraft reached the 28R bar at {arrival.IasKts:F2} kt ({arrival.DistToBarFt:F0} ft out) — "
                + "a stored takeoff clearance must not leave a stop planned at the bar"
        );
    }

    /// <summary>
    /// No clearance, no flow-through: the bar is a stop and the aircraft arrives at rest. This is the
    /// runway-incursion guard the route-end speed must never weaken.
    /// </summary>
    [Fact]
    public void TaxiingPhase_WithoutClearance_StopsAtTheBar()
    {
        var run = RunTaxiToBar(new TaxiRunSpec(AircraftType: "BE36", ClearForTakeoff: false, CancelAtDistToBarFt: null, MaxSeconds: 45));
        if (run is null)
        {
            return;
        }

        var final = run.Samples[^1];
        Assert.True(
            final.IasKts < 0.5,
            $"the aircraft was still doing {final.IasKts:F2} kt at the 28R bar ({final.DistToBarFt:F0} ft out, "
                + $"phase={final.Phase}) — an uncleared destination-runway bar is a stop"
        );
        Assert.True(
            final.DistToBarFt < 120.0,
            $"the aircraft came to rest {final.DistToBarFt:F0} ft short of the 28R bar — it never reached the hold-short"
        );
    }

    /// <summary>
    /// A Heavy flows through the bar like everything else — the flow-through has no weight-class gate, and it
    /// needs none: 7110.65 §3-9-6.c bans the rolling <em>takeoff</em>, and an aircraft that crosses the bar
    /// rolling and then stops in position has not performed one. What §3-9-6.c does require is that the takeoff
    /// roll begins from a standing start, so the wake-turbulence separation timers the controller runs off the
    /// power application stay valid. The stop comes from the line-up, which for a Heavy is planned non-rolling
    /// (<see cref="LineUpPhase.IsAircraftEligibleForRollingTakeoff"/> refuses it), and the margin that makes it
    /// is geometry-dependent — so pin the standstill itself (<see cref="StandingStartIasKts"/>), not merely the
    /// phase order.
    /// </summary>
    [Fact]
    public void TaxiingPhase_HeavyClearedForTakeoff_ComesToAStopBeforeTheTakeoffRoll()
    {
        Assert.False(
            LineUpPhase.IsAircraftEligibleForRollingTakeoff(HeavyType),
            $"{HeavyType} must be Heavy for this test to mean anything (7110.65 §3-9-6.c)"
        );

        var run = RunTaxiToBar(new TaxiRunSpec(AircraftType: HeavyType, ClearForTakeoff: true, CancelAtDistToBarFt: null, MaxSeconds: 180));
        if (run is null)
        {
            return;
        }

        int takeoffIdx = run.Samples.FindIndex(s => s.Phase == TakeoffPhaseName);
        Assert.True(takeoffIdx >= 0, $"{HeavyType} never reached the takeoff roll (last phase={run.Samples[^1].Phase})");

        var rollStart = run.Samples[takeoffIdx];
        output.WriteLine(
            $"{HeavyType} entered the takeoff roll at t={rollStart.Second}s doing {rollStart.IasKts:F2} kt "
                + $"(the second before: {run.Samples[takeoffIdx - 1].IasKts:F2} kt, phase={run.Samples[takeoffIdx - 1].Phase})"
        );
        Assert.True(
            rollStart.IasKts < StandingStartIasKts,
            $"{HeavyType} was already doing {rollStart.IasKts:F2} kt on the first second of its takeoff roll (t={rollStart.Second}s) — "
                + "the roll did not begin from a standstill, so it is the rolling takeoff 7110.65 §3-9-6.c bars a Heavy from "
                + $"(phases: {string.Join(" -> ", run.Samples.Take(takeoffIdx + 1).Select(s => s.Phase).Distinct())})"
        );
    }

    /// <summary>
    /// A cancelled takeoff clearance re-arms the stop at the bar, and the aircraft must still make it: 7110.65
    /// §3-9-11 lets the controller cancel a takeoff clearance, and §3-9-4.k requires the hold instruction on any
    /// amendment issued to an aircraft holding short precisely so the amendment cannot end in an inadvertent
    /// runway entry. The CTOC here arrives with less than the stopping distance nominally remaining (a piston
    /// brakes at <see cref="CategoryPerformance.TaxiDecelRate"/> = 2 kt/s, so 10 kt needs ~42 ft), which is the
    /// worst case the flow-through creates.
    /// </summary>
    [Fact]
    public void TaxiingPhase_TakeoffClearanceCancelledApproachingTheBar_DoesNotCrossTheHoldingPosition()
    {
        var run = RunTaxiToBar(new TaxiRunSpec(AircraftType: "BE36", ClearForTakeoff: true, CancelAtDistToBarFt: LateCancelDistFt, MaxSeconds: 60));
        if (run is null)
        {
            return;
        }

        Assert.NotNull(run.CancelledAtDistToBarFt);
        double deepestCrossFt = run.Samples.Min(s => s.CrossFt);
        var final = run.Samples[^1];

        output.WriteLine(
            $"CTOC at {run.CancelledAtDistToBarFt:F0} ft from the bar; deepest cross-track {deepestCrossFt:F1} ft "
                + $"vs the bar's {run.BarCrossFt:F1} ft (margin {deepestCrossFt - run.BarCrossFt:F1} ft); "
                + $"settled at {final.IasKts:F2} kt, {final.DistToBarFt:F0} ft from the bar, phase={final.Phase}"
        );

        Assert.True(
            deepestCrossFt > run.BarCrossFt - PastBarToleranceFt,
            $"the aircraft got {deepestCrossFt:F1} ft from the 28R centerline after the clearance was cancelled "
                + $"{run.CancelledAtDistToBarFt:F0} ft from the bar, which sits {run.BarCrossFt:F1} ft out — "
                + "a cancelled takeoff clearance must not end in a runway incursion (7110.65 §3-9-11, §3-9-4.k)"
        );
        Assert.True(
            final.IasKts < 0.5,
            $"the aircraft was still doing {final.IasKts:F2} kt {final.DistToBarFt:F0} ft from the bar (phase={final.Phase}) — "
                + "a cancelled takeoff clearance re-arms the stop at the bar"
        );
    }

    /// <summary>One taxi run to the 28R bar: which aircraft, whether it gets a takeoff clearance two seconds in,
    /// whether that clearance is cancelled again (null = never; otherwise the distance-to-bar in feet at which
    /// <c>CTOC</c> is issued), and how many seconds to run.</summary>
    private sealed record TaxiRunSpec(string AircraftType, bool ClearForTakeoff, double? CancelAtDistToBarFt, int MaxSeconds);

    /// <summary>One second of a taxi run: the phase, the speed, the distance to the bar node and the distance
    /// from the 28R centerline (the measure a runway incursion shows up in).</summary>
    private sealed record TaxiSample(int Second, string Phase, double IasKts, double DistToBarFt, double CrossFt);

    /// <summary>
    /// What a taxi run did, second by second, plus the distance at which the clearance was cancelled (null when
    /// it never was) and the bar's own distance from the centerline to measure incursions against.
    /// </summary>
    private sealed record TaxiRun(List<TaxiSample> Samples, double? CancelledAtDistToBarFt, double BarCrossFt)
    {
        /// <summary>
        /// The last second the aircraft was still taxiing — the speed it arrives at the bar with.
        /// <see cref="TaxiingPhase.OnEnd"/> hands the speed on to the line-up, but the phase after the hand-off
        /// owns it from then on, so the arrival has to be read while the taxi is still running.
        /// </summary>
        public TaxiSample? LastTaxiing => Samples.LastOrDefault(s => s.Phase == "Taxiing");
    }

    /// <summary>
    /// Taxi one aircraft the last stretch of SFO taxiway E to the 28R bar per <paramref name="spec"/>, and
    /// record every second of it. Returns null when the fixture data is missing.
    /// </summary>
    private TaxiRun? RunTaxiToBar(TaxiRunSpec spec)
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            output.WriteLine("SKIP: navdata not available");
            return null;
        }

        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("SFO") is null)
        {
            output.WriteLine("SKIP: SFO ground layout not available");
            return null;
        }

        var runway = TestVnasData.NavigationDb.GetRunway("KSFO", "28R");
        if (runway is null)
        {
            output.WriteLine("SKIP: KSFO 28R not in navdata");
            return null;
        }

        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        var engine = new SimulationEngine(groundData);

        int headingMag = (int)
            Math.Round(
                MagneticDeclination.TrueToMagnetic(ApproachHeadingDeg, ApproachPose.Lat, ApproachPose.Lon, MagneticDeclination.EvaluationDateUtc)
            );
        foreach (
            string warning in engine.LoadScenario(
                BuildScenarioJson(headingMag, spec.AircraftType),
                rngSeed: 42,
                sessionStartUtc: MagneticDeclination.EvaluationDateUtc
            )
        )
        {
            output.WriteLine($"[WARN] {warning}");
        }

        var samples = new List<TaxiSample>();
        double? cancelledAtFt = null;
        bool cleared = false;

        for (int t = 1; t <= spec.MaxSeconds; t++)
        {
            engine.TickOneSecond();
            var ac = engine.FindAircraft("TEST1");
            if (ac is null)
            {
                break;
            }

            if (spec.ClearForTakeoff && !cleared && t >= 2)
            {
                cleared = ClearForTakeoff(engine, ac, t);
            }

            double distFt = GeoMath.DistanceNm(ac.Position, BarPose) * GeoMath.FeetPerNm;
            double crossFt = CrossFt(ac.Position, runway);
            string phase = ac.Phases?.CurrentPhase?.Name ?? "(none)";
            samples.Add(new TaxiSample(t, phase, ac.IndicatedAirspeed, distFt, crossFt));
            output.WriteLine($"[t={t}] phase={phase} ias={ac.IndicatedAirspeed:F2}kt distToBar={distFt:F0}ft cross={crossFt:F0}ft");

            if (
                cleared
                && (cancelledAtFt is null)
                && (spec.CancelAtDistToBarFt is { } trigger)
                && (distFt <= trigger)
                && (ac.IndicatedAirspeed > FlowingIasKts)
            )
            {
                var cancel = engine.SendCommand("TEST1", "CTOC");
                output.WriteLine($"[t={t}] CTOC at {distFt:F0}ft out, {ac.IndicatedAirspeed:F2}kt: success={cancel.Success} {cancel.Message}");
                Assert.True(cancel.Success, $"CTOC approaching the bar should succeed: {cancel.Message}");
                cancelledAtFt = distFt;
            }

            if (phase == TakeoffPhaseName)
            {
                break;
            }
        }

        Assert.NotEmpty(samples);
        return new TaxiRun(samples, cancelledAtFt, CrossFt(BarPose, runway));
    }

    /// <summary>
    /// Clear the taxiing aircraft for takeoff and pin the shape the flow-through keys on: the route's last
    /// hold-short is its destination-runway bar and it is the route's final node. Returns true.
    /// </summary>
    private bool ClearForTakeoff(SimulationEngine engine, AircraftState ac, int second)
    {
        var result = engine.SendCommand("TEST1", "CTO");
        output.WriteLine($"[t={second}] CTO: success={result.Success} {result.Message}");
        Assert.True(result.Success, $"CTO during the taxi should succeed: {result.Message}");

        var route = ac.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);
        var lastHs = route.HoldShortPoints.Count > 0 ? route.HoldShortPoints[^1] : null;
        Assert.NotNull(lastHs);
        Assert.Equal(HoldShortReason.DestinationRunway, lastHs.Reason);
        Assert.Equal(route.Segments[^1].ToNodeId, lastHs.NodeId);
        return true;
    }

    /// <summary>Distance (ft) of a position from the 28R centerline — the measure a runway incursion shows up in.</summary>
    private static double CrossFt(LatLon pos, RunwayInfo runway) =>
        Math.Abs(GeoMath.SignedCrossTrackDistanceNm(pos.Lat, pos.Lon, runway.ThresholdLatitude, runway.ThresholdLongitude, runway.TrueHeading))
        * GeoMath.FeetPerNm;

    /// <summary>One aircraft on SFO taxiway E short of the 28R bar, with one preset taxi to it.</summary>
    private static string BuildScenarioJson(int headingMag, string aircraftType)
    {
        return $$"""
            {
              "id": "test-route-end-speed",
              "name": "Route End Speed Test",
              "artccId": "ZOA",
              "primaryAirportId": "SFO",
              "initializationTriggers": [],
              "aircraftGenerators": [],
              "aircraft": [
                {
                  "id": "test-route-end-speed-1",
                  "aircraftId": "TEST1",
                  "aircraftType": "{{aircraftType}}",
                  "transponderMode": "Standby",
                  "startingConditions": {
                    "type": "Coordinates",
                    "coordinates": {"lat": {{ApproachPose.Lat}}, "lon": {{ApproachPose.Lon}}},
                    "heading": {{headingMag}}
                  },
                  "onAltitudeProfile": false,
                  "flightplan": {
                    "rules": "IFR",
                    "departure": "KSFO",
                    "destination": "KLAX",
                    "cruiseAltitude": 8000,
                    "cruiseSpeed": 170,
                    "route": "",
                    "remarks": "",
                    "aircraftType": "{{aircraftType}}/G"
                  },
                  "presetCommands": [
                    {"id": "p1", "command": "TAXI E RWY 28R", "timeOffset": 0}
                  ],
                  "spawnDelay": 0,
                  "airportId": "SFO",
                  "difficulty": "Easy"
                }
              ],
              "atc": [],
              "studentPositionId": "",
              "autoDeleteMode": "Parked",
              "flightStripConfigurations": []
            }
            """;
    }
}
