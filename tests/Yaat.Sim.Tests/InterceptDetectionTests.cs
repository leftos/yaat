using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
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
        // Place aircraft 5nm from threshold on the extended centerline
        // (5nm < 7nm default → should warn)
        var (aircraft, phaseList) = CreateAircraftOnFinal(distanceNm: 5.0, heading: 280);

        var phase = new FinalApproachPhase();
        phaseList.Add(phase);
        var ctx = CreateContext(aircraft);

        phase.OnStart(ctx);
        phase.OnTick(ctx);

        Assert.Single(aircraft.PendingWarnings);
        Assert.Contains("Illegal intercept", aircraft.PendingWarnings[0]);
        Assert.Contains("7110.65", aircraft.PendingWarnings[0]);
    }

    [Fact]
    public void InterceptFarEnough_NoWarning()
    {
        // Place aircraft 8nm from threshold (8 > 7nm default → no warning)
        var (aircraft, phaseList) = CreateAircraftOnFinal(distanceNm: 8.0, heading: 280);

        var phase = new FinalApproachPhase();
        phaseList.Add(phase);
        var ctx = CreateContext(aircraft);

        phase.OnStart(ctx);
        phase.OnTick(ctx);

        Assert.Empty(aircraft.PendingWarnings);
    }

    [Fact]
    public void PatternTraffic_ExemptFromWarning()
    {
        // Pattern traffic (TrafficDirection set) should never warn
        var (aircraft, phaseList) = CreateAircraftOnFinal(distanceNm: 3.0, heading: 280);
        phaseList.TrafficDirection = PatternDirection.Left;

        var phase = new FinalApproachPhase();
        phaseList.Add(phase);
        var ctx = CreateContext(aircraft);

        phase.OnStart(ctx);
        phase.OnTick(ctx);

        Assert.Empty(aircraft.PendingWarnings);
    }

    [Fact]
    public void InterceptCheck_FiresOnlyOnce()
    {
        // Place aircraft 5nm from threshold → triggers warning on first tick
        var (aircraft, phaseList) = CreateAircraftOnFinal(distanceNm: 5.0, heading: 280);

        var phase = new FinalApproachPhase();
        phaseList.Add(phase);
        var ctx = CreateContext(aircraft);

        phase.OnStart(ctx);
        phase.OnTick(ctx);
        phase.OnTick(ctx); // Second tick should not add another warning
        phase.OnTick(ctx); // Third tick too

        Assert.Single(aircraft.PendingWarnings);
    }

    [Fact]
    public void AircraftNotEstablished_NoWarningUntilEstablished()
    {
        // Aircraft at 5nm but heading 45° off runway heading →
        // not established → no warning on first tick
        var (aircraft, phaseList) = CreateAircraftOnFinal(distanceNm: 5.0, heading: 325); // 280 + 45 = 325

        var phase = new FinalApproachPhase();
        phaseList.Add(phase);
        var ctx = CreateContext(aircraft);

        phase.OnStart(ctx);
        phase.OnTick(ctx);

        // Should not warn because aircraft is not aligned with runway
        Assert.Empty(aircraft.PendingWarnings);
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

        var phaseList = new PhaseList { AssignedRunway = TestRunway };

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
            Logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
        };
    }
}
