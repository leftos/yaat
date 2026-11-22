using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Tests.Physics;

/// <summary>
/// Ground steering under the ramp turn model: a rolling aircraft's nose swings through a yaw rate that builds at
/// alpha = ground ceiling / ground steer ramp time, holds and eases out, passes through zero before it reverses, and is
/// capped by lateral acceleration at 1.25 x 0.13 g (binding from about 15 kt for a jet). Below the 3 kt creep the turn is
/// instant at the same ceiling with its rate recorded; helicopters on wheels and the instant model are unchanged.
/// </summary>
public class GroundSteeringRampTests
{
    private const double Dt = 0.25;

    private static readonly PhysicsTickOptions RampOptions = PhysicsTickOptions.Default with { TurnModel = TurnModel.Ramp };

    private readonly RunwayInfo _runway;

    public GroundSteeringRampTests()
    {
        TestVnasData.EnsureInitialized();
        RunwayInfo? runway = NavigationDatabase.Instance.GetRunway("OAK", "30");
        Assert.NotNull(runway);
        _runway = runway;
    }

    private static double Alpha(AircraftCategory cat) => CategoryPerformance.GroundTurnRate(cat) / CategoryPerformance.GroundSteerRampSeconds(cat);

    // A 60 deg change at 15 kt ramps in at alpha * dt per tick at most, peaks at the ceiling (the lateral cap binds just
    // below the jet's 12 deg/s there) and lands on the target with no overshoot.
    [Theory]
    [InlineData("B738", AircraftCategory.Jet)]
    [InlineData("C172", AircraftCategory.Piston)]
    public void SixtyDegreeChange_AtFifteenKnots_RampsAndLandsOnTheTarget(string type, AircraftCategory cat)
    {
        AircraftState ac = MakeGround(type, cat, groundSpeedKts: 15);
        double target = new TrueHeading(_runway.TrueHeading.Degrees + 60).Degrees;
        ac.Targets.TargetTrueHeading = new TrueHeading(target);
        double alphaStep = Alpha(cat) * Dt;
        double maxRate = CategoryPerformance.GroundSteerRateCeilingDegPerSec(cat, type, 15, rateOverrideDegPerSec: null);
        Assert.Equal(11.83, maxRate, 0.01);

        double previous = 0;
        double peak = 0;
        int ticks = 0;
        while ((ac.TrueHeading.Degrees != target) || (ac.AppliedTurnRateDegPerSec != 0))
        {
            Assert.True(ticks++ < 400, $"Never settled: heading {ac.TrueHeading.Degrees:F4}, rate {ac.AppliedTurnRateDegPerSec:F4}");
            Tick(ac, RampOptions, 15);
            double applied = Assert.NotNull(ac.AppliedTurnRateDegPerSec);
            if (ticks == 1)
            {
                Assert.Equal(alphaStep, applied, 1e-9);
            }

            Assert.True(Math.Abs(applied - previous) <= alphaStep + 1e-9, $"Rate stepped more than alpha*dt: {previous:F4} -> {applied:F4}");
            Assert.True(SignedDelta(ac.TrueHeading.Degrees, target) >= -1e-9, $"Overshot the target: heading {ac.TrueHeading.Degrees:F4}");
            peak = Math.Max(peak, applied);
            previous = applied;
        }

        Assert.Equal(maxRate, peak, 1e-6);
        Assert.Equal(0.0, ac.BankAngle);
    }

    // A target swung to the other side mid-turn: the rate passes through zero once, stepping at most alpha * dt.
    [Fact]
    public void Reversal_MidTurn_PassesThroughZeroOnce()
    {
        AircraftState ac = MakeGround("B738", AircraftCategory.Jet, groundSpeedKts: 15);
        ac.Targets.TargetTrueHeading = new TrueHeading(_runway.TrueHeading.Degrees + 30);
        double alphaStep = Alpha(AircraftCategory.Jet) * Dt;
        for (int i = 0; i < 6; i++)
        {
            Tick(ac, RampOptions, 15);
        }

        double previous = Assert.NotNull(ac.AppliedTurnRateDegPerSec);
        Assert.True(previous > 0, $"Expected a right yaw on, got {previous:F4}");
        double target = new TrueHeading(ac.TrueHeading.Degrees - 30).Degrees;
        ac.Targets.TargetTrueHeading = new TrueHeading(target);

        int signChanges = 0;
        int lastSign = 1;
        int ticks = 0;
        while ((ac.TrueHeading.Degrees != target) || (ac.AppliedTurnRateDegPerSec != 0))
        {
            Assert.True(ticks++ < 400, "Reversal never settled");
            Tick(ac, RampOptions, 15);
            double applied = Assert.NotNull(ac.AppliedTurnRateDegPerSec);
            Assert.True(Math.Abs(applied - previous) <= alphaStep + 1e-9, $"|dw/dt| above alpha: {previous:F4} -> {applied:F4}");
            int sign = Math.Sign(applied);
            if ((sign != 0) && (sign != lastSign))
            {
                signChanges++;
                lastSign = sign;
            }

            previous = applied;
        }

        Assert.Equal(1, signChanges);
    }

    // At speed the rate is capped by lateral acceleration, 1.25 x 0.13 g / v.
    [Theory]
    [InlineData(100.0, 1.78)]
    [InlineData(30.0, 5.92)]
    public void HighSpeed_RateCappedByLateralAcceleration(double groundSpeedKts, double expectedCapDegPerSec)
    {
        double cap = CategoryPerformance.GroundSteerYawRateCapDegPerSec(groundSpeedKts);
        Assert.Equal(expectedCapDegPerSec, cap, 0.01);
        AircraftState ac = MakeGround("B738", AircraftCategory.Jet, groundSpeedKts);
        ac.Targets.TargetTrueHeading = new TrueHeading(_runway.TrueHeading.Degrees + 20);
        ac.Targets.TurnRateOverride = CategoryPerformance.GroundTurnRate(AircraftCategory.Jet);

        double peak = 0;
        for (int i = 0; i < 40; i++)
        {
            Tick(ac, RampOptions, groundSpeedKts);
            double applied = Assert.NotNull(ac.AppliedTurnRateDegPerSec);
            Assert.True(applied <= cap + 1e-9, $"Rate {applied:F4} above the lateral cap {cap:F4} at {groundSpeedKts} kt");
            peak = Math.Max(peak, applied);
        }

        Assert.Equal(cap, peak, 1e-6);
    }

    // The lateral cap falls below a jet's 12 deg/s ceiling, and so binds, from about 15 kt.
    [Fact]
    public void LateralCap_BindsForAJetFromAboutFifteenKnots()
    {
        double ceiling = CategoryPerformance.GroundTurnRate(AircraftCategory.Jet);
        Assert.True(CategoryPerformance.GroundSteerYawRateCapDegPerSec(14.5) > ceiling);
        Assert.True(CategoryPerformance.GroundSteerYawRateCapDegPerSec(15.0) < ceiling);
    }

    // A heading already on the target with a carried rate above one rate step is finished as it stands: neither the
    // heading nor the rate moves, so a pose written this tick is never pushed past.
    [Fact]
    public void OnTarget_WithACarriedRate_LeavesHeadingAndBleedsTheRate()
    {
        AircraftState ac = MakeGround("B738", AircraftCategory.Jet, groundSpeedKts: 15);
        double heading = ac.TrueHeading.Degrees;
        ac.Targets.TargetTrueHeading = new TrueHeading(heading);
        ac.AppliedTurnRateDegPerSec = 5.0;
        double alphaStep = Alpha(AircraftCategory.Jet) * Dt;
        Assert.True(5.0 > alphaStep);

        Tick(ac, RampOptions, 15);

        Assert.Equal(heading, ac.TrueHeading.Degrees);
        Assert.Equal(5.0 - alphaStep, Assert.NotNull(ac.AppliedTurnRateDegPerSec), 1e-12);
    }

    // A creep turn that lands exactly on a steady target carries its rate; rolling on above 3 kt, the rate bleeds to zero
    // by alpha * dt a tick and the heading never moves.
    [Fact]
    public void CreepLandingOnTarget_ThenRollingOn_BleedsTheRateWithoutMovingTheNose()
    {
        AircraftState ac = MakeGround("B738", AircraftCategory.Jet, groundSpeedKts: 2.5);
        double target = new TrueHeading(_runway.TrueHeading.Degrees + 11).Degrees;
        ac.Targets.TargetTrueHeading = new TrueHeading(target);
        for (int i = 0; (i < 20) && (Math.Abs(SignedDelta(ac.TrueHeading.Degrees, target)) >= 1e-9); i++)
        {
            Tick(ac, RampOptions, 2.5);
        }

        double landed = ac.TrueHeading.Degrees;
        Assert.True(Math.Abs(SignedDelta(landed, target)) < 1e-9, $"Creep never landed on the target: {landed:F6} vs {target:F6}");
        double previous = Assert.NotNull(ac.AppliedTurnRateDegPerSec);
        double alphaStep = Alpha(AircraftCategory.Jet) * Dt;
        Assert.True(previous > alphaStep, $"Expected the creep to land with more than one rate step on, got {previous:F4}");

        for (int i = 0; i < 12; i++)
        {
            Tick(ac, RampOptions, 4);
            Assert.True(Math.Abs(SignedDelta(landed, ac.TrueHeading.Degrees)) < 1e-9, $"The nose moved to {ac.TrueHeading.Degrees:F6}");
            double applied = Assert.NotNull(ac.AppliedTurnRateDegPerSec);
            Assert.Equal(Math.Max(0, previous - alphaStep), applied, 1e-12);
            previous = applied;
        }

        Assert.Equal(0.0, previous);
    }

    // Speeding up through 3 kt, the ramp takes over from the creep's rate without stepping by more than alpha * dt.
    [Fact]
    public void SpeedingUpThroughCreepSpeed_FirstRampedTick_StepsAtMostAlpha()
    {
        AircraftState ac = MakeGround("B738", AircraftCategory.Jet, groundSpeedKts: 2.5);
        ac.Targets.TargetTrueHeading = new TrueHeading(_runway.TrueHeading.Degrees + 40);
        Tick(ac, RampOptions, 2.5);
        Tick(ac, RampOptions, 2.5);
        double creep = Assert.NotNull(ac.AppliedTurnRateDegPerSec);
        Assert.Equal(CategoryPerformance.GroundSteerRateCeilingDegPerSec(AircraftCategory.Jet, "B738", 2.5, null), creep, 1e-9);

        Tick(ac, RampOptions, 3.5);

        double ramped = Assert.NotNull(ac.AppliedTurnRateDegPerSec);
        Assert.True(Math.Abs(ramped - creep) <= (Alpha(AircraftCategory.Jet) * Dt) + 1e-9, $"Rate jumped {creep:F4} -> {ramped:F4}");
    }

    // Slowing through 3 kt, the creep never turns faster than the ramp ceiling it left.
    [Fact]
    public void SlowingThroughCreepSpeed_CreepRateStaysWithinTheRampCeiling()
    {
        AircraftState ac = MakeGround("B738", AircraftCategory.Jet, groundSpeedKts: 4);
        ac.Targets.TargetTrueHeading = new TrueHeading(_runway.TrueHeading.Degrees + 90);
        for (int i = 0; i < 16; i++)
        {
            Tick(ac, RampOptions, 4);
        }

        double rampCeiling = CategoryPerformance.GroundSteerRateCeilingDegPerSec(AircraftCategory.Jet, "B738", 4, null);
        for (int i = 0; i < 4; i++)
        {
            Tick(ac, RampOptions, 2.9);
            double creep = Assert.NotNull(ac.AppliedTurnRateDegPerSec);
            Assert.True(Math.Abs(creep) <= rampCeiling + 1e-9, $"Creep rate {creep:F4} above the ramp ceiling {rampCeiling:F4}");
        }
    }

    // Below 3 kt the turn is the instant one, and the applied rate is the heading change over the tick.
    [Fact]
    public void BelowCreepSpeed_TurnsInstantly_AndRecordsTheRate()
    {
        const double gs = 2.0;
        AircraftState ramp = MakeGround("B738", AircraftCategory.Jet, gs);
        AircraftState instant = MakeGround("B738", AircraftCategory.Jet, gs);
        double yaw = CategoryPerformance.GroundYawRateAtSpeed(AircraftCategory.Jet, "B738", gs);
        AircraftState[] pair = [ramp, instant];
        foreach (AircraftState ac in pair)
        {
            ac.Targets.TargetTrueHeading = new TrueHeading(_runway.TrueHeading.Degrees + 10);
            ac.Targets.TurnRateOverride = yaw;
        }

        for (int i = 0; i < 12; i++)
        {
            double before = ramp.TrueHeading.Degrees;
            Tick(ramp, RampOptions, gs);
            Tick(instant, PhysicsTickOptions.Default, gs);
            Assert.Equal(instant.TrueHeading.Degrees, ramp.TrueHeading.Degrees);
            Assert.Equal(instant.BankAngle, ramp.BankAngle);
            Assert.Null(instant.AppliedTurnRateDegPerSec);
            double applied = Assert.NotNull(ramp.AppliedTurnRateDegPerSec);
            Assert.Equal(SignedDelta(before, ramp.TrueHeading.Degrees) / Dt, applied, 1e-12);
            if (i == 0)
            {
                Assert.Equal(yaw, applied, 1e-9);
            }
        }

        Assert.Equal(new TrueHeading(_runway.TrueHeading.Degrees + 10).Degrees, ramp.TrueHeading.Degrees, 1e-9);
    }

    // A helicopter on its wheels keeps the instant pedal turn under the ramp.
    [Fact]
    public void HelicopterOnWheels_TurnsExactlyAsTheInstantModel()
    {
        AircraftState ramp = MakeGround("EC35", AircraftCategory.Helicopter, groundSpeedKts: 10);
        AircraftState instant = MakeGround("EC35", AircraftCategory.Helicopter, groundSpeedKts: 10);
        AircraftState[] pair = [ramp, instant];
        foreach (AircraftState ac in pair)
        {
            ac.Targets.TargetTrueHeading = new TrueHeading(_runway.TrueHeading.Degrees + 60);
        }

        for (int i = 0; i < 16; i++)
        {
            Tick(ramp, RampOptions, 10);
            Tick(instant, PhysicsTickOptions.Default, 10);
            Assert.Equal(instant.TrueHeading.Degrees, ramp.TrueHeading.Degrees);
            Assert.Equal(instant.BankAngle, ramp.BankAngle);
            Assert.Null(ramp.AppliedTurnRateDegPerSec);
        }

        Assert.NotEqual(_runway.TrueHeading.Degrees, ramp.TrueHeading.Degrees);
    }

    // Under the instant model the ground turn is the full override rate from the first tick, with no applied rate.
    [Fact]
    public void InstantModel_TurnsAtTheFullOverrideRateFromTheFirstTick()
    {
        AircraftState ac = MakeGround("B738", AircraftCategory.Jet, groundSpeedKts: 15);
        double start = ac.TrueHeading.Degrees;
        ac.Targets.TargetTrueHeading = new TrueHeading(start + 30);
        ac.Targets.TurnRateOverride = CategoryPerformance.GroundTurnRate(AircraftCategory.Jet);
        var expected = new TrueHeading(start);

        for (int i = 0; i < 9; i++)
        {
            Tick(ac, PhysicsTickOptions.Default, 15);
            expected = new TrueHeading(expected.Degrees + (CategoryPerformance.GroundTurnRate(AircraftCategory.Jet) * Dt));
            Assert.Equal(expected.Degrees, ac.TrueHeading.Degrees);
            Assert.Null(ac.AppliedTurnRateDegPerSec);
        }

        Tick(ac, PhysicsTickOptions.Default, 15);
        Assert.Equal(new TrueHeading(start + 30).Degrees, ac.TrueHeading.Degrees, 1e-9);
    }

    // The airborne ramp through the shared rate law flies the same headings as the law written out in full.
    [Fact]
    public void AirborneRamp_ThroughTheSharedLaw_FliesTheSameHeadings()
    {
        var ac = new AircraftState
        {
            Callsign = "TST100",
            AircraftType = "B738",
            Position = new LatLon(37.721, -122.221),
            TrueHeading = new TrueHeading(90),
            TrueTrack = new TrueHeading(90),
            Altitude = 10000,
            IndicatedAirspeed = 250,
        };
        ac.Targets.TargetTrueHeading = new TrueHeading(180);
        double fullRate = AircraftPerformance.TurnRate("B738", AircraftCategory.Jet);
        double alpha = fullRate / CategoryPerformance.JetRollInSeconds;
        double maxStep = alpha * Dt;
        var heading = new TrueHeading(90);
        double applied = 0;

        for (int i = 0; i < 60; i++)
        {
            double remaining = Math.Abs(180 - heading.Degrees);
            double braking = Math.Sqrt((2.0 * alpha * remaining) + (maxStep * maxStep / 4.0)) - (maxStep / 2.0);
            double commanded = Math.Min(fullRate, braking);
            double step = commanded - applied;
            applied = Math.Abs(step) <= maxStep ? commanded : applied + (Math.Sign(step) * maxStep);
            heading = new TrueHeading(heading.Degrees + (applied * Dt));

            FlightPhysics.Update(ac, Dt, aircraftLookup: null, weather: null, simTimeSeconds: 0, RampOptions);
            Assert.Equal(heading.Degrees, ac.TrueHeading.Degrees);
            Assert.Equal(applied, Assert.NotNull(ac.AppliedTurnRateDegPerSec));
        }
    }

    // Line-up swing length under the ramp: v * (theta/omega + omega/alpha) when the full rate is reached, else v * 2 sqrt(theta/alpha).
    [Theory]
    [InlineData(45.0)]
    [InlineData(0.5)]
    public void ResumedSwingRolloutLength_UnderTheRamp_UsesTheRampTime(double headingOffDeg)
    {
        double speedKts = LineUpGeometry.ResumedRolloutSpeedKts(AircraftCategory.Jet);
        double swingRate = CategoryPerformance.GroundYawRateAtSpeed(AircraftCategory.Jet, aircraftType: null, speedKts);
        double omega = CategoryPerformance.GroundSteerRateCeilingDegPerSec(AircraftCategory.Jet, aircraftType: null, speedKts, swingRate);
        double alpha = Alpha(AircraftCategory.Jet);
        double seconds =
            headingOffDeg >= (omega * omega / alpha) ? (headingOffDeg / omega) + (omega / alpha) : 2.0 * Math.Sqrt(headingOffDeg / alpha);
        double expectedFt = LineUpGeometry.RolloutLengthFt + (seconds * speedKts * GeoMath.FeetPerNm / 3600.0);

        double rampFt = LineUpGeometry.ResumedSwingRolloutLengthFt(AircraftCategory.Jet, headingOffDeg, swingRate, TurnModel.Ramp);
        double instantFt = LineUpGeometry.ResumedSwingRolloutLengthFt(AircraftCategory.Jet, headingOffDeg, swingRate, TurnModel.Instant);

        Assert.Equal(expectedFt, rampFt, 1e-9);
        Assert.True(rampFt > instantFt, $"Ramp {rampFt:F1}ft must roll further than instant {instantFt:F1}ft");
    }

    // A helicopter's line-up swing keeps the instant length under the ramp: it pedal-turns on its wheels.
    [Fact]
    public void ResumedSwingRolloutLength_Helicopter_IgnoresTheRamp()
    {
        double speedKts = LineUpGeometry.ResumedRolloutSpeedKts(AircraftCategory.Helicopter);
        double swingRate = CategoryPerformance.GroundYawRateAtSpeed(AircraftCategory.Helicopter, aircraftType: null, speedKts);

        double rampFt = LineUpGeometry.ResumedSwingRolloutLengthFt(AircraftCategory.Helicopter, 45.0, swingRate, TurnModel.Ramp);
        double instantFt = LineUpGeometry.ResumedSwingRolloutLengthFt(AircraftCategory.Helicopter, 45.0, swingRate, TurnModel.Instant);

        Assert.Equal(instantFt, rampFt);
    }

    // Physics flying the resumed swing at the rollout speed and the swing rate settles on the runway heading within the
    // length the geometry sized, to one tick.
    [Fact]
    public void ResumedSwing_FlownByPhysics_SettlesWithinTheSizedLength()
    {
        const double headingOffDeg = 45.0;
        double speedKts = LineUpGeometry.ResumedRolloutSpeedKts(AircraftCategory.Jet);
        double swingRate = CategoryPerformance.GroundYawRateAtSpeed(AircraftCategory.Jet, "B738", speedKts);
        double swingFt =
            LineUpGeometry.ResumedSwingRolloutLengthFt(AircraftCategory.Jet, headingOffDeg, swingRate, TurnModel.Ramp)
            - LineUpGeometry.RolloutLengthFt;
        double speedFtPerSec = speedKts * GeoMath.FeetPerNm / 3600.0;

        AircraftState ac = MakeGround("B738", AircraftCategory.Jet, speedKts);
        double target = _runway.TrueHeading.Degrees;
        ac.TrueHeading = new TrueHeading(target - headingOffDeg);
        ac.Targets.TargetTrueHeading = new TrueHeading(target);
        ac.Targets.TurnRateOverride = swingRate;

        int ticks = 0;
        while ((ac.TrueHeading.Degrees != target) || (ac.AppliedTurnRateDegPerSec != 0))
        {
            Assert.True(ticks++ < 400, "The swing never settled");
            Tick(ac, RampOptions, speedKts);
        }

        double flownFt = ticks * Dt * speedFtPerSec;
        Assert.True(
            Math.Abs(flownFt - swingFt) <= (Dt * speedFtPerSec) + 1e-9,
            $"Settled after {flownFt:F1}ft ({ticks} ticks), sized {swingFt:F1}ft, one tick {Dt * speedFtPerSec:F1}ft"
        );
    }

    private AircraftState MakeGround(string type, AircraftCategory cat, double groundSpeedKts)
    {
        Assert.Equal(cat, AircraftCategorization.Categorize(type));
        var ac = new AircraftState
        {
            Callsign = "TST200",
            AircraftType = type,
            Position = new LatLon(_runway.ThresholdLatitude, _runway.ThresholdLongitude),
            TrueHeading = _runway.TrueHeading,
            TrueTrack = _runway.TrueHeading,
            Altitude = 9,
            IsOnGround = true,
            IndicatedAirspeed = groundSpeedKts,
        };
        ac.Targets.TargetSpeed = groundSpeedKts;
        return ac;
    }

    private static void Tick(AircraftState ac, PhysicsTickOptions options, double groundSpeedKts)
    {
        ac.IndicatedAirspeed = groundSpeedKts; // on the ground GroundSpeed is the IAS
        FlightPhysics.Update(ac, Dt, aircraftLookup: null, weather: null, simTimeSeconds: 0, options);
    }

    /// <summary>The signed heading change from <paramref name="from"/> to <paramref name="to"/>, in (-180, 180].</summary>
    private static double SignedDelta(double from, double to)
    {
        double d = (to - from) % 360;
        if (d > 180)
        {
            d -= 360;
        }
        else if (d <= -180)
        {
            d += 360;
        }

        return d;
    }
}
