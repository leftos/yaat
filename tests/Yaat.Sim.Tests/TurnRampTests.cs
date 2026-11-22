using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Tests;

/// <summary>
/// The ramp turn model (YAAT-2341): an airborne turn rolls in at alpha = full rate / roll-in time, eases out on the
/// sqrt(2 * alpha * remaining) law so the wings are level exactly at the target heading, and is carried by the scenario's
/// <see cref="TurnModel"/> so recordings made before it replay with instant-rate turns.
/// </summary>
public class TurnRampTests
{
    private const double Dt = 0.25;
    private const string RecordingPath = "TestData/diagonal-lineup-28r-recording.zip";

    private static readonly PhysicsTickOptions RampOptions = PhysicsTickOptions.Default with { TurnModel = TurnModel.Ramp };

    public TurnRampTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void FirstTick_RollsIn_TurnsLessThanHalfTheFullRate()
    {
        AircraftState ac = MakeAircraft("B738", heading: 90, ias: 250, altitude: 10000);
        ac.Targets.TargetTrueHeading = new TrueHeading(180);
        double fullRate = AircraftPerformance.TurnRate("B738", AircraftCategory.Jet);

        Tick(ac, RampOptions);

        double turned = SignedDelta(90, ac.TrueHeading.Degrees);
        Assert.True(turned > 0, $"Expected a right turn to start, turned {turned:F4}");
        Assert.True(turned < 0.5 * fullRate * Dt, $"Expected under half the full-rate step {0.5 * fullRate * Dt:F4}, turned {turned:F4}");
        Assert.True(ac.BankAngle > 0, $"Expected a right bank, got {ac.BankAngle:F2}");
        Assert.True(ac.BankAngle < 15, $"Expected bank under 15 deg on the first tick, got {ac.BankAngle:F2}");
    }

    [Fact]
    public void Jet_ReachesFullRateAfterRollInTime_AndHoldsItUntilTheRollOut()
    {
        AircraftState ac = MakeAircraft("B738", heading: 90, ias: 250, altitude: 10000);
        ac.Targets.TargetTrueHeading = new TrueHeading(180);
        double fullRate = AircraftPerformance.TurnRate("B738", AircraftCategory.Jet);
        int rollTicks = (int)Math.Round(CategoryPerformance.JetRollInSeconds / Dt);

        for (int i = 0; i < rollTicks; i++)
        {
            double before = ac.TrueHeading.Degrees;
            Tick(ac, RampOptions);
            if (i < rollTicks - 1)
            {
                Assert.True(SignedDelta(before, ac.TrueHeading.Degrees) < (fullRate * Dt) - 1e-6, $"Full rate reached early, at tick {i + 1}");
            }
        }

        // Past the roll-in, hold the full rate until the roll-out begins (full rate * T_roll / 2 degrees from the target).
        double rollOutStartDeg = fullRate * CategoryPerformance.JetRollInSeconds / 2;
        int fullRateTicks = 0;
        while (SignedDelta(ac.TrueHeading.Degrees, 180) > rollOutStartDeg + (2 * fullRate * Dt))
        {
            double before = ac.TrueHeading.Degrees;
            Tick(ac, RampOptions);
            Assert.Equal(fullRate * Dt, SignedDelta(before, ac.TrueHeading.Degrees), 1e-6);
            fullRateTicks++;
        }

        Assert.True(fullRateTicks > 50, $"Expected a long full-rate segment, got {fullRateTicks} ticks");
    }

    [Fact]
    public void RollOut_EasesTheRate_AndEndsExactlyOnTargetWingsLevel()
    {
        // 8 deg out: beyond the braking distance from the full rate (about full rate * T_roll / 2 = 6.25 deg).
        AircraftState ac = MakeAircraft("B738", heading: 172, ias: 250, altitude: 10000);
        ac.Targets.TargetTrueHeading = new TrueHeading(180);
        double fullRate = AircraftPerformance.TurnRate("B738", AircraftCategory.Jet);
        ac.AppliedTurnRateDegPerSec = fullRate;

        bool easedBelowFull = false;
        int ticks = 0;
        while ((ac.TrueHeading.Degrees != 180) || (ac.AppliedTurnRateDegPerSec != 0))
        {
            Assert.True(ticks++ < 100, "Roll-out never finished");
            double before = ac.TrueHeading.Degrees;
            Tick(ac, RampOptions);
            double rate = SignedDelta(before, ac.TrueHeading.Degrees) / Dt;
            if ((rate < fullRate - 1e-6) && (ac.TrueHeading.Degrees != 180))
            {
                easedBelowFull = true;
            }

            Assert.True(SignedDelta(ac.TrueHeading.Degrees, 180) >= -1e-9, $"Overshot the target: heading {ac.TrueHeading.Degrees:F4}");
        }

        Assert.True(easedBelowFull, "The rate never eased below full before the target");
        Assert.Equal(180.0, ac.TrueHeading.Degrees);
        Assert.Equal(0.0, ac.AppliedTurnRateDegPerSec);
        Assert.Equal(0.0, ac.BankAngle);
    }

    [Fact]
    public void SmallTurn_NeverReachesFullRate_PeaksAtSqrtAlphaTheta_ArrivesWingsLevel()
    {
        AircraftState ac = MakeAircraft("B738", heading: 90, ias: 250, altitude: 10000);
        ac.Targets.TargetTrueHeading = new TrueHeading(93);
        double fullRate = AircraftPerformance.TurnRate("B738", AircraftCategory.Jet);
        double alpha = fullRate / CategoryPerformance.JetRollInSeconds;

        double peak = 0;
        int ticks = 0;
        double? arrivalRate = null;
        while ((ac.TrueHeading.Degrees != 93) || (ac.AppliedTurnRateDegPerSec != 0))
        {
            Assert.True(ticks++ < 100, "Small turn never finished");
            Tick(ac, RampOptions);
            peak = Math.Max(peak, ac.AppliedTurnRateDegPerSec ?? 0);
            if ((arrivalRate is null) && (ac.TrueHeading.Degrees == 93))
            {
                arrivalRate = ac.AppliedTurnRateDegPerSec;
            }
        }

        Assert.Equal(Math.Sqrt(alpha * 3), peak, 0.05);
        Assert.NotNull(arrivalRate);
        Assert.True(Math.Abs(arrivalRate.Value) <= (alpha * Dt) + 1e-9, $"Arrived on the target with {arrivalRate:F4} deg/s still on");
        Assert.True(
            Math.Abs((ticks * Dt) - SmallTurnDiscreteFinishSeconds) <= Dt,
            $"Expected to finish in {SmallTurnDiscreteFinishSeconds:F2} s, took {ticks * Dt:F2} s"
        );
    }

    // The discrete braking law's finish for a 3 deg jet turn (alpha 0.5 deg/s^2, 0.25 s ticks), traced tick by tick from the
    // law: arrival on the target at 4.75 s with 0.0765 deg/s on, the snap at 5.0 s; the continuous 2 * sqrt(theta / alpha) is 4.9 s.
    private const double SmallTurnDiscreteFinishSeconds = 5.0;

    [Theory]
    [InlineData(90.0411, 180.93)]
    [InlineData(10.37, 97.2)]
    [InlineData(271.999, 3.0001)]
    [InlineData(45.5, 46.6)]
    [InlineData(200.123456, 110.654321)]
    [InlineData(359.71, 0.29)]
    public void RollOut_FromFractionalHeadings_LandsExactlyOnTheTarget_WithNoOvershoot(double start, double goal)
    {
        AircraftState ac = MakeAircraft("B738", heading: start, ias: 250, altitude: 10000);
        ac.Targets.TargetTrueHeading = new TrueHeading(goal);
        double target = new TrueHeading(goal).Degrees;
        double alphaStep = AircraftPerformance.TurnRate("B738", AircraftCategory.Jet) / CategoryPerformance.JetRollInSeconds * Dt;
        double side = Math.Sign(SignedDelta(start, goal));

        bool arrived = false;
        for (int ticks = 0; (ticks < 400) && !((ac.TrueHeading.Degrees == target) && (ac.AppliedTurnRateDegPerSec == 0)); ticks++)
        {
            Tick(ac, RampOptions);
            Assert.True(side * SignedDelta(ac.TrueHeading.Degrees, target) >= 0, $"Passed the target: {ac.TrueHeading.Degrees}");
            if (!arrived && (ac.TrueHeading.Degrees == target))
            {
                arrived = true;
                Assert.True(
                    Math.Abs(ac.AppliedTurnRateDegPerSec!.Value) <= alphaStep + 1e-9,
                    $"Arrived with {ac.AppliedTurnRateDegPerSec:F4} deg/s on"
                );
            }
        }

        Assert.True(arrived, "Never landed on the target");
        Assert.Equal(target, ac.TrueHeading.Degrees);
        Assert.Equal(0.0, ac.AppliedTurnRateDegPerSec);
        Assert.Equal(0.0, ac.BankAngle);
    }

    [Fact]
    public void PresentHeadingAtFullRate_OvershootsByTheBrakingDistance_ReturnsAndFinishesOnIt()
    {
        AircraftState ac = MakeAircraft("B738", heading: 90, ias: 250, altitude: 10000);
        ac.Targets.TargetTrueHeading = new TrueHeading(270);
        double alphaStep = AircraftPerformance.TurnRate("B738", AircraftCategory.Jet) / CategoryPerformance.JetRollInSeconds * Dt;
        for (int i = 0; i < 30; i++)
        {
            Tick(ac, RampOptions);
        }

        double captured = ac.TrueHeading.Degrees;
        ac.Targets.TargetTrueHeading = new TrueHeading(captured);
        double maxPast = 0;
        int ticks = 0;
        while ((ac.TrueHeading.Degrees != captured) || (ac.AppliedTurnRateDegPerSec != 0))
        {
            Assert.True(ticks++ < 200, "Never settled on the present heading");
            double before = ac.TrueHeading.Degrees;
            Tick(ac, RampOptions);
            maxPast = Math.Max(maxPast, SignedDelta(captured, ac.TrueHeading.Degrees));
            bool settled = (ac.TrueHeading.Degrees == captured) && (ac.AppliedTurnRateDegPerSec == 0);
            if (!settled && (Math.Abs(ac.AppliedTurnRateDegPerSec!.Value) > alphaStep + 1e-9))
            {
                Assert.NotEqual(before, ac.TrueHeading.Degrees);
            }
        }

        // Braking from the full rate over the roll-in time: about full rate * T_roll / 2 = 6.25 deg past.
        Assert.True(maxPast > 3, $"Expected to roll out past the captured heading, max past {maxPast:F2}");
        Assert.Equal(captured, ac.TrueHeading.Degrees);
        Assert.Equal(0.0, ac.BankAngle);
    }

    [Fact]
    public void AssignedLongWay_TurnLeftHeading100From090_RampsRoundTheLongWay()
    {
        AircraftState ac = MakeAircraft("B738", heading: 90, ias: 250, altitude: 10000);
        ac.Targets.TargetTrueHeading = new TrueHeading(100);
        ac.Targets.PreferredTurnDirection = TurnDirection.Left;
        double turned = 0;
        int ticks = 0;
        while ((ac.TrueHeading.Degrees != 100) || (ac.AppliedTurnRateDegPerSec != 0))
        {
            Assert.True(ticks++ < 1000, "Long-way turn never finished");
            double before = ac.TrueHeading.Degrees;
            Tick(ac, RampOptions);
            double step = SignedDelta(before, ac.TrueHeading.Degrees);
            Assert.True(step <= 0, $"Turned right {step:F4} on a left turn");
            turned += step;
        }

        Assert.Equal(-350, turned, 1e-6);
        Assert.Equal(0.0, ac.BankAngle);
    }

    [Fact]
    public void MovingTarget_DirectToAFix_FinishesWithTheSnap()
    {
        AircraftState ac = MakeAircraft("B738", heading: 90, ias: 250, altitude: 10000);
        var fix = new LatLon(37.95, -122.30);
        bool snapped = false;
        for (int i = 0; (i < 400) && !snapped; i++)
        {
            ac.Targets.TargetTrueHeading = new TrueHeading(GeoMath.BearingTo(ac.Position, fix));
            Tick(ac, RampOptions);
            snapped = (ac.TrueHeading.Degrees == ac.Targets.TargetTrueHeading.Value.Degrees) && (ac.AppliedTurnRateDegPerSec == 0);
        }

        Assert.True(snapped, $"Never settled on the fix: heading {ac.TrueHeading.Degrees:F3}, rate {ac.AppliedTurnRateDegPerSec:F4}");
        Assert.Equal(0.0, ac.BankAngle);
        for (int i = 0; i < 20; i++)
        {
            ac.Targets.TargetTrueHeading = new TrueHeading(GeoMath.BearingTo(ac.Position, fix));
            Tick(ac, RampOptions);
            Assert.True(Math.Abs(SignedDelta(ac.TrueHeading.Degrees, GeoMath.BearingTo(ac.Position, fix))) < 1, "Wandered off the fix");
        }
    }

    [Fact]
    public void Helicopter_CrossingThirtyKnotsMidTurn_SwitchesModel()
    {
        AircraftState ac = MakeAircraft("EC35", heading: 90, ias: 25, altitude: 1000);
        ac.Targets.TargetTrueHeading = new TrueHeading(270);
        double fullRate = AircraftPerformance.TurnRate("EC35", AircraftCategory.Helicopter);
        double alphaStep = fullRate / CategoryPerformance.HelicopterRollInSeconds * Dt;

        double before = ac.TrueHeading.Degrees;
        Tick(ac, RampOptions);
        Assert.Equal(fullRate * Dt, SignedDelta(before, ac.TrueHeading.Degrees), 1e-9);
        Assert.Null(ac.AppliedTurnRateDegPerSec);

        ac.IndicatedAirspeed = 40;
        before = ac.TrueHeading.Degrees;
        Tick(ac, RampOptions);
        Assert.Equal(alphaStep, ac.AppliedTurnRateDegPerSec!.Value, 1e-9);
        Assert.Equal(alphaStep * Dt, SignedDelta(before, ac.TrueHeading.Degrees), 1e-9);

        ac.IndicatedAirspeed = 20;
        before = ac.TrueHeading.Degrees;
        Tick(ac, RampOptions);
        Assert.Null(ac.AppliedTurnRateDegPerSec);
        Assert.Equal(fullRate * Dt, SignedDelta(before, ac.TrueHeading.Degrees), 1e-9);
    }

    [Fact]
    public void Reversal_PassesThroughZeroRate_AndWingsLevel()
    {
        AircraftState ac = MakeAircraft("B738", heading: 90, ias: 250, altitude: 10000);
        ac.Targets.TargetTrueHeading = new TrueHeading(180);
        double fullRate = AircraftPerformance.TurnRate("B738", AircraftCategory.Jet);
        double alphaStep = fullRate / CategoryPerformance.JetRollInSeconds * Dt;
        for (int i = 0; i < 30; i++)
        {
            Tick(ac, RampOptions);
        }

        Assert.Equal(fullRate, ac.AppliedTurnRateDegPerSec!.Value, 1e-6);

        ac.Targets.TargetTrueHeading = new TrueHeading(0);
        double previous = ac.AppliedTurnRateDegPerSec.Value;
        double minAbsBank = double.MaxValue;
        bool reversed = false;
        for (int i = 0; i < 80; i++)
        {
            Tick(ac, RampOptions);
            double applied = ac.AppliedTurnRateDegPerSec!.Value;
            minAbsBank = Math.Min(minAbsBank, Math.Abs(ac.BankAngle));
            if (Math.Sign(applied) != Math.Sign(previous))
            {
                Assert.True(
                    (Math.Abs(applied) <= alphaStep + 1e-9) || (Math.Abs(previous) <= alphaStep + 1e-9),
                    $"Rate jumped across zero: {previous:F4} -> {applied:F4}"
                );
            }

            Assert.True(Math.Abs(applied - previous) <= alphaStep + 1e-9, $"Rate stepped more than alpha*dt: {previous:F4} -> {applied:F4}");
            reversed |= applied < 0;
            previous = applied;
        }

        Assert.True(reversed, "The turn never reversed to the left");
        Assert.True(minAbsBank < 1.0, $"Bank never went through wings level, min |bank| {minAbsBank:F2}");
    }

    [Fact]
    public void Piston_ReachesFullRateAfterThreeSeconds()
    {
        AircraftState ac = MakeAircraft("C172", heading: 90, ias: 90, altitude: 2000);
        AssertReachesFullRateAfter(ac, AircraftCategory.Piston, CategoryPerformance.PistonRollInSeconds);
    }

    [Fact]
    public void HelicopterInForwardFlight_ReachesFullRateAfterTwoSeconds()
    {
        AircraftState ac = MakeAircraft("EC35", heading: 90, ias: 100, altitude: 1500);
        AssertReachesFullRateAfter(ac, AircraftCategory.Helicopter, CategoryPerformance.HelicopterRollInSeconds);
    }

    [Fact]
    public void HelicopterBelowThirtyKnotsIndicated_TurnsInstantly()
    {
        Assert.Equal(AircraftCategory.Helicopter, AircraftCategorization.Categorize("EC35"));
        AircraftState ramp = MakeAircraft("EC35", heading: 90, ias: 10, altitude: 500);
        AircraftState instant = MakeAircraft("EC35", heading: 90, ias: 10, altitude: 500);
        foreach (AircraftState ac in new[] { ramp, instant })
        {
            ac.Targets.TargetTrueHeading = new TrueHeading(180);
        }

        for (int i = 0; i < 4; i++)
        {
            Tick(ramp, RampOptions);
            Tick(instant, PhysicsTickOptions.Default);
            Assert.Equal(instant.TrueHeading.Degrees, ramp.TrueHeading.Degrees);
            Assert.Equal(instant.BankAngle, ramp.BankAngle);
            Assert.Null(ramp.AppliedTurnRateDegPerSec);
        }

        Assert.Equal(90 + (4 * AircraftPerformance.TurnRate("EC35", AircraftCategory.Helicopter) * Dt), ramp.TrueHeading.Degrees, 1e-9);
    }

    [Fact]
    public void OnGround_RollingAboveCreepSpeed_RampsTheNoseWheelSteering()
    {
        AircraftState ramp = MakeAircraft("B738", heading: 90, ias: 10, altitude: 10);
        AircraftState instant = MakeAircraft("B738", heading: 90, ias: 10, altitude: 10);
        AircraftState[] pair = [ramp, instant];
        foreach (AircraftState ac in pair)
        {
            ac.IsOnGround = true;
            ac.Targets.TargetTrueHeading = new TrueHeading(120);
            ac.Targets.TurnRateOverride = 10;
        }

        Tick(ramp, RampOptions);
        Tick(instant, PhysicsTickOptions.Default);

        double alphaStep = CategoryPerformance.GroundTurnRate(AircraftCategory.Jet) / CategoryPerformance.JetGroundSteerRampSeconds * Dt;
        Assert.Equal(alphaStep, Assert.NotNull(ramp.AppliedTurnRateDegPerSec), 1e-9);
        Assert.Equal(90 + (alphaStep * Dt), ramp.TrueHeading.Degrees, 1e-9);
        Assert.Equal(90 + (10 * Dt), instant.TrueHeading.Degrees, 1e-9);
        Assert.Null(instant.AppliedTurnRateDegPerSec);
    }

    [Fact]
    public void TurnRateOverride_IsTheFullRate_ReachedAfterTheJetRollInTime()
    {
        AircraftState ac = MakeAircraft("B738", heading: 90, ias: 250, altitude: 10000);
        ac.Targets.TurnRateOverride = 1.5;
        ac.Targets.TargetTrueHeading = new TrueHeading(180);
        int rollTicks = (int)Math.Round(CategoryPerformance.JetRollInSeconds / Dt);

        for (int i = 0; i < rollTicks - 1; i++)
        {
            Tick(ac, RampOptions);
        }

        Assert.True(ac.AppliedTurnRateDegPerSec < 1.5 - 1e-3, $"Override rate reached early: {ac.AppliedTurnRateDegPerSec:F4}");
        Tick(ac, RampOptions);
        Assert.Equal(1.5, ac.AppliedTurnRateDegPerSec!.Value, 1e-6);
        for (int i = 0; i < 20; i++)
        {
            Tick(ac, RampOptions);
            Assert.Equal(1.5, ac.AppliedTurnRateDegPerSec!.Value, 1e-6);
        }
    }

    [Fact]
    public void ScenarioTurnModel_RoundTripsThroughTheSnapshot()
    {
        var scenario = new SimScenarioState
        {
            ScenarioId = "test",
            ScenarioName = "Test",
            RngSeed = 1,
            OriginalScenarioJson = "{}",
            TurnModel = TurnModel.Ramp,
        };

        ScenarioSnapshotDto dto = scenario.ToSnapshot();
        Assert.Equal(TurnModel.Ramp, dto.TurnModel);

        string json = JsonSerializer.Serialize(dto, RecordingJsonOptions.Default);
        Assert.Equal(TurnModel.Ramp, JsonSerializer.Deserialize<ScenarioSnapshotDto>(json, RecordingJsonOptions.Default)!.TurnModel);

        JsonObject node = JsonNode.Parse(json)!.AsObject();
        string key = node.Select(p => p.Key).Single(k => string.Equals(k, nameof(ScenarioSnapshotDto.TurnModel), StringComparison.OrdinalIgnoreCase));
        node.Remove(key);
        Assert.Equal(TurnModel.Instant, node.Deserialize<ScenarioSnapshotDto>(RecordingJsonOptions.Default)!.TurnModel);
    }

    [Fact]
    public void Replay_TakesTheRecordingsTurnModel_CommittedFixtureReadsInstant()
    {
        SessionRecording? recording = RecordingLoader.Load(RecordingPath);
        Assert.NotNull(recording);
        Assert.NotNull(TestVnasData.NavigationDb);
        Assert.Equal(TurnModel.Instant, recording.TurnModel);

        var instantEngine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped());
        instantEngine.Replay(recording, 1);
        Assert.Equal(TurnModel.Instant, instantEngine.Scenario!.TurnModel);

        var rampRecording = new SessionRecording
        {
            ScenarioJson = recording.ScenarioJson,
            RngSeed = recording.RngSeed,
            WeatherJson = recording.WeatherJson,
            ArtccConfigJson = recording.ArtccConfigJson,
            ToweredAirportIds = recording.ToweredAirportIds,
            SessionStartUtc = recording.SessionStartUtc,
            InitialActiveRunways = recording.InitialActiveRunways,
            Actions = [],
            TotalElapsedSeconds = 1,
            TurnModel = TurnModel.Ramp,
        };
        var rampEngine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped());
        rampEngine.Replay(rampRecording, 1);
        Assert.Equal(TurnModel.Ramp, rampEngine.Scenario!.TurnModel);
    }

    [Fact]
    public void SnapshotRestoreMidTurn_ContinuesIdenticallyToAnUninterruptedRun()
    {
        AircraftState uninterrupted = MakeAircraft("B738", heading: 90, ias: 250, altitude: 10000);
        uninterrupted.Targets.TargetTrueHeading = new TrueHeading(180);
        for (int i = 0; i < 8; i++)
        {
            Tick(uninterrupted, RampOptions);
        }

        string json = JsonSerializer.Serialize(uninterrupted.ToSnapshot(), RecordingJsonOptions.Default);
        var restored = AircraftState.FromSnapshot(JsonSerializer.Deserialize<AircraftSnapshotDto>(json, RecordingJsonOptions.Default)!, null);
        Assert.Equal(uninterrupted.AppliedTurnRateDegPerSec, restored.AppliedTurnRateDegPerSec);
        Assert.NotNull(restored.AppliedTurnRateDegPerSec);

        for (int i = 0; i < 40; i++)
        {
            Tick(uninterrupted, RampOptions);
            Tick(restored, RampOptions);
            Assert.Equal(uninterrupted.TrueHeading.Degrees, restored.TrueHeading.Degrees);
            Assert.Equal(uninterrupted.AppliedTurnRateDegPerSec, restored.AppliedTurnRateDegPerSec);
            Assert.Equal(uninterrupted.BankAngle, restored.BankAngle);
        }
    }

    private static void AssertReachesFullRateAfter(AircraftState ac, AircraftCategory cat, double rollSeconds)
    {
        Assert.Equal(cat, AircraftCategorization.Categorize(ac.AircraftType));
        ac.Targets.TargetTrueHeading = new TrueHeading(ac.TrueHeading.Degrees + 120);
        double fullRate = AircraftPerformance.TurnRate(ac.AircraftType, cat);
        int rollTicks = (int)Math.Round(rollSeconds / Dt);

        for (int i = 0; i < rollTicks - 1; i++)
        {
            Tick(ac, RampOptions);
        }

        Assert.True(ac.AppliedTurnRateDegPerSec < fullRate - 1e-3, $"Full rate reached before {rollSeconds} s: {ac.AppliedTurnRateDegPerSec:F4}");
        Tick(ac, RampOptions);
        Assert.Equal(fullRate, ac.AppliedTurnRateDegPerSec!.Value, 1e-6);
    }

    private static void Tick(AircraftState ac, PhysicsTickOptions options) =>
        FlightPhysics.Update(ac, Dt, aircraftLookup: null, weather: null, simTimeSeconds: 0, options);

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

    private static AircraftState MakeAircraft(string type, double heading, double ias, double altitude)
    {
        return new AircraftState
        {
            Callsign = "TST100",
            AircraftType = type,
            Position = new LatLon(37.721, -122.221),
            TrueHeading = new TrueHeading(heading),
            TrueTrack = new TrueHeading(heading),
            Altitude = altitude,
            IndicatedAirspeed = ias,
        };
    }
}
