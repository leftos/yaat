using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E test for diagonal runway lineup on SFO RWY 28R from taxiway E.
/// Recording S1-SFO-2 Ground Control 28/01 — N346G gets CTO at t=250 and
/// must line up on 28R cleanly. The historical V1 implementation cut a
/// diagonal across grass from the hold-short to the centerline, leaving
/// the aircraft parallel-offset at 3.1 ft / 5.9° off runway heading at
/// rollout end. Design D (closed-form plan playback) fixes this by
/// construction — invariant I2 guarantees position and heading are both
/// functions of a single scalar phase variable during the arc, and
/// rollout steers on runway heading unconditionally rather than on a
/// bearing to an off-centerline stop node.
///
/// This test asserts the aircraft completes the lineup with the Design D
/// end-state contract: cross-track ≤ 5 ft, heading diff ≤ 2°, ground
/// speed ≤ 1 kt at the first tick after LineUpPhase exits. Tolerances
/// are slightly looser than the synthetic scenario tests in
/// <c>LineUpPhaseTests</c> because this is a full replay starting from
/// live taxi state rather than a clean fixture, but still tight enough
/// to catch the V1 diagonal regression.
/// </summary>
public class SfoLineupDiagonalTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/09304e0c727e.zip";

    private static SessionRecording? LoadRecording() => RecordingLoader.Load(RecordingPath);

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();

        return new SimulationEngine(groundData);
    }

    /// <summary>
    /// N346G replay to CTO at t=250, then tick forward and assert the
    /// aircraft transitions through <see cref="LineUpPhase"/> into
    /// <see cref="LinedUpAndWaitingPhase"/> or <see cref="TakeoffPhase"/>
    /// with an on-centerline, aligned, stopped end state. This is the
    /// replacement for the V1-shape <c>N346G_LineUp28R_TickByTickTrace</c>
    /// regression test: the V1 test asserted "no stuck intermediate
    /// heading for &gt;1s", which is a V1-specific pathology assertion;
    /// V2 uses a closed-form pivot arc where a stuck heading cannot
    /// occur, so we assert the actual desired end state instead.
    /// </summary>
    [Fact]
    public void N346G_LineUp28R_CompletesWithOnCenterlineAlignedStop()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            output.WriteLine("SKIP: recording or navdata not available");
            return;
        }

        // Resolve runway from navdata rather than hardcoding threshold
        // coordinates — the test fails loudly if SFO 28R changes in
        // NavData.dat rather than silently drifting against stale
        // constants.
        var runway = TestVnasData.NavigationDb!.GetRunway("KSFO", "28R");
        Assert.NotNull(runway);
        var rwyHdg = runway.TrueHeading;
        double rwyThreshLat = runway.ThresholdLatitude;
        double rwyThreshLon = runway.ThresholdLongitude;

        // Replay up to the second before CTO so we can observe the
        // transition into LineUpPhase on the next tick.
        engine.Replay(recording, 249);

        var ac = engine.FindAircraft("N346G");
        Assert.NotNull(ac);
        bool enteredLineUp = false;
        bool exitedLineUp = false;
        int enterTick = -1;
        int exitTick = -1;

        // Pose captured at the first tick where CurrentPhase is no
        // longer LineUpPhase. We check this frame against the Design D
        // end-state contract — after this point the aircraft may start
        // its takeoff roll and drift away from zero ground speed.
        double finalCrossFt = double.NaN;
        double finalHdgDiffDeg = double.NaN;
        double finalGsKts = double.NaN;
        string finalPhase = "(none)";
        bool wasRolling = false;
        double arcSpeedKts = 0;

        // 60 seconds budget at 0.25-s sub-ticks (240 iterations). Poll at
        // sub-tick granularity so we catch the exact tick LineUpPhase exits
        // — polling at whole-second granularity lets TakeoffPhase accelerate
        // the aircraft before we observe it, invalidating the "stopped at
        // exit" assertion.
        const int budgetSubTicks = 60 * 4;
        for (int sub = 0; sub < budgetSubTicks; sub++)
        {
            engine.ReplayOneSubTick();
            ac = engine.FindAircraft("N346G");
            Assert.NotNull(ac);

            var phase = ac.Phases?.CurrentPhase;

            if (!enteredLineUp && phase is LineUpPhase)
            {
                enteredLineUp = true;
                enterTick = sub;
                output.WriteLine($"[sub={sub}] entered LineUpPhase");
            }

            // Snapshot rolling mode + arc speed while the phase is live.
            if (enteredLineUp && phase is LineUpPhase livePhase)
            {
                wasRolling = wasRolling || livePhase.RollingMode;
                if (livePhase.PathPlan is { } plan && arcSpeedKts == 0)
                {
                    arcSpeedKts = plan.ArcSpeedKts;
                }
            }

            if (enteredLineUp && phase is not LineUpPhase)
            {
                exitedLineUp = true;
                exitTick = sub;
                finalPhase = phase?.Name ?? "(null)";

                double signedCrossNm = GeoMath.SignedCrossTrackDistanceNm(ac.Latitude, ac.Longitude, rwyThreshLat, rwyThreshLon, rwyHdg);
                finalCrossFt = Math.Abs(signedCrossNm) * GeoMath.FeetPerNm;
                finalHdgDiffDeg = Math.Abs(rwyHdg.SignedAngleTo(ac.TrueHeading));
                finalGsKts = ac.GroundSpeed;

                output.WriteLine(
                    $"[sub={sub}] exited LineUpPhase -> {finalPhase} | "
                        + $"cross={finalCrossFt:F2}ft hdgDiff={finalHdgDiffDeg:F2}° gs={finalGsKts:F2}kt "
                        + $"rolling={wasRolling} arcSpeed={arcSpeedKts:F2}kt"
                );
                break;
            }
        }

        Assert.True(enteredLineUp, $"Aircraft never entered LineUpPhase (CTO at t=250, enterSubTick={enterTick})");
        Assert.True(exitedLineUp, $"Aircraft never exited LineUpPhase within 60 s (entered at sub={enterTick}, budget {budgetSubTicks} sub-ticks)");

        // Design D end-state contract. Tolerances are slightly looser than
        // the synthetic LineUpPhaseTests fixtures (3 ft / 1° / 0.5 kt)
        // because this is a full replay with live taxi state feeding the
        // phase entry, but still tight enough that the V1 diagonal-cut
        // failure (3.1 ft / 5.9°) would not pass the heading assertion.
        Assert.True(
            finalCrossFt < 5.0,
            $"cross-centerline {finalCrossFt:F2}ft exceeds 5 ft tolerance at LineUpPhase exit (t={exitTick}, phase={finalPhase})"
        );
        Assert.True(
            finalHdgDiffDeg < 2.0,
            $"heading-diff {finalHdgDiffDeg:F2}° exceeds 2° tolerance at LineUpPhase exit (t={exitTick}, phase={finalPhase})"
        );

        // Ground speed bound is mode-dependent. LUAW mode brakes to 0;
        // rolling mode hands off at ~arc speed.
        double gsBoundKts = wasRolling ? arcSpeedKts + 1.0 : 1.0;
        Assert.True(
            finalGsKts < gsBoundKts,
            $"gs {finalGsKts:F2}kt exceeds {gsBoundKts:F2} kt bound at LineUpPhase exit (t={exitTick}, phase={finalPhase}, rolling={wasRolling})"
        );
    }

    /// <summary>
    /// Tick recorder for the SFO 28R lineup scenario. Writes
    /// <c>.tmp/sfo-lineup28r-ticks.csv</c> covering the replay window
    /// around CTO (t=248..270). Render with LayoutInspector --ticks.
    /// </summary>
    [Fact]
    public void Diagnostic_RecordLineupTicks()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, 248);

        var ac = engine.FindAircraft("N346G");
        if (ac is null)
        {
            return;
        }

        var recorder = new TickRecorder(ac);
        recorder.Record(248);

        // Capture seconds 249..280 (CTO at 250, give a generous window)
        for (int t = 249; t <= 280; t++)
        {
            engine.ReplayOneSecond();
            ac = engine.FindAircraft("N346G");
            if (ac is null)
            {
                break;
            }
            recorder.Record(t);

            if (ac.Phases?.CurrentPhase is TakeoffPhase or LinedUpAndWaitingPhase)
            {
                output.WriteLine($"[diag] lineup-complete at t={t} phase={ac.Phases.CurrentPhase.Name}");
                // Record a few more ticks past the transition then stop
                for (int t2 = t + 1; t2 <= Math.Min(t + 5, 280); t2++)
                {
                    engine.ReplayOneSecond();
                    ac = engine.FindAircraft("N346G");
                    if (ac is null)
                    {
                        break;
                    }
                    recorder.Record(t2);
                }
                break;
            }
        }

        string csvPath = Path.Combine(TickRecorder.FindRepoRoot(), ".tmp", "sfo-lineup28r-ticks.csv");
        recorder.WriteCsv(csvPath);
        output.WriteLine($"[diag] wrote {recorder.Count} ticks to {csvPath}");
    }
}
