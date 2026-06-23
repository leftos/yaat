using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E regression for the OAK runway-30 LUAW/CTO bug: SWA aircraft told to line
/// up and wait (or cleared for takeoff) drove straight past the runway and into
/// the bay in ~2 of 20 departures.
///
/// Root cause: <see cref="LineUpPhase"/> crosses the perpendicular
/// <see cref="LineUpPhase.State.PivotStraight"/> segment at corner speed (15 kt
/// for a jet = ~6.3 ft per 0.25 s sub-tick) but the arrival check was a pure
/// straight-line distance &lt; 3 ft. The ~6 ft-wide window is narrower than the
/// step, so when the target lands in the blind gap between two samples (e.g.
/// SWA1261: +3.3 ft then −3.1 ft, both &gt; 3 ft) the transition to
/// <see cref="LineUpPhase.State.PivotTurn2"/> never fires and the aircraft drives
/// off at the perpendicular heading forever. A ~1 ft difference in the
/// hold-short pose decides it — hence intermittent with no obvious pattern. The
/// fix arrives on along-track-remaining ≤ tolerance, which also catches overshoot.
///
/// These tests use hybrid replay (restore the recorded snapshot at the exact
/// hold-short pose, then replay the command and tick forward). Full replay from
/// t=0 does NOT reproduce the bug: 50 min of accumulated taxi drift shifts the
/// hold-short heading enough that the overshoot lands inside the window by luck.
///
/// Recording: S2-OAK-5 (2) Practical Exam Prep, ARTCC ZOA, KOAK.
/// </summary>
public class SwaLuawRwy30OvershootTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/swa-luaw-rwy30-overshoot-recording.yaat-bug-report-bundle.zip";

    /// <summary>OAK runway 30 true heading (from the recorded rollout/lineup pose).</summary>
    private const double Runway30TrueHeadingDeg = 310.12;

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
    /// Restore the recorded snapshot at/just before <paramref name="restoreSec"/>,
    /// then replay (applying recorded commands) up to and including
    /// <paramref name="throughSec"/>. Leaves the replay paused there so the caller
    /// can drive the aircraft with <see cref="SimulationEngine.TickOneSecond"/>
    /// (which does NOT apply later recorded actions — important here because the
    /// instructor deleted the runaway aircraft mid-recording).
    /// </summary>
    private SimulationEngine? ReplayThroughCommand(RecordingArchive archive, SessionRecording recording, int restoreSec, int throughSec)
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return null;
        }

        engine.Replay(recording, 0);
        var snap = archive.ReadSnapshotAt(restoreSec);
        if (snap is null)
        {
            return null;
        }

        engine.RestoreFromSnapshot(snap.State);
        int t = (int)snap.ElapsedSeconds;
        engine.FastForwardTo(t + 1, recording.Actions);
        t += 1;
        while (t < throughSec)
        {
            engine.ReplayOneSecond();
            t++;
        }
        return engine;
    }

    [Fact]
    public void SWA1261_ClearedForTakeoff_LinesUpAndTakesOff_NotIntoTheBay()
    {
        var archive = RecordingLoader.OpenArchive(RecordingPath);
        if (archive is null || BuildEngine() is null)
        {
            output.WriteLine("Skipped: recording or NavData not available");
            return;
        }

        using (archive)
        {
            var recording = archive.ToBaseSessionRecording();
            // CTO is at t=3046; restore from the hold short just before and replay through it.
            var engine = ReplayThroughCommand(archive, recording, restoreSec: 3040, throughSec: 3047);
            Assert.NotNull(engine);

            var ac = engine.FindAircraft("SWA1261");
            Assert.NotNull(ac);
            Assert.True(
                ac.Phases?.Phases.Any(p => p is LineUpPhase or TakeoffPhase or InitialClimbPhase) == true,
                "SWA1261 should have a lineup/takeoff chain after CTO"
            );

            var startPos = ac.Position;
            double minHeadingErr = double.MaxValue;
            double maxOffCenterlineFt = 0;
            bool airborne = false;

            for (int t = 1; t <= 120; t++)
            {
                engine.TickOneSecond();
                ac = engine.FindAircraft("SWA1261");
                if (ac is null)
                {
                    break;
                }

                minHeadingErr = Math.Min(minHeadingErr, GeoMath.AbsBearingDifference(ac.TrueHeading.Degrees, Runway30TrueHeadingDeg));
                maxOffCenterlineFt = Math.Max(maxOffCenterlineFt, GeoMath.DistanceNm(ac.Position, startPos) * GeoMath.FeetPerNm);

                if (!ac.IsOnGround && ac.Altitude > 50)
                {
                    airborne = true;
                    break;
                }
            }

            // Before the fix: stuck in PivotStraight at 220° (≈90° off the runway),
            // driving away from the hold short for the full window (~1800 ft / 120 s).
            Assert.True(
                minHeadingErr < 5.0,
                $"SWA1261 never aligned with runway 30 ({Runway30TrueHeadingDeg:F0}°); best error {minHeadingErr:F0}° "
                    + $"(drove {maxOffCenterlineFt:F0} ft off the hold short — into the bay)"
            );
            Assert.True(airborne, "SWA1261 never became airborne after CTO");
        }
    }

    [Fact]
    public void SWA897_LineUpAndWait_ReachesCenterline_NotIntoTheBay()
    {
        var archive = RecordingLoader.OpenArchive(RecordingPath);
        if (archive is null || BuildEngine() is null)
        {
            output.WriteLine("Skipped: recording or NavData not available");
            return;
        }

        using (archive)
        {
            var recording = archive.ToBaseSessionRecording();
            // LUAW is at t=1839; restore from the hold short just before and replay through it.
            var engine = ReplayThroughCommand(archive, recording, restoreSec: 1835, throughSec: 1841);
            Assert.NotNull(engine);

            var ac = engine.FindAircraft("SWA897");
            Assert.NotNull(ac);

            double minHeadingErr = double.MaxValue;
            bool linedUpAndWaiting = false;

            for (int t = 1; t <= 120; t++)
            {
                engine.TickOneSecond();
                ac = engine.FindAircraft("SWA897");
                if (ac is null)
                {
                    break;
                }

                minHeadingErr = Math.Min(minHeadingErr, GeoMath.AbsBearingDifference(ac.TrueHeading.Degrees, Runway30TrueHeadingDeg));
                if (ac.Phases?.CurrentPhase is LinedUpAndWaitingPhase)
                {
                    linedUpAndWaiting = true;
                    break;
                }
            }

            Assert.True(minHeadingErr < 5.0, $"SWA897 never aligned with runway 30; best error {minHeadingErr:F0}° (drove into the bay)");
            Assert.True(linedUpAndWaiting, "SWA897 never reached lined-up-and-waiting on the centerline");
        }
    }
}
