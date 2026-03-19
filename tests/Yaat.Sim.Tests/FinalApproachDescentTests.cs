using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Tests;

public class FinalApproachDescentTests
{
    private readonly ITestOutputHelper _output;

    public FinalApproachDescentTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private record DescentResult(double FinalAltitude, double FinalDistNm, int CompleteTick, bool GoAroundTriggered, List<string> Warnings);

    private DescentResult RunDescentScenario(
        string label,
        double distNm,
        double startAltitude,
        double startSpeed,
        double thresholdElevation = 9,
        bool autoClearedToLand = true,
        int? mapAltitudeFt = null,
        int maxTicks = 600
    )
    {
        var rwy = TestRunwayFactory.Make(
            designator: "28R",
            airportId: "OAK",
            thresholdLat: 37.72,
            thresholdLon: -122.22,
            heading: 280,
            elevationFt: thresholdElevation
        );

        // Place aircraft on the extended centerline at given distance
        var reciprocal = rwy.TrueHeading.ToReciprocal();
        var startPos = GeoMath.ProjectPoint(rwy.ThresholdLatitude, rwy.ThresholdLongitude, reciprocal, distNm);

        var ac = new AircraftState
        {
            Callsign = "UAL123",
            AircraftType = "B738",
            Latitude = startPos.Lat,
            Longitude = startPos.Lon,
            TrueHeading = rwy.TrueHeading,
            Altitude = startAltitude,
            IndicatedAirspeed = startSpeed,
            IsOnGround = false,
            Destination = "OAK",
        };

        var clearance = new ApproachClearance
        {
            ApproachId = "I28R",
            AirportCode = "OAK",
            RunwayId = "28R",
            FinalApproachCourse = rwy.TrueHeading,
            MapAltitudeFt = mapAltitudeFt,
        };

        ac.Phases = new PhaseList { AssignedRunway = rwy, ActiveApproach = clearance };
        if (autoClearedToLand)
        {
            ac.Phases.LandingClearance = ClearanceType.ClearedToLand;
        }

        ac.Targets.TargetSpeed = startSpeed;

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
            AutoClearedToLand = autoClearedToLand,
        };

        phase.OnStart(ctx);

        _output.WriteLine($"--- {label} ---");
        _output.WriteLine("tick,altitude,gsAltitude,distNm,vertRate,groundSpeed");

        int completeTick = maxTicks;
        bool goAroundTriggered = false;
        for (int tick = 0; tick < maxTicks; tick++)
        {
            double dist = GeoMath.DistanceNm(ac.Latitude, ac.Longitude, rwy.ThresholdLatitude, rwy.ThresholdLongitude);
            double gsAlt = GlideSlopeGeometry.AltitudeAtDistance(dist, thresholdElevation);

            _output.WriteLine($"{tick},{ac.Altitude:F0},{gsAlt:F0},{dist:F2},{ac.Targets.DesiredVerticalRate:F0},{ac.GroundSpeed:F0}");

            bool done = phase.OnTick(ctx);
            FlightPhysics.Update(ac, 1.0);

            if (done)
            {
                completeTick = tick;
                // If we got a go-around warning, the phase returned false (not true).
                // Actually, go-around sets _goAroundTriggered and returns false on the NEXT tick.
                // The phase returns false when triggering go-around (line 87).
                break;
            }

            // Detect go-around via warnings
            if (ac.PendingWarnings.Any(w => w.Contains("going around", StringComparison.OrdinalIgnoreCase)))
            {
                goAroundTriggered = true;
                completeTick = tick;
                break;
            }
        }

        double finalDist = GeoMath.DistanceNm(ac.Latitude, ac.Longitude, rwy.ThresholdLatitude, rwy.ThresholdLongitude);

        _output.WriteLine($"# {label}: alt={ac.Altitude:F0}ft, dist={finalDist:F2}nm, tick={completeTick}");
        _output.WriteLine("");

        return new DescentResult(ac.Altitude, finalDist, completeTick, goAroundTriggered, [.. ac.PendingWarnings]);
    }

    [Fact]
    public void HighAircraft_ConvergesBeforeThreshold()
    {
        // 1300ft above GS at 6.8nm, 140kts
        double gsAt6_8 = GlideSlopeGeometry.AltitudeAtDistance(6.8, 9);
        double startAlt = gsAt6_8 + 1300; // ~3474ft

        var r = RunDescentScenario("High aircraft 1300ft above GS at 6.8nm", distNm: 6.8, startAltitude: startAlt, startSpeed: 140);

        // At 2nm from threshold, GS altitude is ~645ft. Aircraft should be within 100ft.
        // We check the final result: aircraft should have converged and crossed the threshold
        // at a reasonable altitude (not 750ft AGL).
        double thresholdAgl = r.FinalAltitude - 9;
        _output.WriteLine($"Threshold AGL: {thresholdAgl:F0}ft");

        // Must converge: final altitude within 200ft of field elevation at threshold
        Assert.True(thresholdAgl < 200, $"Aircraft crossed threshold at {thresholdAgl:F0}ft AGL — should have converged to GS");
    }

    [Fact]
    public void OnGlideslope_MaintainsStandardRate()
    {
        // On GS at 5nm
        double gsAt5 = GlideSlopeGeometry.AltitudeAtDistance(5.0, 9);
        double standardFpm = GlideSlopeGeometry.RequiredDescentRate(140, GlideSlopeGeometry.StandardAngleDeg);

        var rwy = TestRunwayFactory.Make(
            designator: "28R",
            airportId: "OAK",
            thresholdLat: 37.72,
            thresholdLon: -122.22,
            heading: 280,
            elevationFt: 9
        );

        var reciprocal = rwy.TrueHeading.ToReciprocal();
        var startPos = GeoMath.ProjectPoint(rwy.ThresholdLatitude, rwy.ThresholdLongitude, reciprocal, 5.0);

        var ac = new AircraftState
        {
            Callsign = "UAL123",
            AircraftType = "B738",
            Latitude = startPos.Lat,
            Longitude = startPos.Lon,
            TrueHeading = rwy.TrueHeading,
            Altitude = gsAt5,
            IndicatedAirspeed = 140,
            IsOnGround = false,
            Destination = "OAK",
        };

        ac.Phases = new PhaseList { AssignedRunway = rwy };
        ac.Targets.TargetSpeed = 140;

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
        phase.OnTick(ctx);

        double actualRate = Math.Abs(ac.Targets.DesiredVerticalRate ?? 0);
        double tolerance = standardFpm * 0.1;

        _output.WriteLine($"Standard FPM: {standardFpm:F0}, Actual: {actualRate:F0}, Tolerance: {tolerance:F0}");
        Assert.InRange(actualRate, standardFpm - tolerance, standardFpm + tolerance);
    }

    [Fact]
    public void BelowGlideslope_ReducesRate()
    {
        // 300ft below GS at 5nm
        double gsAt5 = GlideSlopeGeometry.AltitudeAtDistance(5.0, 9);
        double standardFpm = GlideSlopeGeometry.RequiredDescentRate(140, GlideSlopeGeometry.StandardAngleDeg);

        var rwy = TestRunwayFactory.Make(
            designator: "28R",
            airportId: "OAK",
            thresholdLat: 37.72,
            thresholdLon: -122.22,
            heading: 280,
            elevationFt: 9
        );

        var reciprocal = rwy.TrueHeading.ToReciprocal();
        var startPos = GeoMath.ProjectPoint(rwy.ThresholdLatitude, rwy.ThresholdLongitude, reciprocal, 5.0);

        var ac = new AircraftState
        {
            Callsign = "UAL123",
            AircraftType = "B738",
            Latitude = startPos.Lat,
            Longitude = startPos.Lon,
            TrueHeading = rwy.TrueHeading,
            Altitude = gsAt5 - 300,
            IndicatedAirspeed = 140,
            IsOnGround = false,
            Destination = "OAK",
        };

        ac.Phases = new PhaseList { AssignedRunway = rwy };
        ac.Targets.TargetSpeed = 140;

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
        phase.OnTick(ctx);

        double actualRate = Math.Abs(ac.Targets.DesiredVerticalRate ?? 0);

        _output.WriteLine($"Standard FPM: {standardFpm:F0}, Actual (below GS): {actualRate:F0}");
        Assert.True(actualRate < standardFpm, $"Rate {actualRate:F0} should be less than standard {standardFpm:F0} when below GS");
    }

    [Fact]
    public void TooHighAtMAP_TriggersGoAround()
    {
        // 800ft AGL at 0.5nm with CTL — should trigger go-around
        double thresholdElev = 9;
        double startAlt = thresholdElev + 800;

        var r = RunDescentScenario(
            "Too high at MAP",
            distNm: 0.5,
            startAltitude: startAlt,
            startSpeed: 140,
            thresholdElevation: thresholdElev,
            autoClearedToLand: true,
            mapAltitudeFt: (int)(thresholdElev + 200),
            maxTicks: 10
        );

        Assert.True(r.GoAroundTriggered, "Go-around should have triggered for aircraft 800ft AGL at 0.5nm");
        Assert.Contains(r.Warnings, w => w.Contains("going around", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void OnGlideslope_NoGoAroundAtMAP()
    {
        // ~160ft AGL at 0.5nm with CTL — should NOT go around
        double thresholdElev = 9;
        double gsAtHalfNm = GlideSlopeGeometry.AltitudeAtDistance(0.5, thresholdElev);

        var r = RunDescentScenario(
            "On GS at MAP — no go-around",
            distNm: 0.5,
            startAltitude: gsAtHalfNm,
            startSpeed: 140,
            thresholdElevation: thresholdElev,
            autoClearedToLand: true,
            mapAltitudeFt: (int)(thresholdElev + 200),
            maxTicks: 60
        );

        Assert.False(r.GoAroundTriggered, "Go-around should NOT trigger when aircraft is on glideslope");
        Assert.DoesNotContain(r.Warnings, w => w.Contains("going around", StringComparison.OrdinalIgnoreCase));
    }
}
