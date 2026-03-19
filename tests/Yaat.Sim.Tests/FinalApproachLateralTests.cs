using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Tests;

public class FinalApproachLateralTests
{
    private readonly ITestOutputHelper _output;

    public FinalApproachLateralTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private record ScenarioResult(double FinalXte, double FinalHdgDiff, int CompleteTick);

    private ScenarioResult RunScenario(
        string label,
        double alongTrackNm,
        double offsetNm,
        double startHeading,
        double startAltitude,
        double startSpeed,
        AircraftCategory category,
        bool isPatternTraffic,
        PatternDirection patternDir = PatternDirection.Left,
        int maxTicks = 600
    )
    {
        var rwy = TestRunwayFactory.Make(designator: "28", heading: 280, elevationFt: 6);

        // Place aircraft: offset perpendicular to centerline at given along-track distance
        var reciprocal = rwy.TrueHeading.ToReciprocal();
        var alongPoint = GeoMath.ProjectPoint(rwy.ThresholdLatitude, rwy.ThresholdLongitude, reciprocal, alongTrackNm);
        double perpSign = patternDir == PatternDirection.Left ? -90.0 : 90.0;
        var perpHeading = rwy.TrueHeading + perpSign;
        var startPos = GeoMath.ProjectPoint(alongPoint.Lat, alongPoint.Lon, perpHeading, offsetNm);

        var ac = new AircraftState
        {
            Callsign = "TEST",
            AircraftType = category == AircraftCategory.Piston ? "C172" : "B738",
            Latitude = startPos.Lat,
            Longitude = startPos.Lon,
            TrueHeading = new TrueHeading(startHeading),
            Altitude = startAltitude,
            IndicatedAirspeed = startSpeed,
            IsOnGround = false,
            Departure = "KTEST",
        };
        ac.Phases = new PhaseList { AssignedRunway = rwy };
        if (isPatternTraffic)
        {
            ac.Phases.TrafficDirection = patternDir;
        }

        ac.Targets.TargetSpeed = CategoryPerformance.ApproachSpeed(category);
        if (isPatternTraffic)
        {
            ac.Targets.TurnRateOverride = CategoryPerformance.PatternTurnRate(category);
        }

        var phase = new FinalApproachPhase { SkipInterceptCheck = true };
        var ctx = new PhaseContext
        {
            Aircraft = ac,
            Targets = ac.Targets,
            Category = category,
            DeltaSeconds = 1.0,
            Runway = rwy,
            FieldElevation = rwy.ElevationFt,
            Logger = NullLogger.Instance,
            AutoClearedToLand = true,
        };

        phase.OnStart(ctx);

        _output.WriteLine($"--- {label} ---");
        _output.WriteLine("tick,lat,lon,heading,targetHeading,crossTrackNm,distNm,groundSpeed");

        int completeTick = maxTicks;
        for (int tick = 0; tick < maxTicks; tick++)
        {
            double xte = GeoMath.SignedCrossTrackDistanceNm(
                ac.Latitude,
                ac.Longitude,
                rwy.ThresholdLatitude,
                rwy.ThresholdLongitude,
                rwy.TrueHeading
            );
            double dist = GeoMath.DistanceNm(ac.Latitude, ac.Longitude, rwy.ThresholdLatitude, rwy.ThresholdLongitude);

            _output.WriteLine(
                $"{tick},{ac.Latitude:F6},{ac.Longitude:F6},{ac.TrueHeading.Degrees:F1},{ac.Targets.TargetTrueHeading?.Degrees:F1},{xte:F4},{dist:F3},{ac.GroundSpeed:F1}"
            );

            bool done = phase.OnTick(ctx);
            FlightPhysics.Update(ac, 1.0);

            if (done)
            {
                completeTick = tick;
                break;
            }
        }

        double finalXte = Math.Abs(
            GeoMath.SignedCrossTrackDistanceNm(ac.Latitude, ac.Longitude, rwy.ThresholdLatitude, rwy.ThresholdLongitude, rwy.TrueHeading)
        );
        double finalHdgDiff = Math.Abs(ac.TrueHeading.SignedAngleTo(rwy.TrueHeading));

        _output.WriteLine($"# {label}: XTE={finalXte:F4}nm, hdgDiff={finalHdgDiff:F1}°, tick={completeTick}");
        _output.WriteLine("");

        return new ScenarioResult(finalXte, finalHdgDiff, completeTick);
    }

    // ---- VFR Pattern: smooth arc behavior ----

    [Fact]
    public void VFR_Pattern_1nm_Final()
    {
        var waypoints = PatternGeometry.Compute(
            TestRunwayFactory.Make(designator: "28", heading: 280, elevationFt: 6),
            AircraftCategory.Piston,
            PatternDirection.Left
        );

        var r = RunScenario(
            "VFR Pattern 1nm",
            alongTrackNm: 1.0,
            offsetNm: 0.8,
            startHeading: waypoints.BaseHeading.Degrees,
            startAltitude: 500,
            startSpeed: 80,
            AircraftCategory.Piston,
            isPatternTraffic: true
        );

        Assert.True(r.FinalXte < 0.1, $"XTE {r.FinalXte:F4}nm");
        Assert.True(r.FinalHdgDiff < 15, $"HdgDiff {r.FinalHdgDiff:F1}°");
    }

    [Fact]
    public void VFR_Pattern_3nm_Final()
    {
        var waypoints = PatternGeometry.Compute(
            TestRunwayFactory.Make(designator: "28", heading: 280, elevationFt: 6),
            AircraftCategory.Piston,
            PatternDirection.Left
        );

        var r = RunScenario(
            "VFR Pattern 3nm",
            alongTrackNm: 3.0,
            offsetNm: 0.8,
            startHeading: waypoints.BaseHeading.Degrees,
            startAltitude: 1200,
            startSpeed: 80,
            AircraftCategory.Piston,
            isPatternTraffic: true
        );

        Assert.True(r.FinalXte < 0.05, $"XTE {r.FinalXte:F4}nm");
        Assert.True(r.FinalHdgDiff < 5, $"HdgDiff {r.FinalHdgDiff:F1}°");
    }

    [Fact]
    public void VFR_Pattern_RightTraffic_1nm()
    {
        var waypoints = PatternGeometry.Compute(
            TestRunwayFactory.Make(designator: "28", heading: 280, elevationFt: 6),
            AircraftCategory.Piston,
            PatternDirection.Right
        );

        var r = RunScenario(
            "VFR Right Pattern 1nm",
            alongTrackNm: 1.0,
            offsetNm: 0.8,
            startHeading: waypoints.BaseHeading.Degrees,
            startAltitude: 500,
            startSpeed: 80,
            AircraftCategory.Piston,
            isPatternTraffic: true,
            patternDir: PatternDirection.Right
        );

        Assert.True(r.FinalXte < 0.1, $"XTE {r.FinalXte:F4}nm");
        Assert.True(r.FinalHdgDiff < 15, $"HdgDiff {r.FinalHdgDiff:F1}°");
    }

    // ---- IFR / Visual Approach: ~30° intercept behavior ----

    [Fact]
    public void IFR_Visual_3nm_Final()
    {
        // Realistic: 30° intercept heading (250° for rwy 280), 0.8nm offset, 130kts
        var r = RunScenario(
            "IFR Visual 3nm",
            alongTrackNm: 3.0,
            offsetNm: 0.8,
            startHeading: 250,
            startAltitude: 1500,
            startSpeed: 130,
            AircraftCategory.Turboprop,
            isPatternTraffic: false
        );

        Assert.True(r.FinalXte < 0.05, $"XTE {r.FinalXte:F4}nm");
        Assert.True(r.FinalHdgDiff < 5, $"HdgDiff {r.FinalHdgDiff:F1}°");
    }

    [Fact]
    public void IFR_Visual_5nm_Final()
    {
        // Realistic: 30° intercept heading, 1.5nm offset, 160kts jet
        var r = RunScenario(
            "IFR Visual 5nm",
            alongTrackNm: 5.0,
            offsetNm: 1.5,
            startHeading: 250,
            startAltitude: 2500,
            startSpeed: 160,
            AircraftCategory.Jet,
            isPatternTraffic: false
        );

        Assert.True(r.FinalXte < 0.05, $"XTE {r.FinalXte:F4}nm");
        Assert.True(r.FinalHdgDiff < 5, $"HdgDiff {r.FinalHdgDiff:F1}°");
    }

    [Fact]
    public void IFR_Visual_10nm_Final()
    {
        var r = RunScenario(
            "IFR Visual 10nm",
            alongTrackNm: 10.0,
            offsetNm: 2.0,
            startHeading: 250,
            startAltitude: 3500,
            startSpeed: 180,
            AircraftCategory.Jet,
            isPatternTraffic: false
        );

        Assert.True(r.FinalXte < 0.05, $"XTE {r.FinalXte:F4}nm");
        Assert.True(r.FinalHdgDiff < 5, $"HdgDiff {r.FinalHdgDiff:F1}°");
    }
}
