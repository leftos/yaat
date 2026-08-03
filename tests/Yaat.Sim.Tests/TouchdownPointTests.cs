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
/// interaction of the glidepath's aiming point (<see cref="CategoryPerformance.WheelCrossingHeightFt"/>),
/// the flare entry AGL, the flare rate, and the Vref→Vtd speed ramp.
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
            WidthFt = 150,
        };
    }

    /// <summary>Where an approach put the aircraft vertically, measured end-to-end.</summary>
    /// <param name="TouchdownFt">Along-track distance past the threshold at touchdown (negative = short of it).</param>
    /// <param name="ThresholdCrossingAgl">Height above the runway as the aircraft crossed the threshold.</param>
    /// <param name="OneNmAgl">
    /// Height above the runway 1 nm out. Every category is still well above its flare entry there, so this
    /// samples the glidepath itself rather than the flare that overlays it near the threshold.
    /// </param>
    private sealed record ApproachProfile(double TouchdownFt, double ThresholdCrossingAgl, double OneNmAgl);

    private double MeasureTouchdownFt(string aircraftType, double approachSpeedKt, double startDistNm = 4.0) =>
        FlyApproach(aircraftType, approachSpeedKt, startDistNm).TouchdownFt;

    /// <summary>
    /// Flies <paramref name="aircraftType"/> from a stabilized final to touchdown, recording where it
    /// crossed the threshold and where it touched down.
    /// </summary>
    private ApproachProfile FlyApproach(string aircraftType, double approachSpeedKt, double startDistNm = 4.0)
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
            // Start on the glidepath the phase will fly, so there is no capture transient.
            Altitude = GlideSlopeGeometry.AltitudeAtDistance(startDistNm, ThresholdElev, AircraftCategorization.Categorize(aircraftType)),
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

        var threshold = new LatLon(ThresholdLat, ThresholdLon);
        double prevAlongFt = GeoMath.AlongTrackDistanceNm(ac.Position, threshold, course) * GeoMath.FeetPerNm;
        double prevAgl = ac.Altitude - ThresholdElev;
        double crossingAgl = double.NaN;
        double oneNmAgl = double.NaN;

        for (int tick = 0; tick < maxTicks; tick++)
        {
            world.Tick(dt, PreTick);

            foreach (var (callsign, warning) in world.DrainAllWarnings())
            {
                _output.WriteLine($"# tick {tick} {callsign}: {warning}");
                Assert.DoesNotContain("going around", warning, StringComparison.OrdinalIgnoreCase);
            }

            double alongFt = GeoMath.AlongTrackDistanceNm(ac.Position, threshold, course) * GeoMath.FeetPerNm;
            double agl = ac.Altitude - ThresholdElev;

            // Interpolate the height at each gate — at 0.25 s cadence a jet covers ~59 ft per tick, so
            // taking the first sample past the gate would read a few feet low.
            if (double.IsNaN(oneNmAgl) && (prevAlongFt < -GeoMath.FeetPerNm) && (alongFt >= -GeoMath.FeetPerNm))
            {
                oneNmAgl = Interpolate(-GeoMath.FeetPerNm, prevAlongFt, alongFt, prevAgl, agl);
                _output.WriteLine($"{aircraftType}: 1 nm out at {oneNmAgl:F1} ft AGL");
            }

            if (double.IsNaN(crossingAgl) && (prevAlongFt < 0) && (alongFt >= 0))
            {
                crossingAgl = Interpolate(0, prevAlongFt, alongFt, prevAgl, agl);
                _output.WriteLine($"{aircraftType}: crossed the threshold at {crossingAgl:F1} ft AGL");
            }

            prevAlongFt = alongFt;
            prevAgl = agl;

            if (ac.IsOnGround)
            {
                _output.WriteLine(
                    $"{aircraftType}: touchdown at tick {tick} ({tick * dt:F1}s), {alongFt:F0} ft past threshold, IAS {ac.IndicatedAirspeed:F0}"
                );
                return new ApproachProfile(alongFt, crossingAgl, oneNmAgl);
            }
        }

        Assert.Fail($"{aircraftType} never touched down within {maxTicks * dt:F0} s");
        return new ApproachProfile(double.NaN, double.NaN, double.NaN);
    }

    private static double Interpolate(double atAlong, double prevAlong, double along, double prevAgl, double agl)
    {
        double fraction = (atAlong - prevAlong) / (along - prevAlong);
        return prevAgl + ((agl - prevAgl) * fraction);
    }

    [Theory]
    // B738 jet — 30 ft crossing height, flare entry 30 ft AGL.
    [InlineData("B738", 140.0, AircraftCategory.Jet)]
    // DH8D turboprop — 25 ft crossing height, flare entry 20 ft AGL.
    [InlineData("DH8D", 120.0, AircraftCategory.Turboprop)]
    // C172 piston — 20 ft crossing height, flare entry 15 ft AGL.
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
    /// Landing inside the touchdown zone is the floor; landing where the category actually aims is the
    /// bar. AIM 2-3-3.b.4 puts the aiming point markings ~1,000 ft down the runway, and transports touch
    /// down on or just past them. Light singles are flown to the numbers instead (FAA-H-8083-3 ch. 8),
    /// so they belong short of the markings, not on them.
    /// </summary>
    [Theory]
    [InlineData("B738", 140.0, 1000.0, 1600.0)]
    [InlineData("DH8D", 120.0, 500.0, 1200.0)]
    [InlineData("C172", 70.0, 200.0, 800.0)]
    public void TouchdownIsAtTheCategoryAimingPoint(string aircraftType, double approachSpeedKt, double minFt, double maxFt)
    {
        double alongFt = MeasureTouchdownFt(aircraftType, approachSpeedKt);

        Assert.InRange(alongFt, minFt, maxFt);
    }

    /// <summary>
    /// The glidepath must carry a threshold-crossing height, so that 1 nm out — still well above every
    /// category's flare entry — the aircraft is <c>crossingHeight + 318 ft</c> above the runway rather
    /// than on a path aimed at the surface.
    ///
    /// AIM 1-1-9.d.7: "a comfortable wheel crossing height is approximately 20 to 30 feet, depending on
    /// the type of aircraft." YAAT tracks a point that becomes the wheels at touchdown, so the modelled
    /// glidepath is the wheel path, and that band — not the 30–50 ft published *antenna* TCH of
    /// AIM 5-4-5.b.3 — is the one it has to satisfy.
    /// </summary>
    [Theory]
    [InlineData("B738", 140.0, AircraftCategory.Jet)]
    [InlineData("DH8D", 120.0, AircraftCategory.Turboprop)]
    [InlineData("C172", 70.0, AircraftCategory.Piston)]
    public void GlidepathCarriesAWheelCrossingHeight(string aircraftType, double approachSpeedKt, AircraftCategory category)
    {
        double crossingHeight = CategoryPerformance.WheelCrossingHeightFt(category);
        Assert.InRange(crossingHeight, 20.0, 30.0);

        var profile = FlyApproach(aircraftType, approachSpeedKt);

        Assert.InRange(
            profile.OneNmAgl,
            crossingHeight + GlideSlopeGeometry.FeetPerNm(3.0) - 10,
            crossingHeight + GlideSlopeGeometry.FeetPerNm(3.0) + 10
        );
    }

    /// <summary>
    /// The height actually flown across the threshold must be the modelled crossing height, give or take
    /// the path-tracking tolerance — the flare has already begun by then for a jet, so this catches a
    /// flare retune that drags the aircraft in low or balloons it over the threshold.
    /// </summary>
    [Theory]
    [InlineData("B738", 140.0, AircraftCategory.Jet)]
    [InlineData("DH8D", 120.0, AircraftCategory.Turboprop)]
    [InlineData("C172", 70.0, AircraftCategory.Piston)]
    public void ThresholdIsCrossedAtTheModelledHeight(string aircraftType, double approachSpeedKt, AircraftCategory category)
    {
        double crossingHeight = CategoryPerformance.WheelCrossingHeightFt(category);

        var profile = FlyApproach(aircraftType, approachSpeedKt);

        Assert.InRange(profile.ThresholdCrossingAgl, crossingHeight - 5, crossingHeight + 5);
    }

    /// <summary>
    /// Pins the measured touchdown point per category so a change to
    /// <see cref="CategoryPerformance.WheelCrossingHeightFt"/>, the flare profile, or the glidepath
    /// model shows up as a failure here rather than silently moving where aircraft land.
    ///
    /// Each is the sum of two terms: the aiming point the crossing height puts on the runway
    /// (<c>height / tan 3°</c> — 572 / 477 / 382 ft) plus the flare float past it. The jet's float is by
    /// far the longest because it enters the flare highest and fastest.
    /// </summary>
    [Theory]
    [InlineData("B738", 140.0, 1367.0)]
    [InlineData("DH8D", 120.0, 798.0)]
    [InlineData("C172", 70.0, 401.0)]
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
