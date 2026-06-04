using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Tests;

/// <summary>
/// The glideslope must not be captured (descent must not begin) until the aircraft is laterally
/// established on the final approach course — within ~5° of the FAC and on centerline — matching
/// the real "maintain until established on the localizer, cleared ILS" sequence (AIM 5-4-7;
/// 7110.65 5-9-1.3 / 5-9-4). Forced intercepts (PTACF / implied-PTAC) and visual approaches bypass
/// the lateral gate because they intentionally S-turn / are pilot-judged.
/// </summary>
public class FinalApproachGlideslopeEstablishedTests
{
    private static (FinalApproachPhase phase, PhaseContext ctx) Setup(
        double headingOffsetDeg,
        double lateralOffsetNm,
        double? captureAngleDeg,
        string approachId = "I28R"
    )
    {
        var rwy = TestRunwayFactory.Make(
            designator: "28R",
            airportId: "OAK",
            thresholdLat: 37.72,
            thresholdLon: -122.22,
            heading: 280,
            elevationFt: 9
        );

        const double distNm = 6.0;
        double gsAlt = GlideSlopeGeometry.AltitudeAtDistance(distNm, 9, GlideSlopeGeometry.AngleForCategory(AircraftCategory.Jet));
        double startAlt = gsAlt + 200; // above the GS: capture → descend, hold → stays high

        var reciprocal = rwy.TrueHeading.ToReciprocal();
        var onCenter = GeoMath.ProjectPoint(rwy.ThresholdLatitude, rwy.ThresholdLongitude, reciprocal, distNm);
        var posCoord =
            lateralOffsetNm == 0
                ? onCenter
                : GeoMath.ProjectPoint(onCenter.Lat, onCenter.Lon, new TrueHeading(rwy.TrueHeading.Degrees + 90), lateralOffsetNm);

        var heading = new TrueHeading(rwy.TrueHeading.Degrees + headingOffsetDeg);
        var ac = new AircraftState
        {
            Callsign = "UAL123",
            AircraftType = "B738",
            Position = new LatLon(posCoord.Lat, posCoord.Lon),
            TrueHeading = heading,
            TrueTrack = heading,
            Altitude = startAlt,
            IndicatedAirspeed = 170,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan { Destination = "OAK" },
        };

        var clearance = new ApproachClearance
        {
            ApproachId = approachId,
            AirportCode = "OAK",
            RunwayId = "28R",
            FinalApproachCourse = rwy.TrueHeading,
            InterceptCaptureAngleDeg = captureAngleDeg,
        };

        ac.Phases = new PhaseList
        {
            AssignedRunway = rwy,
            ActiveApproach = clearance,
            LandingClearance = ClearanceType.ClearedToLand,
        };
        ac.Targets.TargetSpeed = 170;
        ac.Targets.AssignedAltitude = startAlt;
        ac.Targets.TargetAltitude = startAlt;

        var phase = new FinalApproachPhase { SkipInterceptCheck = true };
        var ctx = new PhaseContext
        {
            Aircraft = ac,
            Targets = ac.Targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = 1.0,
            Runway = rwy,
            FieldElevation = rwy.ElevationFt,
            Logger = NullLogger.Instance,
            AutoClearedToLand = true,
        };
        phase.OnStart(ctx);
        return (phase, ctx);
    }

    private static bool GsCaptured(FinalApproachPhase phase) => ((FinalApproachPhaseDto)phase.ToSnapshot()).GsCaptured;

    [Fact]
    public void OffCourseHeading_AtGsAltitude_DoesNotCaptureGlideslope()
    {
        var (phase, ctx) = Setup(headingOffsetDeg: 20, lateralOffsetNm: 0, captureAngleDeg: 20);

        phase.OnTick(ctx);

        Assert.False(GsCaptured(phase), "Glideslope must not capture while crossing the localizer 20° off the FAC.");
    }

    [Fact]
    public void LaterallyDisplaced_AtGsAltitude_DoesNotCaptureGlideslope()
    {
        var (phase, ctx) = Setup(headingOffsetDeg: 0, lateralOffsetNm: 0.3, captureAngleDeg: 20);

        phase.OnTick(ctx);

        Assert.False(GsCaptured(phase), "Glideslope must not capture while 0.3 nm off centerline.");
    }

    [Fact]
    public void Established_AtGsAltitude_CapturesGlideslope()
    {
        var (phase, ctx) = Setup(headingOffsetDeg: 0, lateralOffsetNm: 0, captureAngleDeg: 20);

        phase.OnTick(ctx);

        Assert.True(GsCaptured(phase), "Glideslope must capture once aligned (≤5°) and on centerline.");
    }

    [Fact]
    public void ForcedIntercept_OffCourse_BypassesGate_CapturesGlideslope()
    {
        // Capture angle > 30° marks a forced intercept (PTACF / implied-PTAC) that S-turns back.
        var (phase, ctx) = Setup(headingOffsetDeg: 20, lateralOffsetNm: 0, captureAngleDeg: 45);

        phase.OnTick(ctx);

        Assert.True(GsCaptured(phase), "Forced intercept bypasses the lateral GS gate.");
    }

    [Fact]
    public void VisualApproach_OffCourse_BypassesGate_CapturesGlideslope()
    {
        var (phase, ctx) = Setup(headingOffsetDeg: 20, lateralOffsetNm: 0, captureAngleDeg: 20, approachId: "VIS28R");

        phase.OnTick(ctx);

        Assert.True(GsCaptured(phase), "Visual approaches have no electronic-GS establishment gate.");
    }
}
