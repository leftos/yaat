using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Tests;

public class InterceptDetectionTests
{
    // OAK runway 28L: threshold at (37.72, -122.22), heading 280°
    private static readonly RunwayInfo TestRunway = TestRunwayFactory.Make(
        designator: "28L",
        airportId: "OAK",
        thresholdLat: 37.72,
        thresholdLon: -122.22,
        endLat: 37.72,
        endLon: -122.26,
        heading: 280,
        elevationFt: 10
    );

    [Fact]
    public void InterceptTooClose_TriggersWarning()
    {
        // Approach gate database not initialized → uses 7nm default
        // Place aircraft 5nm from threshold, slightly off course so InterceptCoursePhase captures.
        // (5nm < 7nm default → should warn at capture)
        var (aircraft, phaseList) = CreateAircraftOnFinal(distanceNm: 5.0, heading: 275);

        var phase = new InterceptCoursePhase
        {
            FinalApproachCourse = TestRunway.TrueHeading,
            ThresholdLat = TestRunway.ThresholdLatitude,
            ThresholdLon = TestRunway.ThresholdLongitude,
            ApproachId = "I28L",
        };
        phaseList.Add(phase);
        phaseList.Add(new FinalApproachPhase());
        phaseList.Add(new LandingPhase());
        var ctx = CreateContext(aircraft);

        phase.Status = PhaseStatus.Active;
        phase.OnStart(ctx);
        phase.OnTick(ctx); // Should capture and warn

        Assert.Single(aircraft.PendingWarnings);
        Assert.Contains("Illegal intercept", aircraft.PendingWarnings[0]);
        Assert.Contains("7110.65", aircraft.PendingWarnings[0]);
    }

    [Fact]
    public void InterceptFarEnough_NoWarning()
    {
        // Place aircraft 8nm from threshold (8 > 7nm default → no warning)
        var (aircraft, phaseList) = CreateAircraftOnFinal(distanceNm: 8.0, heading: 275);

        var phase = new InterceptCoursePhase
        {
            FinalApproachCourse = TestRunway.TrueHeading,
            ThresholdLat = TestRunway.ThresholdLatitude,
            ThresholdLon = TestRunway.ThresholdLongitude,
            ApproachId = "I28L",
        };
        phaseList.Add(phase);
        phaseList.Add(new FinalApproachPhase());
        phaseList.Add(new LandingPhase());
        var ctx = CreateContext(aircraft);

        phase.Status = PhaseStatus.Active;
        phase.OnStart(ctx);
        phase.OnTick(ctx);

        Assert.Empty(aircraft.PendingWarnings);
    }

    [Fact]
    public void PatternTraffic_ExemptFromWarning()
    {
        // Pattern traffic (TrafficDirection set) should never warn
        var (aircraft, phaseList) = CreateAircraftOnFinal(distanceNm: 3.0, heading: 275);
        phaseList.TrafficDirection = PatternDirection.Left;

        var phase = new InterceptCoursePhase
        {
            FinalApproachCourse = TestRunway.TrueHeading,
            ThresholdLat = TestRunway.ThresholdLatitude,
            ThresholdLon = TestRunway.ThresholdLongitude,
            ApproachId = "I28L",
        };
        phaseList.Add(phase);
        phaseList.Add(new FinalApproachPhase());
        phaseList.Add(new LandingPhase());
        var ctx = CreateContext(aircraft);

        phase.Status = PhaseStatus.Active;
        phase.OnStart(ctx);
        phase.OnTick(ctx);

        Assert.Empty(aircraft.PendingWarnings);
    }

    [Fact]
    public void InterceptCheck_FiresOnlyOnce()
    {
        // Capture fires once → warning fires once. After capture, phase completes.
        var (aircraft, phaseList) = CreateAircraftOnFinal(distanceNm: 5.0, heading: 275);

        var phase = new InterceptCoursePhase
        {
            FinalApproachCourse = TestRunway.TrueHeading,
            ThresholdLat = TestRunway.ThresholdLatitude,
            ThresholdLon = TestRunway.ThresholdLongitude,
            ApproachId = "I28L",
        };
        phaseList.Add(phase);
        phaseList.Add(new FinalApproachPhase());
        phaseList.Add(new LandingPhase());
        var ctx = CreateContext(aircraft);

        phase.Status = PhaseStatus.Active;
        phase.OnStart(ctx);
        bool complete = phase.OnTick(ctx);

        // InterceptCoursePhase completes on capture — only one warning
        Assert.True(complete);
        Assert.Single(aircraft.PendingWarnings);
    }

    [Fact]
    public void AircraftNotEstablished_NoWarningUntilCapture()
    {
        // Aircraft at 5nm but heading 45° off runway heading →
        // not aligned → InterceptCoursePhase does not capture
        var (aircraft, phaseList) = CreateAircraftOnFinal(distanceNm: 5.0, heading: 325); // 280 + 45

        var phase = new InterceptCoursePhase
        {
            FinalApproachCourse = TestRunway.TrueHeading,
            ThresholdLat = TestRunway.ThresholdLatitude,
            ThresholdLon = TestRunway.ThresholdLongitude,
            ApproachId = "I28L",
        };
        phaseList.Add(phase);
        phaseList.Add(new FinalApproachPhase());
        phaseList.Add(new LandingPhase());
        var ctx = CreateContext(aircraft);

        phase.Status = PhaseStatus.Active;
        phase.OnStart(ctx);
        phase.OnTick(ctx);

        // Should not warn because aircraft hasn't captured yet
        Assert.Empty(aircraft.PendingWarnings);
    }

    [Fact]
    public void ScoreDistanceLegal_NoWarning_WhenFarEnough()
    {
        // Aircraft directly on final at 8nm — no InterceptCoursePhase involved.
        // FinalApproachPhase scores distance as legal but no warning (warnings are at capture).
        var (aircraft, phaseList) = CreateAircraftOnFinal(distanceNm: 8.0, heading: 280);

        var phase = new FinalApproachPhase();
        phaseList.Add(phase);
        var ctx = CreateContext(aircraft);

        phase.OnStart(ctx);
        phase.OnTick(ctx);

        Assert.Empty(aircraft.PendingWarnings);
        Assert.NotNull(aircraft.ActiveApproachScore);
        Assert.True(aircraft.ActiveApproachScore.IsInterceptDistanceLegal);
    }

    private static (AircraftState Aircraft, PhaseList PhaseList) CreateAircraftOnFinal(double distanceNm, double heading)
    {
        // Project aircraft position along reciprocal of runway heading
        var (lat, lon) = GeoMath.ProjectPoint(
            TestRunway.ThresholdLatitude,
            TestRunway.ThresholdLongitude,
            TestRunway.TrueHeading.ToReciprocal(),
            distanceNm
        );

        var clearance = new ApproachClearance
        {
            ApproachId = "I28L",
            AirportCode = "OAK",
            RunwayId = "28L",
            FinalApproachCourse = TestRunway.TrueHeading,
        };

        var phaseList = new PhaseList { AssignedRunway = TestRunway, ActiveApproach = clearance };

        var aircraft = new AircraftState
        {
            Callsign = "TEST001",
            AircraftType = "B738",
            Latitude = lat,
            Longitude = lon,
            TrueHeading = new TrueHeading(heading),
            Altitude = 2000,
            Phases = phaseList,
        };

        return (aircraft, phaseList);
    }

    private static PhaseContext CreateContext(AircraftState aircraft)
    {
        return new PhaseContext
        {
            Aircraft = aircraft,
            Targets = aircraft.Targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = 1.0,
            Runway = TestRunway,
            FieldElevation = TestRunway.ElevationFt,
            Logger = NullLogger.Instance,
        };
    }
}
