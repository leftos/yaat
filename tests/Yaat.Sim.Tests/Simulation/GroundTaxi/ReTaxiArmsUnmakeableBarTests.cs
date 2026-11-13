using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// YAAT-2073: a hold short a <c>TAXI</c> arms on a moving aircraft is judged where it is armed. A standalone <c>HS</c> inside the
/// firm stopping distance is flagged unable at dispatch (<c>GroundCommandHandler.MarkUnmakeableHoldShort</c>), but the same hold
/// short carried in a re-issued <c>TAXI</c> was never judged: the aircraft braked at the firm rate, came to rest past the stop and
/// reported "unable to hold short" with the bar never flagged. Owner ruling: the bar is flagged unmakeable silently, as the dirty
/// re-aim flags it (no unable call when the TAXI is read back), and the aircraft stops past it.
///
/// <para>The rig, at KOAK: a B738 taxis from GA16 via F, C, B to runway 28R, cleared across runway 33. Once across 33 it is
/// re-routed <c>TAXI C B RWY 28R</c>, and rolling east on C at taxi speed, with taxiway E's set-back stop within half its firm-rate
/// stopping distance, it is re-routed <c>TAXI C B RWY 28R HS E</c>. The guards stage the same re-route where the stop is made,
/// from rest, and the runway 33 bar a <c>TAXI C B RWY 28R</c> re-arms inside the stopping distance before the crossing.</para>
/// </summary>
public class ReTaxiArmsUnmakeableBarTests(ITestOutputHelper output)
{
    private const string Callsign = "N738SP";
    private const string Jet = "B738";
    private const string FirstTaxi = "TAXI F C B CROSS 33 RWY 28R";
    private const string BaseReRoute = "TAXI C B RWY 28R";
    private const string ReRouteWithHoldShort = "TAXI C B RWY 28R HS E";
    private const string TaxiwayTarget = "E";
    private const string CrossedRunway = "33";
    private const string RestReport = "holding position on C";
    private const int ApproachBudgetSeconds = 400;
    private const int StopBudgetSeconds = 60;
    private const int ReportSeconds = 10;

    /// <summary>The least the firm-rate stopping distance must exceed the stop by (ft) for the staging to be unmakeable beyond doubt.</summary>
    private const double UnmakeableMarginFt = 10.0;

    /// <summary>The least the stop must lie past the firm-rate stopping distance (ft) for the staging to be makeable beyond doubt.</summary>
    private const double MakeableMarginFt = 20.0;

    /// <summary>How far behind the nose (ft) the stop may lie for the at-the-nose staging: a sub-tick's travel at taxi speed.</summary>
    private const double AtNoseWindowFt = 15.0;

    /// <summary>How far ahead (ft) a stop counts as at the nose: the handler places a stop the nose has passed where it stands.</summary>
    private const double AtNoseToleranceFt = 1.0;

    /// <summary>How far (ft) past the firm-rate rest point the aircraft may come to rest for the moved-stop assertion.</summary>
    private const double RestToleranceFt = 10.0;

    /// <summary>The ground speed (kt) at or below which the aircraft counts as at rest for the from-rest staging.</summary>
    private const double AtRestKts = 0.1;

    private const string Scenario = """
        {
          "id": "01JH02G8F0L1W5T7R9N2K4X6QC",
          "name": "TAXI arming a hold short inside the firm stopping distance",
          "artccId": "ZOA",
          "primaryAirportId": "OAK",
          "studentPositionId": "01GEAMCGAZ418Q26GEPYNXWZ4A",
          "autoDeleteMode": "None",
          "initializationTriggers": [],
          "aircraftGenerators": [],
          "aircraft": [
            {
              "id": "01JH02N738SPG8F0L1W5T7R9ND",
              "aircraftId": "N738SP",
              "aircraftType": "B738",
              "transponderMode": "Standby",
              "startingConditions": { "type": "Parking", "parking": "GA16" },
              "onAltitudeProfile": false,
              "flightplan": {
                "rules": "VFR",
                "departure": "KOAK",
                "destination": "KLVK",
                "cruiseAltitude": 3500,
                "cruiseSpeed": 0,
                "route": "",
                "remarks": "/V/",
                "aircraftType": "B738"
              },
              "presetCommands": [],
              "spawnDelay": 0,
              "airportId": "OAK",
              "difficulty": "Easy"
            }
          ],
          "atc": [
            {
              "id": "01G9CQW3XEE3RW84EBYKEBCPQD",
              "artccId": "ZOA",
              "facilityId": "OAK",
              "positionId": "01GEAMB98RKCPP9HCNPW5AVDA5",
              "autoConnect": true,
              "autoTrackAirportIds": []
            }
          ],
          "flightStripConfigurations": []
        }
        """;

    /// <summary>Where a staging arms its re-route: which bar, the command, and when along the approach it is issued.</summary>
    /// <param name="RunwayBar">Whether the bar is runway 33's crossing bar on the first route rather than taxiway E's.</param>
    /// <param name="ReRoute">The TAXI that arms the bar.</param>
    /// <param name="Arms">Whether to arm at this sub-tick, given the along-route distance to the stop and the firm-rate stop (ft).</param>
    /// <param name="FromRest">Whether the aircraft is first held to a stop with <c>HOLD</c> and re-routed at rest.</param>
    private sealed record Staging(bool RunwayBar, string ReRoute, Func<double, double, bool> Arms, bool FromRest);

    /// <summary>A staged run, stepped until the aircraft holds short of the bar the re-route armed.</summary>
    private sealed record Run(
        AircraftState Aircraft,
        HoldShortPoint Bar,
        CommandResult ReRoute,
        bool UnableAtArming,
        double ArmedToStopFt,
        double FirmStopFt,
        LatLon PaintedStop,
        HoldingShortPhase? Holding,
        List<string> SpeechAfterArming
    )
    {
        public string AtArming => $"{Aircraft.Callsign} armed {ReRoute.Message}: stop {ArmedToStopFt:F1} ft ahead, firm-rate stop {FirmStopFt:F1} ft";
    }

    [Fact]
    public void TaxiHoldShortInsideFirmStoppingDistance_IsFlaggedUnableSilently()
    {
        if (Stage(new Staging(false, ReRouteWithHoldShort, UnmakeableArms, false)) is not { } run)
        {
            return;
        }

        Assert.True(
            run.ArmedToStopFt + UnmakeableMarginFt < run.FirmStopFt,
            $"{run.AtArming}: the firm rate makes the stop, so the staging no longer arms an unmakeable bar"
        );
        Assert.False(
            run.ReRoute.Message?.StartsWith("Unable", StringComparison.OrdinalIgnoreCase) ?? false,
            $"the re-route was answered '{run.ReRoute.Message}': the ruling flags the bar silently"
        );

        HoldingShortPhase holding = Assert.IsType<HoldingShortPhase>(run.Holding);
        Assert.True(run.Bar.Unable, $"taxiway {TaxiwayTarget}'s bar, armed at {run.AtArming}, was never flagged unable");
        Assert.True(run.Bar.StopMoved, $"taxiway {TaxiwayTarget}'s stop was never moved past the painted stop: {run.AtArming}");
        Assert.Equal(HoldShortArrival.BeyondAfterUnableCall, holding.Arrival);
        double pastPaintedFt = GeoMath.DistanceNm(run.Aircraft.Position, run.PaintedStop) * GeoMath.FeetPerNm;
        Assert.True(
            Math.Abs(pastPaintedFt - (run.FirmStopFt - run.ArmedToStopFt)) <= RestToleranceFt,
            $"at rest the aircraft stands {pastPaintedFt:F1} ft past the painted stop, not the firm-rate "
                + $"{run.FirmStopFt - run.ArmedToStopFt:F1} ft: {run.AtArming}"
        );
    }

    /// <summary>Non-example for the arming judge: the same re-route armed where the firm rate makes the stop is not flagged.</summary>
    [Fact]
    public void TaxiHoldShortTheFirmRateMakes_IsNotFlaggedAndHeldShort()
    {
        if (Stage(new Staging(false, ReRouteWithHoldShort, MakeableArms, false)) is not { } run)
        {
            return;
        }

        Assert.True(run.ArmedToStopFt > run.FirmStopFt, $"{run.AtArming}: the stop lies inside the firm-rate stop");
        Assert.False(run.UnableAtArming, $"{run.AtArming}: a stop the firm rate makes was flagged unable");
        HoldingShortPhase holding = Assert.IsType<HoldingShortPhase>(run.Holding);
        Assert.False(run.Bar.Unable, $"{run.AtArming}: the bar was flagged unable on the way to the stop");
        Assert.Equal(HoldShortArrival.HeldShort, holding.Arrival);
    }

    /// <summary>A hold short carried in a re-TAXI given at rest is never flagged: the stopping distance is nil.</summary>
    [Fact]
    public void TaxiHoldShortFromRest_IsNotFlaggedAndHeldShort()
    {
        static bool Arms(double toStopFt, double firmStopFt) => (toStopFt > 3.0 * firmStopFt) && (toStopFt <= 4.0 * firmStopFt);
        if (Stage(new Staging(false, ReRouteWithHoldShort, Arms, true)) is not { } run)
        {
            return;
        }

        Assert.True(run.ArmedToStopFt > 0.0, $"{run.AtArming}: the aircraft came to rest past the stop, so the staging proves nothing");
        Assert.False(run.UnableAtArming, $"{run.AtArming}: a hold short armed at rest was flagged unable");
        HoldingShortPhase holding = Assert.IsType<HoldingShortPhase>(run.Holding);
        Assert.False(run.Bar.Unable, $"{run.AtArming}: the bar was flagged unable on the way to the stop");
        Assert.Equal(HoldShortArrival.HeldShort, holding.Arrival);
    }

    /// <summary>
    /// Runway bars stay excluded: a re-TAXI that drops the crossing clearance inside the firm stopping distance of runway 33's bar
    /// keeps that bar on the last-resort dead stop, never flagged unable (hold-short-placement.md, "Inside the stopping distance
    /// without HS").
    /// </summary>
    [Fact]
    public void ReTaxiRunwayBarInsideFirmStoppingDistance_IsNeverFlaggedUnable()
    {
        if (Stage(new Staging(true, BaseReRoute, UnmakeableArms, false)) is not { } run)
        {
            return;
        }

        Assert.True(
            run.ArmedToStopFt + UnmakeableMarginFt < run.FirmStopFt,
            $"{run.AtArming}: the firm rate makes the stop, so the staging no longer arms the runway bar inside it"
        );
        Assert.True(HoldingShortPhase.ProtectsRunway(run.Bar), $"{run.AtArming}: the re-armed bar does not protect runway {CrossedRunway}");
        Assert.False(run.UnableAtArming, $"{run.AtArming}: runway {CrossedRunway}'s bar was flagged unable at arming");
        Assert.False(run.Bar.Unable, $"{run.AtArming}: runway {CrossedRunway}'s bar was flagged unable on the way to the stop");
        HoldingShortPhase holding = Assert.IsType<HoldingShortPhase>(run.Holding);
        Assert.True(
            holding.Arrival == HoldShortArrival.HeldShort,
            $"runway {CrossedRunway}'s bar was not held short at the marking (arrival {holding.Arrival}): {run.AtArming}"
        );
    }

    /// <summary>
    /// The flag is silent: the flagged re-route is answered exactly as the unflagged one, the pilot says nothing "unable" after
    /// it, and at rest reports only "holding position on C", as after the dirty re-aim. (This bare rig queues no readback text.)
    /// </summary>
    [Fact]
    public void FlaggedReTaxi_IsAnsweredAsTheUnflaggedOne_AndReportsHoldingPositionOnC()
    {
        Run? flagged = Stage(new Staging(false, ReRouteWithHoldShort, UnmakeableArms, false));
        Run? unflagged = Stage(new Staging(false, ReRouteWithHoldShort, MakeableArms, false));
        if ((flagged is null) || (unflagged is null))
        {
            return;
        }

        Assert.True(flagged.Bar.Unable, $"{flagged.AtArming}: the staging no longer flags the bar");
        Assert.False(unflagged.Bar.Unable, $"{unflagged.AtArming}: the non-example flags the bar");
        Assert.Equal(unflagged.ReRoute.Message, flagged.ReRoute.Message);
        Assert.DoesNotContain(flagged.SpeechAfterArming, s => s.Contains("unable", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(flagged.SpeechAfterArming, s => s.StartsWith(RestReport, StringComparison.Ordinal));
    }

    /// <summary>
    /// A bar whose stop is at the nose or just behind it when the re-route arms it is not flagged: the aircraft is already at or
    /// past the stop, so it is reported beyond the bar with no unable call made, as P5 of <c>HoldShortArrivalReportTests</c> is.
    /// </summary>
    [Fact]
    public void TaxiHoldShortAtTheNose_IsNotFlaggedAndReportedBeyondWithoutUnableCall()
    {
        if (
            Stage(new Staging(false, ReRouteWithHoldShort, static (toStopFt, _) => (toStopFt <= 0.0) && (toStopFt > -AtNoseWindowFt), false))
            is not { } run
        )
        {
            return;
        }

        Assert.True(
            (run.ArmedToStopFt <= AtNoseToleranceFt) && (run.ArmedToStopFt > -AtNoseWindowFt),
            $"{run.AtArming}: the stop is not at the nose, so the staging proves nothing"
        );
        Assert.False(run.UnableAtArming, $"{run.AtArming}: a stop at the nose was flagged unable");
        HoldingShortPhase holding = Assert.IsType<HoldingShortPhase>(run.Holding);
        Assert.False(run.Bar.Unable, $"{run.AtArming}: the bar was flagged unable after arming");
        Assert.Equal(HoldShortArrival.BeyondWithoutUnableCall, holding.Arrival);
    }

    /// <summary>Arms where the stop lies past the firm-rate stop by <see cref="MakeableMarginFt"/>, short of half as far again.</summary>
    private static bool MakeableArms(double toStopFt, double firmStopFt) =>
        (toStopFt > firmStopFt + MakeableMarginFt) && (toStopFt <= 1.5 * firmStopFt);

    /// <summary>Arms where the stop lies ahead of the aircraft and within half the firm-rate stop, so the bar is unmakeable.</summary>
    private static bool UnmakeableArms(double toStopFt, double firmStopFt) => (toStopFt > 0.0) && (toStopFt <= firmStopFt / 2.0);

    /// <summary>
    /// Flies the scenario to the staging's arming point, issues its re-route, and steps on until the aircraft holds short (or the
    /// budget runs out), then for <see cref="ReportSeconds"/> more. Null when the KOAK layout is unavailable.
    /// </summary>
    private Run? Stage(Staging staging)
    {
        TestVnasData.EnsureInitialized();
        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("OAK") is null)
        {
            output.WriteLine("SKIP: KOAK layout unavailable");
            return null;
        }

        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        var engine = new SimulationEngine(groundData, PushPlanStore.CreateShipped());
        engine.LoadScenario(Scenario, 1, MagneticDeclination.EvaluationDateUtc);
        Assert.IsType<SimScenarioState>(engine.Scenario).RpoShowPilotSpeech = true;
        AircraftState aircraft = Assert.IsType<AircraftState>(engine.FindAircraft(Callsign));
        var speech = new List<string>();
        engine.PilotSpeechEmitted += (who, said) =>
        {
            if (who == Callsign)
            {
                speech.Add(said.Terminal);
            }
        };

        Assert.True(engine.SendCommand(Callsign, FirstTaxi).Success, $"{FirstTaxi} was rejected");
        engine.TickOneSecond();
        var approach = new Approach(engine, aircraft, staging, Assert.IsType<AirportGroundLayout>(aircraft.Ground.Layout));
        int speechAtArming = 0;
        for (int second = 0; (second < ApproachBudgetSeconds) && (approach.Armed is null); second++)
        {
            speechAtArming = speech.Count;
            StepSecond(engine, approach.BeforeSubTick);
        }

        Assert.True(approach.Armed is not null, $"the {Jet} never reached the arming point of '{staging.ReRoute}'");
        output.WriteLine($"{approach.Armed.AtArming}, unable at arming={approach.Armed.UnableAtArming}");
        for (int second = 0; (second < StopBudgetSeconds) && (aircraft.Phases?.CurrentPhase is not HoldingShortPhase); second++)
        {
            StepSecond(engine, static () => { });
        }

        var holding = aircraft.Phases?.CurrentPhase as HoldingShortPhase;
        for (int second = 0; second < ReportSeconds; second++)
        {
            StepSecond(engine, static () => { });
        }

        Run run = approach.Armed with { Holding = holding, SpeechAfterArming = speech[speechAtArming..] };
        output.WriteLine(
            $"at rest: {holding?.Arrival}, bar Unable={run.Bar.Unable} StopMoved={run.Bar.StopMoved}, gs={aircraft.GroundSpeed:F2} kt, "
                + $"{GeoMath.DistanceNm(aircraft.Position, run.PaintedStop) * GeoMath.FeetPerNm:F1} ft past the painted stop"
        );
        output.WriteLine($"said after arming: {string.Join(" | ", run.SpeechAfterArming)}");
        return run;
    }

    /// <summary>The approach to the arming point, stepped a physics sub-tick at a time.</summary>
    private sealed class Approach
    {
        private readonly SimulationEngine _engine;
        private readonly AircraftState _aircraft;
        private readonly Staging _staging;
        private readonly AirportGroundLayout _layout;
        private readonly TaxiRoute _firstRoute;
        private readonly int _farBarSegment;
        private readonly double _firmRateKtsPerSec = CategoryPerformance.ExpediteExitDecelRate(AircraftCategory.Jet);
        private TaxiRoute? _baseRoute;
        private TaxiRoute? _probe;
        private HoldShortPoint? _probeBar;
        private bool _holdGiven;

        public Approach(SimulationEngine engine, AircraftState aircraft, Staging staging, AirportGroundLayout layout)
        {
            _engine = engine;
            _aircraft = aircraft;
            _staging = staging;
            _layout = layout;
            _firstRoute = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
            _farBarSegment = _firstRoute.Segments.FindLastIndex(segment => IsRunwayBarAt(_firstRoute, segment.ToNodeId));
            Assert.True(_farBarSegment >= 0, $"no runway {CrossedRunway} bar on {_firstRoute.ToSummary()}");
            if (staging.RunwayBar)
            {
                int nearBarSegment = _firstRoute.Segments.FindIndex(segment => IsRunwayBarAt(_firstRoute, segment.ToNodeId));
                (_probe, _probeBar) = (_firstRoute, _firstRoute.GetHoldShortAt(_firstRoute.Segments[nearBarSegment].ToNodeId));
            }
        }

        public Run? Armed { get; private set; }

        public void BeforeSubTick()
        {
            if (!CanArmNow())
            {
                return;
            }

            if (!_staging.RunwayBar && (_baseRoute is null))
            {
                ArmBaseReRouteOnceAcross33();
                return;
            }

            if (_baseRoute is not null)
            {
                _probe!.CurrentSegmentIndex = _baseRoute.CurrentSegmentIndex;
            }

            double toStopFt = TaxiingPhase.AlongRouteDistanceToHoldShortFt(_layout, _probe!, _aircraft.Position, _probeBar!);
            double firmStopFt = StoppingDistanceFt(_aircraft.GroundSpeed, _firmRateKtsPerSec);
            if (_holdGiven)
            {
                TryArmFromRest(firmStopFt);
                return;
            }

            TryArmMoving(toStopFt, firmStopFt);
        }

        /// <summary>Whether the arming point can be judged on this sub-tick: not armed yet, and still taxiing.</summary>
        private bool CanArmNow() => (Armed is null) && (_aircraft.Phases?.CurrentPhase is TaxiingPhase);

        /// <summary>Re-routes to the base route once the aircraft is past runway 33's bar, and probes taxiway E's bar on it.</summary>
        private void ArmBaseReRouteOnceAcross33()
        {
            if (_firstRoute.CurrentSegmentIndex <= _farBarSegment)
            {
                return;
            }

            Assert.True(_engine.SendCommand(Callsign, BaseReRoute).Success, $"{BaseReRoute} was rejected");
            _baseRoute = Assert.IsType<TaxiRoute>(_aircraft.Ground.AssignedTaxiRoute);
            (_probe, _probeBar) = ProbeTaxiwayBar(_layout, _baseRoute);
        }

        /// <summary>Arms the re-route once the aircraft a HOLD was given has come to rest.</summary>
        private void TryArmFromRest(double firmStopFt)
        {
            if (_aircraft.GroundSpeed <= AtRestKts)
            {
                Arm(firmStopFt);
            }
        }

        /// <summary>Arms the re-route where <see cref="Staging.Arms"/> holds, or gives HOLD first for a from-rest staging.</summary>
        private void TryArmMoving(double toStopFt, double firmStopFt)
        {
            if ((_aircraft.GroundSpeed <= 3.0) || double.IsInfinity(toStopFt) || !_staging.Arms(toStopFt, firmStopFt))
            {
                return;
            }

            if (!_staging.FromRest)
            {
                Arm(firmStopFt);
                return;
            }

            Assert.True(_engine.SendCommand(Callsign, "HOLD").Success, "HOLD was rejected");
            _holdGiven = true;
        }

        private void Arm(double firmStopFt)
        {
            CommandResult reRoute = _engine.SendCommand(Callsign, _staging.ReRoute);
            Assert.True(reRoute.Success, $"{_staging.ReRoute} was rejected: {reRoute.Message}");
            TaxiRoute route = Assert.IsType<TaxiRoute>(_aircraft.Ground.AssignedTaxiRoute);
            HoldShortPoint? armed = route.GetHoldShortAt(_probeBar!.NodeId);
            string target = _staging.RunwayBar ? CrossedRunway : TaxiwayTarget;
            Assert.True(
                (armed is { IsCleared: false }) && HoldShortAnnotator.TargetMatches(armed.TargetName, target),
                $"{_staging.ReRoute} did not hold short of {target} at node {_probeBar.NodeId}"
            );
            double armedToStopFt = TaxiingPhase.AlongRouteDistanceToHoldShortFt(_layout, route, _aircraft.Position, armed);
            Armed = new Run(_aircraft, armed, reRoute, armed.Unable, armedToStopFt, firmStopFt, PaintedStop(armed), null, []);
        }

        private static bool IsRunwayBarAt(TaxiRoute route, int nodeId) =>
            (route.GetHoldShortAt(nodeId) is { } bar) && HoldShortAnnotator.TargetMatches(bar.TargetName, CrossedRunway);
    }

    /// <summary>Taxiway E's bar as <see cref="ReRouteWithHoldShort"/> places it, worked out on a copy of <paramref name="route"/>.</summary>
    private static (TaxiRoute Probe, HoldShortPoint Bar) ProbeTaxiwayBar(AirportGroundLayout layout, TaxiRoute route)
    {
        var probe = new TaxiRoute { Segments = [.. route.Segments], HoldShortPoints = [] };
        var target = HoldShortTarget.Parse(TaxiwayTarget);
        ExplicitHoldShortPlan plan = HoldShortAnnotator.PlanExplicitHoldShort(layout, probe, target);
        Assert.Equal(ExplicitHoldShortOutcome.Add, plan.Outcome);
        HoldShortAnnotator.ApplyExplicitHoldShort(probe, plan, target);
        HoldShortAnnotator.ComputeHoldShortPositions(layout, probe, AircraftLength.ResolveFt(Jet), null, HoldShortAnnotator.CombinedName);
        return (probe, Assert.IsType<HoldShortPoint>(probe.GetHoldShortAt(plan.NodeId)));
    }

    /// <summary><paramref name="bar"/>'s painted stop, read before the taxi phase moves it to the firm-rate stop.</summary>
    private static LatLon PaintedStop(HoldShortPoint? bar)
    {
        if ((bar?.Latitude is { } lat) && (bar.Longitude is { } lon))
        {
            return new LatLon(lat, lon);
        }

        throw new InvalidOperationException($"the armed bar at node {bar?.NodeId} has no computed stop position");
    }

    private static double StoppingDistanceFt(double speedKts, double decelKtsPerSec) =>
        (speedKts * speedKts) / (2.0 * decelKtsPerSec) * (GeoMath.FeetPerNm / 3600.0);

    /// <summary>One sim-second, stepped a physics sub-tick at a time, calling <paramref name="beforeSubTick"/> before each.</summary>
    private static void StepSecond(SimulationEngine engine, Action beforeSubTick)
    {
        engine.BeginSecond();
        engine.OpenSecond(engine.BareHost);
        engine.RunPrePhysics(engine.BareHost);
        for (int sub = 0; sub < SimulationEngine.PhysicsSubTickRate; sub++)
        {
            beforeSubTick();
            engine.RunPhysicsSubTick(1.0 / SimulationEngine.PhysicsSubTickRate, sub);
        }

        engine.RunPostPhysics(engine.BareHost);
        engine.RunEndOfSecond(engine.BareHost);
    }
}
