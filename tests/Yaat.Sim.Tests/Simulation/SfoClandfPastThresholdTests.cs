using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// CLANDF issued to an aircraft already past the landing threshold (bundle "S1-SFO-2 | Ground Control 28/01",
/// fixture <c>sfo-gc-28-01</c>). SKW5416 (CRJ7) went around off the I28L at t=1130 for SWA2644 crossing 28L; the
/// instructor then forced the landing at t=1160 with the aircraft ~5,200 ft past the 28L threshold at 1,860 ft,
/// still climbing.
///
/// Recorded outcome: the forced guidance aimed at the threshold behind the aircraft and dived at ~6,000 fpm, the
/// speed bled off at the airborne type rate, the aircraft touched down ~10,000 ft down an ~11,360 ft runway at
/// 130 kt, stopped ~2,600 ft past the far end, and sat in <see cref="RunwayExitPhase"/> with no exit for the rest
/// of the session — so TAI562 went around at t=1265 with SKW5416 as the blocking occupant.
///
/// Hybrid replay (docs/e2e-tdd-issue-debugging.md §5b): restore the snapshot just before the CLANDF and step the
/// recording forward, so the recorded CLANDF is what applies. The restore is at t=1150 rather than t=1155 because the
/// t=1155 snapshot restores SKW3396 mid-fillet and the navigator's teleport guard throws on the first tick.
///
/// The descent cap (<see cref="ForcedLandingProfile.DescentCapFpm"/>) gives way when the capped path cannot reach the
/// ground by the aim point — the instructor's forced landing always lands and stops on the runway. SKW5416's geometry
/// is such a case (the aim point is pulled back to leave room to stop), so there the assertion is the touchdown at or
/// before the aim point and a stop short of the runway end; the synthetic arms cover a geometry the cap suffices for.
/// </summary>
public class SfoClandfPastThresholdTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/sfo-gc-28-01-recording.yaat-bug-report-bundle.zip";
    private const string Callsign = "SKW5416";
    private const string Follower = "TAI562";
    private const int RestoreAt = 1150;
    private const int ClandfAt = 1160;
    private const int LeaveRunwayBudgetSeconds = 180;

    /// <summary>A forced landing's run as the tests watched it, measured along the landing runway from its landing threshold.</summary>
    private sealed class LandingTrace(double runwayLengthFt, double runwayHalfWidthFt)
    {
        public double RunwayLengthFt { get; } = runwayLengthFt;
        public double RunwayHalfWidthFt { get; } = runwayHalfWidthFt;
        public double? TouchdownAlongFt { get; set; }
        public double TouchdownCrossTrackFt { get; set; }
        public double MaxAlongOnRunwayFt { get; set; } = double.MinValue;
        public double? FirstStopAlongFt { get; set; }
        public double MaxDescentFpm { get; set; }
        public bool CapGaveWay { get; set; }

        /// <summary>
        /// The pulled-back aim point as it stands at touchdown: the furthest touchdown that still leaves room to stop at
        /// <see cref="ForcedLandingProfile.StopPlanningDecelKtsPerSec"/> from the touchdown ground speed with
        /// <see cref="ForcedLandingProfile.StopPlanningMarginFt"/> to spare. Measured at the touchdown sample rather than
        /// a sample before it, so the check does not depend on the one-second sampling.
        /// </summary>
        public double TouchdownAimLimitFt { get; set; }

        public int? LeftRunwaySecond { get; set; }
        public List<string> FollowerGoAroundWarnings { get; } = [];

        /// <summary>Folds one sample of <paramref name="ac"/> in; <paramref name="forcedWindow"/> is true once the CLANDF applies.</summary>
        public void Sample(AircraftState ac, LatLon threshold, TrueHeading heading, double fieldElevationFt, int second, bool forcedWindow)
        {
            double alongFt = GeoMath.AlongTrackDistanceNm(ac.Position, threshold, heading) * GeoMath.FeetPerNm;
            Phase? phase = ac.Phases?.CurrentPhase;
            if (forcedWindow && !ac.IsOnGround)
            {
                double aglFt = ac.Altitude - fieldElevationFt;
                MaxDescentFpm = Math.Max(MaxDescentFpm, -ac.VerticalSpeed);
                double capFpm =
                    aglFt > ForcedLandingProfile.LowDescentCapAglFt ? ForcedLandingProfile.DescentCapFpm : ForcedLandingProfile.LowDescentCapFpm;
                CapGaveWay |= ForcedLandingProfile.DescentRateFpm(aglFt, alongFt, ac.GroundSpeed, RunwayLengthFt) > capFpm;
            }

            // The landing phase records the exact touchdown point and speed; the one-second sample can be up to a second
            // of rollout later, so it is only the fallback for a touchdown the landing phase did not see.
            if (forcedWindow && ac.IsOnGround && (TouchdownAlongFt is null))
            {
                (LatLon touchdown, double touchdownGs) = phase is LandingPhase { TouchdownPosition: { } p, TouchdownGroundSpeedKts: { } gs }
                    ? (p, gs)
                    : (ac.Position, ac.GroundSpeed);
                TouchdownAlongFt = GeoMath.AlongTrackDistanceNm(touchdown, threshold, heading) * GeoMath.FeetPerNm;
                TouchdownCrossTrackFt = GeoMath.SignedCrossTrackDistanceNm(touchdown, threshold, heading) * GeoMath.FeetPerNm;
                double stopFt = RolloutBraking.BrakingDistanceNm(touchdownGs, 0, ForcedLandingProfile.StopPlanningDecelKtsPerSec) * GeoMath.FeetPerNm;
                TouchdownAimLimitFt = RunwayLengthFt - stopFt - ForcedLandingProfile.StopPlanningMarginFt;
            }

            if (ac.IsOnGround && (phase is LandingPhase or RunwayExitPhase))
            {
                MaxAlongOnRunwayFt = Math.Max(MaxAlongOnRunwayFt, alongFt);
                if ((FirstStopAlongFt is null) && (ac.GroundSpeed < 1.0))
                {
                    FirstStopAlongFt = alongFt;
                }
            }

            if ((TouchdownAlongFt is not null) && (LeftRunwaySecond is null) && (phase is HoldingAfterExitPhase or TaxiingPhase))
            {
                LeftRunwaySecond = second;
            }
        }

        public string Describe() =>
            $"touchdown {TouchdownAlongFt:F0} ft (xte {TouchdownCrossTrackFt:F0} ft), max along on runway {MaxAlongOnRunwayFt:F0} ft, "
            + $"first stop {FirstStopAlongFt?.ToString("F0") ?? "none"} ft, of {RunwayLengthFt:F0} ft; max descent {MaxDescentFpm:F0} fpm "
            + $"(cap gave way: {CapGaveWay}, aim limit at touchdown {TouchdownAimLimitFt:F0} ft); left runway t={LeftRunwaySecond}";
    }

    private static (RunwayInfo Runway, AirportGroundLayout Layout, LatLon Threshold) Sfo28L()
    {
        RunwayInfo? runway = NavigationDatabase.Instance.GetRunway("SFO", "28L");
        Assert.NotNull(runway);
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("SFO");
        Assert.NotNull(layout);
        return (runway, layout, LandingThreshold.Resolve(runway, layout));
    }

    private LandingTrace? RunReplay(int endAt)
    {
        RecordingArchive? archive = RecordingLoader.OpenArchive(RecordingPath);
        if (archive is null)
        {
            return null;
        }

        using (archive)
        {
            TestVnasData.EnsureInitialized();
            if (TestVnasData.NavigationDb is null)
            {
                return null;
            }

            SimLogBuilder
                .CreateForTest(output)
                .EnableCategory("OccupiedRunwayGoAround", LogLevel.Debug)
                .EnableCategory("RunwayExitPhase", LogLevel.Information)
                .EnableCategory("LandingPhase", LogLevel.Information)
                .InitializeSimLog();

            SessionRecording recording = archive.ToBaseSessionRecording();
            var engine = new SimulationEngine(new TestAirportGroundData());
            engine.Replay(recording, 0);

            TimedSnapshot? snapshot = archive.ReadSnapshotAt(RestoreAt);
            if (snapshot is null)
            {
                return null;
            }
            engine.RestoreFromSnapshot(snapshot.State);

            (RunwayInfo runway, AirportGroundLayout layout, LatLon threshold) = Sfo28L();
            var trace = new LandingTrace(ForcedLandingProfile.LandingDistanceFt(runway, layout), runway.WidthFt / 2.0);
            engine.WarningEmitted += (callsign, warning) =>
            {
                if ((callsign == Follower) && warning.Contains("go-around", StringComparison.Ordinal))
                {
                    trace.FollowerGoAroundWarnings.Add(warning);
                }
            };

            for (int t = RestoreAt + 1; t <= endAt; t++)
            {
                engine.ReplayOneSecond();
                if (engine.FindAircraft(Callsign) is not { } ac)
                {
                    continue;
                }

                trace.Sample(ac, threshold, runway.TrueHeading, runway.ElevationFt, t, forcedWindow: t >= ClandfAt);
                if (t % 5 == 0)
                {
                    output.WriteLine(
                        $"t={t} phase={ac.Phases?.CurrentPhase?.GetType().Name} alt={ac.Altitude:F0} vs={ac.VerticalSpeed:F0} "
                            + $"ias={ac.IndicatedAirspeed:F0} gs={ac.GroundSpeed:F0} hdg={ac.TrueHeading.Degrees:F0} onGround={ac.IsOnGround}"
                    );
                }
            }

            output.WriteLine(trace.Describe());
            return trace;
        }
    }

    /// <summary>
    /// A forced landing ends on the runway: touchdown on the pavement, never past its end, and — when the descent cap
    /// had to give way — a touchdown at or before the aim point and any stop at least 300 ft short of the end. When the
    /// cap held, the descent never exceeded it.
    /// </summary>
    private static void AssertLandsAndStopsOnRunway(LandingTrace trace)
    {
        string summary = trace.Describe();
        Assert.True(trace.TouchdownAlongFt is not null, $"never touched down: {summary}");
        Assert.True((trace.TouchdownAlongFt >= 0) && (trace.TouchdownAlongFt <= trace.RunwayLengthFt), $"touched down off the runway: {summary}");
        Assert.True(Math.Abs(trace.TouchdownCrossTrackFt) <= trace.RunwayHalfWidthFt, $"touched down off the centerline: {summary}");
        Assert.True(trace.MaxAlongOnRunwayFt <= trace.RunwayLengthFt, $"rolled past the runway end: {summary}");

        if (!trace.CapGaveWay)
        {
            Assert.True(trace.MaxDescentFpm <= ForcedLandingProfile.DescentCapFpm + 1.0, $"exceeded the descent cap it did not need to: {summary}");
            return;
        }

        Assert.True(trace.TouchdownAlongFt <= trace.TouchdownAimLimitFt, $"touched down beyond the aim point: {summary}");
        Assert.True(
            (trace.FirstStopAlongFt is null) || (trace.FirstStopAlongFt <= trace.RunwayLengthFt - ForcedLandingProfile.RunwayEndStopMarginFt),
            $"stopped within 300 ft of the runway end: {summary}"
        );
    }

    /// <summary>
    /// The recorded CLANDF lands SKW5416 on 28L, stops it on the runway, and gets it off the runway within 180 s.
    /// </summary>
    [Fact]
    public void Skw5416_ClandfPastThreshold_LandsStopsAndVacates28L()
    {
        LandingTrace? trace = RunReplay(ClandfAt + LeaveRunwayBudgetSeconds);
        if (trace is null)
        {
            return;
        }

        AssertLandsAndStopsOnRunway(trace);
        Assert.True(
            trace.LeftRunwaySecond is not null,
            $"SKW5416 never reached HoldingAfterExit or Taxiing within {LeaveRunwayBudgetSeconds} s of the CLANDF: {trace.Describe()}"
        );
    }

    /// <summary>TAI562, next onto 28L, must not go around for SKW5416 still sitting on the runway (t=1265 in the recording).</summary>
    [Fact]
    public void Tai562_DoesNotGoAroundForSkw5416()
    {
        LandingTrace? trace = RunReplay(1300);
        if (trace is null)
        {
            return;
        }

        foreach (string warning in trace.FollowerGoAroundWarnings)
        {
            output.WriteLine($"{Follower}: {warning}");
        }

        Assert.DoesNotContain(trace.FollowerGoAroundWarnings, w => w.Contains($"{Follower} go-around: {Callsign}", StringComparison.Ordinal));
    }

    /// <summary>
    /// SFO 28L, real navdata and layout: a CRJ7 0.8 nm past the threshold at 1,800 ft and 160 kt gets CLANDF, lands on
    /// the runway, stops short of its end and vacates. The cap gives way here, as it does for SKW5416.
    /// </summary>
    [Fact]
    public void Clandf_PastThresholdAt1800Ft_LandsAndStopsOnRunway()
    {
        LandingTrace? trace = RunSynthetic(offsetNm: 0.8, aglFt: 1800, iasKts: 160);
        if (trace is null)
        {
            return;
        }

        AssertLandsAndStopsOnRunway(trace);
        Assert.True(trace.LeftRunwaySecond is not null, $"never vacated the runway: {trace.Describe()}");
    }

    /// <summary>
    /// A CRJ7 0.2 nm past the threshold at 400 ft and 150 kt: the capped descent reaches the runway in time, so the
    /// forced landing never descends faster than 3,000 fpm, and it lands, stops short of the end and vacates.
    /// </summary>
    [Fact]
    public void Clandf_ShortlyPastThresholdAt400Ft_StaysWithinDescentCap()
    {
        LandingTrace? trace = RunSynthetic(offsetNm: 0.2, aglFt: 400, iasKts: 150);
        if (trace is null)
        {
            return;
        }

        Assert.False(trace.CapGaveWay, $"the cap should suffice for this geometry: {trace.Describe()}");
        AssertLandsAndStopsOnRunway(trace);
        Assert.True(trace.LeftRunwaySecond is not null, $"never vacated the runway: {trace.Describe()}");
    }

    /// <summary>
    /// The same forced landing from a normal 3 nm final at 1,000 ft still lands in the touchdown zone (AIM 2-1-5.b:
    /// the first 3,000 ft), not at the threshold and not deep.
    /// </summary>
    [Fact]
    public void Clandf_FromNormalThreeMileFinal_LandsInTouchdownZone()
    {
        LandingTrace? trace = RunSynthetic(offsetNm: -3.0, aglFt: 1000, iasKts: 140);
        if (trace is null)
        {
            return;
        }

        Assert.True(
            (trace.TouchdownAlongFt >= 500) && (trace.TouchdownAlongFt <= 3000),
            $"touched down outside the 500-3,000 ft touchdown zone: {trace.Describe()}"
        );
        AssertLandsAndStopsOnRunway(trace);
        Assert.True(trace.LeftRunwaySecond is not null, $"never vacated the runway: {trace.Describe()}");
    }

    /// <summary>
    /// A CRJ7 on the 28L centerline <paramref name="offsetNm"/> from the landing threshold (positive = past it) at
    /// <paramref name="aglFt"/>, sent CLANDF, ticked until it vacates or 300 s run out.
    /// </summary>
    private LandingTrace? RunSynthetic(double offsetNm, double aglFt, double iasKts)
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        SimLogBuilder
            .CreateForTest(output)
            .EnableCategory("RunwayExitPhase", LogLevel.Information)
            .EnableCategory("LandingPhase", LogLevel.Information)
            .InitializeSimLog();

        var engine = new SimulationEngine(new TestAirportGroundData());
        (RunwayInfo runway, AirportGroundLayout layout, LatLon threshold) = Sfo28L();
        LatLon start = GeoMath.ProjectPoint(threshold, offsetNm >= 0 ? runway.TrueHeading : runway.TrueHeading.ToReciprocal(), Math.Abs(offsetNm));
        var aircraft = new AircraftState
        {
            Callsign = "TSTAC",
            AircraftType = "CRJ7",
            Position = start,
            TrueHeading = runway.TrueHeading,
            TrueTrack = runway.TrueHeading,
            Altitude = runway.ElevationFt + aglFt,
            IndicatedAirspeed = iasKts,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "SFO",
                Destination = "SFO",
                FlightRules = "IFR",
                Altitude = PlannedAltitude.Ifr(3000),
            },
        };
        aircraft.Targets.TargetSpeed = iasKts;

        aircraft.Phases = new PhaseList { AssignedRunway = runway };
        aircraft.Phases.Add(new FinalApproachPhase { SkipInterceptCheck = true });
        aircraft.Phases.Add(new LandingPhase());
        aircraft.Phases.Add(new RunwayExitPhase());
        aircraft.Phases.Add(new HoldingAfterExitPhase());
        aircraft.Ground.Layout = layout;

        PhaseContext ctx = CommandDispatcher.BuildMinimalContext(aircraft, layout);
        aircraft.Phases.Start(ctx);
        engine.World.AddAircraft(aircraft);
        engine.Scenario = new SimScenarioState
        {
            ScenarioId = "test-sfo-clandf",
            ScenarioName = "SFO CLANDF Test",
            RngSeed = 42,
            OriginalScenarioJson = "{}",
            PrimaryAirportId = "SFO",
        };

        CommandResult result = engine.SendCommand("TSTAC", "CLANDF");
        Assert.True(result.Success, $"CLANDF failed: {result.Message}");

        var trace = new LandingTrace(ForcedLandingProfile.LandingDistanceFt(runway, layout), runway.WidthFt / 2.0);
        for (int t = 1; t <= 300; t++)
        {
            engine.TickOneSecond();
            trace.Sample(aircraft, threshold, runway.TrueHeading, runway.ElevationFt, t, forcedWindow: true);
            if (t % 5 == 0)
            {
                output.WriteLine(
                    $"t={t} phase={aircraft.Phases?.CurrentPhase?.GetType().Name} alt={aircraft.Altitude:F0} vs={aircraft.VerticalSpeed:F0} "
                        + $"ias={aircraft.IndicatedAirspeed:F0} hdg={aircraft.TrueHeading.Degrees:F0} onGround={aircraft.IsOnGround}"
                );
            }

            if (trace.LeftRunwaySecond is not null)
            {
                break;
            }
        }

        output.WriteLine(trace.Describe());
        return trace;
    }
}
