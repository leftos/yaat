using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests;

/// <summary>
/// How a simulated aircraft accelerates away from a standstill: the takeoff roll, the taxi roll, and
/// the first sub-tick after a takeoff clearance. Each test prints the per-second speed table it
/// measured; the assertions pin the target model — the takeoff roll follows the
/// <see cref="GroundRollProfile"/> spool ramp, the taxi roll is a constant category rate integrated by
/// physics alone — so a regression to a constant-thrust roll or to a phase-side integrator fails here.
/// </summary>
[Collection("NavDbMutator")]
public sealed class StandstillAccelerationProfileTests
{
    /// <summary>The shipping sim runs four physics sub-ticks per second; a ground roll must be ticked at that rate.</summary>
    private const double SubTick = 0.25;

    /// <summary>One knot flown for one second covers 1.6878 ft.</summary>
    private const double FeetPerKnotSecond = 1.6878;

    /// <summary>One knot per second of acceleration is 0.5144 m/s².</summary>
    private const double Mps2PerKnotPerSecond = 0.5144;

    private const double RunwayHeading = 280;

    private readonly ITestOutputHelper _output;

    public StandstillAccelerationProfileTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    /// <summary>One whole second of a ground roll: speed, the gain over the previous second, and distance covered.</summary>
    private sealed record RollSecond(int Second, double Ias, double DeltaKtPerSec, double Mps2, double DistanceFt);

    /// <summary>What one measured roll produced: the per-whole-second table and where liftoff landed.</summary>
    private sealed record RollMeasurement(List<RollSecond> Rows, double LiftoffSeconds, double LiftoffDistanceFt);

    /// <summary>Prints the measured roll beside the ramp's own closed-form prediction.</summary>
    private void PrintRollTable(string aircraftType, GroundRollProfile profile, double vr, RollMeasurement measured)
    {
        double expectedLiftoffSeconds = profile.TimeAtSpeed(vr);
        double expectedRollFt = profile.DistanceKtSecondsAt(expectedLiftoffSeconds) * FeetPerKnotSecond;

        _output.WriteLine(
            $"=== {aircraftType} takeoff roll (category={AircraftCategorization.Categorize(aircraftType)}, Vr={vr:F1}kt, "
                + $"idle={profile.IdleRateKtPerSec:F2}kt/s, steady={profile.SteadyRateKtPerSec:F2}kt/s, spool={profile.SpoolSeconds:F1}s) ==="
        );
        _output.WriteLine("t | kt | Δkt/s | m/s² | ft");
        foreach (var row in measured.Rows)
        {
            _output.WriteLine($"{row.Second} | {row.Ias:F2} | {row.DeltaKtPerSec:F2} | {row.Mps2:F2} | {row.DistanceFt:F1}");
        }

        _output.WriteLine(
            $"liftoff at t={measured.LiftoffSeconds:F2}s (expected {expectedLiftoffSeconds:F2}s = TimeAtSpeed(Vr)), "
                + $"roll={measured.LiftoffDistanceFt:F1}ft (expected {expectedRollFt:F1}ft)"
        );
        _output.WriteLine($"roll deviation from the ramp's distance integral: {((measured.LiftoffDistanceFt / expectedRollFt) - 1) * 100:F2}%");
    }

    // -------------------------------------------------------------------------
    // Takeoff roll
    // -------------------------------------------------------------------------

    /// <summary>
    /// The takeoff roll from a standing start: the acceleration ramps from the category's idle-thrust
    /// rate to its steady rate over the spool, so every whole second sits on
    /// <see cref="GroundRollProfile.SpeedAt"/>, liftoff comes at <see cref="GroundRollProfile.TimeAtSpeed"/>
    /// of Vr, and the roll is the ramp's own distance integral.
    /// </summary>
    [Theory]
    [InlineData("B738")]
    [InlineData("C172")]
    [InlineData("DH8D")]
    public void Takeoff_Ksfo28L_speed_profile(string aircraftType)
    {
        var category = AircraftCategorization.Categorize(aircraftType);
        double vr = AircraftPerformance.RotationSpeed(aircraftType, category);
        var profile = GroundRollProfile.For(aircraftType, category);

        var runway = TestRunwayFactory.Make(designator: "28L", airportId: "KSFO", heading: RunwayHeading, elevationFt: 0);
        var phase = new TakeoffPhase();
        var aircraft = new AircraftState
        {
            Callsign = "ROLL01",
            AircraftType = aircraftType,
            Position = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude),
            TrueHeading = new TrueHeading(RunwayHeading),
            TrueTrack = new TrueHeading(RunwayHeading),
            Altitude = 0,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            Phases = new PhaseList { AssignedRunway = runway },
        };
        var ctx = new PhaseContext
        {
            Aircraft = aircraft,
            Targets = aircraft.Targets,
            Category = category,
            DeltaSeconds = SubTick,
            Runway = runway,
            FieldElevation = 0,
            Logger = NullLogger.Instance,
        };

        phase.OnStart(ctx);

        // Prime the wind cache the way the real tick loop does. Calm wind: weather is null.
        FlightPhysics.Update(aircraft, SubTick, null, null, simTimeSeconds: 0);

        var rows = new List<RollSecond>();
        double distanceFt = 0;
        double liftoffSeconds = -1;
        double liftoffDistanceFt = 0;
        double previousWholeSecondIas = 0;

        const int maxSubTicks = (int)(120 / SubTick);
        for (int k = 1; k <= maxSubTicks; k++)
        {
            double t = k * SubTick;

            phase.OnTick(ctx);

            if (!aircraft.IsOnGround)
            {
                liftoffSeconds = t;
                liftoffDistanceFt = distanceFt;
                break;
            }

            FlightPhysics.Update(aircraft, SubTick, null, null, simTimeSeconds: t);
            distanceFt += aircraft.IndicatedAirspeed * SubTick * FeetPerKnotSecond;

            if (Math.Abs(t - Math.Round(t)) < 1e-9)
            {
                double delta = aircraft.IndicatedAirspeed - previousWholeSecondIas;
                rows.Add(new RollSecond((int)Math.Round(t), aircraft.IndicatedAirspeed, delta, delta * Mps2PerKnotPerSecond, distanceFt));
                previousWholeSecondIas = aircraft.IndicatedAirspeed;
            }
        }

        double expectedLiftoffSeconds = profile.TimeAtSpeed(vr);
        double expectedRollFt = profile.DistanceKtSecondsAt(expectedLiftoffSeconds) * FeetPerKnotSecond;

        PrintRollTable(aircraftType, profile, vr, new RollMeasurement(rows, liftoffSeconds, liftoffDistanceFt));

        Assert.True(liftoffSeconds > 0, $"{aircraftType} never became airborne within 120 s");
        Assert.NotEmpty(rows);

        // Every whole second of the roll sits on the ramp's closed form — the strongest pin there is
        // on the discrete integration, which gains exactly SpeedAt(tEnd) - SpeedAt(tStart) per sub-tick.
        foreach (var row in rows)
        {
            Assert.Equal(profile.SpeedAt(row.Second), row.Ias, 0.01);
        }

        // Liftoff at the ramp's own time to Vr, within the 0.25 s sub-tick the rotation gate is checked on.
        Assert.Equal(expectedLiftoffSeconds, liftoffSeconds, 0.3);

        // The roll is the ramp's distance integral; the discrete sum of IAS·dt trails it by the trapezoid error.
        Assert.Equal(expectedRollFt, liftoffDistanceFt, expectedRollFt * 0.015);
    }

    /// <summary>
    /// A rolling takeoff does not re-spool. <see cref="TakeoffPhase.OnStart"/> seeds the roll clock from the
    /// speed the aircraft arrives with — a CTO handed over by <c>LineUpPhase</c> in rolling mode brought its
    /// thrust up during the line-up — so the first second gains what the ramp says at THAT point on it, not
    /// the 1.4 kt a standing start gains. The 0 kt case is the table above, which is unchanged.
    /// </summary>
    [Fact]
    public void RollingTakeoff_AtRolloutSpeed_DoesNotRespool()
    {
        var profile = GroundRollProfile.For("B738", AircraftCategory.Jet);

        // 15 kt is exactly the jet's spool-exit speed (TimeAtSpeed(15) = 5 s), so the whole first second
        // is at the steady rate: 5.0 kt of gain.
        double gainFromFifteen = OneSecondOfRollGainKts(entrySpeedKts: 15.0);
        _output.WriteLine($"entering at 15 kt (clock {profile.TimeAtSpeed(15.0):F2}s): +{gainFromFifteen:F2} kt in the first second");
        Assert.Equal(5.0, gainFromFifteen, 0.01);

        // 5 kt is still on the ramp (clock 2.5 s), so the first second gains SpeedAt(3.5) - 5 = 3.4 kt —
        // less than the steady rate, but well past the 1.4 kt a roll from a standing start gains.
        double expectedFromFive = profile.SpeedAt(profile.TimeAtSpeed(5.0) + 1.0) - 5.0;
        double gainFromFive = OneSecondOfRollGainKts(entrySpeedKts: 5.0);
        _output.WriteLine($"entering at 5 kt (clock {profile.TimeAtSpeed(5.0):F2}s): +{gainFromFive:F2} kt (expected {expectedFromFive:F2})");
        Assert.Equal(expectedFromFive, gainFromFive, 0.01);
        Assert.True(
            gainFromFive > profile.SpeedAt(1.0),
            $"a rolling entry re-spooled from idle: +{gainFromFive:F2} kt vs +{profile.SpeedAt(1.0):F2}"
        );
    }

    /// <summary>
    /// Speed (kt) a B738 gains over the first whole second of a <see cref="TakeoffPhase"/> entered at
    /// <paramref name="entrySpeedKts"/>, ticked at the shipping sub-tick rate with physics in the loop.
    /// </summary>
    private static double OneSecondOfRollGainKts(double entrySpeedKts)
    {
        var runway = TestRunwayFactory.Make(designator: "28L", airportId: "KSFO", heading: RunwayHeading, elevationFt: 0);
        var aircraft = new AircraftState
        {
            Callsign = "ROLL02",
            AircraftType = "B738",
            Position = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude),
            TrueHeading = new TrueHeading(RunwayHeading),
            TrueTrack = new TrueHeading(RunwayHeading),
            Altitude = 0,
            IndicatedAirspeed = entrySpeedKts,
            IsOnGround = true,
            Phases = new PhaseList { AssignedRunway = runway },
        };
        var ctx = new PhaseContext
        {
            Aircraft = aircraft,
            Targets = aircraft.Targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = SubTick,
            Runway = runway,
            FieldElevation = 0,
            Logger = NullLogger.Instance,
        };

        var phase = new TakeoffPhase();
        aircraft.Phases.Add(phase);
        phase.OnStart(ctx);

        // Prime the wind cache the way the real tick loop does. Calm wind: weather is null.
        FlightPhysics.Update(aircraft, SubTick, null, null, simTimeSeconds: 0);

        for (int k = 1; k <= (int)(1.0 / SubTick); k++)
        {
            phase.OnTick(ctx);
            FlightPhysics.Update(aircraft, SubTick, null, null, simTimeSeconds: k * SubTick);
        }

        return aircraft.IndicatedAirspeed - entrySpeedKts;
    }

    // -------------------------------------------------------------------------
    // Taxi roll
    // -------------------------------------------------------------------------

    /// <summary>
    /// A B738 given <c>TAXI C 10L</c> from a standstill on SFO taxiway C, which runs straight for
    /// 11,676 ft with no corner: a constant 1.0 kt/s (0.51 m/s², the category's breakaway-thrust taxi
    /// rate) up to the 30 kt straight-taxi target. Physics is the only integrator — the navigator
    /// publishes the target and no longer adds a second increment of its own.
    /// </summary>
    [Fact]
    public void Taxi_B738_Ksfo_from_standstill_speed_profile()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            _output.WriteLine("SKIP: navdata or SFO layout not available");
            return;
        }

        // Node 571 on taxiway C: the head of an 11,676 ft straight run at ~118.6° true toward node 552.
        const double spawnLat = 37.630031;
        const double spawnLon = -122.392799;
        int spawnHdgMag = (int)Math.Round(MagneticDeclination.TrueToMagnetic(118.6, spawnLat, spawnLon, MagneticDeclination.EvaluationDateUtc));

        string json = BuildScenarioJson(spawnLat, spawnLon, spawnHdgMag);
        var warnings = engine.LoadScenario(json, rngSeed: 42, sessionStartUtc: MagneticDeclination.EvaluationDateUtc);
        foreach (var w in warnings)
        {
            _output.WriteLine($"[WARN] {w}");
        }

        var spawned = engine.FindAircraft("TEST1");
        Assert.NotNull(spawned);

        const int seconds = 60;
        var ias = new double[seconds + 1];
        ias[0] = spawned.IndicatedAirspeed;

        string previousPhase = spawned.Phases?.CurrentPhase?.Name ?? "(null)";
        _output.WriteLine($"[t=0] spawn heading={spawnHdgMag}° magnetic, phase={previousPhase}, ias={ias[0]:F2}kt");

        for (int t = 1; t <= seconds; t++)
        {
            engine.TickOneSecond();
            var ac = engine.FindAircraft("TEST1");
            Assert.NotNull(ac);
            ias[t] = ac.IndicatedAirspeed;

            string phase = ac.Phases?.CurrentPhase?.Name ?? "(null)";
            if (phase != previousPhase)
            {
                _output.WriteLine($"[t={t}] phase {previousPhase} -> {phase}");
                previousPhase = phase;
            }
        }

        _output.WriteLine("=== B738 taxi from standstill (TAXI C 10L, spawn on SFO taxiway C node 571) ===");
        _output.WriteLine("t | kt | Δkt/s | m/s²");
        for (int t = 0; t <= seconds; t++)
        {
            double delta = t == 0 ? 0 : ias[t] - ias[t - 1];
            _output.WriteLine($"{t} | {ias[t]:F2} | {delta:F2} | {delta * Mps2PerKnotPerSecond:F2}");
        }

        int firstMoving = -1;
        for (int t = 1; t <= seconds; t++)
        {
            if (ias[t] > 0)
            {
                firstMoving = t;
                break;
            }
        }

        Assert.True(firstMoving > 0, $"the aircraft never started moving within {seconds} s of the preset TAXI");
        _output.WriteLine($"first moving second: t={firstMoving} (the preset TAXI fires at t=1)");
        Assert.True(firstMoving is 1 or 2, $"expected motion on the first or second tick after dispatch; got t={firstMoving}");

        // Physics is the only integrator: 1.0 kt/s (0.51 m/s²) from the standstill, so the first five
        // seconds of motion are 1, 2, 3, 4, 5 kt. The taxiway C kink caps measured on this route only
        // bite above 12 kt, so nothing intervenes this early.
        for (int i = 0; i <= 4; i++)
        {
            double expected = i + 1;
            Assert.Equal(expected, ias[firstMoving + i], 0.01);
        }

        // The straight-taxi target is the ceiling the route settles at: 30 kt, reached at 1.0 kt/s.
        double maxIas = 0;
        int maxAt = -1;
        for (int t = 1; t <= seconds; t++)
        {
            if (ias[t] > maxIas)
            {
                maxIas = ias[t];
                maxAt = t;
            }
        }

        _output.WriteLine($"maximum IAS within {seconds}s: {maxIas:F2} kt at t={maxAt}s");
        Assert.Equal(30.0, maxIas, 0.01);
    }

    // -------------------------------------------------------------------------
    // Takeoff clearance from a lineup
    // -------------------------------------------------------------------------

    /// <summary>
    /// A lined-up aircraft whose takeoff clearance is already in hand: how many 0.25 s sub-ticks
    /// pass between the clearance and the first movement. The lineup phase spends the first
    /// sub-tick completing and handing over to <see cref="TakeoffPhase"/> (which is started but
    /// not ticked in the same pass), so the roll begins on the second sub-tick.
    /// </summary>
    [Fact]
    public void Cto_from_luaw_rolls_on_second_subtick()
    {
        var runway = TestRunwayFactory.Make(designator: "28L", airportId: "KSFO", heading: RunwayHeading, elevationFt: 0);
        var init = AircraftInitializer.InitializeOnRunway(runway, AircraftCategory.Jet);
        var aircraft = new AircraftState
        {
            Callsign = "LUAW01",
            AircraftType = "B738",
            Position = init.Position,
            TrueHeading = init.TrueHeading,
            TrueTrack = init.TrueHeading,
            Altitude = init.Altitude,
            IndicatedAirspeed = init.Speed,
            IsOnGround = init.IsOnGround,
            Phases = init.Phases,
        };
        var ctx = new PhaseContext
        {
            Aircraft = aircraft,
            Targets = aircraft.Targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = SubTick,
            Runway = runway,
            FieldElevation = 0,
            Logger = NullLogger.Instance,
        };

        var luaw = Assert.IsType<LinedUpAndWaitingPhase>(init.Phases.CurrentPhase);
        Assert.True(luaw.SatisfyClearance(ClearanceType.ClearedForTakeoff));

        PhaseRunner.Tick(aircraft, ctx);
        FlightPhysics.Update(aircraft, SubTick, null, null, simTimeSeconds: 0);
        double iasAfterFirstSubTick = aircraft.IndicatedAirspeed;
        string phaseAfterFirstSubTick = aircraft.Phases?.CurrentPhase?.Name ?? "(null)";

        PhaseRunner.Tick(aircraft, ctx);
        FlightPhysics.Update(aircraft, SubTick, null, null, simTimeSeconds: SubTick);
        double iasAfterSecondSubTick = aircraft.IndicatedAirspeed;
        string phaseAfterSecondSubTick = aircraft.Phases?.CurrentPhase?.Name ?? "(null)";

        _output.WriteLine("=== CTO already satisfied on a lined-up B738 ===");
        _output.WriteLine($"sub-tick 1 (t=0.25s): phase={phaseAfterFirstSubTick} ias={iasAfterFirstSubTick:F2}kt");
        _output.WriteLine($"sub-tick 2 (t=0.50s): phase={phaseAfterSecondSubTick} ias={iasAfterSecondSubTick:F2}kt");

        // The phase hand-over costs one sub-tick: LinedUpAndWaitingPhase completes and starts
        // TakeoffPhase, whose OnTick does not run until the next pass, so the aircraft is still
        // stationary after the first sub-tick and picks up the spool ramp's first sub-tick of gain
        // (0.275 kt at idle thrust, not the steady rate's 1.25) on the second.
        Assert.Equal("Takeoff", phaseAfterFirstSubTick);
        Assert.Equal(0.0, iasAfterFirstSubTick, 0.01);
        Assert.Equal(GroundRollProfile.For("B738", AircraftCategory.Jet).SpeedAt(SubTick), iasAfterSecondSubTick, 0.01);
    }

    // -------------------------------------------------------------------------
    // Engine harness
    // -------------------------------------------------------------------------

    private SimulationEngine? BuildEngine()
    {
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("SFO") is null)
        {
            return null;
        }

        SimLogBuilder.CreateForTest(_output).InitializeSimLog();
        return new SimulationEngine(groundData);
    }

    /// <summary>
    /// A minimal single-aircraft SFO scenario placing a B738 at <paramref name="lat"/>/<paramref name="lon"/>
    /// with one preset <c>TAXI C 10L</c> that fires at t=1.
    /// </summary>
    private static string BuildScenarioJson(double lat, double lon, int headingMag)
    {
        return $$"""
            {
              "id": "test-standstill-taxi",
              "name": "Standstill Taxi Acceleration Test",
              "artccId": "ZOA",
              "primaryAirportId": "SFO",
              "initializationTriggers": [],
              "aircraftGenerators": [],
              "aircraft": [
                {
                  "id": "test-standstill-taxi-1",
                  "aircraftId": "TEST1",
                  "aircraftType": "B738",
                  "transponderMode": "Standby",
                  "startingConditions": {
                    "type": "Coordinates",
                    "coordinates": {"lat": {{lat}}, "lon": {{lon}}},
                    "heading": {{headingMag}}
                  },
                  "onAltitudeProfile": false,
                  "flightplan": {
                    "rules": "IFR",
                    "departure": "KSFO",
                    "destination": "KLAX",
                    "cruiseAltitude": 35000,
                    "cruiseSpeed": 450,
                    "route": "",
                    "remarks": "",
                    "aircraftType": "B738/L"
                  },
                  "presetCommands": [
                    {"id": "p1", "command": "TAXI C 10L", "timeOffset": 0}
                  ],
                  "spawnDelay": 0,
                  "airportId": "SFO",
                  "difficulty": "Easy"
                }
              ],
              "atc": [],
              "studentPositionId": "",
              "autoDeleteMode": "Parked",
              "flightStripConfigurations": []
            }
            """;
    }
}
