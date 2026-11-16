using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests;

/// <summary>
/// Turn leads for the ramp turn model (YAAT-2341): fly-by anticipation, intercept capture and pattern turn starts lead by
/// the roll-in lag (<see cref="TurnShape.LeadNm"/>) so a ramped aircraft does not overshoot, and lead nothing under
/// <see cref="TurnModel.Instant"/>.
/// </summary>
public class TurnLeadTests
{
    private const double Dt = 0.25;
    private const double FeetPerNm = 6076.12;

    private static readonly PhysicsTickOptions RampOptions = PhysicsTickOptions.Default with { TurnModel = TurnModel.Ramp };

    public TurnLeadTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Theory]
    [InlineData(3.0, 1.8494897427831781)]
    [InlineData(5.0, 2.1622776601683795)]
    [InlineData(10.0, 2.4721359549995796)]
    [InlineData(12.5, 2.5)]
    [InlineData(90.0, 2.5)]
    public void LeadNm_Jet250Kt_UsesHalfTheRollTime_OrTheCornerKeepingLagBelowTheFullRateTurn(double thetaDeg, double expectedLagSeconds)
    {
        // Jet: T_roll 5 s at 2.5 deg/s (alpha 0.5 deg/s^2), so a turn of 12.5 deg or more reaches the full rate; below it
        // the lag is sqrt(theta / alpha) - theta / (2 * omega).
        double leadNm = TurnShape.LeadNm(250, thetaDeg, 2.5, AircraftCategory.Jet, TurnModel.Ramp);

        Assert.Equal(250 * expectedLagSeconds / 3600.0, leadNm, 12);
    }

    [Theory]
    [InlineData(90.0, 0.0, 2.5)]
    [InlineData(90.0, 1.25, 0.625)]
    [InlineData(90.0, 2.5, 0.0)]
    [InlineData(90.0, -1.0, 2.5)]
    [InlineData(-90.0, -1.25, 0.625)]
    [InlineData(3.0, 1.25, 0.625)]
    [InlineData(3.0, 0.25, 1.8494897427831781)]
    public void LeadNm_AlreadyTurning_CoversOnlyTheRollInStillToCome(double thetaDeg, double appliedDegPerSec, double expectedLagSeconds)
    {
        // Jet, omega 2.5 deg/s, T_roll 5 s: remaining lag (omega - r)^2 * T / (2 * omega^2), capped by the turn's own lag;
        // a rate the other way counts as wings level.
        var turn = new TurnLeadInput(250, thetaDeg, 2.5, AircraftCategory.Jet, TurnModel.Ramp) { AppliedTurnRateDegPerSec = appliedDegPerSec };

        Assert.Equal(250 * expectedLagSeconds / 3600.0, TurnShape.LeadNm(turn), 12);
    }

    [Fact]
    public void LeadNm_Instant_IsZero() => Assert.Equal(0.0, TurnShape.LeadNm(250, 90, 2.5, AircraftCategory.Jet, TurnModel.Instant));

    [Theory]
    [InlineData(20.0)]
    [InlineData(30.0)]
    [InlineData(90.0)]
    public void InterceptLeadNm_Ramp_AddsTheLeadsCrossTrackComponentToTheRadius(double thetaDeg)
    {
        // Used by both the localizer capture and a departure procedure's intercept leg.
        double radiusNm = TurnShape.RadiusNm(250, 2.5);
        double leadNm = TurnShape.LeadNm(250, thetaDeg, 2.5, AircraftCategory.Jet, TurnModel.Ramp);

        double rampNm = TurnShape.InterceptLeadNm(new TurnLeadInput(250, thetaDeg, 2.5, AircraftCategory.Jet, TurnModel.Ramp));
        double instantNm = TurnShape.InterceptLeadNm(new TurnLeadInput(250, thetaDeg, 2.5, AircraftCategory.Jet, TurnModel.Instant));

        Assert.Equal(radiusNm + (leadNm * Math.Sin(thetaDeg * Math.PI / 180.0)), rampNm, 12);
        Assert.Equal(radiusNm, instantNm);
    }

    // --- Per-site decisions: the same position, the phase context on Ramp (led) vs Instant (ramp without leads) ---

    [Theory]
    [InlineData(20.0)]
    [InlineData(30.0)]
    public void InterceptCapture_InsideTheLeadBand_CapturesOnlyWithTheLead(double interceptDeg)
    {
        Assert.True(InterceptCapturesInLeadBand(interceptDeg, TurnModel.Ramp), "The led capture did not fire inside (R, R + L sin theta]");
        Assert.False(InterceptCapturesInLeadBand(interceptDeg, TurnModel.Instant), "The capture fired outside the radius without a lead");
    }

    [Fact]
    public void FinalApproachAimPoint_WithTheLead_InterceptsAtLeastOneDegreeShallower()
    {
        double ledDeg = FinalApproachInterceptAngleDeg(TurnModel.Ramp);
        double unledDeg = FinalApproachInterceptAngleDeg(TurnModel.Instant);

        Assert.True(ledDeg < unledDeg - 1.0, $"Led intercept angle {ledDeg:F2} deg, without the lead {unledDeg:F2} deg");
    }

    [Fact]
    public void BaseTurnStart_InsideTheLeadBand_StartsOnlyWithTheLead()
    {
        Assert.True(BaseTurnStartsInLeadBand(TurnModel.Ramp), "The led base-to-final turn did not start inside (R, R + L]");
        Assert.False(BaseTurnStartsInLeadBand(TurnModel.Instant), "The base-to-final turn started outside the radius without a lead");
    }

    [Fact]
    public void DownwindBaseTrigger_InsideTheLeadBand_FiresOnlyWithTheLead()
    {
        Assert.True(DownwindBaseTriggerInLeadBand(TurnModel.Ramp), "The led base trigger did not fire L earlier along the downwind");
        Assert.False(DownwindBaseTriggerInLeadBand(TurnModel.Instant), "The base trigger fired early without a lead");
    }

    [Fact]
    public void CrosswindCompressedTurn_InsideTheLeadBand_StartsOnlyWithTheLead()
    {
        Assert.True(CrosswindCompressedTurnInLeadBand(TurnModel.Ramp), "The led compressed downwind turn did not start L earlier");
        Assert.False(CrosswindCompressedTurnInLeadBand(TurnModel.Instant), "The compressed downwind turn started early without a lead");
    }

    [Fact]
    public void FlyBy_BareAnticipationInsideNavArrival_SequencesThereWithNoAnticipation_ThoughTheLedOneReachesPastIt()
    {
        var legStart = new LatLon(37.0, -122.0);
        LatLon fix = GeoMath.ProjectPoint(legStart, new TrueHeading(0), 10.0);
        AircraftState ac = MakeAircraft("B738", legStart, heading: 0, ias: 250, altitude: 1000);
        double radiusNm = TurnShape.RadiusNm(ac.GroundSpeed, AircraftPerformance.TurnRate("B738", AircraftCategory.Jet));
        double turnDeg = 2.0 * Math.Atan(0.45 / radiusNm) * 180.0 / Math.PI;
        LatLon next = GeoMath.ProjectPoint(fix, new TrueHeading(turnDeg), 10.0);
        double bareNm = FlightPhysics.ComputeAnticipationDistanceNm(FlightPhysics.FlyByTurnFor(ac, 0, turnDeg, TurnModel.Instant));
        double ledNm = FlightPhysics.ComputeAnticipationDistanceNm(FlightPhysics.FlyByTurnFor(ac, 0, turnDeg, TurnModel.Ramp));
        Assert.True((bareNm <= 0.5) && (ledNm > 0.5), $"Setup: bare {bareNm:F3} nm, led {ledNm:F3} nm around NavArrivalNm 0.5");

        (double thresholdNm, double anticipationNm) = FlightPhysics.SequencingThreshold(
            ac,
            legStart,
            new NavigationTarget { Name = "FIXA", Position = fix },
            new NavigationTarget { Name = "FIXB", Position = next },
            TurnModel.Ramp
        );

        Assert.Equal(0.5, thresholdNm);
        Assert.Equal(0.0, anticipationNm);
    }

    [Fact]
    public void DepartureIntercept_InsideTheLeadBand_CapturesOnlyWithTheLead()
    {
        Assert.Equal(1, DepartureInterceptLegIndexInLeadBand(TurnModel.Ramp));
        Assert.Equal(0, DepartureInterceptLegIndexInLeadBand(TurnModel.Instant));
    }

    /// <summary>
    /// The leg index after one tick of a heading-to-intercept leg (300) onto a 270 course, 30 degrees, from inside
    /// (R, R + L sin theta] of the course line.
    /// </summary>
    private static int DepartureInterceptLegIndexInLeadBand(TurnModel turnModel)
    {
        const double CourseDeg = 270.0;
        const double InterceptDeg = 30.0;
        var anchor = new LatLon(37.5, -122.0);
        AircraftState ac = MakeAircraft("B738", anchor, heading: CourseDeg + InterceptDeg, ias: 200, altitude: 3000);
        double turnRate = AircraftPerformance.TurnRate("B738", AircraftCategory.Jet);
        double radiusNm = TurnShape.RadiusNm(ac.GroundSpeed, turnRate);
        double bandNm =
            TurnShape.InterceptLeadNm(new TurnLeadInput(ac.GroundSpeed, InterceptDeg, turnRate, AircraftCategory.Jet, TurnModel.Ramp)) - radiusNm;
        LatLon upstream = GeoMath.ProjectPoint(anchor, new TrueHeading(CourseDeg - 180.0), 5.0);
        ac.Position = GeoMath.ProjectPoint(upstream, new TrueHeading(CourseDeg - 90.0), radiusNm + (bandNm / 2));
        var phase = new DepartureProcedurePhase
        {
            Legs =
            [
                new ProcedureLeg { Type = ProcedureLegType.HeadingToIntercept, CourseMagnetic = CourseDeg + InterceptDeg },
                new ProcedureLeg
                {
                    Type = ProcedureLegType.CourseToFix,
                    FixName = "ANCHR",
                    FixPosition = anchor,
                    CourseMagnetic = CourseDeg,
                },
            ],
            PostRoute = [],
        };
        ac.Phases = new PhaseList();
        ac.Phases.Add(phase);
        StartAndTick(phase, Ctx(ac, Rwy28(), AircraftCategory.Jet, turnModel));
        return phase.ActiveLegIndex;
    }

    [Fact]
    public void ApproachNavFlyBy_InsideTheLeadBand_SequencesOnlyWithTheLead()
    {
        Assert.Equal(1, ApproachNavFixIndexInLeadBand(TurnModel.Ramp));
        Assert.Equal(0, ApproachNavFixIndexInLeadBand(TurnModel.Instant));
    }

    private static bool InterceptCapturesInLeadBand(double interceptDeg, TurnModel turnModel)
    {
        RunwayInfo rwy = Rwy28();
        var threshold = new LatLon(rwy.ThresholdLatitude, rwy.ThresholdLongitude);
        AircraftState ac = MakeAircraft("B738", threshold, heading: rwy.TrueHeading.Degrees + interceptDeg, ias: 180, altitude: 3000);
        double turnRate = AircraftPerformance.TurnRate("B738", AircraftCategory.Jet);
        double radiusNm = TurnShape.RadiusNm(ac.GroundSpeed, turnRate);
        double bandNm =
            TurnShape.InterceptLeadNm(new TurnLeadInput(ac.GroundSpeed, interceptDeg, turnRate, AircraftCategory.Jet, TurnModel.Ramp)) - radiusNm;
        LatLon onCourse = GeoMath.ProjectPoint(threshold, rwy.TrueHeading.ToReciprocal(), 10.0);
        ac.Position = GeoMath.ProjectPoint(onCourse, new TrueHeading(rwy.TrueHeading.Degrees - 90.0), radiusNm + (bandNm / 2));
        var phase = new InterceptCoursePhase
        {
            FinalApproachCourse = rwy.TrueHeading,
            ThresholdLat = rwy.ThresholdLatitude,
            ThresholdLon = rwy.ThresholdLongitude,
            AssignedInterceptHeading = null,
        };
        ac.Phases = new PhaseList { AssignedRunway = rwy };
        ac.Phases.Add(phase);
        ac.Phases.Add(new FinalApproachPhase { SkipInterceptCheck = true });
        return StartAndTick(phase, Ctx(ac, rwy, AircraftCategory.Jet, turnModel));
    }

    private static double FinalApproachInterceptAngleDeg(TurnModel turnModel)
    {
        RunwayInfo rwy = Rwy28();
        var threshold = new LatLon(rwy.ThresholdLatitude, rwy.ThresholdLongitude);
        LatLon onCourse = GeoMath.ProjectPoint(threshold, rwy.TrueHeading.ToReciprocal(), 8.0);
        LatLon start = GeoMath.ProjectPoint(onCourse, new TrueHeading(rwy.TrueHeading.Degrees - 90.0), 0.5);
        AircraftState ac = MakeAircraft("B738", start, heading: rwy.TrueHeading.Degrees + 30.0, ias: 180, altitude: 2500);
        var phase = new FinalApproachPhase { SkipInterceptCheck = true };
        ac.Phases = new PhaseList { AssignedRunway = rwy };
        ac.Phases.Add(phase);
        StartAndTick(phase, Ctx(ac, rwy, AircraftCategory.Jet, turnModel));
        return Math.Abs(ac.Targets.TargetTrueHeading!.Value.SignedAngleTo(rwy.TrueHeading));
    }

    private static bool BaseTurnStartsInLeadBand(TurnModel turnModel)
    {
        RunwayInfo rwy = Rwy28();
        PatternWaypoints waypoints = PistonPattern(rwy);
        var threshold = new LatLon(rwy.ThresholdLatitude, rwy.ThresholdLongitude);
        AircraftState ac = MakeAircraft("C172", threshold, heading: waypoints.BaseHeading.Degrees, ias: 70, altitude: 800);
        double radiusNm = BasePhase.TurnRadiusNm(ac.GroundSpeed, AircraftCategory.Piston, waypoints);
        double leadNm = BasePhase.TurnLeadNm(ac.GroundSpeed, AircraftCategory.Piston, waypoints, TurnModel.Ramp);
        LatLon onCentreline = GeoMath.ProjectPoint(threshold, rwy.TrueHeading.ToReciprocal(), 1.0);
        ac.Position = PatternSidePoint(rwy, waypoints, onCentreline, radiusNm + (leadNm / 2));
        var phase = new BasePhase { Waypoints = waypoints };
        ac.Phases = new PhaseList { AssignedRunway = rwy, TrafficDirection = PatternDirection.Left };
        ac.Phases.Add(phase);
        ac.Phases.Add(new FinalApproachPhase { SkipInterceptCheck = true });
        return StartAndTick(phase, Ctx(ac, rwy, AircraftCategory.Piston, turnModel));
    }

    private static bool DownwindBaseTriggerInLeadBand(TurnModel turnModel)
    {
        RunwayInfo rwy = Rwy28();
        PatternWaypoints waypoints = PistonPattern(rwy);
        var baseTurn = new LatLon(waypoints.BaseTurnLat, waypoints.BaseTurnLon);
        AircraftState ac = MakeAircraft("C172", baseTurn, heading: waypoints.DownwindHeading.Degrees, ias: 80, altitude: waypoints.PatternAltitude);
        double leadNm = BasePhase.TurnLeadNm(ac.GroundSpeed, AircraftCategory.Piston, waypoints, TurnModel.Ramp);
        ac.Position = GeoMath.ProjectPoint(baseTurn, waypoints.DownwindHeading.ToReciprocal(), DownwindPhase.AlongTrackToleranceNm + (leadNm / 2));
        var phase = new DownwindPhase { Waypoints = waypoints };
        ac.Phases = new PhaseList { AssignedRunway = rwy, TrafficDirection = PatternDirection.Left };
        ac.Phases.Add(phase);
        ac.Phases.Add(new BasePhase { Waypoints = waypoints });
        return StartAndTick(phase, Ctx(ac, rwy, AircraftCategory.Piston, turnModel));
    }

    private static bool CrosswindCompressedTurnInLeadBand(TurnModel turnModel)
    {
        const double CompressedRateDegPerSec = 2.0;
        RunwayInfo rwy = Rwy28();
        PatternWaypoints waypoints = WithCompressedTurnRate(PistonPattern(rwy), CompressedRateDegPerSec);
        var crosswindTurn = new LatLon(waypoints.CrosswindTurnLat, waypoints.CrosswindTurnLon);
        AircraftState ac = MakeAircraft("C172", crosswindTurn, heading: waypoints.CrosswindHeading.Degrees, ias: 70, altitude: 700);
        double radiusNm = BasePhase.TurnRadiusNm(ac.GroundSpeed, CompressedRateDegPerSec);
        double leadNm = BasePhase.TurnLeadNm(ac.GroundSpeed, CompressedRateDegPerSec, AircraftCategory.Piston, TurnModel.Ramp);
        double outNm = waypoints.PatternSizeNm - radiusNm - (leadNm / 2);
        ac.Position = GeoMath.ProjectPoint(crosswindTurn, waypoints.CrosswindHeading, outNm);
        var phase = new CrosswindPhase { Waypoints = waypoints };
        ac.Phases = new PhaseList { AssignedRunway = rwy, TrafficDirection = PatternDirection.Left };
        ac.Phases.Add(phase);
        ac.Phases.Add(new DownwindPhase { Waypoints = waypoints });
        return StartAndTick(phase, Ctx(ac, rwy, AircraftCategory.Piston, turnModel));
    }

    private static int ApproachNavFixIndexInLeadBand(TurnModel turnModel)
    {
        const double NextLegDeg = 120.0;
        LatLon fix1 = GeoMath.ProjectPoint(new LatLon(37.0, -122.0), new TrueHeading(0), 10.0);
        LatLon fix2 = GeoMath.ProjectPoint(fix1, new TrueHeading(NextLegDeg), 5.0);
        LatLon fix3 = GeoMath.ProjectPoint(fix2, new TrueHeading(NextLegDeg), 5.0);
        AircraftState ac = MakeAircraft("B738", fix1, heading: 0, ias: 250, altitude: 5000);
        double unledNm = FlightPhysics.ComputeAnticipationDistanceNm(FlightPhysics.FlyByTurnFor(ac, 0, NextLegDeg, TurnModel.Instant));
        double ledNm = FlightPhysics.ComputeAnticipationDistanceNm(FlightPhysics.FlyByTurnFor(ac, 0, NextLegDeg, TurnModel.Ramp));
        ac.Position = GeoMath.ProjectPoint(fix1, new TrueHeading(180), (unledNm + ledNm) / 2);
        var phase = new ApproachNavigationPhase
        {
            Fixes =
            [
                new ApproachFix("FIXA", fix1.Lat, fix1.Lon),
                new ApproachFix("FIXB", fix2.Lat, fix2.Lon),
                new ApproachFix("FIXC", fix3.Lat, fix3.Lon),
            ],
        };
        ac.Phases = new PhaseList();
        ac.Phases.Add(phase);
        StartAndTick(phase, Ctx(ac, Rwy28(), AircraftCategory.Jet, turnModel));
        return phase.CurrentFixIndex;
    }

    private static bool StartAndTick(Phase phase, PhaseContext ctx)
    {
        phase.OnStart(ctx);
        return phase.OnTick(ctx);
    }

    private static RunwayInfo Rwy28() => TestRunwayFactory.Make(designator: "28", heading: 280, elevationFt: 6);

    private static PatternWaypoints PistonPattern(RunwayInfo rwy) =>
        PatternGeometry.Compute(
            new PatternGeometryRequest(rwy, AircraftCategory.Piston, "", 0, PatternDirection.Left)
            {
                SizeOverrideNm = null,
                AltitudeOverrideFt = null,
                AirportRunways = null,
                AuthoredRunway = null,
                ActiveRunways = ActiveRunways.Empty,
                Commanded = PatternCommanded.None,
            }
        );

    /// <summary>The point <paramref name="distanceNm"/> abeam <paramref name="onCentreline"/>, on the pattern's side.</summary>
    private static LatLon PatternSidePoint(RunwayInfo rwy, PatternWaypoints waypoints, LatLon onCentreline, double distanceNm)
    {
        var threshold = new LatLon(rwy.ThresholdLatitude, rwy.ThresholdLongitude);
        double patternSign = Math.Sign(
            GeoMath.SignedCrossTrackDistanceNm(new LatLon(waypoints.BaseTurnLat, waypoints.BaseTurnLon), threshold, rwy.TrueHeading)
        );
        LatLon right = GeoMath.ProjectPoint(onCentreline, new TrueHeading(rwy.TrueHeading.Degrees + 90.0), distanceNm);
        LatLon left = GeoMath.ProjectPoint(onCentreline, new TrueHeading(rwy.TrueHeading.Degrees - 90.0), distanceNm);
        return Math.Sign(GeoMath.SignedCrossTrackDistanceNm(right, threshold, rwy.TrueHeading)) == patternSign ? right : left;
    }

    private static PatternWaypoints WithCompressedTurnRate(PatternWaypoints w, double turnRateDegPerSec) =>
        new()
        {
            DepartureEndLat = w.DepartureEndLat,
            DepartureEndLon = w.DepartureEndLon,
            CrosswindTurnLat = w.CrosswindTurnLat,
            CrosswindTurnLon = w.CrosswindTurnLon,
            DownwindStartLat = w.DownwindStartLat,
            DownwindStartLon = w.DownwindStartLon,
            DownwindAbeamLat = w.DownwindAbeamLat,
            DownwindAbeamLon = w.DownwindAbeamLon,
            BaseTurnLat = w.BaseTurnLat,
            BaseTurnLon = w.BaseTurnLon,
            ThresholdLat = w.ThresholdLat,
            ThresholdLon = w.ThresholdLon,
            UpwindHeading = w.UpwindHeading,
            CrosswindHeading = w.CrosswindHeading,
            DownwindHeading = w.DownwindHeading,
            BaseHeading = w.BaseHeading,
            FinalHeading = w.FinalHeading,
            PatternAltitude = w.PatternAltitude,
            PatternSizeNm = w.PatternSizeNm,
            Direction = w.Direction,
            CompressedDownwindSpeedKts = w.CompressedDownwindSpeedKts,
            CompressedBaseSpeedKts = w.CompressedBaseSpeedKts,
            CompressedTurnRateDegPerSec = turnRateDegPerSec,
            RunwayCrossing = w.RunwayCrossing,
        };

    [Theory]
    [InlineData(25.0, 250.0, 100.0)]
    [InlineData(30.0, 250.0, 100.0)]
    [InlineData(45.0, 250.0, 100.0)]
    // The 60 deg turn measures ramp 1733 ft against instant 1484 ft (+249 ft): the arc's inbound bearing is taken from the
    // live position, so its course change shrinks through the turn and the widened arc runs wider than the instant one.
    [InlineData(60.0, 250.0, 300.0)]
    [InlineData(30.0, 210.0, 100.0)]
    public void FlyBy_SmallerTurns_RampOvershootsNoWorseThanInstantPlusTheMargin(double turnDeg, double ias, double marginFt)
    {
        double rampNm = FlyByOvershootNm(turnDeg, ias, RampOptions);
        double instantNm = FlyByOvershootNm(turnDeg, ias, PhysicsTickOptions.Default);

        Assert.True(
            (rampNm * FeetPerNm) <= (instantNm * FeetPerNm) + marginFt,
            $"{turnDeg} deg at {ias} kt: ramp overshoot {rampNm * FeetPerNm:F0} ft, instant {instantNm * FeetPerNm:F0} ft, margin {marginFt:F0} ft"
        );
    }

    /// <summary>The farthest a B738 flying a fly-by of <paramref name="turnDeg"/> right runs outside the outbound leg.</summary>
    private static double FlyByOvershootNm(double turnDeg, double ias, PhysicsTickOptions options)
    {
        var start = new LatLon(37.0, -122.0);
        LatLon wp1 = GeoMath.ProjectPoint(start, new TrueHeading(0), 10.0);
        var outbound = new TrueHeading(turnDeg);
        LatLon wp2 = GeoMath.ProjectPoint(wp1, outbound, 10.0);
        AircraftState ac = MakeAircraft("B738", start, heading: 0, ias: ias, altitude: 1000);
        ac.Targets.NavigationRoute.Add(new NavigationTarget { Name = "WP1", Position = wp1 });
        ac.Targets.NavigationRoute.Add(new NavigationTarget { Name = "WP2", Position = wp2 });
        LatLon outsidePoint = GeoMath.ProjectPoint(wp1, new TrueHeading(turnDeg - 90.0), 1.0);
        double outsideSign = Math.Sign(GeoMath.SignedCrossTrackDistanceNm(outsidePoint, wp1, outbound));

        double overshootNm = 0;
        for (int i = 0; (i < 2400) && (ac.Targets.NavigationRoute.Count > 0) && (GeoMath.DistanceNm(ac.Position, wp2) > 2.0); i++)
        {
            FlightPhysics.Update(ac, Dt, aircraftLookup: null, weather: null, simTimeSeconds: 0, options);
            if (GeoMath.AlongTrackDistanceNm(ac.Position, wp1, outbound) > 0)
            {
                overshootNm = Math.Max(overshootNm, outsideSign * GeoMath.SignedCrossTrackDistanceNm(ac.Position, wp1, outbound));
            }
        }

        Assert.True(GeoMath.DistanceNm(ac.Position, wp2) <= 2.0, "The aircraft never flew the outbound leg");
        return overshootNm;
    }

    [Fact]
    public void FlyBy_Jet250Kt_90DegTurn_OvershootsTheOutboundLegByLessThanATenth()
    {
        var start = new LatLon(37.0, -122.0);
        LatLon wp1 = GeoMath.ProjectPoint(start, new TrueHeading(0), 10.0);
        LatLon wp2 = GeoMath.ProjectPoint(wp1, new TrueHeading(90), 10.0);
        var outbound = new TrueHeading(90);
        AircraftState ac = MakeAircraft("B738", start, heading: 0, ias: 250, altitude: 1000);
        ac.Targets.NavigationRoute.Add(new NavigationTarget { Name = "WP1", Position = wp1 });
        ac.Targets.NavigationRoute.Add(new NavigationTarget { Name = "WP2", Position = wp2 });
        double outsideSign = Math.Sign(GeoMath.SignedCrossTrackDistanceNm(GeoMath.ProjectPoint(wp1, new TrueHeading(0), 1.0), wp1, outbound));

        double overshootNm = 0;
        for (int i = 0; (i < 2400) && (ac.Targets.NavigationRoute.Count > 0) && (GeoMath.DistanceNm(ac.Position, wp2) > 2.0); i++)
        {
            FlightPhysics.Update(ac, Dt, aircraftLookup: null, weather: null, simTimeSeconds: 0, RampOptions);
            overshootNm = Math.Max(overshootNm, outsideSign * GeoMath.SignedCrossTrackDistanceNm(ac.Position, wp1, outbound));
        }

        Assert.True(GeoMath.DistanceNm(ac.Position, wp2) <= 2.0, "The aircraft never flew the outbound leg");
        Assert.True(overshootNm < 0.1, $"Overshot the outbound leg by {overshootNm:F4} nm");
    }

    [Fact]
    public void LocalizerIntercept_Jet180Kt_30Deg_From3Nm_CrossesByLessThan300Ft_AndSettles()
    {
        RunwayInfo rwy = TestRunwayFactory.Make(designator: "28", heading: 280, elevationFt: 6);
        var threshold = new LatLon(rwy.ThresholdLatitude, rwy.ThresholdLongitude);
        LatLon onCourse = GeoMath.ProjectPoint(threshold, rwy.TrueHeading.ToReciprocal(), 15.0);
        LatLon start = GeoMath.ProjectPoint(onCourse, new TrueHeading(rwy.TrueHeading.Degrees - 90.0), 3.0);
        AircraftState ac = MakeAircraft("B738", start, heading: rwy.TrueHeading.Degrees + 30.0, ias: 180, altitude: 3000);
        ac.Targets.TargetTrueHeading = ac.TrueHeading;
        ac.Phases = new PhaseList { AssignedRunway = rwy };
        ac.Phases.Add(
            new InterceptCoursePhase
            {
                FinalApproachCourse = rwy.TrueHeading,
                ThresholdLat = rwy.ThresholdLatitude,
                ThresholdLon = rwy.ThresholdLongitude,
                AssignedInterceptHeading = null,
            }
        );
        ac.Phases.Add(new FinalApproachPhase { SkipInterceptCheck = true });
        PhaseContext ctx = Ctx(ac, rwy, AircraftCategory.Jet, TurnModel.Ramp);
        double farSign = -Math.Sign(GeoMath.SignedCrossTrackDistanceNm(start, threshold, rwy.TrueHeading));

        double crossedNm = 0;
        for (int i = 0; (i < 4800) && (GeoMath.DistanceNm(ac.Position, threshold) > 4.0); i++)
        {
            Step(ac, ctx);
            crossedNm = Math.Max(crossedNm, farSign * GeoMath.SignedCrossTrackDistanceNm(ac.Position, threshold, rwy.TrueHeading));
        }

        double settledNm = Math.Abs(GeoMath.SignedCrossTrackDistanceNm(ac.Position, threshold, rwy.TrueHeading));
        Assert.True(GeoMath.DistanceNm(ac.Position, threshold) <= 4.0, "The aircraft never flew down the final");
        Assert.True(crossedNm * FeetPerNm < 300, $"Crossed the course by {crossedNm * FeetPerNm:F0} ft");
        Assert.True(settledNm * FeetPerNm < 100, $"Not settled on the course: {settledNm * FeetPerNm:F0} ft off at 4 nm");
    }

    [Fact]
    public void Pattern_C172_BaseToFinalTurn_EndsWithin300FtOfTheCentreline()
    {
        RunwayInfo rwy = TestRunwayFactory.Make(designator: "28", heading: 280, elevationFt: 6);
        var threshold = new LatLon(rwy.ThresholdLatitude, rwy.ThresholdLongitude);
        PatternWaypoints waypoints = PatternGeometry.Compute(
            new PatternGeometryRequest(rwy, AircraftCategory.Piston, "", 0, PatternDirection.Left)
            {
                SizeOverrideNm = null,
                AltitudeOverrideFt = null,
                AirportRunways = null,
                AuthoredRunway = null,
                ActiveRunways = ActiveRunways.Empty,
                Commanded = PatternCommanded.None,
            }
        );
        var start = new LatLon(waypoints.BaseTurnLat, waypoints.BaseTurnLon);
        AircraftState ac = MakeAircraft("C172", start, heading: waypoints.BaseHeading.Degrees, ias: 70, altitude: 800);
        ac.Phases = new PhaseList { AssignedRunway = rwy, TrafficDirection = PatternDirection.Left };
        ac.Phases.Add(new BasePhase { Waypoints = waypoints });
        ac.Phases.Add(new FinalApproachPhase { SkipInterceptCheck = true });
        PhaseContext ctx = Ctx(ac, rwy, AircraftCategory.Piston, TurnModel.Ramp);
        double farSign = -Math.Sign(GeoMath.SignedCrossTrackDistanceNm(start, threshold, rwy.TrueHeading));

        double overshootNm = 0;
        for (int i = 0; (i < 2400) && (GeoMath.DistanceNm(ac.Position, threshold) > 0.3); i++)
        {
            Step(ac, ctx);
            overshootNm = Math.Max(overshootNm, farSign * GeoMath.SignedCrossTrackDistanceNm(ac.Position, threshold, rwy.TrueHeading));
        }

        Assert.True(GeoMath.DistanceNm(ac.Position, threshold) <= 0.3, "The aircraft never flew down the final");
        Assert.True(overshootNm * FeetPerNm < 300, $"The turn to final overshot the centreline by {overshootNm * FeetPerNm:F0} ft");
    }

    private static void Step(AircraftState ac, PhaseContext ctx)
    {
        FlightPhysics.Update(ac, Dt, aircraftLookup: null, weather: null, simTimeSeconds: 0, RampOptions);
        PhaseRunner.Tick(ac, ctx);
    }

    private static PhaseContext Ctx(AircraftState ac, RunwayInfo rwy, AircraftCategory category, TurnModel turnModel) =>
        new()
        {
            ActiveRunways = ActiveRunways.Empty,
            Aircraft = ac,
            Targets = ac.Targets,
            Category = category,
            DeltaSeconds = Dt,
            Runway = rwy,
            FieldElevation = rwy.ElevationFt,
            AircraftLookup = _ => null,
            Logger = NullLogger.Instance,
            AutoClearedToLand = true,
            TurnModel = turnModel,
        };

    private static AircraftState MakeAircraft(string type, LatLon position, double heading, double ias, double altitude) =>
        new()
        {
            Callsign = "TST200",
            AircraftType = type,
            Position = position,
            TrueHeading = new TrueHeading(heading),
            TrueTrack = new TrueHeading(heading),
            Altitude = altitude,
            IndicatedAirspeed = ias,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan { Departure = "KTEST" },
        };
}
