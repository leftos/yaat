using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E tests for lineup on OAK RWY 28R from taxiway B hold-shorts. The
/// historical V1 analog LineUpPhase implementation cut a diagonal across
/// the runway surface when given CTO — it would steer toward the on-runway
/// graph node instead of first reaching the centerline. The original V1
/// regression tests asserted a very specific "turn perpendicular, cross
/// straight, then align" shape which V2 (closed-form plan playback) does
/// not follow: V2 enters the runway tangentially via a category-sized
/// pivot arc.
///
/// These V2 tests assert the actual desired end state — aircraft completes
/// lineup with an on-centerline, aligned, stopped pose — without caring
/// about the intermediate maneuver shape. That's the contract the V1
/// tests were really exercising; the perpendicular-cross-align assertion
/// was just V1 implementation detail.
///
/// Recording: S2-OAK-4 VFR Transitions/Radar Concepts — OAK tower
/// scenario. Two aircraft (N436MS at t=47, N342T at t=688) each start
/// at hold-short of 28R on taxiway B and receive CTO.
/// </summary>
public class DiagonalLineup28rTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/diagonal-lineup-28r-recording.zip";

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
    /// Common end-state assertion: replay up to <paramref name="ctoSecond"/>
    /// minus one so we observe the transition into LineUpPhase on the next
    /// tick, then poll the replay at 0.25-s sub-tick granularity until the
    /// aircraft exits LineUpPhase, and assert the Design D end-state
    /// contract (cross ≤ 5 ft, hdgDiff ≤ 2°, gs ≤ 1 kt). Sub-tick polling
    /// is critical: at whole-second granularity, TakeoffPhase can
    /// accelerate the aircraft inside the replay second where
    /// LineUpPhase exits, which would falsely fail the gs assertion.
    /// </summary>
    private void AssertLineUpCompletesCleanly(string callsign, int ctoSecond, int budgetSeconds)
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            output.WriteLine("SKIP: recording or navdata not available");
            return;
        }

        var runway = TestVnasData.NavigationDb!.GetRunway("KOAK", "28R");
        Assert.NotNull(runway);
        var rwyHdg = runway.TrueHeading;
        double rwyThreshLat = runway.ThresholdLatitude;
        double rwyThreshLon = runway.ThresholdLongitude;

        engine.Replay(recording, ctoSecond - 1);

        var ac = engine.FindAircraft(callsign);
        Assert.NotNull(ac);

        bool enteredLineUp = false;
        bool exitedLineUp = false;
        bool wasRolling = false;
        double arcSpeedKts = 0;
        int enterSubTick = -1;
        int exitSubTick = -1;
        double finalCrossFt = double.NaN;
        double finalHdgDiffDeg = double.NaN;
        double finalGsKts = double.NaN;
        string finalPhase = "(none)";

        int budgetSubTicks = budgetSeconds * 4;
        for (int sub = 0; sub < budgetSubTicks; sub++)
        {
            engine.ReplayOneSubTick();
            ac = engine.FindAircraft(callsign);
            Assert.NotNull(ac);

            var phase = ac.Phases?.CurrentPhase;

            if (!enteredLineUp && phase is LineUpPhase)
            {
                enteredLineUp = true;
                enterSubTick = sub;
                output.WriteLine($"[sub={sub}] {callsign} entered LineUpPhase");
            }

            // Snapshot rolling mode + arc speed while the phase is live.
            if (enteredLineUp && phase is LineUpPhase livePhase)
            {
                wasRolling = wasRolling || livePhase.RollingMode;
                if (livePhase.Plan is { } plan && arcSpeedKts == 0)
                {
                    arcSpeedKts = plan.ArcSpeedKts;
                }
            }

            if (enteredLineUp && phase is not LineUpPhase)
            {
                exitedLineUp = true;
                exitSubTick = sub;
                finalPhase = phase?.Name ?? "(null)";

                double signedCrossNm = GeoMath.SignedCrossTrackDistanceNm(ac.Latitude, ac.Longitude, rwyThreshLat, rwyThreshLon, rwyHdg);
                finalCrossFt = Math.Abs(signedCrossNm) * GeoMath.FeetPerNm;
                finalHdgDiffDeg = Math.Abs(rwyHdg.SignedAngleTo(ac.TrueHeading));
                finalGsKts = ac.GroundSpeed;

                output.WriteLine(
                    $"[sub={sub}] {callsign} exited LineUpPhase -> {finalPhase} | "
                        + $"cross={finalCrossFt:F2}ft hdgDiff={finalHdgDiffDeg:F2}° gs={finalGsKts:F2}kt "
                        + $"rolling={wasRolling} arcSpeed={arcSpeedKts:F2}kt"
                );
                break;
            }
        }

        Assert.True(enteredLineUp, $"{callsign} never entered LineUpPhase (CTO at t={ctoSecond}, enterSubTick={enterSubTick})");
        Assert.True(
            exitedLineUp,
            $"{callsign} never exited LineUpPhase within {budgetSeconds} s (entered at sub={enterSubTick}, budget {budgetSubTicks} sub-ticks)"
        );

        Assert.True(
            finalCrossFt < 5.0,
            $"{callsign} cross-centerline {finalCrossFt:F2}ft exceeds 5 ft tolerance at LineUpPhase exit (sub={exitSubTick}, phase={finalPhase})"
        );
        Assert.True(
            finalHdgDiffDeg < 2.0,
            $"{callsign} heading-diff {finalHdgDiffDeg:F2}° exceeds 2° tolerance at LineUpPhase exit (sub={exitSubTick}, phase={finalPhase})"
        );

        // Ground speed bound is mode-dependent. LUAW mode brakes to 0;
        // rolling mode hands off at ~arc speed.
        double gsBoundKts = wasRolling ? arcSpeedKts + 1.0 : 1.0;
        Assert.True(
            finalGsKts < gsBoundKts,
            $"{callsign} gs {finalGsKts:F2}kt exceeds {gsBoundKts:F2} kt bound at LineUpPhase exit "
                + $"(sub={exitSubTick}, phase={finalPhase}, rolling={wasRolling})"
        );
    }

    /// <summary>
    /// Diagnostic: record per-tick CSVs for the four scenario aircraft
    /// through their final-taxi → hold-short → CTO → lineup windows.
    /// Writes <c>.tmp/oak-lineup-fault-{callsign}.csv</c> for each
    /// aircraft. Inspect via <c>Yaat.TickInspector</c> and render with
    /// <c>Yaat.LayoutInspector --ticks</c>. CTO seconds from the
    /// recording's actions.json:
    ///   N436MS t=47 (works), N346G t=109 (faults — 180° turn),
    ///   N172SP t=402 (faults — not converging), N342T t=688 (works).
    /// </summary>
    [Fact]
    public void Diagnostic_RecordTicksForLineupFaults()
    {
        var recording = LoadRecording();
        if (recording is null)
        {
            output.WriteLine("SKIP: recording not available");
            return;
        }

        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            output.WriteLine("SKIP: navdata not available");
            return;
        }

        SimLogBuilder
            .CreateForTest(output)
            .EnableCategory("LineUpPlanBuilder", LogLevel.Debug)
            .EnableCategory("LineUpPhase", LogLevel.Debug)
            .InitializeSimLog();

        var groundData = new TestAirportGroundData();
        var engine = new SimulationEngine(groundData);

        var windows = new (string Callsign, int Start, int End)[]
        {
            ("N436MS", 20, 90),
            ("N346G", 40, 140),
            ("N172SP", 130, 440),
            ("N342T", 490, 735),
        };

        engine.Replay(recording, 0);

        var recorders = new Dictionary<string, TickRecorder>();
        int maxSecond = 0;
        foreach (var w in windows)
        {
            maxSecond = Math.Max(maxSecond, w.End);
        }

        for (int t = 1; t <= maxSecond; t++)
        {
            engine.ReplayOneSecond();
            foreach (var w in windows)
            {
                if (t < w.Start || t > w.End)
                {
                    continue;
                }

                var ac = engine.FindAircraft(w.Callsign);
                if (ac is null)
                {
                    continue;
                }

                if (!recorders.TryGetValue(w.Callsign, out var rec))
                {
                    rec = new TickRecorder(ac);
                    recorders[w.Callsign] = rec;
                }
                rec.Record(t);
            }
        }

        string repoRoot = TickRecorder.FindRepoRoot();
        foreach (var (cs, rec) in recorders)
        {
            string path = Path.Combine(repoRoot, ".tmp", $"oak-lineup-fault-{cs}.csv");
            rec.WriteCsv(path);
            output.WriteLine($"[diag] wrote {rec.Count} ticks for {cs} -> {path}");
        }
    }

    /// <summary>
    /// N436MS at OAK receives CTO at t=47 from hold-short of 28R on
    /// taxiway B. V2 must complete lineup with proper end state.
    /// </summary>
    [Fact]
    public void N436MS_LineUp28R_CompletesWithOnCenterlineAlignedStop()
    {
        AssertLineUpCompletesCleanly(callsign: "N436MS", ctoSecond: 47, budgetSeconds: 60);
    }

    /// <summary>
    /// N342T at OAK receives CTO at t=688 from hold-short of 28R on
    /// taxiway B. V2 must complete lineup with proper end state. Second
    /// aircraft in the same scenario — verifies the phase handles
    /// multiple departures independently.
    /// </summary>
    [Fact]
    public void N342T_LineUp28R_CompletesWithOnCenterlineAlignedStop()
    {
        AssertLineUpCompletesCleanly(callsign: "N342T", ctoSecond: 688, budgetSeconds: 60);
    }
}
