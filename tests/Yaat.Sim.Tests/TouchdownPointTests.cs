using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Tests;

/// <summary>
/// Measures where each aircraft category actually touches down, relative to the runway threshold the
/// approach is flown to. Drives the production path — <see cref="SimulationWorld.Tick"/> with a preTick
/// that runs <see cref="PhaseRunner.Tick"/> over a real <c>[FinalApproachPhase, LandingPhase]</c> list —
/// rather than reasoning about the flare closed-form, because the touchdown point emerges from the
/// interaction of <see cref="CategoryPerformance.LandingAimPointOffsetFt"/>, the flare entry AGL, the
/// flare rate, and the Vref→Vtd speed ramp.
///
/// AIM 2-3-3.b.4 puts the aiming point markings ~1,000 ft from the landing threshold; AIM 2-1-5.b puts
/// the touchdown zone at 100–3,000 ft. A transport landing short of the threshold is landing on pavement
/// that may be displaced (departures-only).
/// </summary>
public class TouchdownPointTests
{
    private readonly ITestOutputHelper _output;

    public TouchdownPointTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private const double ThresholdLat = 37.72;
    private const double ThresholdLon = -122.22;
    private const double ThresholdElev = 9.0;
    private const double RunwayHeadingDeg = 280.0;

    private static RunwayInfo MakeRunway()
    {
        var (endLat, endLon) = GeoMath.ProjectPoint(ThresholdLat, ThresholdLon, new TrueHeading(RunwayHeadingDeg), 10_000.0 / GeoMath.FeetPerNm);
        return new RunwayInfo
        {
            AirportId = "KOAK",
            Id = new RunwayIdentifier("28R", "10L"),
            Designator = "28R",
            Lat1 = ThresholdLat,
            Lon1 = ThresholdLon,
            TrueHeading1 = new TrueHeading(RunwayHeadingDeg),
            Elevation1Ft = ThresholdElev,
            Lat2 = endLat,
            Lon2 = endLon,
            TrueHeading2 = new TrueHeading((RunwayHeadingDeg + 180) % 360),
            Elevation2Ft = ThresholdElev,
            LengthFt = 10_000,
            WidthFt = 150,
        };
    }

    /// <summary>
    /// Flies <paramref name="aircraftType"/> from a stabilized 4 nm final to touchdown and returns the
    /// along-track distance past the threshold in feet (negative = touched down short of it).
    /// </summary>
    private double MeasureTouchdownFt(string aircraftType, double approachSpeedKt, double startDistNm = 4.0)
    {
        var rwy = MakeRunway();
        var course = new TrueHeading(RunwayHeadingDeg);
        var (startLat, startLon) = GeoMath.ProjectPoint(ThresholdLat, ThresholdLon, course.ToReciprocal(), startDistNm);

        var ac = new AircraftState
        {
            Callsign = "TEST1",
            AircraftType = aircraftType,
            Position = new LatLon(startLat, startLon),
            TrueHeading = course,
            // Start on the 3° glidepath the phase will fly, so there is no capture transient.
            Altitude = GlideSlopeGeometry.AltitudeAtDistance(startDistNm, ThresholdElev),
            IndicatedAirspeed = approachSpeedKt,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan { Destination = "KOAK" },
        };

        ac.Phases = new PhaseList
        {
            AssignedRunway = rwy,
            ActiveApproach = new ApproachClearance
            {
                ApproachId = "I28R",
                AirportCode = "OAK",
                RunwayId = "28R",
                FinalApproachCourse = course,
            },
            LandingClearance = ClearanceType.ClearedToLand,
        };
        ac.Phases.Add(new FinalApproachPhase { SkipInterceptCheck = true });
        ac.Phases.Add(new LandingPhase());
        ac.Targets.TargetSpeed = approachSpeedKt;

        var world = new SimulationWorld();
        world.AddAircraft(ac);

        static void PreTick(AircraftState aircraft, double dt)
        {
            if (aircraft.Phases is null || aircraft.Phases.IsComplete)
            {
                return;
            }

            PhaseRunner.Tick(
                aircraft,
                new PhaseContext
                {
                    Aircraft = aircraft,
                    Targets = aircraft.Targets,
                    Category = AircraftCategorization.Categorize(aircraft.AircraftType),
                    DeltaSeconds = dt,
                    Runway = aircraft.Phases.AssignedRunway,
                    FieldElevation = aircraft.Phases.AssignedRunway?.ElevationFt ?? 0,
                    Logger = NullLogger.Instance,
                    AutoClearedToLand = true,
                }
            );
        }

        // Production cadence: SimulationEngine runs 4 physics sub-ticks per second.
        const double dt = 0.25;
        const int maxTicks = 4 * 300;

        for (int tick = 0; tick < maxTicks; tick++)
        {
            world.Tick(dt, PreTick);

            foreach (var (callsign, warning) in world.DrainAllWarnings())
            {
                _output.WriteLine($"# tick {tick} {callsign}: {warning}");
                Assert.DoesNotContain("going around", warning, StringComparison.OrdinalIgnoreCase);
            }

            if (ac.IsOnGround)
            {
                double alongFt =
                    GeoMath.AlongTrackDistanceNm(ac.Position, new LatLon(ThresholdLat, ThresholdLon), new TrueHeading(RunwayHeadingDeg))
                    * GeoMath.FeetPerNm;
                _output.WriteLine(
                    $"{aircraftType}: touchdown at tick {tick} ({tick * dt:F1}s), {alongFt:F0} ft past threshold, IAS {ac.IndicatedAirspeed:F0}"
                );
                return alongFt;
            }
        }

        Assert.Fail($"{aircraftType} never touched down within {maxTicks * dt:F0} s");
        return double.NaN;
    }

    [Theory]
    // B738 jet — LandingAimPointOffsetFt 0, flare entry 30 ft AGL.
    [InlineData("B738", 140.0, AircraftCategory.Jet)]
    // DH8D turboprop — offset 450 ft, flare entry 20 ft AGL.
    [InlineData("DH8D", 120.0, AircraftCategory.Turboprop)]
    // C172 piston — offset 400 ft, flare entry 15 ft AGL.
    [InlineData("C172", 70.0, AircraftCategory.Piston)]
    public void TouchdownLandsInTheTouchdownZone(string aircraftType, double approachSpeedKt, AircraftCategory expectedCategory)
    {
        Assert.Equal(expectedCategory, AircraftCategorization.Categorize(aircraftType));

        double alongFt = MeasureTouchdownFt(aircraftType, approachSpeedKt);

        // AIM 2-1-5.b: the touchdown zone runs 100–3,000 ft beyond the landing threshold. Landing short
        // of the threshold is the failure this pins — that pavement can be displaced/departures-only.
        Assert.InRange(alongFt, 100, 3000);
    }

    /// <summary>
    /// Pins the measured touchdown point per category so a change to
    /// <see cref="CategoryPerformance.LandingAimPointOffsetFt"/>, the flare profile, or the glidepath
    /// model shows up as a failure here rather than silently moving where aircraft land.
    ///
    /// The jet's 1,705 ft is the long one: with a 0 ft aim-point offset the glidepath reaches the surface
    /// at the threshold, and the whole distance is flare float. It sits inside the touchdown zone but
    /// beyond the ~1,000 ft aiming point markings, which is the visible symptom of the missing
    /// threshold-crossing height (issue #325). Raising the jet's offset alone would push touchdown to
    /// ~2,700 ft, at the far edge of the zone — the offset and the flare profile have to move together.
    /// </summary>
    [Theory]
    [InlineData("B738", 140.0, 1705.0)]
    [InlineData("DH8D", 120.0, 990.0)]
    [InlineData("C172", 70.0, 453.0)]
    public void TouchdownPointMatchesTheModelledAimPoint(string aircraftType, double approachSpeedKt, double expectedFt)
    {
        double alongFt = MeasureTouchdownFt(aircraftType, approachSpeedKt);

        Assert.InRange(alongFt, expectedFt - 150, expectedFt + 150);
    }

    /// <summary>
    /// The touchdown point is a property of the glidepath and flare, not of how far out the approach
    /// started — so an aircraft established at 6 nm must land in the same place as one established at
    /// 3 nm. Guards the pinned values above against being an artifact of the chosen start distance.
    /// </summary>
    [Theory]
    [InlineData(3.0)]
    [InlineData(6.0)]
    public void TouchdownPointIsIndependentOfApproachStartDistance(double startDistNm)
    {
        double baseline = MeasureTouchdownFt("B738", 140.0);
        double alongFt = MeasureTouchdownFt("B738", 140.0, startDistNm);

        Assert.InRange(alongFt, baseline - 100, baseline + 100);
    }
}
