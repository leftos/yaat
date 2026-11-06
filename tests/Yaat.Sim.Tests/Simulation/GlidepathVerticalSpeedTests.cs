using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// An aircraft on a captured glidepath reports the descent rate it flies as <see cref="AircraftState.VerticalSpeed"/>: the
/// 3° path rate (about 5.31 fpm per knot of groundspeed), not the 0 an altitude snap to a target at the current distance
/// leaves behind. Staged on real KSFO 28R navdata with a <see cref="FinalApproachPhase"/> ticked through
/// <see cref="SimulationWorld.Tick(double, double, Action{AircraftState, double})"/> at the engine's four physics
/// sub-ticks a second (the staging of <c>LowApproachLevelOffTests.La_OffsetFinalCourse_CrossesTheThresholdOnTheCenterline</c>).
/// </summary>
public class GlidepathVerticalSpeedTests(ITestOutputHelper output)
{
    private const double SubTickSeconds = 0.25;
    private const int SubTicksPerSecond = 4;

    /// <summary>The 3° path rate, fpm per knot of groundspeed.</summary>
    private const double PathFpmPerKt = 5.31;

    /// <summary>The band around the path rate a snap-free descent stays in.</summary>
    private const double RateTolerance = 0.15;

    /// <summary>Sub-ticks skipped at the start while the phase starts and the wind is first applied.</summary>
    private const int SettleSubTicks = SubTicksPerSecond;

    private sealed record Sample(int SubTick, Phase? Phase, double Altitude, double PathFt, double VerticalSpeed, double GroundSpeed);

    [Fact]
    public void OnPath_Jet_ReportsThePathRate()
    {
        if (Runway28R() is not { } runway)
        {
            return;
        }

        AircraftState ac = Stage(runway, "B738", 140, distNm: 6.0, aboveFt: 0);
        List<Sample> samples = Fly(ac, runway, seconds: 60, weather: null, beforeSubTick: null);

        AssertPathRate([.. samples.Skip(SettleSubTicks)]);
    }

    [Fact]
    public void SlowFinal_TracksThePathBelow200Fpm()
    {
        if (Runway28R() is not { } runway)
        {
            return;
        }

        var weather = new WeatherProfile
        {
            WindLayers =
            [
                new WindLayer
                {
                    Altitude = 0,
                    Direction = runway.TrueHeading.Degrees,
                    Speed = 30,
                },
            ],
        };
        AircraftState ac = Stage(runway, "C172", 65, distNm: 3.0, aboveFt: 0);
        List<Sample> samples = Fly(ac, runway, seconds: 60, weather, beforeSubTick: null);

        List<Sample> settled = [.. samples.Skip(SettleSubTicks)];
        double meanGs = settled.Average(s => s.GroundSpeed);
        Assert.True(meanGs is > 25 and < 45, $"the staging is not a slow final: mean groundspeed {meanGs:F0} kt");
        Assert.True(settled.Count >= 60 * SubTicksPerSecond - SettleSubTicks, $"the final ended after {settled.Count} sub-ticks");
        AssertPathRate(settled);
        foreach (Sample s in settled)
        {
            Assert.True(
                Math.Abs(s.Altitude - s.PathFt) <= 50,
                $"t={s.SubTick * SubTickSeconds:F2}s: altitude {s.Altitude:F0} ft is {s.Altitude - s.PathFt:F0} ft off the path {s.PathFt:F0} ft"
            );
        }
    }

    [Fact]
    public void BelowThePath_NeverClimbs()
    {
        if (Runway28R() is not { } runway)
        {
            return;
        }

        // Captured on the path first, then put 100 ft below it: a captured aircraft below the path never climbs back.
        const int DropAtSubTick = 2 * SubTicksPerSecond;
        AircraftState ac = Stage(runway, "B738", 140, distNm: 6.0, aboveFt: 0);
        List<Sample> samples = Fly(
            ac,
            runway,
            seconds: 40,
            weather: null,
            beforeSubTick: (subTick, aircraft) =>
            {
                if (subTick == DropAtSubTick)
                {
                    aircraft.Altitude -= 100;
                }
            }
        );

        List<Sample> below = [.. samples.Where(s => s.SubTick >= DropAtSubTick)];
        Assert.NotEmpty(below);
        Assert.True(
            below[0].PathFt - below[0].Altitude > 90,
            $"the drop left the aircraft {below[0].PathFt - below[0].Altitude:F0} ft below the path"
        );
        foreach (Sample s in below)
        {
            Assert.True(
                s.VerticalSpeed < 0,
                $"t={s.SubTick * SubTickSeconds:F2}s: {s.PathFt - s.Altitude:F0} ft below the path, vertical speed {s.VerticalSpeed:F0} fpm"
            );
        }
    }

    [Fact]
    public void AboveThePath_ConvergesFasterThanThePathRate()
    {
        if (Runway28R() is not { } runway)
        {
            return;
        }

        AircraftState ac = Stage(runway, "B738", 140, distNm: 6.0, aboveFt: 300);
        List<Sample> samples = Fly(ac, runway, seconds: 120, weather: null, beforeSubTick: null);

        int converged = samples.FindIndex(s => s.Altitude - s.PathFt <= 50);
        Assert.True(converged >= 0, "the aircraft never came within 50 ft of the path");
        foreach (Sample s in samples.Take(converged).Skip(SettleSubTicks))
        {
            double pathRate = -PathFpmPerKt * s.GroundSpeed;
            Assert.True(
                s.VerticalSpeed < pathRate,
                $"t={s.SubTick * SubTickSeconds:F2}s: {s.Altitude - s.PathFt:F0} ft above the path, vertical speed {s.VerticalSpeed:F0} fpm "
                    + $"is not steeper than the path rate {pathRate:F0} fpm"
            );
        }
    }

    [Fact]
    public void InsideHalfMile_OnPath_KeepsThePathRate()
    {
        if (Runway28R() is not { } runway)
        {
            return;
        }

        AircraftState ac = Stage(runway, "B738", 140, distNm: 0.45, aboveFt: 0);
        List<Sample> samples = Fly(ac, runway, seconds: 30, weather: null, beforeSubTick: null);

        Assert.IsType<LandingPhase>(ac.Phases?.CurrentPhase);
        AssertPathRate([.. samples.Skip(SettleSubTicks)]);
    }

    [Fact]
    public void LowApproach_LevelsAtTheFloor()
    {
        if (Runway28R() is not { } runway)
        {
            return;
        }

        AircraftState ac = Stage(runway, "C172", 70, distNm: 1.5, aboveFt: 0);
        ClearForLowApproach(ac);
        AircraftCategory category = AircraftCategorization.Categorize(ac.AircraftType);
        double floor = new LowApproachPhase().FloorMsl(runway.ElevationFt, category);
        List<Sample> samples = Fly(ac, runway, seconds: 120, weather: null, beforeSubTick: null);

        Assert.IsType<LowApproachPhase>(ac.Phases?.CurrentPhase);
        int level = 0;
        foreach (Sample s in samples.Skip(SettleSubTicks))
        {
            string at = $"t={s.SubTick * SubTickSeconds:F2}s alt={s.Altitude:F1} floor={floor:F0}";
            Assert.True(s.Altitude >= floor - 0.5, $"{at}: {floor - s.Altitude:F1} ft below the floor");
            if (s.Altitude - floor > FlightPhysics.AltitudeSnapFt)
            {
                Assert.True(s.VerticalSpeed < 0, $"{at}: descending toward the floor, vertical speed {s.VerticalSpeed:F0} fpm");
            }
            else if (Math.Abs(s.Altitude - floor) <= 0.5)
            {
                level++;
                Assert.True(s.VerticalSpeed == 0, $"{at}: level at the floor, vertical speed {s.VerticalSpeed:F0} fpm");
            }
        }

        Assert.True(level > 0, "the aircraft never levelled at the floor on final");
    }

    /// <summary>
    /// A pattern final joined from the base turn closes on the threshold slowly, so the path falls at the closure rate,
    /// not at the groundspeed rate. The aircraft must keep the path and land on the clearance it holds.
    /// </summary>
    [Fact]
    public void OakC210_JoiningFinalFromTheTurn_KeepsThePathAndLandsWithItsClearance()
    {
        SessionRecording? recording = RecordingLoader.Load("TestData/19801dcd5dc2.zip");
        TestVnasData.EnsureInitialized();
        if ((recording is null) || (TestVnasData.NavigationDb is null))
        {
            return;
        }

        var engine = new SimulationEngine(
            RecordingGroundData.Evergreen("a pattern final joined from the base turn on today's OAK"),
            PushPlanStore.CreateShipped()
        );
        engine.Replay(recording, 2140);
        RunwayInfo rwy = NavigationDatabase.Instance.GetRunway("OAK", "28R")!;
        AircraftState ac = Assert.IsType<AircraftState>(engine.FindAircraft("N655EX"));
        LatLon threshold = LandingThreshold.Resolve(rwy, engine.ResolveGroundLayout(ac));
        for (int t = 2141; t <= 2200; t++)
        {
            engine.ReplayOneSecond();
            ac = Assert.IsType<AircraftState>(engine.FindAircraft("N655EX"));
            Assert.IsNotType<GoAroundPhase>(ac.Phases?.CurrentPhase);
            if (ac.Phases?.CurrentPhase is FinalApproachPhase)
            {
                double pathFt = GlideSlopeGeometry.AltitudeAtDistance(
                    GeoMath.DistanceNm(ac.Position, threshold),
                    rwy.ElevationFt,
                    AircraftCategorization.Categorize(ac.AircraftType)
                );
                Assert.True(ac.Altitude - pathFt >= -15.0, $"t={t}: {pathFt - ac.Altitude:F1} ft below the glidepath");
            }
        }

        Assert.True(ac.IsOnGround, "N655EX should have landed with the clearance issued at t=2160");
    }

    /// <summary>
    /// The path falls at the rate the straight-line distance to the threshold shrinks. A final tracking parallel to the
    /// course but offset laterally closes slower than its groundspeed, so its path rate is the groundspeed's component
    /// toward the threshold.
    /// </summary>
    [Fact]
    public void OffsetFinal_TrackParallelToCourse_DescendsAtThePathsOwnRate()
    {
        if (Runway28R() is not { } runway)
        {
            return;
        }

        AircraftState ac = Stage(runway, "B738", 140, distNm: 1.5, aboveFt: 0);
        LatLon threshold = LandingThreshold.Resolve(runway, (AirportGroundLayout?)null);

        // 1.5 nm from the threshold and 0.5 nm off the extended centerline, on the sim's straight-line path.
        double alongNm = Math.Sqrt((1.5 * 1.5) - (0.5 * 0.5));
        LatLon onCenterline = GeoMath.ProjectPoint(threshold, runway.TrueHeading.ToReciprocal(), alongNm);
        ac.Position = GeoMath.ProjectPoint(onCenterline, new TrueHeading(runway.TrueHeading.Degrees + 90.0), 0.5);
        ac.TrueTrack = runway.TrueHeading;
        ac.Altitude = GlideSlopeGeometry.AltitudeAtDistance(
            GeoMath.DistanceNm(ac.Position, threshold),
            runway.ElevationFt,
            AircraftCategorization.Categorize(ac.AircraftType)
        );

        // The phase reads the state before physics, so the expected rate comes from the staged track, position and speed.
        double bearingToThreshold = GeoMath.BearingTo(ac.Position, threshold);
        double closureKts = ac.GroundSpeed * Math.Cos(ac.TrueTrack.AbsAngleTo(new TrueHeading(bearingToThreshold)) * Math.PI / 180.0);
        double expectedFpm = GlideSlopeGeometry.RequiredDescentRate(
            closureKts,
            GlideSlopeGeometry.AngleForCategory(AircraftCategorization.Categorize(ac.AircraftType))
        );

        Fly(ac, runway, seconds: SubTickSeconds, weather: null, beforeSubTick: null);

        double actualFpm = Math.Abs(ac.Targets.DesiredVerticalRate ?? 0);
        Assert.InRange(actualFpm, expectedFpm - 5.0, expectedFpm + 5.0);
    }

    /// <summary>
    /// A high aircraft turning onto final has only a small component toward the threshold, but the descent ceiling must
    /// not shrink to it: the ceiling is the groundspeed rate, so the convergence descent stays steep.
    /// </summary>
    [Fact]
    public void HighInTheTurnOntoFinal_DescendsFasterThanTheFloor()
    {
        if (Runway28R() is not { } runway)
        {
            return;
        }

        AircraftState ac = Stage(runway, "B738", 140, distNm: 3.0, aboveFt: 300);
        LatLon threshold = LandingThreshold.Resolve(runway, (AirportGroundLayout?)null);
        ac.TrueTrack = new TrueHeading(GeoMath.BearingTo(ac.Position, threshold) + 80.0);

        Fly(ac, runway, seconds: SubTickSeconds, weather: null, beforeSubTick: null);

        double commandedFpm = -(ac.Targets.DesiredVerticalRate ?? 0);
        Assert.True(commandedFpm > 400.0, $"the commanded descent is {commandedFpm:F0} fpm, not steeper than 400 fpm");
    }

    private static void AssertPathRate(List<Sample> samples)
    {
        Assert.NotEmpty(samples);
        foreach (Sample s in samples)
        {
            double pathRate = -PathFpmPerKt * s.GroundSpeed;
            Assert.True(
                (s.VerticalSpeed < 0) && (Math.Abs(s.VerticalSpeed - pathRate) <= RateTolerance * Math.Abs(pathRate)),
                $"t={s.SubTick * SubTickSeconds:F2}s: vertical speed {s.VerticalSpeed:F0} fpm, path rate {pathRate:F0} fpm at {s.GroundSpeed:F0} kt"
            );
        }
    }

    private static RunwayInfo? Runway28R()
    {
        TestVnasData.EnsureInitialized();
        return TestVnasData.NavigationDb?.GetRunway("KSFO", "28R");
    }

    /// <summary>
    /// An aircraft <paramref name="distNm"/> from the 28R landing threshold on the extended centerline,
    /// <paramref name="aboveFt"/> above its category's glidepath, cleared to land.
    /// </summary>
    private static AircraftState Stage(RunwayInfo runway, string type, double ias, double distNm, double aboveFt)
    {
        LatLon threshold = LandingThreshold.Resolve(runway, (AirportGroundLayout?)null);
        AircraftCategory category = AircraftCategorization.Categorize(type);
        double pathFt = GlideSlopeGeometry.AltitudeAtDistance(distNm, runway.ElevationFt, category);
        var ac = new AircraftState
        {
            Callsign = "GPVS1",
            AircraftType = type,
            Position = GeoMath.ProjectPoint(threshold, runway.TrueHeading.ToReciprocal(), distNm),
            TrueHeading = runway.TrueHeading,
            TrueTrack = runway.TrueHeading,
            Altitude = pathFt + aboveFt,
            IndicatedAirspeed = ias,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan { Destination = "KSFO" },
            Phases = new PhaseList { AssignedRunway = runway, LandingClearance = ClearanceType.ClearedToLand },
        };
        ac.Phases.Add(new FinalApproachPhase { SkipInterceptCheck = true });
        ac.Phases.Add(new LandingPhase());
        ac.Targets.TargetSpeed = ias;
        return ac;
    }

    /// <summary>Turns a <see cref="Stage"/>d aircraft's landing clearance into a low approach, before it first ticks.</summary>
    private static void ClearForLowApproach(AircraftState ac)
    {
        PhaseList phases = Assert.IsType<PhaseList>(ac.Phases);
        phases.LandingClearance = ClearanceType.ClearedLowApproach;
        phases.ReplaceUpcoming([new LowApproachPhase()]);
    }

    /// <summary>
    /// Ticks <paramref name="ac"/> at four sub-ticks a second for up to <paramref name="seconds"/>, sampling every sub-tick
    /// while <see cref="FinalApproachPhase"/> is current; stops when it no longer is.
    /// </summary>
    private List<Sample> Fly(AircraftState ac, RunwayInfo runway, double seconds, WeatherProfile? weather, Action<int, AircraftState>? beforeSubTick)
    {
        var world = new SimulationWorld(PushPlanStore.CreateShipped()) { Weather = weather };
        world.AddAircraft(ac);
        LatLon threshold = LandingThreshold.Resolve(runway, (AirportGroundLayout?)null);
        AircraftCategory category = AircraftCategorization.Categorize(ac.AircraftType);

        var samples = new List<Sample>();
        int subTicks = (int)(seconds * SubTicksPerSecond);
        for (int subTick = 0; subTick < subTicks; subTick++)
        {
            beforeSubTick?.Invoke(subTick, ac);
            world.Tick(SubTickSeconds, subTick * SubTickSeconds, PreTick);
            Phase? phase = ac.Phases?.CurrentPhase;
            if (phase is not FinalApproachPhase)
            {
                output.WriteLine($"t={subTick * SubTickSeconds:F2}s handed off to {phase?.Name} at alt={ac.Altitude:F0}");
                break;
            }

            double distNm = GeoMath.DistanceNm(ac.Position, threshold);
            double pathFt = GlideSlopeGeometry.AltitudeAtDistance(distNm, runway.ElevationFt, category);
            samples.Add(new Sample(subTick, phase, ac.Altitude, pathFt, ac.VerticalSpeed, ac.GroundSpeed));
            if (subTick % SubTicksPerSecond == 0)
            {
                output.WriteLine(
                    $"t={subTick * SubTickSeconds:F2}s dist={distNm:F2}nm alt={ac.Altitude:F0} path={pathFt:F0} "
                        + $"dev={ac.Altitude - pathFt:F0} vs={ac.VerticalSpeed:F0} gs={ac.GroundSpeed:F0} "
                        + $"pathRate={-PathFpmPerKt * ac.GroundSpeed:F0}"
                );
            }
        }

        return samples;
    }

    private static void PreTick(AircraftState aircraft, double dt)
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
}
