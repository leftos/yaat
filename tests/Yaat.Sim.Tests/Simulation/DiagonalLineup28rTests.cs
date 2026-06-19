using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E tests for lineup on OAK RWY 28R from taxiway B hold-shorts. LineUpPhase
/// enters the runway tangentially via a category-sized pivot arc that ends on
/// the centerline, rather than cutting a diagonal straight to the on-runway
/// graph node.
///
/// These tests assert the actual desired end state — aircraft completes
/// lineup with an on-centerline, aligned, stopped pose — without caring
/// about the intermediate maneuver shape.
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
                if (livePhase.PathPlan is { } plan && arcSpeedKts == 0)
                {
                    arcSpeedKts = plan.ArcSpeedKts;
                }
            }

            if (enteredLineUp && phase is not LineUpPhase)
            {
                exitedLineUp = true;
                exitSubTick = sub;
                finalPhase = phase?.Name ?? "(null)";

                double signedCrossNm = GeoMath.SignedCrossTrackDistanceNm(ac.Position.Lat, ac.Position.Lon, rwyThreshLat, rwyThreshLon, rwyHdg);
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
    /// Writes <c>.tmp/oak-lineup-fault-{callsign}.json</c> for each
    /// aircraft. Inspect as text via <c>Yaat.LayoutInspector --tick-table</c>
    /// and render visually with <c>Yaat.LayoutInspector --ticks</c>. CTO seconds from the
    /// recording's actions.json:
    /// <summary>
    /// N436MS at OAK receives CTO at t=47 from hold-short of 28R on
    /// taxiway B and must complete lineup with proper end state.
    /// </summary>
    [Fact]
    public void N436MS_LineUp28R_CompletesWithOnCenterlineAlignedStop()
    {
        AssertLineUpCompletesCleanly(callsign: "N436MS", ctoSecond: 47, budgetSeconds: 60);
    }

    /// <summary>
    /// Issue #203: lining up from taxiway B (a ~106° turn onto 28R) the aircraft
    /// must turn toward the centerline, not drive forward along its hold-short
    /// heading to the runway-start corner and double back. Asserts the aircraft
    /// never backs behind the 28R threshold during the lineup maneuver — the
    /// along-track distance from the threshold stays positive (forward of the
    /// runway start). Before the fix N436MS reaches ≈ −10 ft (behind the
    /// threshold, at "the node at the very start of 28R"); after the fix it
    /// crosses toward the centerline at the hold-short's perpendicular foot
    /// (≈ +40 ft, well forward of the start).
    /// </summary>
    [Fact]
    public void N436MS_LineUp28R_DoesNotBackToRunwayStartCorner()
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

        const int ctoSecond = 47;
        const int budgetSeconds = 60;
        engine.Replay(recording, ctoSecond - 1);

        var ac = engine.FindAircraft("N436MS");
        Assert.NotNull(ac);

        bool enteredLineUp = false;
        double minAlongFt = double.PositiveInfinity;
        double minAlongHdg = double.NaN;

        int budgetSubTicks = budgetSeconds * 4;
        for (int sub = 0; sub < budgetSubTicks; sub++)
        {
            engine.ReplayOneSubTick();
            ac = engine.FindAircraft("N436MS");
            Assert.NotNull(ac);

            var phase = ac.Phases?.CurrentPhase;
            if (phase is LineUpPhase)
            {
                enteredLineUp = true;
                double alongFt =
                    GeoMath.AlongTrackDistanceNm(ac.Position.Lat, ac.Position.Lon, rwyThreshLat, rwyThreshLon, rwyHdg) * GeoMath.FeetPerNm;
                if (alongFt < minAlongFt)
                {
                    minAlongFt = alongFt;
                    minAlongHdg = ac.TrueHeading.Degrees;
                }
            }
            else if (enteredLineUp)
            {
                // Exited LineUpPhase — maneuver complete.
                break;
            }
        }

        Assert.True(enteredLineUp, "N436MS never entered LineUpPhase");
        output.WriteLine($"N436MS min along-track from 28R threshold during lineup: {minAlongFt:F1}ft (hdg {minAlongHdg:F0}°)");

        // The aircraft must stay forward of the 28R threshold throughout the
        // lineup. A small positive floor cleanly separates the corner-cutting
        // bug (≈ −10 ft) from the correct cross-toward-centerline path (≈ +40 ft).
        Assert.True(
            minAlongFt > 5.0,
            $"N436MS backed to within {minAlongFt:F1}ft of (or behind) the 28R threshold during lineup — "
                + "drove to the runway-start corner instead of turning toward the centerline"
        );
    }
}
