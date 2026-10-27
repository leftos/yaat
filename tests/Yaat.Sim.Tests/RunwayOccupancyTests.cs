using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.LiveTraffic;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests;

/// <summary>
/// <see cref="RunwayOccupancy"/> classifies every aircraft against a runway from geometry, with phase
/// evidence taking precedence so phase-driven aircraft keep the answers the runway consumers gave
/// before the classifier existed.
/// </summary>
public class RunwayOccupancyTests
{
    private const double ElevationFt = 100;
    private const double PavementLengthFt = 10_000;

    private static readonly RunwayInfo Runway = MakeRunway();

    public RunwayOccupancyTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private static RunwayInfo MakeRunway()
    {
        var threshold = new LatLon(37.0, -122.0);
        LatLon end = GeoMath.ProjectPoint(threshold, new TrueHeading(280), PavementLengthFt / GeoMath.FeetPerNm);
        return TestRunwayFactory.Make(
            designator: "28",
            thresholdLat: threshold.Lat,
            thresholdLon: threshold.Lon,
            endLat: end.Lat,
            endLon: end.Lon,
            heading: 280,
            elevationFt: ElevationFt,
            widthFt: 150
        );
    }

    private static LatLon Threshold => new(Runway.ThresholdLatitude, Runway.ThresholdLongitude);

    /// <summary>A point <paramref name="alongFt"/> down the runway from the threshold, offset <paramref name="rightFt"/> to the right.</summary>
    private static LatLon OnRunway(double alongFt, double rightFt)
    {
        LatLon along = GeoMath.ProjectPoint(Threshold, Runway.TrueHeading, alongFt / GeoMath.FeetPerNm);
        return rightFt == 0 ? along : GeoMath.ProjectPoint(along, Runway.TrueHeading + 90, rightFt / GeoMath.FeetPerNm);
    }

    private static LatLon OnFinal(double distanceNm, double crossTrackNm)
    {
        LatLon along = GeoMath.ProjectPoint(Threshold, Runway.TrueHeading.ToReciprocal(), distanceNm);
        return crossTrackNm == 0 ? along : GeoMath.ProjectPoint(along, Runway.TrueHeading + 90, crossTrackNm);
    }

    private static AircraftState Ground(LatLon position, double headingDeg, double groundSpeedKts, Phase? phase, RunwayInfo? assignedRunway)
    {
        var ac = new AircraftState
        {
            Callsign = "TEST",
            AircraftType = "B738",
            Position = position,
            TrueHeading = new TrueHeading(headingDeg),
            TrueTrack = new TrueHeading(headingDeg),
            Altitude = ElevationFt,
            IndicatedAirspeed = groundSpeedKts,
            IsOnGround = true,
        };
        AttachPhase(ac, phase, assignedRunway);
        return ac;
    }

    private static AircraftState Airborne(LatLon position, double trackDeg, double altitudeFt, double verticalSpeedFpm, string type)
    {
        return new AircraftState
        {
            Callsign = "TEST",
            AircraftType = type,
            Position = position,
            TrueHeading = new TrueHeading(trackDeg),
            TrueTrack = new TrueHeading(trackDeg),
            Altitude = altitudeFt,
            IndicatedAirspeed = 130,
            VerticalSpeed = verticalSpeedFpm,
            IsOnGround = false,
        };
    }

    /// <summary>An airborne rotorcraft at a hover or air taxi: heading and track are independent, speed is explicit.</summary>
    private static AircraftState Rotorcraft(
        LatLon position,
        double headingDeg,
        double trackDeg,
        double aglFt,
        double verticalSpeedFpm,
        double groundSpeedKts
    )
    {
        return new AircraftState
        {
            Callsign = "TEST",
            AircraftType = "EC35",
            Position = position,
            TrueHeading = new TrueHeading(headingDeg),
            TrueTrack = new TrueHeading(trackDeg),
            Altitude = ElevationFt + aglFt,
            IndicatedAirspeed = groundSpeedKts,
            VerticalSpeed = verticalSpeedFpm,
            IsOnGround = false,
        };
    }

    private static void AttachPhase(AircraftState ac, Phase? phase, RunwayInfo? assignedRunway)
    {
        if (phase is null && assignedRunway is null)
        {
            return;
        }

        ac.Phases = new PhaseList { AssignedRunway = assignedRunway };
        if (phase is not null)
        {
            ac.Phases.Add(phase);
            ac.Phases.CurrentPhase!.Status = PhaseStatus.Active;
        }
    }

    private static RunwayUseKind? Kind(AircraftState ac) => RunwayOccupancy.Classify(ac, Runway, layout: null)?.Kind;

    // --- Phase evidence -------------------------------------------------------------------------

    [Fact]
    public void LinedUpAndWaiting_IsOnSurface_EvenWhenGeometryDisagrees()
    {
        // Holding at a far-side taxiway node 500 ft off the centerline: the phase says it is on the runway.
        AircraftState ac = Ground(OnRunway(3000, 500), 280, 0, new LinedUpAndWaitingPhase(), Runway);

        Assert.Equal(RunwayUseKind.OnSurface, Kind(ac));
    }

    [Fact]
    public void TakeoffPhase_IsDeparting()
    {
        AircraftState ac = Ground(OnRunway(1500, 0), 280, 90, new TakeoffPhase(), Runway);

        Assert.Equal(RunwayUseKind.Departing, Kind(ac));
    }

    [Fact]
    public void TouchAndGoOnTheGround_IsOnSurface_NotDeparting()
    {
        AircraftState ac = Ground(OnRunway(2500, 0), 280, 70, new TouchAndGoPhase(), Runway);

        Assert.Equal(RunwayUseKind.OnSurface, Kind(ac));
    }

    /// <summary>
    /// 7110.65 3-8-2: a touch-and-go is an arrival until it touches down and a departure thereafter, so once it has lifted
    /// off it is a departing aircraft over the runway, not a landed occupant on it. A stop-and-go is the same after its
    /// second roll.
    /// </summary>
    [Fact]
    public void TouchAndGoAndStopAndGoAirborne_AreDeparting()
    {
        AircraftState touchAndGo = Airborne(OnRunway(4500, 0), 280, ElevationFt + 100, 1500, "B738");
        AttachPhase(touchAndGo, LiftedOffOptionPhases.TouchAndGo(Runway), Runway);
        AircraftState stopAndGo = Airborne(OnRunway(5500, 0), 280, ElevationFt + 100, 1500, "B738");
        AttachPhase(stopAndGo, LiftedOffOptionPhases.StopAndGo(Runway), Runway);

        Assert.Equal(RunwayUseKind.Departing, Kind(touchAndGo));
        Assert.Equal(RunwayUseKind.Departing, Kind(stopAndGo));
    }

    /// <summary>
    /// A low approach is a departure once it crosses the landing threshold (the 3-8-2 rule applied to the go-around off it):
    /// past the threshold it is departing; short of it the phase says nothing and it is classified as before.
    /// </summary>
    [Fact]
    public void LowApproach_PastTheThresholdIsDeparting_ShortOfItUnchanged()
    {
        AircraftState past = Airborne(OnRunway(2000, 0), 280, ElevationFt + 100, 0, "B738");
        AttachPhase(past, new LowApproachPhase(), Runway);
        AircraftState shortOfIt = Airborne(OnFinal(0.5, 0), 280, ElevationFt + 150, -700, "B738");
        AttachPhase(shortOfIt, new LowApproachPhase(), Runway);

        Assert.Equal(RunwayUseKind.Departing, Kind(past));
        Assert.Null(RunwayOccupancy.ClassifyByPhase(shortOfIt, Runway));
        Assert.Null(Kind(shortOfIt));
    }

    /// <summary>
    /// A low approach past the threshold is a departure only below 400 ft AGL, where a takeoff or touch-and-go hands off; above
    /// it, climbing out to its 1,500 ft completion, it is no runway user at all.
    /// </summary>
    [Fact]
    public void LowApproachPastTheThreshold_DepartingOnlyBelow400FtAgl()
    {
        AircraftState low = Airborne(OnRunway(2000, 0), 280, ElevationFt + 300, 1500, "B738");
        AttachPhase(low, new LowApproachPhase(), Runway);
        AircraftState high = Airborne(OnRunway(2000, 0), 280, ElevationFt + 500, 1500, "B738");
        AttachPhase(high, new LowApproachPhase(), Runway);

        Assert.Equal(RunwayUseKind.Departing, Kind(low));
        Assert.Null(RunwayOccupancy.ClassifyByPhase(high, Runway));
        Assert.Null(Kind(high));
    }

    [Fact]
    public void LandingPhase_AirborneIsLanding_OnGroundIsOnSurface()
    {
        AircraftState flaring = Airborne(OnRunway(800, 0), 280, ElevationFt + 20, -300, "B738");
        AttachPhase(flaring, new LandingPhase(), Runway);
        AircraftState rolling = Ground(OnRunway(3000, 0), 280, 80, new LandingPhase(), Runway);

        Assert.Equal(RunwayUseKind.Landing, Kind(flaring));
        Assert.Equal(RunwayUseKind.OnSurface, Kind(rolling));
    }

    [Fact]
    public void PhaseEvidenceForAnotherRunway_DoesNotCount()
    {
        RunwayInfo other = TestRunwayFactory.Make(designator: "10L", thresholdLat: 37.1, thresholdLon: -122.1, heading: 100);
        AircraftState ac = Ground(OnRunway(3000, 500), 280, 0, new LinedUpAndWaitingPhase(), other);

        Assert.Null(Kind(ac));
    }

    [Fact]
    public void HoldingInPosition_CountsOnlyWhenPhysicallyOnThePavement()
    {
        AircraftState onPavement = Ground(OnRunway(200, 0), 280, 0, new HoldingInPositionPhase(), Runway);
        AircraftState farSide = Ground(OnRunway(3000, 500), 280, 0, new HoldingInPositionPhase(), Runway);

        Assert.Equal(RunwayUseKind.OnSurface, Kind(onPavement));
        Assert.Null(Kind(farSide));
        Assert.Equal(RunwayUseKind.OnSurface, RunwayOccupancy.ClassifyByPhase(onPavement, Runway));
        Assert.Null(RunwayOccupancy.ClassifyByPhase(onPavement, runway: null));
    }

    [Fact]
    public void RunwayExit_IsOnSurfaceWhileRollingOnTheCenterline()
    {
        // A fresh RunwayExitPhase is still rolling on the centerline (the state it starts in).
        AircraftState exiting = Ground(OnRunway(6000, 0), 280, 30, new RunwayExitPhase(), Runway);

        Assert.Equal(RunwayUseKind.OnSurface, RunwayOccupancy.ClassifyByPhase(exiting, runway: null));
        Assert.Equal(RunwayUseKind.OnSurface, Kind(exiting));
    }

    [Fact]
    public void TaxiingAcrossThePavement_IsCrossing()
    {
        AircraftState ac = Ground(OnRunway(4000, 0), 10, 15, new TaxiingPhase(), assignedRunway: null);

        Assert.Equal(RunwayUseKind.Crossing, Kind(ac));
        Assert.False(RunwayOccupancy.OccupiesSurface(Kind(ac)));
    }

    [Fact]
    public void TaxiingAlongThePavement_IsCrossingForPhaseDrivenAircraft()
    {
        // Back-taxi under a taxi phase: today's ground-conflict logic gives it no runway priority, and
        // the classifier must not change that.
        AircraftState ac = Ground(OnRunway(4000, 0), 100, 15, new TaxiingPhase(), assignedRunway: null);

        Assert.Equal(RunwayUseKind.Crossing, Kind(ac));
    }

    // --- Geometry (phase-less aircraft) ---------------------------------------------------------

    [Fact]
    public void PhaselessOnTheNumbersAligned_IsOnSurface()
    {
        AircraftState ac = Ground(OnRunway(300, 20), 280, 0, phase: null, assignedRunway: null);

        Assert.Equal(RunwayUseKind.OnSurface, Kind(ac));
    }

    [Fact]
    public void PhaselessBackTaxi_IsOnSurface()
    {
        AircraftState ac = Ground(OnRunway(4000, 0), 100, 20, phase: null, assignedRunway: null);

        Assert.Equal(RunwayUseKind.OnSurface, Kind(ac));
    }

    [Fact]
    public void PhaselessRollingAligned_IsDeparting()
    {
        AircraftState ac = Ground(OnRunway(2000, 0), 280, 60, phase: null, assignedRunway: null);

        Assert.Equal(RunwayUseKind.Departing, Kind(ac));
    }

    [Fact]
    public void PhaselessOnPavementNotAligned_IsCrossing()
    {
        AircraftState ac = Ground(OnRunway(4000, 0), 10, 15, phase: null, assignedRunway: null);

        Assert.Equal(RunwayUseKind.Crossing, Kind(ac));
    }

    [Fact]
    public void PhaselessOnParallelTaxiway_IsNotOnTheRunway()
    {
        AircraftState ac = Ground(OnRunway(4000, 200), 280, 15, phase: null, assignedRunway: null);

        Assert.Null(Kind(ac));
    }

    [Fact]
    public void PavementEdge_HalfWidthPlusSlackIsTheBoundary()
    {
        double edge = (Runway.WidthFt / 2.0) + RunwayOccupancy.LateralSlackFt;
        AircraftState inside = Ground(OnRunway(4000, edge - 2), 280, 0, phase: null, assignedRunway: null);
        AircraftState outside = Ground(OnRunway(4000, edge + 2), 280, 0, phase: null, assignedRunway: null);

        Assert.Equal(RunwayUseKind.OnSurface, Kind(inside));
        Assert.Null(Kind(outside));
    }

    [Fact]
    public void PhaselessOnHighSpeedExit_IsCrossing()
    {
        // 30° off the axis, still inside the pavement edge: leaving, not using, the runway.
        AircraftState ac = Ground(OnRunway(6000, 40), 250, 40, phase: null, assignedRunway: null);

        Assert.Equal(RunwayUseKind.Crossing, Kind(ac));
    }

    [Fact]
    public void ShortFinal_AlignedDescendingInsideTwoMiles()
    {
        AircraftState ac = Airborne(OnFinal(1.5, 0), 280, ElevationFt + 500, -700, "B738");

        Assert.Equal(RunwayUseKind.ShortFinal, Kind(ac));
    }

    [Fact]
    public void ShortFinal_LevelAtMdaStillCounts_ClimbingDoesNot()
    {
        AircraftState level = Airborne(OnFinal(1.5, 0), 280, ElevationFt + 500, 0, "B738");
        AircraftState climbing = Airborne(OnFinal(1.5, 0), 280, ElevationFt + 500, +800, "B738");

        Assert.Equal(RunwayUseKind.ShortFinal, Kind(level));
        Assert.Null(Kind(climbing));
    }

    [Fact]
    public void ShortFinal_OutsideTwoMiles_OrTooHigh_OrOffCourse_IsNothing()
    {
        AircraftState far = Airborne(OnFinal(4.0, 0), 280, ElevationFt + 1200, -700, "B738");
        AircraftState high = Airborne(OnFinal(1.5, 0), 280, ElevationFt + 2000, -700, "B738");
        AircraftState offCourse = Airborne(OnFinal(1.5, 0.6), 280, ElevationFt + 500, -700, "B738");
        AircraftState wrongWay = Airborne(OnFinal(1.5, 0), 100, ElevationFt + 500, -700, "B738");

        Assert.Null(Kind(far));
        Assert.Null(Kind(high));
        Assert.Null(Kind(offCourse));
        Assert.Null(Kind(wrongWay));
    }

    [Fact]
    public void ShortFinal_ThirtyDegreeIntercept_StillCounts()
    {
        AircraftState ac = Airborne(OnFinal(1.8, 0.2), 250, ElevationFt + 600, -600, "B738");

        Assert.Equal(RunwayUseKind.ShortFinal, Kind(ac));
    }

    [Fact]
    public void Landing_OverThePavementBelowFiftyFeet()
    {
        AircraftState ac = Airborne(OnRunway(500, 0), 280, ElevationFt + 30, -400, "B738");

        Assert.Equal(RunwayUseKind.Landing, Kind(ac));
    }

    [Fact]
    public void Landing_AglIsMeasuredFromTheAlignedEnd()
    {
        // 10 ft above the far (opposite) end's elevation, which sits 80 ft below the threshold end.
        RunwayInfo sloped = TestRunwayFactory.Make(
            designator: "28",
            thresholdLat: Runway.Lat1,
            thresholdLon: Runway.Lon1,
            endLat: Runway.Lat2,
            endLon: Runway.Lon2,
            heading: 280,
            elevationFt: ElevationFt,
            endElevationFt: ElevationFt - 80
        );
        RunwayInfo reciprocal = sloped.ForApproach("10");
        LatLon pos = GeoMath.ProjectPoint(new LatLon(sloped.Lat2, sloped.Lon2), new TrueHeading(100), 400 / GeoMath.FeetPerNm);
        AircraftState ac = Airborne(pos, 100, ElevationFt - 80 + 30, -300, "B738");

        Assert.Equal(RunwayUseKind.Landing, RunwayOccupancy.Classify(ac, reciprocal, layout: null)?.Kind);
        // Against the field elevation it would read −50 ft AGL; the classifier must not use it.
        Assert.Null(RunwayOccupancy.Classify(Airborne(pos, 100, ElevationFt + 30, -300, "B738"), reciprocal, layout: null)?.Kind);
    }

    [Fact]
    public void Helicopters_NeverClassifyAsShortFinal()
    {
        // Rotorcraft arrive at runway points from any direction (§3-11-6), so there is no final to protect.
        AircraftState onFinal = Airborne(OnFinal(1.0, 0), 280, ElevationFt + 300, -400, "EC35");
        AircraftState onGround = Ground(OnRunway(500, 0), 280, 0, phase: null, assignedRunway: null);
        onGround.AircraftType = "EC35";

        Assert.Null(Kind(onFinal));
        Assert.Equal(RunwayUseKind.OnSurface, Kind(onGround));
    }

    [Fact]
    public void Helicopter_AirTaxiingOverThePavement_OccupiesTheRunway()
    {
        // Air taxi is a surface movement below 100 ft AGL (P/CG AIR TAXI, §3-11-3 NOTE): along the axis — either
        // direction — it holds the runway like a rolling aircraft; across it at air-taxi speed it is crossing. Above
        // air-taxi height it is in flight.
        AircraftState alongAxis = Rotorcraft(OnRunway(500, 0), 280, 280, 40, 0, 40);
        AircraftState reciprocal = Rotorcraft(OnRunway(500, 0), 100, 100, 40, 0, 40);
        AircraftState across = Rotorcraft(OnRunway(500, 0), 10, 10, 40, 0, 40);
        AircraftState aboveAirTaxi = Rotorcraft(OnRunway(500, 0), 280, 280, 150, 0, 40);

        Assert.Equal(RunwayUseKind.OnSurface, Kind(alongAxis));
        Assert.Equal(RunwayUseKind.OnSurface, Kind(reciprocal));
        Assert.Equal(RunwayUseKind.Crossing, Kind(across));
        Assert.Null(Kind(aboveAirTaxi));
    }

    [Fact]
    public void Helicopter_HoveringCrosswiseOverThePavement_IsOnTheRunway()
    {
        // Below hover-taxi speed (§3-11-1.b) the heading says nothing about vacating: a hover check or a crosswind spot
        // landing is on the runway. Heading and track deliberately differ (a hover has no track).
        AircraftState hover = Rotorcraft(OnRunway(500, 0), 10, 200, 20, 0, 3);
        AircraftState sidewardHoverTaxi = Rotorcraft(OnRunway(500, 0), 10, 280, 15, 0, 12);

        Assert.Equal(RunwayUseKind.OnSurface, Kind(hover));
        Assert.Equal(RunwayUseKind.OnSurface, Kind(sidewardHoverTaxi));
    }

    [Fact]
    public void Helicopter_ClimbingOutBelowAirTaxiHeight_StillOccupiesTheRunway()
    {
        // §3-10-3.a.2: a departure blocks until it has crossed the runway end; a diverse-direction helicopter departure
        // leaves the pavement laterally and stops blocking the moment it is off it or above 100 ft.
        AircraftState liftingOff = Rotorcraft(OnRunway(500, 0), 280, 280, 60, 500, 30);
        AircraftState clearOfPavement = Rotorcraft(OnRunway(500, 400), 280, 280, 60, 500, 30);

        Assert.Equal(RunwayUseKind.OnSurface, Kind(liftingOff));
        Assert.Null(Kind(clearOfPavement));
    }

    [Fact]
    public void Helicopter_DescendingOntoThePavement_IsLanding_FromAnyDirection()
    {
        AircraftState alongAxis = Rotorcraft(OnRunway(500, 0), 280, 280, 30, -300, 20);
        AircraftState diagonal = Rotorcraft(OnRunway(500, 0), 340, 340, 30, -300, 20);

        Assert.Equal(RunwayUseKind.Landing, Kind(alongAxis));
        Assert.Equal(RunwayUseKind.Landing, Kind(diagonal));
    }

    /// <summary>A surface shadow aligned on the runway whose last five 1 Hz samples reported <paramref name="speeds"/>.</summary>
    private static AircraftState SurfaceShadow(params double[] speeds)
    {
        LatLon pos = OnRunway(500, 0);
        AircraftState? ac = null;
        for (int i = 0; i < speeds.Length; i++)
        {
            var sample = new LiveTrafficSample(i, pos.Lat, pos.Lon, ElevationFt, speeds[i], 280, 0, LiveTrafficSource.Asdex, 4521);
            if (ac is null)
            {
                ac = LiveTrafficKinematics.CreateShadow("LIVE1", "B738", sample, new AircraftFlightPlan { HasFlightPlan = true });
            }
            else
            {
                LiveTrafficKinematics.Apply(ac, sample);
            }
        }

        return ac!;
    }

    [Fact]
    public void Shadow_AcceleratingThroughTaxiSpeed_IsAlreadyDeparting() =>
        // 6 kt/s from brake release: a jet is at 25 kt four seconds in. The fixed 35 kt gate would call it OnSurface
        // for two more seconds; the acceleration branch sees the roll now.
        Assert.Equal(RunwayUseKind.Departing, Kind(SurfaceShadow(1, 7, 13, 19, 25)));

    [Fact]
    public void Shadow_MovingSteadilyAlongTheRunway_IsOnSurface_UntilTheSpeedGate()
    {
        Assert.Equal(RunwayUseKind.OnSurface, Kind(SurfaceShadow(25, 25, 25, 25, 25)));
        Assert.Equal(RunwayUseKind.OnSurface, Kind(SurfaceShadow(20, 22, 24, 26, 28)));
        Assert.Equal(RunwayUseKind.Departing, Kind(SurfaceShadow(36, 36, 36, 36, 36)));
    }

    [Fact]
    public void Shadow_AcceleratingBelowTwentyKnots_OrCoasting_IsNotYetDeparting()
    {
        Assert.Equal(RunwayUseKind.OnSurface, Kind(SurfaceShadow(1, 5, 9, 13, 17)));

        AircraftState coasting = SurfaceShadow(1, 7, 13, 19, 25);
        coasting.LiveTraffic!.IsCoasting = true;
        Assert.Equal(RunwayUseKind.OnSurface, Kind(coasting));
    }

    [Fact]
    public void Helicopter_GroundTaxiingFastAlongTheRunway_IsNeverDeparting()
    {
        // A wheeled helicopter has no takeoff roll (§3-11-1.a ground taxi); 40 kt along the axis is OnSurface, and it
        // earns no §3-10-3.a.2 departure credit.
        AircraftState wheeled = Ground(OnRunway(500, 0), 280, 40, phase: null, assignedRunway: null);
        wheeled.AircraftType = "EC35";

        Assert.Equal(RunwayUseKind.OnSurface, Kind(wheeled));
    }

    [Fact]
    public void HelicopterLandingPhase_ClassifiesLikeALanding()
    {
        AircraftState descending = Airborne(OnRunway(500, 0), 280, ElevationFt + 30, -150, "EC35");
        AttachPhase(descending, new HelicopterLandingPhase(), Runway);
        AircraftState touchedDown = Ground(OnRunway(500, 0), 280, 0, new HelicopterLandingPhase(), Runway);
        touchedDown.AircraftType = "EC35";

        Assert.Equal(RunwayUseKind.Landing, Kind(descending));
        Assert.Equal(RunwayUseKind.OnSurface, Kind(touchedDown));
    }

    [Fact]
    public void AirTaxiPhase_OccupiesWhateverPavementItIsOver()
    {
        // A simulated helicopter air-taxiing across the runway at 100 ft AGL has no runway of its own; it is a surface
        // movement (§3-11-3 NOTE) on the pavement it is over and nothing once past it.
        AircraftState overRunway = Airborne(OnRunway(500, 0), 10, ElevationFt + 100, 0, "EC35");
        AttachPhase(overRunway, new AirTaxiPhase(37.1, -122.1, "H1"), assignedRunway: null);
        AircraftState pastRunway = Airborne(OnRunway(500, 400), 10, ElevationFt + 100, 0, "EC35");
        AttachPhase(pastRunway, new AirTaxiPhase(37.1, -122.1, "H1"), assignedRunway: null);

        Assert.Equal(RunwayUseKind.OnSurface, Kind(overRunway));
        Assert.Null(Kind(pastRunway));
        Assert.Equal(RunwayUseKind.OnSurface, RunwayOccupancy.ClassifyByPhase(overRunway, Runway));
        Assert.Null(RunwayOccupancy.ClassifyByPhase(overRunway, runway: null));
    }

    [Fact]
    public void OccupiesSurface_IsTrueForDepartingLandingAndOnSurfaceOnly()
    {
        Assert.True(RunwayOccupancy.OccupiesSurface(RunwayUseKind.Departing));
        Assert.True(RunwayOccupancy.OccupiesSurface(RunwayUseKind.Landing));
        Assert.True(RunwayOccupancy.OccupiesSurface(RunwayUseKind.OnSurface));
        Assert.False(RunwayOccupancy.OccupiesSurface(RunwayUseKind.ShortFinal));
        Assert.False(RunwayOccupancy.OccupiesSurface(RunwayUseKind.Crossing));
        Assert.False(RunwayOccupancy.OccupiesSurface(RunwayUseKind.Fouling));
        Assert.False(RunwayOccupancy.OccupiesSurface(null));
    }

    [Fact]
    public void FoulsRunway_IsTrueForFoulingOnly()
    {
        foreach (RunwayUseKind kind in Enum.GetValues<RunwayUseKind>())
        {
            Assert.Equal(kind == RunwayUseKind.Fouling, RunwayOccupancy.FoulsRunway(kind));
        }

        Assert.False(RunwayOccupancy.FoulsRunway(null));
    }

    // --- Past the holding position marking (KOAK, real layout) ----------------------------------

    /// <summary>
    /// An aircraft on the ground square to <paramref name="runway"/>'s holding position marking at <paramref name="bar"/> — on the
    /// taxiway axis through the bar (<see cref="RunwayHoldBars.TaxiwayAxisBearingDeg(RunwayInfo, GroundNode)"/>) — moved
    /// <paramref name="alongFt"/> along the runway, with <paramref name="partPastFt"/> of it past the marking toward the runway
    /// (negative: short of it). Facing the runway that part is its nose; facing away, its tail. No phases.
    /// </summary>
    public static AircraftState SquareToTheMarking(
        RunwayInfo runway,
        GroundNode bar,
        string type,
        bool facingRunway,
        double partPastFt,
        double alongFt
    )
    {
        var end1 = new LatLon(runway.Lat1, runway.Lon1);
        var end2 = new LatLon(runway.Lat2, runway.Lon2);
        var along = new TrueHeading(GeoMath.BearingTo(end1, end2));
        LatLon marking = Project(bar.Position, along, alongFt);
        TrueHeading toward = new TrueHeading(RunwayHoldBars.TaxiwayAxisBearingDeg(runway, bar)).ToReciprocal();
        return PlacedToward(runway, type, facingRunway, Project(marking, toward, partPastFt - (AircraftLength.ResolveFt(type) / 2.0)), toward);
    }

    /// <summary>
    /// An aircraft on the ground square to <paramref name="runway"/> through <paramref name="bar"/> — on the perpendicular from the
    /// bar to the centreline — moved <paramref name="alongFt"/> along the runway, with <paramref name="partPastFt"/> of it past the
    /// bar's distance from the centreline toward it (negative: short of it), so a position relative to the centreline is
    /// <c>barFt ± X</c> whatever the taxiway's angle. Facing the runway that part is its nose; facing away, its tail. No phases.
    /// </summary>
    public static AircraftState SquareToTheRunway(
        RunwayInfo runway,
        GroundNode bar,
        string type,
        bool facingRunway,
        double partPastFt,
        double alongFt
    )
    {
        var end1 = new LatLon(runway.Lat1, runway.Lon1);
        var end2 = new LatLon(runway.Lat2, runway.Lon2);
        LatLon marking = Project(bar.Position, new TrueHeading(GeoMath.BearingTo(end1, end2)), alongFt);
        var toward = new TrueHeading(GeoMath.BearingTo(marking, GeoMath.FootOfPerpendicular(marking, end1, end2).Foot));
        return PlacedToward(runway, type, facingRunway, Project(marking, toward, partPastFt - (AircraftLength.ResolveFt(type) / 2.0)), toward);
    }

    private static AircraftState PlacedToward(RunwayInfo runway, string type, bool facingRunway, LatLon centre, TrueHeading toward)
    {
        TrueHeading heading = facingRunway ? toward : toward.ToReciprocal();
        return new AircraftState
        {
            Callsign = "FOUL1",
            AircraftType = type,
            Position = centre,
            TrueHeading = heading,
            TrueTrack = heading,
            Altitude = runway.ElevationFt,
            IsOnGround = true,
            AirportId = "OAK",
        };
    }

    private static LatLon Project(LatLon from, TrueHeading heading, double distanceFt) =>
        GeoMath.ProjectPoint(from, distanceFt >= 0 ? heading : heading.ToReciprocal(), Math.Abs(distanceFt) / GeoMath.FeetPerNm);

    private static AircraftState Placed(RunwayInfo runway, GroundNode bar, bool facingRunway, double partPastFt, double groundSpeedKts, Phase? phase)
    {
        AircraftState ac = SquareToTheMarking(runway, bar, "B738", facingRunway, partPastFt, alongFt: 0);
        ac.IndicatedAirspeed = groundSpeedKts;
        AttachPhase(ac, phase, runway);
        return ac;
    }

    private static AircraftState ExitingArrival(RunwayInfo runway, GroundNode bar, double tailPastFt, double groundSpeedKts) =>
        Placed(runway, bar, facingRunway: false, tailPastFt, groundSpeedKts, ExitPhaseFixture.ExitOffTheCentreline(runway));

    private static GroundNode OtherEnd(IGroundEdge edge, GroundNode node) => edge.Nodes[0].Id == node.Id ? edge.Nodes[1] : edge.Nodes[0];

    private static double AxisDeviationDeg(double headingDeg, RunwayInfo runway)
    {
        double diff = GeoMath.AbsBearingDifference(headingDeg, runway.TrueHeading.Degrees);
        return Math.Min(diff, 180.0 - diff);
    }

    /// <summary>
    /// A B738 on a TaxiingPhase short of the marking at <paramref name="bar"/>, heading <paramref name="offAxisDeg"/> off the runway
    /// axis turned toward the runway, so its wingtip on the runway side is past the marking.
    /// </summary>
    private static AircraftState AlongTheAxis(RunwayInfo runway, GroundNode bar, double offAxisDeg, double groundSpeedKts)
    {
        AircraftState ac = Placed(runway, bar, facingRunway: true, 30.0, groundSpeedKts, new TaxiingPhase());
        double toward = ac.TrueHeading.Degrees;
        double along = GeoMath.BearingTo(new LatLon(runway.Lat1, runway.Lon1), new LatLon(runway.Lat2, runway.Lon2));
        double turnedLeft = (along - offAxisDeg + 360.0) % 360.0;
        double turnedRight = (along + offAxisDeg) % 360.0;
        var heading = new TrueHeading(
            GeoMath.AbsBearingDifference(turnedRight, toward) < GeoMath.AbsBearingDifference(turnedLeft, toward) ? turnedRight : turnedLeft
        );
        ac.TrueHeading = heading;
        ac.TrueTrack = heading;
        return ac;
    }

    private const double ParallelAxisToleranceDeg = 15.0;

    /// <summary>The edges leaving <paramref name="node"/> that run along <paramref name="runway"/>'s axis.</summary>
    private static IEnumerable<IGroundEdge> AxisEdges(RunwayInfo runway, GroundNode node) =>
        node.Edges.Where(e => AxisDeviationDeg(GeoMath.BearingTo(node.Position, OtherEnd(e, node).Position), runway) <= ParallelAxisToleranceDeg);

    /// <summary>
    /// Where the stub through <paramref name="bar"/> meets a taxiway running along the runway, walking away from the runway
    /// up to 600 ft from the bar. Null when there is none.
    /// </summary>
    private static GroundNode? ParallelJunction(RunwayInfo runway, GroundNode bar)
    {
        double barFt = RunwayHoldBars.CentrelineFt(runway, bar.Position);
        var seen = new HashSet<int> { bar.Id };
        var frontier = new Queue<GroundNode>([bar]);
        while (frontier.TryDequeue(out GroundNode? node))
        {
            if ((node.Id != bar.Id) && AxisEdges(runway, node).Any())
            {
                return node;
            }

            foreach (IGroundEdge edge in node.Edges)
            {
                GroundNode next = OtherEnd(edge, node);
                bool awayFromRunway = RunwayHoldBars.CentrelineFt(runway, next.Position) > barFt;
                bool inReach = (GeoMath.DistanceNm(bar.Position, next.Position) * GeoMath.FeetPerNm) <= 600.0;
                if (awayFromRunway && inReach && seen.Add(next.Id))
                {
                    frontier.Enqueue(next);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Points every 50 ft along the parallel taxiway's own edges from <paramref name="junction"/> through <paramref name="first"/>,
    /// up to <paramref name="maxFt"/>, each with the bearing of the edge it lies on.
    /// </summary>
    private static IEnumerable<(LatLon Position, double HeadingDeg)> AlongParallel(
        RunwayInfo runway,
        GroundNode junction,
        IGroundEdge first,
        double maxFt
    )
    {
        GroundNode from = junction;
        GroundNode to = OtherEnd(first, junction);
        var seen = new HashSet<int> { junction.Id };
        double travelledFt = 0.0;
        while ((travelledFt < maxFt) && seen.Add(to.Id))
        {
            double bearing = GeoMath.BearingTo(from.Position, to.Position);
            double lengthFt = GeoMath.DistanceNm(from.Position, to.Position) * GeoMath.FeetPerNm;
            for (double s = 0.0; (s < lengthFt) && ((travelledFt + s) <= maxFt); s += 50.0)
            {
                yield return (Project(from.Position, new TrueHeading(bearing), s), bearing);
            }

            travelledFt += lengthFt;
            GroundNode previous = from;
            IGroundEdge? onward = AxisEdges(runway, to)
                .Where(e => OtherEnd(e, to).Id != previous.Id)
                .OrderBy(e => GeoMath.AbsBearingDifference(GeoMath.BearingTo(to.Position, OtherEnd(e, to).Position), bearing))
                .FirstOrDefault();
            if (onward is null)
            {
                yield break;
            }

            from = to;
            to = OtherEnd(onward, from);
        }
    }

    /// <summary>A B738 stopped on a parallel taxiway at <paramref name="position"/>, heading along it.</summary>
    private static AircraftState OnParallel(RunwayInfo runway, LatLon position, double headingDeg)
    {
        var heading = new TrueHeading(headingDeg);
        AircraftState ac = new()
        {
            Callsign = "PAR1",
            AircraftType = "B738",
            Position = position,
            TrueHeading = heading,
            TrueTrack = heading,
            Altitude = runway.ElevationFt,
            IsOnGround = true,
            AirportId = "OAK",
        };
        AttachPhase(ac, new TaxiingPhase(), runway);
        return ac;
    }

    private static RunwayUseKind? Kind(AircraftState ac, RunwayInfo runway, AirportGroundLayout? layout) =>
        RunwayOccupancy.Classify(ac, runway, layout)?.Kind;

    [Fact]
    public void CorrectHold_NoseAtTheMarking_DoesNotFoul()
    {
        if (RunwayBarFixture.RunwayBar("KOAK", "33") is not { } oak)
        {
            return;
        }

        var holdShort = new HoldShortPoint
        {
            NodeId = oak.Bar.Id,
            Reason = HoldShortReason.RunwayCrossing,
            TargetName = "33",
        };
        AircraftState holding = Placed(oak.Runway, oak.Bar, facingRunway: true, 0.0, 0, new HoldingShortPhase(holdShort));

        Assert.Null(Kind(holding, oak.Runway, oak.Layout));
    }

    [Fact]
    public void NosePastByLessThanTheTolerance_DoesNotFoul()
    {
        if (RunwayBarFixture.RunwayBar("KOAK", "33") is not { } oak)
        {
            return;
        }

        double pastFt = RunwayOccupancy.HoldLineFoulToleranceFt - 1.0;
        AircraftState stopped = Placed(oak.Runway, oak.Bar, facingRunway: true, pastFt, 0, new TaxiingPhase());

        Assert.Null(Kind(stopped, oak.Runway, oak.Layout));
    }

    [Fact]
    public void NosePastByMoreThanTheTolerance_Fouls()
    {
        if (RunwayBarFixture.RunwayBar("KOAK", "33") is not { } oak)
        {
            return;
        }

        double pastFt = RunwayOccupancy.HoldLineFoulToleranceFt + 1.0;
        AircraftState stopped = Placed(oak.Runway, oak.Bar, facingRunway: true, pastFt, 0, new TaxiingPhase());

        Assert.False(RunwayOccupancy.IsOnPavement(stopped, oak.Runway));
        Assert.Equal(RunwayUseKind.Fouling, Kind(stopped, oak.Runway, oak.Layout));
    }

    [Fact]
    public void PartOverTheGrassBetweenBars_DoesNotFoul()
    {
        if (RunwayBarFixture.RunwayBar("KOAK", "33") is not { } oak)
        {
            return;
        }

        // Slide along the runway, on the bar's side, until no bar on that side is within the marked reach: there is no hold line
        // painted there, so a nose nearer the centreline than the bars' line is over the grass, not past a marking.
        bool right = RunwayHoldBars.IsRightOfCentreline(oak.Runway, oak.Bar.Position);
        GroundNode[] sideBars =
        [
            .. RunwayHoldBars.RunwayBars(oak.Layout, oak.Runway).Where(b => RunwayHoldBars.IsRightOfCentreline(oak.Runway, b.Position) == right),
        ];
        var along = new TrueHeading(GeoMath.BearingTo(new LatLon(oak.Runway.Lat1, oak.Runway.Lon1), new LatLon(oak.Runway.Lat2, oak.Runway.Lon2)));
        double? grassFt = null;
        for (double offsetFt = -3000; offsetFt <= 3000; offsetFt += 25)
        {
            LatLon marking = Project(oak.Bar.Position, along, offsetFt);
            bool abeam = !GeoMath
                .FootOfPerpendicular(marking, new LatLon(oak.Runway.Lat1, oak.Runway.Lon1), new LatLon(oak.Runway.Lat2, oak.Runway.Lon2))
                .Clamped;
            double nearestBarFt = sideBars.Min(b => Math.Abs(GeoMath.AlongTrackDistanceNm(marking, b.Position, along)) * GeoMath.FeetPerNm);
            if (abeam && (nearestBarFt > HoldLineFootprint.MarkedBarReachFt + 100.0))
            {
                grassFt = offsetFt;
                break;
            }
        }

        double offset = Assert.IsType<double>(grassFt);
        AircraftState overGrass = SquareToTheMarking(oak.Runway, oak.Bar, "B738", facingRunway: true, 30.0, offset);
        AttachPhase(overGrass, new TaxiingPhase(), oak.Runway);
        AircraftState atTheBar = Placed(oak.Runway, oak.Bar, facingRunway: true, 30.0, 0, new TaxiingPhase());

        Assert.False(RunwayOccupancy.IsOnPavement(overGrass, oak.Runway));
        Assert.Null(Kind(overGrass, oak.Runway, oak.Layout));
        Assert.Equal(RunwayUseKind.Fouling, Kind(atTheBar, oak.Runway, oak.Layout));
    }

    [Fact]
    public void ExitingArrival_TailInside_MovingUnrestricted_IsClear()
    {
        if (RunwayBarFixture.RunwayBar("KOAK", "33") is not { } oak)
        {
            return;
        }

        AircraftState exiting = ExitingArrival(oak.Runway, oak.Bar, tailPastFt: 20.0, groundSpeedKts: 10);

        Assert.False(RunwayOccupancy.IsOnPavement(exiting, oak.Runway));
        Assert.Null(Kind(exiting, oak.Runway, oak.Layout));
    }

    [Fact]
    public void ExitingArrival_TailInside_Stopped_Fouls()
    {
        if (RunwayBarFixture.RunwayBar("KOAK", "33") is not { } oak)
        {
            return;
        }

        AircraftState exiting = ExitingArrival(oak.Runway, oak.Bar, tailPastFt: 20.0, groundSpeedKts: 0);

        Assert.Equal(RunwayUseKind.Fouling, Kind(exiting, oak.Runway, oak.Layout));
    }

    /// <summary>
    /// A B77W stopped on a high-speed exit of SFO 28R with its centre just off the pavement and its tail still over the strict paved
    /// half-width, more than <see cref="HoldLineFootprint.MarkedBarReachFt"/> along the runway from the nearest bar on its side:
    /// part of it is on the runway whatever the bar (AIM 4-3-21.b), so it fouls the runway (YAAT-825).
    /// </summary>
    [Fact]
    public void ExitingArrival_TailOverThePavementFarFromAnyBar_Stopped_Fouls()
    {
        if (RunwayBarFixture.RunwayBar("KSFO", "28R") is null)
        {
            return;
        }

        ExitPose pose = Assert.IsType<ExitPose>(HighSpeedExitFixture.TailOverThePavementOnAHighSpeedExit("KSFO", "28R", "B77W"));
        AircraftState exiting = pose.Stopped();

        // Measured on the layout: the exit to bar #241 leaves 28R heading 271.6° true; the main gear 131.1 ft from the centreline
        // (half-width 100 ft plus the 30 ft slack), the tail 76.0 ft from it and 476.2 ft along the runway from the nearest bar.
        Assert.Equal(241, pose.Bar.Id);
        Assert.Equal(131.1, pose.CentreFt, 0.5);
        Assert.Equal(76.0, pose.TailFt, 0.5);
        Assert.Equal(476.2, pose.TailAlongBarFt, 0.5);
        Assert.False(RunwayOccupancy.IsOnPavement(exiting, pose.Runway));
        Assert.Equal(RunwayUseKind.Fouling, Kind(exiting, pose.Runway, pose.Layout));
    }

    [Fact]
    public void ExitingArrival_TailInside_UnderHold_Fouls()
    {
        if (RunwayBarFixture.RunwayBar("KOAK", "33") is not { } oak)
        {
            return;
        }

        AircraftState exiting = ExitingArrival(oak.Runway, oak.Bar, tailPastFt: 20.0, groundSpeedKts: 10);
        exiting.Ground.Hold = HoldDirective.HoldPosition;

        Assert.Equal(RunwayUseKind.Fouling, Kind(exiting, oak.Runway, oak.Layout));
    }

    [Fact]
    public void ExitingArrival_TailInside_UnderGiveWay_Fouls()
    {
        if (RunwayBarFixture.RunwayBar("KOAK", "33") is not { } oak)
        {
            return;
        }

        // Off the exit and taxiing, where a GIVEWAY takes effect (on the rollout or the exit it is still pending).
        AircraftState exiting = Placed(oak.Runway, oak.Bar, facingRunway: false, 20.0, 10, new TaxiingPhase());
        exiting.Ground.Hold = HoldDirective.GiveWay("N2GW");

        Assert.Equal(RunwayUseKind.Fouling, Kind(exiting, oak.Runway, oak.Layout));
    }

    [Fact]
    public void GiveWayPendingOnLandingRunway_DoesNotCountAsRestricted()
    {
        if (RunwayBarFixture.RunwayBar("KOAK", "33") is not { } oak)
        {
            return;
        }

        // A GIVEWAY given on the exit does not stop the aircraft until it is off its landing runway and taxiing.
        AircraftState exiting = ExitingArrival(oak.Runway, oak.Bar, tailPastFt: 20.0, groundSpeedKts: 10);
        exiting.Ground.Hold = HoldDirective.GiveWay("N2GW");

        Assert.True(exiting.HasGiveWayPendingOnLandingRunway);
        Assert.Null(Kind(exiting, oak.Runway, oak.Layout));
    }

    [Fact]
    public void RouteEndingAtTheLeavingRunwaysBar_IsNotRestricted()
    {
        if (RunwayBarFixture.RunwayBar("KOAK", "33") is not { } oak)
        {
            return;
        }

        // The stub edge into the bar from the runway side: a route that ends on the bar node stops the aircraft tail-clear of it.
        double barFt = RunwayHoldBars.CentrelineFt(oak.Runway, oak.Bar.Position);
        IGroundEdge stub = oak.Bar.Edges.First(e => RunwayHoldBars.CentrelineFt(oak.Runway, OtherEnd(e, oak.Bar).Position) < barFt);
        var segment = new TaxiRouteSegment
        {
            Edge = new DirectionalEdge
            {
                Edge = stub,
                FromNode = OtherEnd(stub, oak.Bar),
                ToNode = oak.Bar,
            },
            TaxiwayName = stub.TaxiwayName,
        };
        AircraftState exiting = ExitingArrival(oak.Runway, oak.Bar, tailPastFt: 20.0, groundSpeedKts: 10);
        exiting.Ground.AssignedTaxiRoute = new TaxiRoute { Segments = [segment], HoldShortPoints = [] };

        Assert.Null(Kind(exiting, oak.Runway, oak.Layout));
    }

    [Fact]
    public void AlongTheAxisHeading_Moving_DoesNotFoul()
    {
        if (RunwayBarFixture.RunwayBar("KOAK", "33") is not { } oak)
        {
            return;
        }

        // 20° off the runway axis, turned toward the runway: a wingtip is past the marking, but the heading says neither
        // approach nor leave (a parallel taxiway, a turn about), so only a stopped or restricted aircraft fouls.
        AircraftState moving = AlongTheAxis(oak.Runway, oak.Bar, offAxisDeg: 20.0, groundSpeedKts: 12);
        AircraftState stopped = AlongTheAxis(oak.Runway, oak.Bar, offAxisDeg: 20.0, groundSpeedKts: 0);

        Assert.Null(Kind(moving, oak.Runway, oak.Layout));
        Assert.Equal(RunwayUseKind.Fouling, Kind(stopped, oak.Runway, oak.Layout));
    }

    [Fact]
    public void LineUpCleared_RollingPastTheLine_IsNotAUse()
    {
        if (RunwayBarFixture.RunwayBar("KOAK", "33") is not { } oak)
        {
            return;
        }

        // Cleared to line up (or for takeoff) while taxiing: the destination runway's hold-short at this bar is cleared.
        AircraftState rolling = Placed(oak.Runway, oak.Bar, facingRunway: true, 20.0, 12, new TaxiingPhase());
        rolling.Ground.AssignedTaxiRoute = new TaxiRoute
        {
            Segments = [],
            HoldShortPoints =
            [
                new HoldShortPoint
                {
                    NodeId = oak.Bar.Id,
                    Reason = HoldShortReason.DestinationRunway,
                    TargetName = "33",
                    IsCleared = true,
                },
            ],
        };

        Assert.False(RunwayOccupancy.IsOnPavement(rolling, oak.Runway));
        Assert.Null(Kind(rolling, oak.Runway, oak.Layout));
    }

    [Fact]
    public void ParallelTaxiwayB738PastAStubBar_NeverFouls()
    {
        TestVnasData.EnsureInitialized();
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("OAK");
        if (layout is null)
        {
            return;
        }

        // Every KOAK stub bar: walk off the runway to the taxiway the stub meets, and stop a B738 on that taxiway's centreline,
        // from the junction up to 300 ft either way along its own edges. Its wingtip may be within the bar's reach along the
        // runway, but no part of it is over the stub's marking.
        int placements = 0;
        foreach (string designator in new[] { "28R", "28L", "30", "33" })
        {
            RunwayInfo runway = Assert.IsType<RunwayInfo>(NavigationDatabase.Instance.GetRunway("OAK", designator));
            foreach (GroundNode bar in RunwayHoldBars.RunwayBars(layout, runway))
            {
                if (ParallelJunction(runway, bar) is not { } junction)
                {
                    continue;
                }

                foreach (IGroundEdge first in AxisEdges(runway, junction))
                {
                    foreach ((LatLon position, double headingDeg) in AlongParallel(runway, junction, first, maxFt: 300.0))
                    {
                        AircraftState onParallel = OnParallel(runway, position, headingDeg);
                        placements++;
                        Assert.True(
                            Kind(onParallel, runway, layout) is null,
                            $"B738 stopped on the parallel from node {junction.Id}, {RunwayHoldBars.CentrelineFt(runway, position):F0} ft "
                                + $"off the centreline, fouls runway {designator} (bar {bar.Id})"
                        );
                    }
                }
            }
        }

        Assert.True(placements > 0, "no KOAK stub bar meets a parallel taxiway");
    }

    /// <summary>
    /// Every runway hold-short bar of <paramref name="airport"/>'s runways with the bearing of each stub edge into it from the
    /// taxiway side (its other end farther from the centreline than the bar), edges along the runway axis aside.
    /// </summary>
    private static IEnumerable<(RunwayInfo Runway, GroundNode Bar, double IntoDeg)> StubsInto(AirportGroundLayout layout, string airport)
    {
        foreach (RunwayInfo runway in RunwayOccupancy.AirportRunways(airport))
        {
            foreach (GroundNode bar in RunwayHoldBars.RunwayBars(layout, runway))
            {
                double barFt = RunwayHoldBars.CentrelineFt(runway, bar.Position);
                IEnumerable<IGroundEdge> stubs = bar.Edges.Where(e => RunwayHoldBars.CentrelineFt(runway, OtherEnd(e, bar).Position) > barFt);
                foreach (IGroundEdge stub in stubs.Except(AxisEdges(runway, bar)))
                {
                    yield return (runway, bar, GeoMath.BearingTo(OtherEnd(stub, bar).Position, bar.Position));
                }
            }
        }
    }

    /// <summary>
    /// A stopped <paramref name="type"/> on the ground with its main gear at <paramref name="position"/>, in
    /// <paramref name="phase"/>.
    /// </summary>
    private static AircraftState StoppedAt(RunwayInfo runway, string airport, string type, LatLon position, double headingDeg, Phase phase)
    {
        var heading = new TrueHeading(headingDeg);
        AircraftState ac = new()
        {
            Callsign = "STUB1",
            AircraftType = type,
            Position = position,
            TrueHeading = heading,
            TrueTrack = heading,
            Altitude = runway.ElevationFt,
            IsOnGround = true,
            AirportId = airport,
        };
        AttachPhase(ac, phase, runway);
        return ac;
    }

    [Theory]
    [InlineData("OAK")]
    [InlineData("SFO")]
    public void CorrectHoldAndExitStop_OnEveryStub_NeverFoul(string airport)
    {
        TestVnasData.EnsureInitialized();
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout(airport);
        if (layout is null)
        {
            return;
        }

        // A hold short stops the nose at the bar, heading along the stub into it; an exit stops the tail at the bar, heading along
        // the stub away from the runway. On a stub angled to the runway the wingtip on the runway side is nearer the centreline
        // than the bar, but neither aircraft has a part beyond the marking that matters: the leading one is on it.
        var fouls = new List<string>();
        int placements = 0;
        foreach ((RunwayInfo runway, GroundNode bar, double intoDeg) in StubsInto(layout, airport))
        {
            var into = new TrueHeading(intoDeg);
            foreach (string type in new[] { "B738", "C172" })
            {
                double halfFt = AircraftBody.Of(type).LengthFt / 2.0;
                var holdShort = new HoldShortPoint
                {
                    NodeId = bar.Id,
                    Reason = HoldShortReason.RunwayCrossing,
                    TargetName = runway.Designator,
                };
                AircraftState holding = StoppedAt(
                    runway,
                    airport,
                    type,
                    Project(bar.Position, into, -halfFt),
                    intoDeg,
                    new HoldingShortPhase(holdShort)
                );
                var exitStop = new HoldingAfterExitPhase(runway.Designator, exitTaxiway: null, bar.Id);
                AircraftState exited = StoppedAt(runway, airport, type, Project(bar.Position, into, -halfFt), into.ToReciprocal().Degrees, exitStop);
                placements += 2;
                foreach ((string pose, AircraftState ac) in new[] { ("holding short", holding), ("stopped after exit", exited) })
                {
                    if (Kind(ac, runway, layout) is { } kind)
                    {
                        fouls.Add($"{type} {pose} at bar {bar.Id} of {runway.Designator}, heading {ac.TrueHeading.Degrees:F0}: {kind}");
                    }
                }
            }
        }

        Assert.True(placements > 0, $"no {airport} runway bar has a stub");
        Assert.True(fouls.Count == 0, string.Join(Environment.NewLine, fouls));
    }

    /// <summary>
    /// A stub at KSFO leaving <c>First</c> for a bar of <c>Next</c>, where a <paramref name="type"/> stopped with its nose at that
    /// bar (AIM 2-3-5.a.1) still has its tail at least 10 ft inside <c>First</c>'s marking, while centred on the bar every part of
    /// it is outside that marking: the half fuselage decides. Null when KSFO has no such stub. (KOAK has none: no stub joins two of
    /// its runways' markings within 400 ft.)
    /// </summary>
    private static (RunwayInfo First, RunwayInfo Next, GroundNode From, GroundNode Bar)? StopBetweenTwoMarkings(
        AirportGroundLayout layout,
        string type
    )
    {
        var body = AircraftBody.Of(type);
        IReadOnlyList<RunwayInfo> runways = RunwayOccupancy.AirportRunways("SFO");
        foreach (RunwayInfo first in runways)
        {
            foreach (RunwayInfo next in runways.Where(r => !r.Id.Overlaps(first.Id)))
            {
                foreach (GroundNode bar in RunwayHoldBars.RunwayBars(layout, next))
                {
                    foreach (IGroundEdge edge in bar.Edges)
                    {
                        GroundNode from = OtherEnd(edge, bar);
                        var heading = new TrueHeading(GeoMath.BearingTo(from.Position, bar.Position));
                        bool leavingFirst =
                            (RunwayHoldBars.CentrelineFt(first, bar.Position) > RunwayHoldBars.CentrelineFt(first, from.Position))
                            && (AxisDeviationDeg(heading.Degrees, first) > RunwayOccupancy.DirectionlessAxisToleranceDeg);
                        LatLon tail = Project(bar.Position, heading, -(body.LengthFt - 20.0));
                        bool tailInside = RunwayHoldBars.IsInsideBarLine(layout, first, tail, tail);
                        bool centredClear = AircraftBody
                            .AllParts.Select(part => body.PointOf(part, bar.Position, heading.Degrees))
                            .All(point => !RunwayHoldBars.IsInsideBarLine(layout, first, point, point));
                        if (leavingFirst && tailInside && centredClear)
                        {
                            return (first, next, from, bar);
                        }
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Two bars of one runway on one side within <see cref="HoldLineFootprint.MarkedBarReachFt"/> of each other along it, at KOAK
    /// or KSFO: an aircraft square to the marking at <c>At</c>, its nose past it, is nearer <c>At</c> than <c>Other</c>.
    /// </summary>
    private static (AirportGroundLayout Layout, RunwayInfo Runway, GroundNode At, GroundNode Other)? TwoCloseBars()
    {
        foreach (string airport in new[] { "OAK", "SFO" })
        {
            if (new TestAirportGroundData().GetLayout(airport) is not { } layout)
            {
                continue;
            }

            foreach (RunwayInfo runway in RunwayOccupancy.AirportRunways(airport))
            {
                var end1 = new LatLon(runway.Lat1, runway.Lon1);
                var end2 = new LatLon(runway.Lat2, runway.Lon2);
                GroundNode[] abeam =
                [
                    .. RunwayHoldBars.RunwayBars(layout, runway).Where(b => !GeoMath.FootOfPerpendicular(b.Position, end1, end2).Clamped),
                ];
                foreach (GroundNode at in abeam)
                {
                    AircraftState placed = SquareToTheMarking(runway, at, "B738", facingRunway: true, 20.0, alongFt: 0);
                    bool right = RunwayHoldBars.IsRightOfCentreline(runway, at.Position);
                    GroundNode[] sameSide = [.. abeam.Where(b => RunwayHoldBars.IsRightOfCentreline(runway, b.Position) == right)];
                    GroundNode nearest = sameSide.MinBy(b => GeoMath.DistanceNm(b.Position, placed.Position))!;
                    GroundNode? other = sameSide.FirstOrDefault(b =>
                        (b.Id != at.Id)
                        && (
                            (Math.Abs(GeoMath.AlongTrackDistanceNm(b.Position, at.Position, runway.TrueHeading)) * GeoMath.FeetPerNm)
                            <= HoldLineFootprint.MarkedBarReachFt
                        )
                    );
                    if ((nearest.Id == at.Id) && (other is not null))
                    {
                        return (layout, runway, at, other);
                    }
                }
            }
        }

        return null;
    }

    [Fact]
    public void CrossingClearedThroughTheOtherOfTwoCloseBars_Moving_IsNotAUse()
    {
        TestVnasData.EnsureInitialized();
        if (new TestAirportGroundData().GetLayout("OAK") is null)
        {
            return;
        }

        (AirportGroundLayout Layout, RunwayInfo Runway, GroundNode At, GroundNode Other) pair = Assert.IsType<(
            AirportGroundLayout,
            RunwayInfo,
            GroundNode,
            GroundNode
        )>(TwoCloseBars());

        // Cleared through one bar of a wide junction while its centre is nearest the other: the clearance covers the marking.
        AircraftState crossing = Placed(pair.Runway, pair.At, facingRunway: true, 20.0, 12, new TaxiingPhase());
        crossing.Ground.AssignedTaxiRoute = new TaxiRoute
        {
            Segments = [],
            HoldShortPoints =
            [
                new HoldShortPoint
                {
                    NodeId = pair.Other.Id,
                    Reason = HoldShortReason.RunwayCrossing,
                    TargetName = pair.Runway.Designator,
                    IsCleared = true,
                },
            ],
        };

        Assert.False(RunwayOccupancy.IsOnPavement(crossing, pair.Runway));
        Assert.Null(Kind(crossing, pair.Runway, pair.Layout));
    }

    [Fact]
    public void NosePastTheMarking_MovingUncleared_Fouls()
    {
        if (RunwayBarFixture.RunwayBar("KOAK", "33") is not { } oak)
        {
            return;
        }

        // Taxiing into the runway with no clearance past the marking and nothing stopping it yet: its nose is over the line.
        AircraftState taxiing = Placed(oak.Runway, oak.Bar, facingRunway: true, 20.0, 8, new TaxiingPhase());

        Assert.False(RunwayOccupancy.IsOnPavement(taxiing, oak.Runway));
        Assert.Equal(RunwayUseKind.Fouling, Kind(taxiing, oak.Runway, oak.Layout));
    }

    /// <summary>
    /// A KOAK runway, one of its bars, and a node just beyond it on the taxiway side where a <paramref name="type"/> whose route ends
    /// there, its main gear on the node and facing away from the runway, still has its tail at least 10 ft inside the marking.
    /// </summary>
    private static (AirportGroundLayout Layout, RunwayInfo Runway, GroundNode Bar, GroundNode End)? RouteEndJustPastABar(string type)
    {
        if (new TestAirportGroundData().GetLayout("OAK") is not { } layout)
        {
            return null;
        }

        double halfFt = AircraftBody.Of(type).LengthFt / 2.0;
        foreach ((RunwayInfo runway, GroundNode bar, double intoDeg) in StubsInto(layout, "OAK"))
        {
            GroundNode end = bar
                .Edges.Select(e => OtherEnd(e, bar))
                .First(n => GeoMath.AbsBearingDifference(GeoMath.BearingTo(n.Position, bar.Position), intoDeg) < 1.0);
            var away = new TrueHeading(GeoMath.BearingTo(bar.Position, end.Position));
            LatLon tail = Project(end.Position, away, -(halfFt - 10.0));
            bool tailInside = RunwayHoldBars.IsInsideBarLine(layout, runway, tail, tail);
            if (tailInside && (end.Type != GroundNodeType.RunwayHoldShort))
            {
                return (layout, runway, bar, end);
            }
        }

        return null;
    }

    [Fact]
    public void ExitingArrival_RouteEndingWithTheTailInside_Moving_Fouls()
    {
        TestVnasData.EnsureInitialized();
        if (new TestAirportGroundData().GetLayout("OAK") is null)
        {
            return;
        }

        // Its taxi route ends a few feet past the bar: moving now, it will stop there with its tail inside the marking.
        const string type = "B744";
        (AirportGroundLayout Layout, RunwayInfo Runway, GroundNode Bar, GroundNode End) exit = Assert.IsType<(
            AirportGroundLayout,
            RunwayInfo,
            GroundNode,
            GroundNode
        )>(RouteEndJustPastABar(type));
        double awayDeg = GeoMath.BearingTo(exit.Bar.Position, exit.End.Position);
        double halfFt = AircraftBody.Of(type).LengthFt / 2.0;
        LatLon position = Project(exit.Bar.Position, new TrueHeading(awayDeg), halfFt - 20.0);
        AircraftState exiting = StoppedAt(exit.Runway, "OAK", type, position, awayDeg, ExitPhaseFixture.ExitOffTheCentreline(exit.Runway));
        exiting.IndicatedAirspeed = 10;
        Assert.Null(Kind(exiting, exit.Runway, exit.Layout));

        exiting.Ground.AssignedTaxiRoute = new TaxiRoute
        {
            Segments =
            [
                new TaxiRouteSegment
                {
                    Edge = new DirectionalEdge
                    {
                        Edge = exit.Bar.Edges.First(e => OtherEnd(e, exit.Bar).Id == exit.End.Id),
                        FromNode = exit.Bar,
                        ToNode = exit.End,
                    },
                    TaxiwayName = "EXIT",
                },
            ],
            HoldShortPoints = [],
        };

        Assert.False(RunwayOccupancy.IsOnPavement(exiting, exit.Runway));
        Assert.Equal(RunwayUseKind.Fouling, Kind(exiting, exit.Runway, exit.Layout));
    }

    [Fact]
    public void CrosserCleared_PastTheNearSideLine_UnderGiveWay_IsNotAUse()
    {
        if (RunwayBarFixture.RunwayBar("KOAK", "33") is not { } oak)
        {
            return;
        }

        // A GIVEWAY does not stop a runway crossing: it runs on until the tail is past the far marking.
        AircraftState crossing = NearSideCrosser(oak.Layout, oak.Runway, oak.Bar, groundSpeedKts: 12);
        crossing.Ground.Hold = HoldDirective.GiveWay("N2GW");

        Assert.Null(Kind(crossing, oak.Runway, oak.Layout));
    }

    [Fact]
    public void LayoutOfAnotherAirport_NeverFouls()
    {
        if ((RunwayBarFixture.RunwayBar("KOAK", "28L") is not { } oak) || (NavigationDatabase.Instance.GetRunway("HWD", "28L") is not { } hayward))
        {
            return;
        }

        // Designators repeat across airports: OAK's 28L bars say nothing about HWD's 28L, wherever the aircraft stands.
        foreach (GroundNode bar in RunwayHoldBars.RunwayBars(oak.Layout, oak.Runway))
        {
            AircraftState stopped = Placed(oak.Runway, bar, facingRunway: true, 30.0, 0, new TaxiingPhase());
            AttachPhase(stopped, new TaxiingPhase(), hayward);

            Assert.Equal(RunwayUseKind.Fouling, Kind(stopped, oak.Runway, oak.Layout));
            Assert.True(Kind(stopped, hayward, oak.Layout) is null, $"stopped past OAK 28L bar {bar.Id}, it fouls HWD 28L");
        }
    }

    [Fact]
    public void RouteStopAtTheNextRunwaysBar_TailInsideTheFirst_Fouls()
    {
        TestVnasData.EnsureInitialized();
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("SFO");
        if (layout is null)
        {
            return;
        }

        // Leaving one runway toward an uncleared hold short of the next: the navigator stops the nose at the next runway's bar, so a
        // long type's tail is a whole fuselage back from it, still inside the first runway's marking.
        const string type = "B744";
        (RunwayInfo First, RunwayInfo Next, GroundNode From, GroundNode Bar) stub = Assert.IsType<(
            RunwayInfo First,
            RunwayInfo Next,
            GroundNode From,
            GroundNode Bar
        )>(StopBetweenTwoMarkings(layout, type));
        double headingDeg = GeoMath.BearingTo(stub.From.Position, stub.Bar.Position);
        double halfFt = AircraftBody.Of(type).LengthFt / 2.0;
        AircraftState leaving = StoppedAt(
            stub.First,
            "SFO",
            type,
            Project(stub.Bar.Position, new TrueHeading(headingDeg), -(halfFt + 10.0)),
            headingDeg,
            new TaxiingPhase()
        );
        leaving.IndicatedAirspeed = 10;
        leaving.Ground.AssignedTaxiRoute = new TaxiRoute
        {
            Segments =
            [
                new TaxiRouteSegment
                {
                    Edge = new DirectionalEdge
                    {
                        Edge = stub.Bar.Edges.First(e => OtherEnd(e, stub.Bar).Id == stub.From.Id),
                        FromNode = stub.From,
                        ToNode = stub.Bar,
                    },
                    TaxiwayName = "STUB",
                },
            ],
            HoldShortPoints =
            [
                new HoldShortPoint
                {
                    NodeId = stub.Bar.Id,
                    Reason = HoldShortReason.RunwayCrossing,
                    TargetName = stub.Next.Designator,
                    Latitude = stub.Bar.Position.Lat,
                    Longitude = stub.Bar.Position.Lon,
                },
            ],
        };

        Assert.False(RunwayOccupancy.IsOnPavement(leaving, stub.First));
        Assert.Equal(RunwayUseKind.Fouling, Kind(leaving, stub.First, layout));
    }

    [Fact]
    public void ExitingArrival_TailInside_YieldingToTraffic_Fouls()
    {
        if (RunwayBarFixture.RunwayBar("KOAK", "33") is not { } oak)
        {
            return;
        }

        AircraftState exiting = ExitingArrival(oak.Runway, oak.Bar, tailPastFt: 20.0, groundSpeedKts: 10);
        exiting.Ground.SpeedLimit = 0.0;
        exiting.Ground.AutoYieldTarget = "N2YD";

        Assert.Equal(RunwayUseKind.Fouling, Kind(exiting, oak.Runway, oak.Layout));
    }

    [Fact]
    public void ExitingArrival_TailPastTheMarking_Stopped_IsClear()
    {
        if (RunwayBarFixture.RunwayBar("KOAK", "33") is not { } oak)
        {
            return;
        }

        AircraftState exiting = ExitingArrival(oak.Runway, oak.Bar, tailPastFt: -20.0, groundSpeedKts: 0);

        Assert.Null(Kind(exiting, oak.Runway, oak.Layout));
    }

    /// <summary>A bar of <paramref name="runway"/> on the side opposite <paramref name="bar"/>: a crossing's far-side bar.</summary>
    public static GroundNode FarSideBar(AirportGroundLayout layout, RunwayInfo runway, GroundNode bar)
    {
        bool right = RunwayHoldBars.IsRightOfCentreline(runway, bar.Position);
        return RunwayHoldBars.RunwayBars(layout, runway).First(b => RunwayHoldBars.IsRightOfCentreline(runway, b.Position) != right);
    }

    private static AircraftState NearSideCrosser(AirportGroundLayout layout, RunwayInfo runway, GroundNode bar, double groundSpeedKts) =>
        Placed(runway, bar, facingRunway: true, 20.0, groundSpeedKts, new CrossingRunwayPhase(bar.Id, FarSideBar(layout, runway, bar).Id, "33"));

    [Fact]
    public void CrosserCleared_PastTheNearSideLine_Moving_IsNotAUse()
    {
        if (RunwayBarFixture.RunwayBar("KOAK", "33") is not { } oak)
        {
            return;
        }

        AircraftState crossing = NearSideCrosser(oak.Layout, oak.Runway, oak.Bar, groundSpeedKts: 12);

        Assert.False(RunwayOccupancy.IsOnPavement(crossing, oak.Runway));
        Assert.Null(Kind(crossing, oak.Runway, oak.Layout));
    }

    [Fact]
    public void CrosserCleared_PastTheNearSideLine_Held_Fouls()
    {
        if (RunwayBarFixture.RunwayBar("KOAK", "33") is not { } oak)
        {
            return;
        }

        AircraftState crossing = NearSideCrosser(oak.Layout, oak.Runway, oak.Bar, groundSpeedKts: 12);
        crossing.Ground.Hold = HoldDirective.HoldPosition;

        Assert.False(RunwayOccupancy.IsOnPavement(crossing, oak.Runway));
        Assert.Equal(RunwayUseKind.Fouling, Kind(crossing, oak.Runway, oak.Layout));
    }

    [Fact]
    public void Shadow_PastTheLine_NeverFouls()
    {
        if (RunwayBarFixture.RunwayBar("KOAK", "33") is not { } oak)
        {
            return;
        }

        AircraftState phaseless = Placed(oak.Runway, oak.Bar, facingRunway: true, 30.0, 0, phase: null);
        phaseless.Phases = null;

        Assert.Null(Kind(phaseless, oak.Runway, oak.Layout));
        Assert.Null(RunwayOccupancy.ClassifyByGeometry(phaseless, oak.Runway, oak.Layout));
    }

    [Fact]
    public void NullLayout_NeverFouls()
    {
        if (RunwayBarFixture.RunwayBar("KOAK", "33") is not { } oak)
        {
            return;
        }

        AircraftState stopped = Placed(oak.Runway, oak.Bar, facingRunway: true, 30.0, 0, new TaxiingPhase());

        Assert.Null(Kind(stopped, oak.Runway, layout: null));
        Assert.Equal(RunwayUseKind.Fouling, Kind(stopped, oak.Runway, oak.Layout));
    }

    // --- Distance / time helpers ---------------------------------------------------------------

    [Fact]
    public void SecondsToLandingThreshold_UsesGroundSpeed()
    {
        AircraftState ac = Airborne(OnFinal(1.0, 0), 280, ElevationFt + 300, -600, "B738");
        double expected = 3600.0 / ac.GroundSpeed;

        Assert.Equal(1.0, RunwayOccupancy.DistanceToLandingThresholdNm(ac, Runway, layout: null), 2);
        Assert.Equal(expected, RunwayOccupancy.SecondsToLandingThreshold(ac, Runway, layout: null), 0.5);
    }

    [Fact]
    public void SecondsToLandingThreshold_IsInfiniteWhenStopped()
    {
        AircraftState ac = Ground(OnRunway(500, 0), 280, 0, phase: null, assignedRunway: null);

        Assert.Equal(double.PositiveInfinity, RunwayOccupancy.SecondsToLandingThreshold(ac, Runway, layout: null));
    }

    // --- Real navdata ----------------------------------------------------------------------------

    [Fact]
    public void Oak28R_PhaselessOnTheNumbers_IsOnSurface()
    {
        RunwayInfo? oak = NavigationDatabase.Instance.GetRunway("OAK", "28R");
        if (oak is null)
        {
            return;
        }

        var threshold = new LatLon(oak.ThresholdLatitude, oak.ThresholdLongitude);
        LatLon pos = GeoMath.ProjectPoint(threshold, oak.TrueHeading, 400 / GeoMath.FeetPerNm);
        var ac = new AircraftState
        {
            Callsign = "OAK1",
            AircraftType = "B738",
            Position = pos,
            TrueHeading = oak.TrueHeading,
            TrueTrack = oak.TrueHeading,
            Altitude = oak.ElevationFt,
            IsOnGround = true,
        };
        var onFinal = new AircraftState
        {
            Callsign = "OAK2",
            AircraftType = "B738",
            Position = GeoMath.ProjectPoint(threshold, oak.TrueHeading.ToReciprocal(), 1.5),
            TrueHeading = oak.TrueHeading,
            TrueTrack = oak.TrueHeading,
            Altitude = oak.ElevationFt + 480,
            IndicatedAirspeed = 140,
            VerticalSpeed = -700,
            IsOnGround = false,
        };

        Assert.Equal(RunwayUseKind.OnSurface, RunwayOccupancy.Classify(ac, oak, layout: null)?.Kind);
        Assert.Equal(RunwayUseKind.ShortFinal, RunwayOccupancy.Classify(onFinal, oak, layout: null)?.Kind);
    }
}
