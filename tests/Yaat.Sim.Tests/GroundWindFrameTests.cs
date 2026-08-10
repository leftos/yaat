using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Tests;

/// <summary>
/// Pins the air↔ground frame model: on the ground IndicatedAirspeed carries wheel speed,
/// rotation happens at Vr INDICATED (headwind shortens the roll by the v² law, density
/// lengthens it), touchdown converts TAS − headwind into wheel speed, and taxi/crosswind
/// operation is wind-immune because the gear resists lateral wind.
/// </summary>
public class GroundWindFrameTests
{
    private const double RunwayHeading = 280;

    private static WeatherProfile SteadyWind(double fromDeg, double speedKts) =>
        new()
        {
            WindLayers =
            [
                new WindLayer
                {
                    Altitude = 0,
                    Direction = fromDeg,
                    Speed = speedKts,
                },
            ],
        };

    private sealed record RollResult(double GroundSpeedAtRotation, double WheelSpeedAtRotation, double RollDistanceNm, double MaxTrackHeadingDiffDeg);

    private static RollResult RunGroundRoll(WeatherProfile? weather, double fieldElevationFt)
    {
        var runway = TestRunwayFactory.Make(designator: "28", airportId: "KSFO", heading: RunwayHeading, elevationFt: fieldElevationFt);
        var phase = new TakeoffPhase();
        var phaseList = new PhaseList { AssignedRunway = runway };
        var aircraft = new AircraftState
        {
            Callsign = "ROLL01",
            AircraftType = "B738",
            Position = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude),
            TrueHeading = new TrueHeading(RunwayHeading),
            TrueTrack = new TrueHeading(RunwayHeading),
            Altitude = fieldElevationFt,
            IsOnGround = true,
            Phases = phaseList,
        };
        var ctx = new PhaseContext
        {
            Aircraft = aircraft,
            Targets = aircraft.Targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = 0.25,
            Runway = runway,
            FieldElevation = fieldElevationFt,
            Logger = NullLogger.Instance,
        };

        phase.OnStart(ctx);

        // Prime the wind cache the way the real tick loop does (physics runs every tick).
        FlightPhysics.Update(aircraft, 0.25, null, weather, simTimeSeconds: 0);

        var threshold = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude);
        double gsAtRotation = 0;
        double maxTrackDiff = 0;
        double rollDistance = 0;
        double lastWheelSpeed = 0;
        for (int t = 1; t < 1200; t++)
        {
            double wheelBefore = aircraft.IndicatedAirspeed;
            phase.OnTick(ctx);
            if (!aircraft.IsOnGround)
            {
                lastWheelSpeed = wheelBefore;
                // At the flip IAS is exactly Vr; the airborne GroundSpeed getter gives the
                // rotation groundspeed from the cached wind (TAS − headwind component).
                Assert.Equal(AircraftPerformance.RotationSpeed("B738", AircraftCategory.Jet), aircraft.IndicatedAirspeed, 1);
                gsAtRotation = aircraft.GroundSpeed;
                rollDistance = GeoMath.DistanceNm(threshold, aircraft.Position);
                return new RollResult(gsAtRotation, lastWheelSpeed, rollDistance, maxTrackDiff);
            }

            FlightPhysics.Update(aircraft, 0.25, null, weather, simTimeSeconds: t * 0.25);
            double diff = Math.Abs(((aircraft.TrueTrack.Degrees - aircraft.TrueHeading.Degrees + 540) % 360) - 180);
            maxTrackDiff = Math.Max(maxTrackDiff, diff);
        }

        Assert.Fail("Aircraft never rotated");
        return new RollResult(0, 0, 0, 0);
    }

    [Fact]
    public void Headwind_RotatesAtLowerGroundspeed_ShorterRoll()
    {
        double vr = AircraftPerformance.RotationSpeed("B738", AircraftCategory.Jet);
        var calm = RunGroundRoll(null, fieldElevationFt: 0);
        var headwind = RunGroundRoll(SteadyWind(RunwayHeading, 20), fieldElevationFt: 0);

        // Sea level: IAS ≈ TAS, so rotation groundspeed drops by the full headwind and
        // the roll shortens by the v² law (≈ (1 − 20/Vr)²).
        Assert.Equal(vr - 20, headwind.GroundSpeedAtRotation, 2.0);
        double expectedRatio = Math.Pow((vr - 20) / vr, 2);
        Assert.Equal(expectedRatio, headwind.RollDistanceNm / calm.RollDistanceNm, 0.06);
    }

    [Fact]
    public void Tailwind_RotatesAtHigherGroundspeed_LongerRoll()
    {
        double vr = AircraftPerformance.RotationSpeed("B738", AircraftCategory.Jet);
        var calm = RunGroundRoll(null, fieldElevationFt: 0);
        var tailwind = RunGroundRoll(SteadyWind((RunwayHeading + 180) % 360, 10), fieldElevationFt: 0);

        Assert.Equal(vr + 10, tailwind.GroundSpeedAtRotation, 2.0);
        Assert.True(
            tailwind.RollDistanceNm > calm.RollDistanceNm * 1.08,
            $"Expected a 10 kt tailwind to lengthen the roll by ~14% (calm={calm.RollDistanceNm:F3} nm wheelRot={calm.WheelSpeedAtRotation:F1}, "
                + $"tailwind={tailwind.RollDistanceNm:F3} nm wheelRot={tailwind.WheelSpeedAtRotation:F1})"
        );
    }

    [Fact]
    public void Crosswind_NoEffectOnRollOrTrack()
    {
        var calm = RunGroundRoll(null, fieldElevationFt: 0);
        var crosswind = RunGroundRoll(SteadyWind((RunwayHeading + 90) % 360, 25), fieldElevationFt: 0);

        // cos 90° = 0: the roll length is unchanged (rotation groundspeed measured after
        // liftoff includes the crosswind drift vector, so the wheels-only invariants are
        // the roll distance and track = heading throughout).
        Assert.Equal(calm.RollDistanceNm, crosswind.RollDistanceNm, 0.02);
        Assert.True(crosswind.MaxTrackHeadingDiffDeg < 0.1, $"Ground track deviated {crosswind.MaxTrackHeadingDiffDeg:F2}° from heading");
    }

    [Fact]
    public void ZeroWeather_RollBehavesAsBefore()
    {
        double vr = AircraftPerformance.RotationSpeed("B738", AircraftCategory.Jet);
        var calm = RunGroundRoll(null, fieldElevationFt: 0);

        // Sea level, no wind: rotation groundspeed equals Vr indicated — bit-compatible
        // with the pre-wind behavior for windless scenarios and recordings.
        Assert.Equal(vr, calm.GroundSpeedAtRotation, 1.0);
    }

    [Fact]
    public void HighElevationField_RollNeedsMoreGroundspeed()
    {
        double vr = AircraftPerformance.RotationSpeed("B738", AircraftCategory.Jet);
        var highField = RunGroundRoll(null, fieldElevationFt: 5000);

        // Density correction: rotation at Vr indicated is a faster TAS (and GS) at altitude.
        double expectedGs = WindInterpolator.IasToTas(vr, 5000);
        Assert.True(expectedGs > vr + 5, "Sanity: TAS at 5000 ft should exceed IAS meaningfully");
        Assert.Equal(expectedGs, highField.GroundSpeedAtRotation, 3.0);
    }

    [Fact]
    public void Touchdown_ConvertsTasMinusHeadwindToWheelSpeed()
    {
        var weather = SteadyWind(RunwayHeading, 15);
        var aircraft = new AircraftState
        {
            Callsign = "LAND01",
            AircraftType = "B738",
            TrueHeading = new TrueHeading(RunwayHeading),
            TrueTrack = new TrueHeading(RunwayHeading),
            Altitude = 50,
            IndicatedAirspeed = 135,
            IsOnGround = false,
        };

        // Cache the wind the way the tick loop does, then flip frames.
        FlightPhysics.Update(aircraft, 0.25, null, weather, simTimeSeconds: 0);
        aircraft.Altitude = 0;
        GroundFrame.EnterGround(aircraft, 135);

        Assert.True(aircraft.IsOnGround);
        Assert.Equal(WindInterpolator.IasToTas(135, 0) - 15, aircraft.IndicatedAirspeed, 1.5);
    }

    [Fact]
    public void Taxi_HeadwindDoesNotSlowTheAircraft()
    {
        // Wheel-driven motion: a 20 kt headwind must not affect a 15 kt taxi.
        var weather = SteadyWind(90, 20);
        var aircraft = new AircraftState
        {
            Callsign = "TAXI01",
            AircraftType = "B738",
            TrueHeading = new TrueHeading(90),
            TrueTrack = new TrueHeading(90),
            Altitude = 0,
            IndicatedAirspeed = 15,
            IsOnGround = true,
        };

        var start = aircraft.Position;
        for (int t = 0; t < 60; t++)
        {
            FlightPhysics.Update(aircraft, 1.0, null, weather, simTimeSeconds: t);
        }

        Assert.Equal(15, aircraft.IndicatedAirspeed, 0.5);
        Assert.Equal(15, aircraft.GroundSpeed, 0.5);
        Assert.Equal(15.0 / 60.0, GeoMath.DistanceNm(start, aircraft.Position), 0.02);
    }
}
