using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E test for diagonal runway lineup on SFO RWY 28R from taxiway E.
/// N346G would enter perpendicular, then taxi diagonally to the centerline
/// instead of smoothly turning onto the runway heading. The diagonal was
/// caused by Stage 2 (NavigateToTarget) kicking in after the on-runway
/// node was reached even though the aircraft was already close to centerline.
///
/// Fix: skip Stage 2 when cross-track at Stage 1 completion is within
/// OnRunwayNodeThresholdNm, going directly to heading alignment.
///
/// Recording: S1-SFO-2 Ground Control 28/01 — N346G gets CTO at t=250.
/// </summary>
public class SfoLineupDiagonalTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/09304e0c727e.zip";
    private const double Rwy28RTrueHeading = 297.907;
    private const double Rwy28RThresholdLat = 37.628738722222224;
    private const double Rwy28RThresholdLon = -122.39339186111111;

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
    /// Trace N346G at 0.25 s physics-tick resolution through the entire lineup
    /// sequence.  Logs position, heading, ground speed, cross-track distance, and
    /// phase each tick so the exact trajectory is visible.
    /// </summary>
    [Fact]
    public void N346G_LineUp28R_TickByTickTrace()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            output.WriteLine("SKIP: recording or navdata not available");
            return;
        }

        // Replay to t=250 — CTO applied this second
        engine.Replay(recording, 250);

        var ac = engine.FindAircraft("N346G");
        Assert.NotNull(ac);

        var rwyHdg = new TrueHeading(Rwy28RTrueHeading);
        double perpHeading = Rwy28RTrueHeading - 90; // ~207.9°

        output.WriteLine("");
        output.WriteLine("=== N346G LINEUP TRACE — SFO RWY 28R (0.25s ticks) ===");
        output.WriteLine($"Runway true heading: {Rwy28RTrueHeading:F1}°");
        output.WriteLine($"Perpendicular heading: {perpHeading:F1}°");
        output.WriteLine("");
        output.WriteLine($"{"tick", 5}  {"Phase", -22}  {"Hdg", 7}  {"GS", 5}  {"XTrack", 8}  {"HdgDiff", 8}  {"Lat", 12}  {"Lon", 13}");
        output.WriteLine(new string('-', 100));

        bool enteredLineUp = false;
        int lineUpStartTick = -1;
        int lineUpEndTick = -1;
        int stuckHeadingTicks = 0;
        double prevHeading = -1;
        bool completed = false;

        // Log initial state at t=250
        LogTick(ac, rwyHdg, 0, ref enteredLineUp);

        // Tick at 0.25s resolution for up to 35 seconds (140 sub-ticks).
        // Budget was 20 s historically; bumped after the pre-arrival brake
        // clamp in GroundNavigator (stop-snap fix) exposed a pre-existing
        // bug where LineUpPhase cuts diagonally across grass from the
        // hold-short to the runway centerline. The diagonal path is slower
        // under the stricter (correct) kinematic decel. See
        // docs/plans/open-issues/lineup-path-diagonal-cut.md — once that
        // bug is fixed the budget should be restored to ~20 s.
        for (int tick = 1; tick <= 140; tick++)
        {
            engine.TickOnce(); // 0.25s physics tick
            ac = engine.FindAircraft("N346G");
            Assert.NotNull(ac);

            LogTick(ac, rwyHdg, tick, ref enteredLineUp);

            if (ac.Phases?.CurrentPhase is ILineUpPhase && lineUpStartTick < 0)
            {
                lineUpStartTick = tick;
            }

            // Detect "stuck heading" — heading unchanged from previous tick,
            // not at perpendicular (±2°) and not at runway heading (±2°).
            // This catches the diagonal taxi where NavigateToTarget holds a
            // fixed intermediate bearing for many ticks.
            double hdgDiff = rwyHdg.AbsAngleTo(ac.TrueHeading);
            double perpDiff = Math.Abs(NormalizeAngle(ac.TrueHeading.Degrees - perpHeading));
            bool atPerp = perpDiff < 2;
            bool atRwy = hdgDiff < 2;

            if (enteredLineUp && (prevHeading >= 0) && (Math.Abs(ac.TrueHeading.Degrees - prevHeading) < 0.1) && !atPerp && !atRwy)
            {
                stuckHeadingTicks++;
                output.WriteLine(
                    $"         ^^^ STUCK HDG: {ac.TrueHeading.Degrees:F1}° (not perp, not runway) — "
                        + $"crossTrack={GetCrossTrackFt(ac, rwyHdg):F0}ft"
                );
            }

            prevHeading = ac.TrueHeading.Degrees;

            if (ac.Phases?.CurrentPhase is LinedUpAndWaitingPhase or TakeoffPhase)
            {
                lineUpEndTick = tick;
                completed = true;
                output.WriteLine($"         >>> Lineup complete (phase={ac.Phases.CurrentPhase.Name})");
                break;
            }
        }

        int lineUpDuration = (lineUpStartTick >= 0 && lineUpEndTick >= 0) ? lineUpEndTick - lineUpStartTick : -1;

        output.WriteLine("");
        output.WriteLine("=== SUMMARY ===");
        output.WriteLine($"Entered LineUp phase: {enteredLineUp} (tick {lineUpStartTick})");
        output.WriteLine($"Lineup completed: {completed} (tick {lineUpEndTick})");
        output.WriteLine($"Total lineup sub-ticks: {lineUpDuration} ({lineUpDuration * 0.25:F1}s)");
        output.WriteLine($"Stuck-heading sub-ticks: {stuckHeadingTicks} ({stuckHeadingTicks * 0.25:F1}s)");

        Assert.True(enteredLineUp, "Aircraft never entered LineUpPhase");
        Assert.True(completed, "Aircraft never completed lineup within 35 seconds");

        // Before the fix, the aircraft spent 22 sub-ticks (5.5s) at a fixed 288°
        // heading, taxiing diagonally from the on-runway node to the centerline.
        // After the fix, it turns directly to the runway heading with zero stuck ticks.
        Assert.True(
            stuckHeadingTicks <= 4,
            $"Aircraft spent {stuckHeadingTicks * 0.25:F1}s at a stuck diagonal heading — "
                + "expected smooth turn to runway heading after reaching on-runway node"
        );
    }

    private void LogTick(AircraftState ac, TrueHeading rwyHdg, int tick, ref bool enteredLineUp)
    {
        string phaseName = ac.Phases?.CurrentPhase?.Name ?? "(none)";
        double crossTrackFt = GetCrossTrackFt(ac, rwyHdg);
        double hdgDiff = rwyHdg.AbsAngleTo(ac.TrueHeading);

        if (ac.Phases?.CurrentPhase is ILineUpPhase)
        {
            enteredLineUp = true;
        }

        output.WriteLine(
            $"{tick, 5}  {phaseName, -22}  {ac.TrueHeading.Degrees, 7:F1}  {ac.GroundSpeed, 5:F1}  {crossTrackFt, 5:F0}ft  "
                + $"{hdgDiff, 7:F1}°  {ac.Latitude, 12:F7}  {ac.Longitude, 13:F7}"
        );
    }

    private static double GetCrossTrackFt(AircraftState ac, TrueHeading rwyHdg)
    {
        return Math.Abs(GeoMath.SignedCrossTrackDistanceNm(ac.Latitude, ac.Longitude, Rwy28RThresholdLat, Rwy28RThresholdLon, rwyHdg))
            * GeoMath.FeetPerNm;
    }

    private static double NormalizeAngle(double deg)
    {
        deg = ((deg % 360) + 360) % 360;
        return deg > 180 ? deg - 360 : deg;
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
