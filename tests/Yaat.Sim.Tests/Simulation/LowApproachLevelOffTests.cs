using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// A low approach levels off at its category height before the threshold, crosses it level, holds level
/// for about ten seconds and then climbs straight ahead (AIM 4-3-12.a: no turns or climb until the pilot
/// has made a thorough visual check).
///
/// Recording: S2-OAK-3 (ZOA, OAK), trimmed to t=0 and t=1705. At t=1705 N172SP (C172, piston) is on
/// final for 28R at about 272 ft MSL on a touch-and-go clearance; the owner then typed <c>LA</c>.
/// Every test restores the t=1705 snapshot and drives the engine with
/// <see cref="SimulationEngine.TickOneSecond"/>.
/// </summary>
public class LowApproachLevelOffTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/low-approach-descends-to-runway-recording.yaat-bug-report-bundle.zip";
    private const string Callsign = "N172SP";
    private const int RestoreTime = 1705;
    private const int MaxTicks = 120;

    /// <summary>The piston level-off height above the runway, feet.</summary>
    private const double PistonLevelOffAgl = 100.0;

    /// <summary>Altitude tolerance, feet: the physics altitude snap.</summary>
    private const double ToleranceFt = 10.0;

    /// <summary>Largest altitude change, feet, between two ticks that still counts as level.</summary>
    private const double LevelChangeFt = 1.0;

    /// <summary>Ticks after the low approach becomes current that it is still level.</summary>
    private const int LevelTicks = 9;

    /// <summary>Ticks after the low approach becomes current by which it has climbed.</summary>
    private const int ClimbedByTick = 15;

    /// <summary>Minimum climb, feet, from the low approach's first tick to <see cref="ClimbedByTick"/>.</summary>
    private const double MinimumClimbFt = 30.0;

    private sealed record Sample(int Tick, Phase? Phase, double Altitude, bool ClimbingOut, bool OnGround);

    [Fact]
    public void La_OnShortFinal28R_LevelsAtTheFloorBeforeTheThreshold()
    {
        RunwayInfo? runway = TestVnasData.NavigationDb?.GetRunway("KOAK", "28R");
        List<Sample>? samples = RunLowApproach();
        if ((samples is null) || (runway is null))
        {
            return;
        }

        double floor = runway.ElevationFt + PistonLevelOffAgl;
        foreach (Sample s in samples.Where(s => (s.Phase is FinalApproachPhase or LowApproachPhase) && !s.ClimbingOut))
        {
            Assert.True(
                s.Altitude >= floor - ToleranceFt,
                $"t+{s.Tick}s in {s.Phase?.Name}: altitude {s.Altitude:F0} ft is below the level-off floor {floor:F0} ft"
            );
        }

        int start = FirstLowApproachIndex(samples);
        Assert.True(start >= 0, "the low approach never became the current phase");
        Sample first = samples[start];
        Assert.True(
            Math.Abs(first.Altitude - floor) <= ToleranceFt,
            $"crossing the threshold at {first.Altitude:F0} ft, not level at the floor {floor:F0} ft"
        );
    }

    [Fact]
    public void La_OnShortFinal28R_HoldsLevelTenSecondsThenClimbs()
    {
        RunwayInfo? runway = TestVnasData.NavigationDb?.GetRunway("KOAK", "28R");
        List<Sample>? samples = RunLowApproach();
        if ((samples is null) || (runway is null))
        {
            return;
        }

        double floor = runway.ElevationFt + PistonLevelOffAgl;
        Assert.DoesNotContain(samples, s => s.OnGround);

        int start = FirstLowApproachIndex(samples);
        Assert.True(start >= 0, "the low approach never became the current phase");
        Assert.True(samples.Count > start + ClimbedByTick, $"the run ended {samples.Count - start} ticks into the low approach");

        for (int k = 0; k < LevelTicks; k++)
        {
            Sample s = samples[start + k];
            Assert.True(
                Math.Abs(s.Altitude - floor) <= ToleranceFt,
                $"low approach tick {k}: altitude {s.Altitude:F0} ft is not level at the floor {floor:F0} ft"
            );
        }

        double climbed = samples[start + ClimbedByTick].Altitude - samples[start].Altitude;
        Assert.True(climbed >= MinimumClimbFt, $"climbed only {climbed:F0} ft by low approach tick {ClimbedByTick}");
    }

    [Fact]
    public void La_ThenCland28R_LiftsTheFloorAndResumesTheGlidepath()
    {
        RunwayInfo? runway = TestVnasData.NavigationDb?.GetRunway("KOAK", "28R");
        RecordingArchive? archive = RecordingLoader.OpenArchive(RecordingPath);
        if ((archive is null) || (runway is null))
        {
            return;
        }

        using (archive)
        {
            SimulationEngine? engine = BuildEngine();
            if (engine is null)
            {
                return;
            }

            AircraftState? ac = RestoreAt(engine, archive, RestoreTime);
            if (ac is null)
            {
                return;
            }

            double floor = runway.ElevationFt + PistonLevelOffAgl;
            CommandResult la = engine.SendCommand(Callsign, "LA");
            Assert.True(la.Success, $"LA was refused: {la.Message}");

            // Wait until the aircraft is held level at the floor while still on final.
            const int LevelledTicks = 3;
            int levelRun = 0;
            double previousAltitude = ac.Altitude;
            AircraftState? live = null;
            for (int t = 1; (t <= MaxTicks) && (levelRun < LevelledTicks); t++)
            {
                engine.TickOneSecond();
                live = engine.FindAircraft(Callsign);
                Assert.NotNull(live);
                bool levelOnFinal =
                    (live.Phases?.CurrentPhase is FinalApproachPhase)
                    && (Math.Abs(live.Altitude - floor) <= ToleranceFt)
                    && (Math.Abs(live.Altitude - previousAltitude) <= LevelChangeFt);
                previousAltitude = live.Altitude;
                levelRun = levelOnFinal ? levelRun + 1 : 0;
                output.WriteLine($"t+{t}s {live.Phases?.CurrentPhase?.Name} alt={live.Altitude:F0}");
            }

            Assert.True(levelRun >= LevelledTicks, $"the aircraft never held level at the floor {floor:F0} ft on final");
            Assert.NotNull(live);

            CommandResult cland = engine.SendCommand(Callsign, "CLAND 28R");
            Assert.True(cland.Success, $"CLAND 28R was refused: {cland.Message}");

            engine.TickOneSecond();
            live = engine.FindAircraft(Callsign);
            Assert.NotNull(live);
            PhaseList? phases = live.Phases;
            Assert.NotNull(phases);
            Assert.IsNotType<LowApproachPhase>(phases.Phases.ElementAtOrDefault(phases.CurrentIndex + 1));
            Assert.True(
                (live.Targets.TargetAltitude is null) || (live.Targets.TargetAltitude < floor - ToleranceFt),
                $"target altitude {live.Targets.TargetAltitude} still holds the level-off floor"
            );

            // The clamp is lifted: the aircraft resumes its descent below the floor.
            bool descended = false;
            for (int t = 1; (t <= 10) && !descended; t++)
            {
                engine.TickOneSecond();
                live = engine.FindAircraft(Callsign);
                Assert.NotNull(live);
                Assert.True(
                    live.Phases?.CurrentPhase is FinalApproachPhase or LandingPhase,
                    $"after CLAND t+{t}s the aircraft is in {live.Phases?.CurrentPhase?.Name}, not on final or landing"
                );
                output.WriteLine($"after CLAND t+{t}s {live.Phases?.CurrentPhase?.Name} alt={live.Altitude:F0}");
                descended = live.Altitude < floor - ToleranceFt;
            }

            Assert.True(descended, $"the aircraft did not descend below {floor - ToleranceFt:F0} ft within 10 s of CLAND");
        }
    }

    [Fact]
    public void La_LevelAtTheFloorOverTheThreshold_ClandIsRefusedAndTheLowApproachContinues()
    {
        if (RestoreAndFlyToTheThreshold() is not { } run)
        {
            return;
        }

        (SimulationEngine engine, AircraftState ac) = run;
        CommandResult cland = engine.SendCommand(Callsign, "CLAND 28R");
        Assert.False(cland.Success, $"CLAND 28R over the threshold was accepted: {cland.Message}");
        Assert.StartsWith("Unable, too high to land", cland.Message);
        Assert.IsType<LowApproachPhase>(ac.Phases?.CurrentPhase);

        AssertClimbsOutWithinFifteenTicks(engine);
    }

    [Fact]
    public void La_LevelAtTheFloorOverTheThreshold_TouchAndGoIsRefused()
    {
        if (RestoreAndFlyToTheThreshold() is not { } run)
        {
            return;
        }

        (SimulationEngine engine, AircraftState ac) = run;
        CommandResult tg = engine.SendCommand(Callsign, "TG");
        Assert.False(tg.Success, $"TG over the threshold was accepted: {tg.Message}");
        Assert.StartsWith("Unable, too high to land", tg.Message);
        Assert.IsType<LowApproachPhase>(ac.Phases?.CurrentPhase);

        AssertClimbsOutWithinFifteenTicks(engine);
    }

    /// <summary>
    /// A final approach course about 3° off the runway heading (KSFO 10L, copied from
    /// <c>FinalApproachFacRampTests.FacOffsetByMagVar_AlignsToRunwayBeforeFlare</c>) with a low approach next:
    /// the course ramp still completes, so the aircraft crosses the threshold aligned with the runway, held
    /// to the tolerances that test applies to a landing.
    /// </summary>
    [Fact]
    public void La_OffsetFinalCourse_CrossesTheThresholdOnTheCenterline()
    {
        const double thresholdLat = 37.628738722;
        const double thresholdLon = -122.39339186;
        const double thresholdElev = 13.1;
        const double runwayHeadingDeg = 117.91;
        const double facDeg = 120.86;

        var rwy = new RunwayInfo
        {
            AirportId = "KSFO",
            Id = new RunwayIdentifier("10L"),
            Designator = "10L",
            Lat1 = thresholdLat,
            Lon1 = thresholdLon,
            TrueHeading1 = new TrueHeading(runwayHeadingDeg),
            Elevation1Ft = thresholdElev,
            Lat2 = 37.61353611,
            Lon2 = -122.357141,
            TrueHeading2 = new TrueHeading((runwayHeadingDeg + 180) % 360),
            Elevation2Ft = thresholdElev,
            WidthFt = 200,
        };

        var fac = new TrueHeading(facDeg);
        (double startLat, double startLon) = GeoMath.ProjectPoint(rwy.ThresholdLatitude, rwy.ThresholdLongitude, fac.ToReciprocal(), 3.0);

        var ac = new AircraftState
        {
            Callsign = "EVA18",
            AircraftType = "B77W",
            Position = new LatLon(startLat, startLon),
            TrueHeading = fac,
            Altitude = 1013,
            IndicatedAirspeed = 149,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan { Destination = "KSFO" },
        };

        var clearance = new ApproachClearance
        {
            TransitionName = null,
            ApproachId = "R10L",
            AirportCode = "SFO",
            RunwayId = "10L",
            FinalApproachCourse = fac,
            FinalApproachAnchorLat = null,
            FinalApproachAnchorLon = null,
        };

        ac.Phases = new PhaseList
        {
            AssignedRunway = rwy,
            ActiveApproach = clearance,
            LandingClearance = ClearanceType.ClearedLowApproach,
        };
        ac.Phases.Add(new FinalApproachPhase { SkipInterceptCheck = true });
        ac.Phases.Add(new LowApproachPhase());
        ac.Targets.TargetSpeed = 149;

        var world = new SimulationWorld(PushPlanStore.CreateShipped());
        world.AddAircraft(ac);

        static void PreTick(AircraftState aircraft, double dt)
        {
            if ((aircraft.Phases is null) || aircraft.Phases.IsComplete)
            {
                return;
            }

            var ctx = new PhaseContext
            {
                ActiveRunways = ActiveRunways.Empty,
                Aircraft = aircraft,
                Targets = aircraft.Targets,
                Category = AircraftCategorization.Categorize(aircraft.AircraftType),
                DeltaSeconds = dt,
                Runway = aircraft.Phases.AssignedRunway,
                FieldElevation = aircraft.Phases.AssignedRunway?.ElevationFt ?? 0,
                Logger = NullLogger.Instance,
                AutoClearedToLand = true,
            };
            PhaseRunner.Tick(aircraft, ctx);
        }

        const double dt = 0.25;
        const int maxTicks = 4 * 240;
        double hdgDiffAtHandoff = double.NaN;
        double xteFtAtHandoff = double.NaN;
        for (int tick = 0; tick < maxTicks; tick++)
        {
            world.Tick(dt, tick * dt, PreTick);
            if (ac.Phases?.CurrentPhase is LowApproachPhase)
            {
                hdgDiffAtHandoff = Math.Abs(ac.TrueHeading.SignedAngleTo(rwy.TrueHeading));
                double xteNm = GeoMath.SignedCrossTrackDistanceNm(ac.Position, new LatLon(thresholdLat, thresholdLon), rwy.TrueHeading);
                xteFtAtHandoff = xteNm * GeoMath.FeetPerNm;
                output.WriteLine($"handoff at tick {tick}: alt={ac.Altitude:F0} hdgDiff={hdgDiffAtHandoff:F2} xte={xteFtAtHandoff:F0} ft");
                break;
            }
        }

        Assert.False(double.IsNaN(hdgDiffAtHandoff), "the low approach never became the current phase");
        Assert.True(hdgDiffAtHandoff < 0.5, $"not aligned with the runway at the threshold (diff={hdgDiffAtHandoff:F2}°)");
        Assert.True(Math.Abs(xteFtAtHandoff) < 30.0, $"off the centerline at the threshold (xte={xteFtAtHandoff:F0} ft)");
    }

    [Theory]
    [InlineData(3.9, true, false)]
    [InlineData(4.1, true, true)]
    [InlineData(8.0, false, false)]
    public void TooHighToLandFromLowApproach_JudgesThePathToTheAimPoint(double pathDeg, bool onLowApproach, bool expectedTooHigh)
    {
        TestVnasData.EnsureInitialized();
        RunwayInfo? runway = TestVnasData.NavigationDb?.GetRunway("KOAK", "28R");
        if (runway is null)
        {
            return;
        }

        const double HeightFt = 100.0;
        double alongNm = HeightFt / Math.Tan(pathDeg * Math.PI / 180.0) / GeoMath.FeetPerNm;
        AircraftState ac = MakeAircraftNearAimPoint(runway, alongNm, runway.ElevationFt + HeightFt, onLowApproach);

        Assert.Equal(expectedTooHigh, PatternCommandHandler.IsTooHighToLandFromLowApproach(ac, runway, null));
    }

    /// <summary>
    /// An aircraft on final above the low-approach floor flies the same glidepath a landing would, so even at a
    /// 6° path to the aim point it is not judged too high: only an aircraft the floor holds level is.
    /// </summary>
    [Fact]
    public void TooHighToLandFromLowApproach_OnBaseAboveTheFloor_IsNotTooHigh()
    {
        TestVnasData.EnsureInitialized();
        RunwayInfo? runway = TestVnasData.NavigationDb?.GetRunway("KOAK", "28R");
        if (runway is null)
        {
            return;
        }

        const double HeightFt = 300.0;
        const double PathDeg = 6.0;
        double alongNm = HeightFt / Math.Tan(PathDeg * Math.PI / 180.0) / GeoMath.FeetPerNm;
        AircraftState ac = MakeAircraftNearAimPoint(runway, alongNm, runway.ElevationFt + HeightFt, onLowApproach: true);

        Assert.False(PatternCommandHandler.IsTooHighToLandFromLowApproach(ac, runway, null));
    }

    /// <summary>
    /// <c>LA</c> issued on the 28R downwind (real KOAK navdata), then <c>CLAND</c>: the aircraft is nowhere near
    /// the level-off floor, so the landing clearance is accepted and the low approach becomes a landing.
    /// </summary>
    [Fact]
    public void La_OnDownwind_ThenClandIsAccepted()
    {
        TestVnasData.EnsureInitialized();
        RunwayInfo? runway = TestVnasData.NavigationDb?.GetRunway("KOAK", "28R");
        if (runway is null)
        {
            return;
        }

        var ac = new AircraftState
        {
            Callsign = "N775JW",
            AircraftType = "C182",
            Position = new LatLon(37.63, -122.21),
            Altitude = 2500,
            TrueHeading = new TrueHeading(0),
            IndicatedAirspeed = 120,
            IsOnGround = false,
            Phases = new PhaseList { AssignedRunway = runway },
        };

        CommandResult entry = PatternCommandHandler.TryEnterPattern(
            ac,
            PatternDirection.Right,
            PatternEntryLeg.Downwind,
            runwayId: "28R",
            finalDistanceNm: null,
            activeRunways: ActiveRunways.Empty,
            warnRunwayCrossing: true,
            groundLayout: null
        );
        Assert.True(entry.Success, entry.Message);

        CommandResult la = PatternCommandHandler.TrySetupLowApproach(ac, OptionPatternModifier.None, TestDispatch.Context(Random.Shared));
        Assert.True(la.Success, la.Message);

        CommandResult cland = PatternCommandHandler.TryClearedToLand(new ClearedToLandCommand(), ac, TestDispatch.Context(Random.Shared));
        Assert.True(cland.Success, $"CLAND on the downwind after LA was refused: {cland.Message}");

        PhaseList phases = ac.Phases!;
        Assert.Contains(phases.Phases, p => (p is LandingPhase) && (p.Status == PhaseStatus.Pending));
        Assert.DoesNotContain(phases.Phases, p => (p is LowApproachPhase) && (p.Status == PhaseStatus.Pending));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-0.1)]
    public void TooHighToLandFromLowApproach_AtOrPastTheAimPoint_IsTooHigh(double alongNm)
    {
        TestVnasData.EnsureInitialized();
        RunwayInfo? runway = TestVnasData.NavigationDb?.GetRunway("KOAK", "28R");
        if (runway is null)
        {
            return;
        }

        AircraftState ac = MakeAircraftNearAimPoint(runway, alongNm, runway.ElevationFt + 50.0, onLowApproach: true);

        Assert.True(PatternCommandHandler.IsTooHighToLandFromLowApproach(ac, runway, null));
    }

    /// <summary>
    /// An aircraft <paramref name="alongNm"/> short of the runway aim point (the threshold plus 1000 ft), on the
    /// extended centerline, with a low approach or a plain landing as its pending terminal.
    /// </summary>
    private static AircraftState MakeAircraftNearAimPoint(RunwayInfo runway, double alongNm, double altitude, bool onLowApproach)
    {
        LatLon aimPoint = GeoMath.ProjectPoint(
            LandingThreshold.Resolve(runway, (AirportGroundLayout?)null),
            runway.TrueHeading,
            1000.0 / GeoMath.FeetPerNm
        );
        LatLon position = GeoMath.ProjectPoint(aimPoint, runway.TrueHeading.ToReciprocal(), alongNm);
        var ac = new AircraftState
        {
            Callsign = "N172SP",
            AircraftType = "C172",
            Position = position,
            TrueHeading = runway.TrueHeading,
            Altitude = altitude,
            IndicatedAirspeed = 65,
            IsOnGround = false,
            Phases = new PhaseList { AssignedRunway = runway },
        };
        ac.Phases.Add(new FinalApproachPhase());
        ac.Phases.Add(onLowApproach ? new LowApproachPhase() : new LandingPhase());
        return ac;
    }

    /// <summary>
    /// Snapshots written before the low approach dropped its go-around height and threshold position still
    /// carry <c>GoAroundAgl</c>, <c>ThresholdLat</c> and <c>ThresholdLon</c>; they deserialize and restore.
    /// </summary>
    [Fact]
    public void Restore_LowApproachPhaseDtoWithRetiredFields_StillDeserializes()
    {
        const string Json =
            """{"Status":1,"ElapsedSeconds":7,"FieldElevation":6,"RunwayHeadingDeg":295.6,"GoAroundAgl":100,"ThresholdLat":37.7,"ThresholdLon":-122.2,"ClimbingOut":true}""";

        LowApproachPhaseDto? dto = JsonSerializer.Deserialize<LowApproachPhaseDto>(Json, RecordingJsonOptions.Default);
        Assert.NotNull(dto);

        var phase = LowApproachPhase.FromSnapshot(dto);
        var restored = (LowApproachPhaseDto)phase.ToSnapshot();
        Assert.Equal(7, phase.ElapsedSeconds);
        Assert.True(restored.ClimbingOut);
    }

    /// <summary>Restores t=1705, issues <c>LA</c> and ticks until the low approach is the current phase.</summary>
    private (SimulationEngine Engine, AircraftState Aircraft)? RestoreAndFlyToTheThreshold()
    {
        RecordingArchive? archive = RecordingLoader.OpenArchive(RecordingPath);
        if (archive is null)
        {
            return null;
        }

        using (archive)
        {
            SimulationEngine? engine = BuildEngine();
            if ((engine is null) || (RestoreAt(engine, archive, RestoreTime) is null))
            {
                return null;
            }

            CommandResult la = engine.SendCommand(Callsign, "LA");
            Assert.True(la.Success, $"LA was refused: {la.Message}");

            for (int t = 1; t <= MaxTicks; t++)
            {
                engine.TickOneSecond();
                AircraftState? live = engine.FindAircraft(Callsign);
                Assert.NotNull(live);
                output.WriteLine($"t+{t}s {live.Phases?.CurrentPhase?.Name} alt={live.Altitude:F0}");
                if (live.Phases?.CurrentPhase is LowApproachPhase)
                {
                    return (engine, live);
                }
            }

            Assert.Fail("the low approach never became the current phase");
            return null;
        }
    }

    /// <summary>Ticks 15 s: the aircraft is never on the ground and is climbing at the end.</summary>
    private void AssertClimbsOutWithinFifteenTicks(SimulationEngine engine)
    {
        double previous = double.NaN;
        double last = double.NaN;
        for (int t = 1; t <= ClimbedByTick; t++)
        {
            engine.TickOneSecond();
            AircraftState? live = engine.FindAircraft(Callsign);
            Assert.NotNull(live);
            Assert.False(live.IsOnGround, $"t+{t}s after the refusal the aircraft is on the ground");
            output.WriteLine($"after refusal t+{t}s {live.Phases?.CurrentPhase?.Name} alt={live.Altitude:F0}");
            previous = last;
            last = live.Altitude;
        }

        Assert.True(last > previous, $"not climbing {ClimbedByTick} s after the refusal ({previous:F0} → {last:F0} ft)");
    }

    private static int FirstLowApproachIndex(List<Sample> samples) => samples.FindIndex(s => s.Phase is LowApproachPhase);

    /// <summary>
    /// Restores t=1705, issues <c>LA</c> and ticks until <see cref="ClimbedByTick"/> ticks after the low approach
    /// became current, or <see cref="MaxTicks"/>. Returns null when the fixture is unavailable.
    /// </summary>
    private List<Sample>? RunLowApproach()
    {
        RecordingArchive? archive = RecordingLoader.OpenArchive(RecordingPath);
        if (archive is null)
        {
            return null;
        }

        using (archive)
        {
            SimulationEngine? engine = BuildEngine();
            if (engine is null)
            {
                return null;
            }

            AircraftState? ac = RestoreAt(engine, archive, RestoreTime);
            if (ac is null)
            {
                return null;
            }

            CommandResult result = engine.SendCommand(Callsign, "LA");
            Assert.True(result.Success, $"LA was refused: {result.Message}");

            var samples = new List<Sample>();
            int lowApproachStart = -1;
            for (int t = 1; t <= MaxTicks; t++)
            {
                engine.TickOneSecond();
                AircraftState? live = engine.FindAircraft(Callsign);
                Assert.NotNull(live);

                Phase? phase = live.Phases?.CurrentPhase;
                bool climbingOut = phase is LowApproachPhase lowApproach && ((LowApproachPhaseDto)lowApproach.ToSnapshot()).ClimbingOut;
                samples.Add(new Sample(t, phase, live.Altitude, climbingOut, live.IsOnGround));
                output.WriteLine($"t+{t}s {phase?.Name} alt={live.Altitude:F0} climbing={climbingOut} onGround={live.IsOnGround}");

                if ((lowApproachStart < 0) && (phase is LowApproachPhase))
                {
                    lowApproachStart = samples.Count - 1;
                }

                if ((lowApproachStart >= 0) && (samples.Count > lowApproachStart + ClimbedByTick))
                {
                    break;
                }
            }

            return samples;
        }
    }

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        SimLogBuilder
            .CreateForTest(output)
            .EnableCategory("LowApproachPhase", LogLevel.Debug)
            .EnableCategory("FinalApproachPhase", LogLevel.Debug)
            .InitializeSimLog();

        return new SimulationEngine(RecordingGroundData.Evergreen("low approach level-off on OAK 28R"), PushPlanStore.CreateShipped());
    }

    /// <summary>
    /// Hybrid restore: load the scenario, then drop straight into the snapshot at
    /// <paramref name="elapsedSeconds"/>. Returns null when the fixture is unavailable.
    /// </summary>
    private AircraftState? RestoreAt(SimulationEngine engine, RecordingArchive archive, int elapsedSeconds)
    {
        engine.Replay(archive.ToBaseSessionRecording(), 0);

        TimedSnapshot? snapshot = archive.ReadSnapshotAt(elapsedSeconds);
        if (snapshot is null)
        {
            output.WriteLine($"No snapshot near t={elapsedSeconds} — skipping");
            return null;
        }

        engine.RestoreFromSnapshot(snapshot.State);

        AircraftState? ac = engine.FindAircraft(Callsign);
        if (ac is null)
        {
            output.WriteLine($"{Callsign} is not in the t={elapsedSeconds} snapshot — skipping");
        }

        return ac;
    }
}
