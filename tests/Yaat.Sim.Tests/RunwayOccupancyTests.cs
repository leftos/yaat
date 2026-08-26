using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;

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
        var end = GeoMath.ProjectPoint(threshold, new TrueHeading(280), PavementLengthFt / GeoMath.FeetPerNm);
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
        var along = GeoMath.ProjectPoint(Threshold, Runway.TrueHeading, alongFt / GeoMath.FeetPerNm);
        return rightFt == 0 ? along : GeoMath.ProjectPoint(along, Runway.TrueHeading + 90, rightFt / GeoMath.FeetPerNm);
    }

    private static LatLon OnFinal(double distanceNm, double crossTrackNm)
    {
        var along = GeoMath.ProjectPoint(Threshold, Runway.TrueHeading.ToReciprocal(), distanceNm);
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
        var ac = Ground(OnRunway(3000, 500), 280, 0, new LinedUpAndWaitingPhase(), Runway);

        Assert.Equal(RunwayUseKind.OnSurface, Kind(ac));
    }

    [Fact]
    public void TakeoffPhase_IsDeparting()
    {
        var ac = Ground(OnRunway(1500, 0), 280, 90, new TakeoffPhase(), Runway);

        Assert.Equal(RunwayUseKind.Departing, Kind(ac));
    }

    [Fact]
    public void TouchAndGoOnTheGround_IsOnSurface_NotDeparting()
    {
        var ac = Ground(OnRunway(2500, 0), 280, 70, new TouchAndGoPhase(), Runway);

        Assert.Equal(RunwayUseKind.OnSurface, Kind(ac));
    }

    [Fact]
    public void LandingPhase_AirborneIsLanding_OnGroundIsOnSurface()
    {
        var flaring = Airborne(OnRunway(800, 0), 280, ElevationFt + 20, -300, "B738");
        AttachPhase(flaring, new LandingPhase(), Runway);
        var rolling = Ground(OnRunway(3000, 0), 280, 80, new LandingPhase(), Runway);

        Assert.Equal(RunwayUseKind.Landing, Kind(flaring));
        Assert.Equal(RunwayUseKind.OnSurface, Kind(rolling));
    }

    [Fact]
    public void PhaseEvidenceForAnotherRunway_DoesNotCount()
    {
        var other = TestRunwayFactory.Make(designator: "10L", thresholdLat: 37.1, thresholdLon: -122.1, heading: 100);
        var ac = Ground(OnRunway(3000, 500), 280, 0, new LinedUpAndWaitingPhase(), other);

        Assert.Null(Kind(ac));
    }

    [Fact]
    public void HoldingInPosition_CountsOnlyWhenPhysicallyOnThePavement()
    {
        var onPavement = Ground(OnRunway(200, 0), 280, 0, new HoldingInPositionPhase(), Runway);
        var farSide = Ground(OnRunway(3000, 500), 280, 0, new HoldingInPositionPhase(), Runway);

        Assert.Equal(RunwayUseKind.OnSurface, Kind(onPavement));
        Assert.Null(Kind(farSide));
        Assert.Equal(RunwayUseKind.OnSurface, RunwayOccupancy.ClassifyByPhase(onPavement, Runway));
        Assert.Null(RunwayOccupancy.ClassifyByPhase(onPavement, runway: null));
    }

    [Fact]
    public void RunwayExit_IsOnSurfaceWhileRollingOnTheCenterline()
    {
        // A fresh RunwayExitPhase is still rolling on the centerline (the state it starts in).
        var exiting = Ground(OnRunway(6000, 0), 280, 30, new RunwayExitPhase(), Runway);

        Assert.Equal(RunwayUseKind.OnSurface, RunwayOccupancy.ClassifyByPhase(exiting, runway: null));
        Assert.Equal(RunwayUseKind.OnSurface, Kind(exiting));
    }

    [Fact]
    public void TaxiingAcrossThePavement_IsCrossing()
    {
        var ac = Ground(OnRunway(4000, 0), 10, 15, new TaxiingPhase(), assignedRunway: null);

        Assert.Equal(RunwayUseKind.Crossing, Kind(ac));
        Assert.False(RunwayOccupancy.OccupiesSurface(Kind(ac)));
    }

    [Fact]
    public void TaxiingAlongThePavement_IsCrossingForPhaseDrivenAircraft()
    {
        // Back-taxi under a taxi phase: today's ground-conflict logic gives it no runway priority, and
        // the classifier must not change that.
        var ac = Ground(OnRunway(4000, 0), 100, 15, new TaxiingPhase(), assignedRunway: null);

        Assert.Equal(RunwayUseKind.Crossing, Kind(ac));
    }

    // --- Geometry (phase-less aircraft) ---------------------------------------------------------

    [Fact]
    public void PhaselessOnTheNumbersAligned_IsOnSurface()
    {
        var ac = Ground(OnRunway(300, 20), 280, 0, phase: null, assignedRunway: null);

        Assert.Equal(RunwayUseKind.OnSurface, Kind(ac));
    }

    [Fact]
    public void PhaselessBackTaxi_IsOnSurface()
    {
        var ac = Ground(OnRunway(4000, 0), 100, 20, phase: null, assignedRunway: null);

        Assert.Equal(RunwayUseKind.OnSurface, Kind(ac));
    }

    [Fact]
    public void PhaselessRollingAligned_IsDeparting()
    {
        var ac = Ground(OnRunway(2000, 0), 280, 60, phase: null, assignedRunway: null);

        Assert.Equal(RunwayUseKind.Departing, Kind(ac));
    }

    [Fact]
    public void PhaselessOnPavementNotAligned_IsCrossing()
    {
        var ac = Ground(OnRunway(4000, 0), 10, 15, phase: null, assignedRunway: null);

        Assert.Equal(RunwayUseKind.Crossing, Kind(ac));
    }

    [Fact]
    public void PhaselessOnParallelTaxiway_IsNotOnTheRunway()
    {
        var ac = Ground(OnRunway(4000, 200), 280, 15, phase: null, assignedRunway: null);

        Assert.Null(Kind(ac));
    }

    [Fact]
    public void PavementEdge_HalfWidthPlusSlackIsTheBoundary()
    {
        double edge = (Runway.WidthFt / 2.0) + RunwayOccupancy.LateralSlackFt;
        var inside = Ground(OnRunway(4000, edge - 2), 280, 0, phase: null, assignedRunway: null);
        var outside = Ground(OnRunway(4000, edge + 2), 280, 0, phase: null, assignedRunway: null);

        Assert.Equal(RunwayUseKind.OnSurface, Kind(inside));
        Assert.Null(Kind(outside));
    }

    [Fact]
    public void PhaselessOnHighSpeedExit_IsCrossing()
    {
        // 30° off the axis, still inside the pavement edge: leaving, not using, the runway.
        var ac = Ground(OnRunway(6000, 40), 250, 40, phase: null, assignedRunway: null);

        Assert.Equal(RunwayUseKind.Crossing, Kind(ac));
    }

    [Fact]
    public void ShortFinal_AlignedDescendingInsideTwoMiles()
    {
        var ac = Airborne(OnFinal(1.5, 0), 280, ElevationFt + 500, -700, "B738");

        Assert.Equal(RunwayUseKind.ShortFinal, Kind(ac));
    }

    [Fact]
    public void ShortFinal_LevelAtMdaStillCounts_ClimbingDoesNot()
    {
        var level = Airborne(OnFinal(1.5, 0), 280, ElevationFt + 500, 0, "B738");
        var climbing = Airborne(OnFinal(1.5, 0), 280, ElevationFt + 500, +800, "B738");

        Assert.Equal(RunwayUseKind.ShortFinal, Kind(level));
        Assert.Null(Kind(climbing));
    }

    [Fact]
    public void ShortFinal_OutsideTwoMiles_OrTooHigh_OrOffCourse_IsNothing()
    {
        var far = Airborne(OnFinal(4.0, 0), 280, ElevationFt + 1200, -700, "B738");
        var high = Airborne(OnFinal(1.5, 0), 280, ElevationFt + 2000, -700, "B738");
        var offCourse = Airborne(OnFinal(1.5, 0.6), 280, ElevationFt + 500, -700, "B738");
        var wrongWay = Airborne(OnFinal(1.5, 0), 100, ElevationFt + 500, -700, "B738");

        Assert.Null(Kind(far));
        Assert.Null(Kind(high));
        Assert.Null(Kind(offCourse));
        Assert.Null(Kind(wrongWay));
    }

    [Fact]
    public void ShortFinal_ThirtyDegreeIntercept_StillCounts()
    {
        var ac = Airborne(OnFinal(1.8, 0.2), 250, ElevationFt + 600, -600, "B738");

        Assert.Equal(RunwayUseKind.ShortFinal, Kind(ac));
    }

    [Fact]
    public void Landing_OverThePavementBelowFiftyFeet()
    {
        var ac = Airborne(OnRunway(500, 0), 280, ElevationFt + 30, -400, "B738");

        Assert.Equal(RunwayUseKind.Landing, Kind(ac));
    }

    [Fact]
    public void Landing_AglIsMeasuredFromTheAlignedEnd()
    {
        // 10 ft above the far (opposite) end's elevation, which sits 80 ft below the threshold end.
        var sloped = TestRunwayFactory.Make(
            designator: "28",
            thresholdLat: Runway.Lat1,
            thresholdLon: Runway.Lon1,
            endLat: Runway.Lat2,
            endLon: Runway.Lon2,
            heading: 280,
            elevationFt: ElevationFt,
            endElevationFt: ElevationFt - 80
        );
        var reciprocal = sloped.ForApproach("10");
        var pos = GeoMath.ProjectPoint(new LatLon(sloped.Lat2, sloped.Lon2), new TrueHeading(100), 400 / GeoMath.FeetPerNm);
        var ac = Airborne(pos, 100, ElevationFt - 80 + 30, -300, "B738");

        Assert.Equal(RunwayUseKind.Landing, RunwayOccupancy.Classify(ac, reciprocal, layout: null)?.Kind);
        // Against the field elevation it would read −50 ft AGL; the classifier must not use it.
        Assert.Null(RunwayOccupancy.Classify(Airborne(pos, 100, ElevationFt + 30, -300, "B738"), reciprocal, layout: null)?.Kind);
    }

    [Fact]
    public void Helicopters_NeverClassifyAsLandingOrShortFinal()
    {
        var overPavement = Airborne(OnRunway(500, 0), 280, ElevationFt + 30, -100, "EC35");
        var onFinal = Airborne(OnFinal(1.0, 0), 280, ElevationFt + 300, -400, "EC35");
        var onGround = Ground(OnRunway(500, 0), 280, 0, phase: null, assignedRunway: null);
        onGround.AircraftType = "EC35";

        Assert.Null(Kind(overPavement));
        Assert.Null(Kind(onFinal));
        Assert.Equal(RunwayUseKind.OnSurface, Kind(onGround));
    }

    [Fact]
    public void OccupiesSurface_IsTrueForDepartingLandingAndOnSurfaceOnly()
    {
        Assert.True(RunwayOccupancy.OccupiesSurface(RunwayUseKind.Departing));
        Assert.True(RunwayOccupancy.OccupiesSurface(RunwayUseKind.Landing));
        Assert.True(RunwayOccupancy.OccupiesSurface(RunwayUseKind.OnSurface));
        Assert.False(RunwayOccupancy.OccupiesSurface(RunwayUseKind.ShortFinal));
        Assert.False(RunwayOccupancy.OccupiesSurface(RunwayUseKind.Crossing));
        Assert.False(RunwayOccupancy.OccupiesSurface(null));
    }

    // --- Distance / time helpers ---------------------------------------------------------------

    [Fact]
    public void SecondsToLandingThreshold_UsesGroundSpeed()
    {
        var ac = Airborne(OnFinal(1.0, 0), 280, ElevationFt + 300, -600, "B738");
        double expected = 3600.0 / ac.GroundSpeed;

        Assert.Equal(1.0, RunwayOccupancy.DistanceToLandingThresholdNm(ac, Runway, layout: null), 2);
        Assert.Equal(expected, RunwayOccupancy.SecondsToLandingThreshold(ac, Runway, layout: null), 0.5);
    }

    [Fact]
    public void SecondsToLandingThreshold_IsInfiniteWhenStopped()
    {
        var ac = Ground(OnRunway(500, 0), 280, 0, phase: null, assignedRunway: null);

        Assert.Equal(double.PositiveInfinity, RunwayOccupancy.SecondsToLandingThreshold(ac, Runway, layout: null));
    }

    // --- Real navdata ----------------------------------------------------------------------------

    [Fact]
    public void Oak28R_PhaselessOnTheNumbers_IsOnSurface()
    {
        var oak = NavigationDatabase.Instance.GetRunway("OAK", "28R");
        if (oak is null)
        {
            return;
        }

        var threshold = new LatLon(oak.ThresholdLatitude, oak.ThresholdLongitude);
        var pos = GeoMath.ProjectPoint(threshold, oak.TrueHeading, 400 / GeoMath.FeetPerNm);
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
