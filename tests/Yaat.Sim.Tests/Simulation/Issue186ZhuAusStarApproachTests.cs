using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E verification tests for GitHub issue #186 ("Issues With STARS", ZHU AUS S3-L3 AUS_F_APP).
///
/// Four reported problems on arrivals into KAUS (ILS 18L/18R):
///   #1/#2  Aircraft don't follow the STAR / descend-via crossing SPEEDs — SKW5288 has
///          `CFIX VADRR 80; CFIX MGTEC 50 220; CFIX JEDYE 40 210; AT JEDYE CAPP` and (old code)
///          held 250kt down the descent instead of crossing MGTEC at 220 / JEDYE at 210.
///   #3     SWA387 told JFAC/JLOC (not CAPP) "descends on their own"; a later CAPP printed
///          "pattern to RWY 18L cancelled by CAPP".
///   #4     SWA8623 given a fine intercept heading (140 to join the 180-ish I18R final) then JLOC,
///          "blew through" and reported "Unable, passing through localizer — I18R".
///
/// Design ruling (issue author): JLOC/JFAC means the pilot turns to join the loc/FAC of their own
/// accord — they should capture even from a steep/late vector. Only a PTAC asks the controller to
/// vector within a limited intercept. So a JFAC/JLOC bust-through is the bug, not a bad vector.
///
/// Strategy: full replay from t=0 for the no-intervention SKW5288 descend-via case; hybrid replay
/// (restore the snapshot just before each JFAC, then replay forward) for the intercept cases so the
/// aircraft's exact recorded geometry is reproduced and only the post-JFAC logic is exercised by
/// current code.
/// </summary>
public class Issue186ZhuAusStarApproachTests
{
    private const string RecordingPath = "TestData/issue186-zhu-aus-star-approach-recording.zip";

    private readonly ITestOutputHelper _output;

    public Issue186ZhuAusStarApproachTests(ITestOutputHelper output)
    {
        _output = output;
        // Pin navdata singletons before any test body runs (static-singleton race).
        TestVnasData.EnsureInitialized();
    }

    private static SessionRecording? LoadRecording() => RecordingLoader.Load(RecordingPath);

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        SimLogBuilder
            .CreateForTest(_output)
            .EnableCategory("InterceptCoursePhase", LogLevel.Debug)
            .EnableCategory("FinalApproachPhase", LogLevel.Debug)
            .InitializeSimLog();

        return new SimulationEngine(new TestAirportGroundData());
    }

    // ---------------------------------------------------------------------------------------------
    // #1/#2 — SKW5288 descend-via: hold the CFIX crossing SPEEDS, fire AT JEDYE CAPP, fly the approach.
    // No user commands were ever issued to SKW5288, so this is the clean automatic-arrival test.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Skw5288_DescendVia_HoldsCrossingSpeeds_FiresAtJedyeCapp()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            _output.WriteLine("Recording or NavData not available, skipping");
            return;
        }

        engine.Replay(recording, 0);

        double iasAtMgtec = -1;
        double iasAtJedye = -1;
        double maxIasDuringDescent = 0;
        bool cappFired = false;
        bool reachedFinal = false;
        var prevRoute = RouteNames(engine.FindAircraft("SKW5288"));

        for (int t = 1; t <= 640; t++)
        {
            engine.ReplayOneSecond();
            var ac = engine.FindAircraft("SKW5288");
            if (ac is null)
            {
                break;
            }

            var route = RouteNames(ac);
            if (ac.Altitude > 4500)
            {
                maxIasDuringDescent = Math.Max(maxIasDuringDescent, ac.IndicatedAirspeed);
            }

            if (prevRoute.Contains("MGTEC") && !route.Contains("MGTEC") && iasAtMgtec < 0)
            {
                iasAtMgtec = ac.IndicatedAirspeed;
                _output.WriteLine($"t={t}: sequenced MGTEC at ias={ac.IndicatedAirspeed:F0} alt={ac.Altitude:F0} ceiling={ac.Targets.SpeedCeiling}");
            }

            if (prevRoute.Contains("JEDYE") && !route.Contains("JEDYE") && iasAtJedye < 0)
            {
                iasAtJedye = ac.IndicatedAirspeed;
                _output.WriteLine($"t={t}: sequenced JEDYE at ias={ac.IndicatedAirspeed:F0} alt={ac.Altitude:F0} ceiling={ac.Targets.SpeedCeiling}");
            }

            if (ac.Phases?.ActiveApproach is not null)
            {
                cappFired = true;
            }

            if (ac.Phases?.Phases.OfType<FinalApproachPhase>().Any(p => p.Status == PhaseStatus.Active) == true)
            {
                reachedFinal = true;
                _output.WriteLine($"t={t}: reached FinalApproach, approach={ac.Phases?.ActiveApproach?.ApproachId}");
                break;
            }

            prevRoute = route;
        }

        _output.WriteLine(
            $"iasAtMgtec={iasAtMgtec:F0} iasAtJedye={iasAtJedye:F0} maxIasAbove4500={maxIasDuringDescent:F0} cappFired={cappFired} reachedFinal={reachedFinal}"
        );

        Assert.True(iasAtMgtec >= 0, "Never sequenced MGTEC");
        Assert.True(iasAtJedye >= 0, "Never sequenced JEDYE");
        // The old code held 250 the whole way; the fix slows to the published crossing speeds.
        Assert.True(iasAtMgtec <= 226, $"SKW5288 should cross MGTEC at ~220kt, but was {iasAtMgtec:F0}kt");
        Assert.True(iasAtJedye <= 216, $"SKW5288 should cross JEDYE at ~210kt, but was {iasAtJedye:F0}kt");
        Assert.True(cappFired, "AT JEDYE CAPP never fired (no approach clearance armed)");
        Assert.True(reachedFinal, "SKW5288 never reached FinalApproach on the I18L approach");
    }

    // ---------------------------------------------------------------------------------------------
    // #4 (headline) — SWA8623: vectored TR 090 / TR 140 then JFAC I18R (~40 deg intercept, ~3nm W).
    // Per the ruling it should turn to join I18R and capture, not blow through.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Swa8623_JfacI18R_JoinsLocalizer_DoesNotBlowThrough()
    {
        RunJoinTest("SWA8623", snapshotBeforeJfac: 731, jfacWindowEnd: 738, watchSeconds: 160);
    }

    // ---------------------------------------------------------------------------------------------
    // #3 — SWA387: 2nd JFAC I18L @463 is a clean ~24 deg intercept. After JFAC it must HOLD its
    // assigned 3000ft (lateral-only, "descends on their own" is the bug) and capture the localizer.
    // CAPP @586 upgrades the join in place (authorizes the descent) — it must NOT emit any
    // "cancelled by CAPP" warning (the reported "pattern to RWY 18L cancelled by CAPP"), and the
    // aircraft then descends on the glideslope.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Swa387_Jfac_HoldsAltitude_ThenCappUpgradesInPlace_NoCancelWarning()
    {
        using var archive = RecordingLoader.OpenArchive(RecordingPath);
        var engine = BuildEngine();
        if (archive is null || engine is null)
        {
            _output.WriteLine("Recording or NavData not available, skipping");
            return;
        }

        var recording = archive.ToBaseSessionRecording();
        engine.Replay(recording, 0);

        var snap = archive.ReadSnapshotAt(461);
        if (snap is null)
        {
            _output.WriteLine("No snapshot near t=461, skipping");
            return;
        }

        var warnings = new List<string>();
        engine.WarningEmitted += (cs, w) =>
        {
            if (cs == "SWA387")
            {
                warnings.Add(w);
            }
        };

        engine.RestoreFromSnapshot(snap.State);
        int start = (int)snap.ElapsedSeconds;

        // Apply JFAC I18L @463.
        engine.ReplayRange(start, 470, recording.Actions);
        var ac = engine.FindAircraft("SWA387");
        Assert.NotNull(ac);
        double altAtJfac = ac.Altitude;
        _output.WriteLine($"after JFAC: alt={altAtJfac:F0} phases={FormatPhases(ac)} lateralOnly={ac.Phases?.ActiveApproach?.LateralInterceptOnly}");

        // Fly the lateral intercept up to just before CAPP. Must capture and hold ~3000.
        engine.ReplayRange(470, 584, recording.Actions);
        ac = engine.FindAircraft("SWA387");
        Assert.NotNull(ac);
        _output.WriteLine(
            $"before CAPP @584: alt={ac.Altitude:F0} ias={ac.IndicatedAirspeed:F0} phases={FormatPhases(ac)} xte={CrossTrackNm(ac):F2} hdgErr={HeadingErrToFacDeg(ac):F0}"
        );

        Assert.NotNull(ac.Phases?.ActiveApproach);
        Assert.True(ac.Altitude > 2900, $"SWA387 on a lateral JFAC must HOLD ~3000ft until CAPP, but descended to {ac.Altitude:F0}ft");
        bool capturedBeforeCapp = ac.Phases!.Phases.OfType<FinalApproachPhase>().Any(p => p.Status == PhaseStatus.Active);

        // Apply CAPP @586, then watch it descend on the glideslope.
        engine.ReplayRange(584, 592, recording.Actions);
        ac = engine.FindAircraft("SWA387");
        Assert.NotNull(ac);
        _output.WriteLine(
            $"after CAPP: alt={ac.Altitude:F0} lateralOnly={ac.Phases?.ActiveApproach?.LateralInterceptOnly} phases={FormatPhases(ac)}"
        );

        double altAfterCapp = ac.Altitude;
        bool descended = false;
        for (int t = 1; t <= 90; t++)
        {
            engine.TickOneSecond();
            ac = engine.FindAircraft("SWA387");
            if (ac is null)
            {
                break;
            }
            if (ac.Altitude < altAfterCapp - 150)
            {
                descended = true;
                break;
            }
        }

        _output.WriteLine($"capturedBeforeCapp={capturedBeforeCapp} descendedAfterCapp={descended} warnings=[{string.Join(" | ", warnings)}]");

        Assert.DoesNotContain(warnings, w => w.Contains("cancelled by CAPP", StringComparison.OrdinalIgnoreCase));
        Assert.True(capturedBeforeCapp, "SWA387 should capture the I18L localizer on the clean ~24 deg JFAC intercept");
        Assert.True(descended, "SWA387 should descend on the glideslope after CAPP upgrades the join in place");
    }

    // ---------------------------------------------------------------------------------------------
    // #3 / ruling — SWA387 1st JFAC I18L @424 is a steep (~90 deg, ~0.7nm) vector. Per the ruling the
    // pilot should still turn to join I18L of their own accord rather than reporting "unable".
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Swa387_SteepJfac_TurnsToJoin_DoesNotBlowThrough()
    {
        RunJoinTest("SWA387", snapshotBeforeJfac: 422, jfacWindowEnd: 430, watchSeconds: 150);
    }

    // ---------------------------------------------------------------------------------------------
    // #1 — SWA1743: JFAC I18L @604 (steep ~90 deg, ~2.6nm E). Per the ruling, turn to join.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Swa1743_Jfac_TurnsToJoin_DoesNotBlowThrough()
    {
        RunJoinTest("SWA1743", snapshotBeforeJfac: 603, jfacWindowEnd: 610, watchSeconds: 150);
    }

    // ---------------------------------------------------------------------------------------------
    // Edge case (aviation-review): a steep JFAC now captures and S-turns onto the localizer; with
    // the old bust behavior, CAPP-after-steep-JFAC was unreachable. If CAPP is issued while the
    // aircraft is still S-turning (1.18nm / 34deg off the localizer here), it must HOLD the assigned
    // altitude through the turn and not start down while laterally far off (7110.65 §5-9-4 / AIM
    // §5-4-7) — and must not bust. It then descends on the published approach once near the
    // centerline. Uses a synthetic CAPP (not in the recording) on SWA1743's steep I18L join, ticking
    // with TickOneSecond so the recording's later re-vectors don't interfere. (Confirms the
    // glideslope-established gate stays scoped to PTACF: a relaxed JLOC join sets
    // ForcedInterceptCapture=false, so CAPP's in-place upgrade holds altitude until established even
    // though the recorded capture angle is steep — the gate is not bypassed.)
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Swa1743_SteepJfac_CappWhileSturning_HoldsAltitudeUntilEstablished()
    {
        using var archive = RecordingLoader.OpenArchive(RecordingPath);
        var engine = BuildEngine();
        if (archive is null || engine is null)
        {
            _output.WriteLine("Recording or NavData not available, skipping");
            return;
        }

        var recording = archive.ToBaseSessionRecording();
        engine.Replay(recording, 0);
        var snap = archive.ReadSnapshotAt(603);
        if (snap is null)
        {
            _output.WriteLine("No snapshot near t=603, skipping");
            return;
        }

        engine.RestoreFromSnapshot(snap.State);
        engine.ReplayRange((int)snap.ElapsedSeconds, 610, recording.Actions); // applies JFAC I18L @604

        // Tick until the steep join captures into FinalApproach (still off the localizer).
        var ac = engine.FindAircraft("SWA1743");
        Assert.NotNull(ac);
        bool captured = false;
        for (int t = 1; t <= 60; t++)
        {
            engine.TickOneSecond();
            ac = engine.FindAircraft("SWA1743");
            Assert.NotNull(ac);
            if (ac.Phases?.Phases.OfType<FinalApproachPhase>().Any(p => p.Status == PhaseStatus.Active) == true)
            {
                captured = true;
                break;
            }
            if (ac.Phases?.ActiveApproach is null)
            {
                Assert.Fail("SWA1743 busted on the steep JFAC join before capture");
            }
        }
        Assert.True(captured, "SWA1743 never captured the steep JFAC join");

        double cappAlt = ac.Altitude;
        double xteAtCapp = CrossTrackNm(ac);
        _output.WriteLine($"CAPP issued: alt={cappAlt:F0} xte={xteAtCapp:F2} facErr={HeadingErrToFacDeg(ac):F0} phases={FormatPhases(ac)}");

        var result = engine.SendCommand("SWA1743", "CAPP");
        Assert.True(result.Success, $"CAPP failed: {result.Message}");

        bool busted = false;
        bool descended = false;
        bool prematureDescent = false;
        for (int t = 1; t <= 180; t++)
        {
            engine.TickOneSecond();
            ac = engine.FindAircraft("SWA1743");
            if (ac is null)
            {
                break;
            }
            if (ac.Phases?.ActiveApproach is null)
            {
                busted = true;
                break;
            }
            // The first meaningful descent must happen only once the aircraft is near the centerline
            // and roughly aligned — never at the 1.18nm / 34deg geometry it had when CAPP was issued.
            // (The published-approach descent that follows is gated by the approach fixes, and the
            // final glideslope by the 5deg/0.15nm establishment gate; this guards the dangerous
            // "descend while still well off the localizer" case.)
            if (!descended && ac.Altitude < cappAlt - 200)
            {
                descended = true;
                double xte = CrossTrackNm(ac);
                double facErr = HeadingErrToFacDeg(ac);
                _output.WriteLine(
                    $"first descent at t+{t}: alt={ac.Altitude:F0} xte={xte:F2} facErr={facErr:F0} captureAngle={ac.Phases?.ActiveApproach?.InterceptCaptureAngleDeg:F0} phases={FormatPhases(ac)}"
                );
                if (!(xte < 0.6) || !(facErr < 20))
                {
                    prematureDescent = true;
                }
            }
        }

        _output.WriteLine($"busted={busted} descended={descended} prematureDescent={prematureDescent}");
        Assert.False(busted, "SWA1743 must not bust when CAPP follows a steep JFAC mid-S-turn");
        Assert.False(prematureDescent, "SWA1743 descended before being laterally established on the localizer");
        Assert.True(descended, "SWA1743 should eventually descend on the glideslope once established");
    }

    // ---------------------------------------------------------------------------------------------
    // #3 (warning) — DAL5534: JFAC I18R @413 then CAPP @430. CAPP upgrades the lateral join in place
    // (authorizes the descent) — it must NOT emit any "cancelled by CAPP" warning, must stay on the
    // approach (no bust), and descend.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Dal5534_JfacThenCapp_UpgradesInPlace_NoCancelWarning()
    {
        using var archive = RecordingLoader.OpenArchive(RecordingPath);
        var engine = BuildEngine();
        if (archive is null || engine is null)
        {
            _output.WriteLine("Recording or NavData not available, skipping");
            return;
        }

        var recording = archive.ToBaseSessionRecording();
        engine.Replay(recording, 0);

        var snap = archive.ReadSnapshotAt(406);
        if (snap is null)
        {
            _output.WriteLine("No snapshot near t=406, skipping");
            return;
        }

        var warnings = new List<string>();
        engine.WarningEmitted += (cs, w) =>
        {
            if (cs == "DAL5534")
            {
                warnings.Add(w);
            }
        };

        engine.RestoreFromSnapshot(snap.State);
        int start = (int)snap.ElapsedSeconds;

        // Apply JFAC I18R @413 and CAPP @430.
        engine.ReplayRange(start, 435, recording.Actions);
        var ac = engine.FindAircraft("DAL5534");
        Assert.NotNull(ac);
        double altAfterCapp = ac.Altitude;
        _output.WriteLine(
            $"DAL5534 after JFAC+CAPP @435: alt={altAfterCapp:F0} phases={FormatPhases(ac)} approach={ac.Phases?.ActiveApproach?.ApproachId} warnings=[{string.Join(" | ", warnings)}]"
        );

        bool descended = false;
        bool busted = false;
        for (int t = 1; t <= 150; t++)
        {
            engine.TickOneSecond();
            ac = engine.FindAircraft("DAL5534");
            if (ac is null)
            {
                break;
            }
            if (ac.Phases?.ActiveApproach is null)
            {
                busted = true;
                _output.WriteLine($"t+{t}: approach cleared (bust) phases={FormatPhases(ac)} notifs={FormatNotifications(ac)}");
                break;
            }
            if (ac.Altitude < altAfterCapp - 300)
            {
                descended = true;
                break;
            }
        }

        _output.WriteLine($"busted={busted} descended={descended} warnings=[{string.Join(" | ", warnings)}]");

        Assert.DoesNotContain(warnings, w => w.Contains("cancelled by CAPP", StringComparison.OrdinalIgnoreCase));
        Assert.False(busted, "DAL5534 should capture I18R and stay on the approach through CAPP");
        Assert.True(descended, "DAL5534 should descend after CAPP");
    }

    // ---------------------------------------------------------------------------------------------
    // Shared hybrid "JFAC should turn to join the localizer" driver.
    // Restores the snapshot just before the JFAC, replays the JFAC window, then ticks forward
    // watching for capture (FinalApproach active) vs bust-through (approach cleared / "passing
    // through localizer").
    // ---------------------------------------------------------------------------------------------

    private void RunJoinTest(string callsign, int snapshotBeforeJfac, int jfacWindowEnd, int watchSeconds)
    {
        using var archive = RecordingLoader.OpenArchive(RecordingPath);
        var engine = BuildEngine();
        if (archive is null || engine is null)
        {
            _output.WriteLine("Recording or NavData not available, skipping");
            return;
        }

        var recording = archive.ToBaseSessionRecording();
        engine.Replay(recording, 0);

        var snap = archive.ReadSnapshotAt(snapshotBeforeJfac);
        if (snap is null)
        {
            _output.WriteLine($"No snapshot near t={snapshotBeforeJfac}, skipping");
            return;
        }

        engine.RestoreFromSnapshot(snap.State);
        int start = (int)snap.ElapsedSeconds;
        engine.ReplayRange(start, jfacWindowEnd, recording.Actions);

        var ac = engine.FindAircraft(callsign);
        Assert.NotNull(ac);
        var intercept = ac.Phases?.Phases.OfType<InterceptCoursePhase>().FirstOrDefault();
        _output.WriteLine(
            $"{callsign} after JFAC: hdg={ac.TrueHeading.Degrees:F0} ias={ac.IndicatedAirspeed:F0} alt={ac.Altitude:F0} approach={ac.Phases?.ActiveApproach?.ApproachId} interceptPhase={(intercept is not null)} xte={CrossTrackNm(ac):F2} facErr={HeadingErrToFacDeg(ac):F0}"
        );
        Assert.NotNull(intercept);

        bool captured = false;
        bool busted = false;
        string? bustNotif = null;
        for (int t = 1; t <= watchSeconds; t++)
        {
            engine.TickOneSecond();
            ac = engine.FindAircraft(callsign);
            if (ac is null)
            {
                break;
            }

            foreach (var n in ac.PendingNotifications)
            {
                if (n.Contains("passing through localizer", StringComparison.OrdinalIgnoreCase))
                {
                    bustNotif = n;
                }
            }

            if (t % 20 == 0)
            {
                _output.WriteLine(
                    $"  t+{t}: hdg={ac.TrueHeading.Degrees:F0} alt={ac.Altitude:F0} xte={CrossTrackNm(ac):F2} facErr={HeadingErrToFacDeg(ac):F0} phases={FormatPhases(ac)}"
                );
            }

            if (ac.Phases?.Phases.OfType<FinalApproachPhase>().Any(p => p.Status == PhaseStatus.Active) == true)
            {
                captured = true;
                _output.WriteLine($"  >>> {callsign} captured at t+{t} (xte={CrossTrackNm(ac):F2}nm facErr={HeadingErrToFacDeg(ac):F0}deg) <<<");
                break;
            }

            if (ac.Phases?.ActiveApproach is null)
            {
                busted = true;
                _output.WriteLine($"  >>> {callsign} approach cleared (bust-through) at t+{t}: notif={bustNotif ?? FormatNotifications(ac)} <<<");
                break;
            }
        }

        Assert.True(bustNotif is null, $"{callsign} reported '{bustNotif}' — JFAC/JLOC should turn to join, not blow through");
        Assert.False(busted, $"{callsign} busted through instead of joining the localizer");
        Assert.True(captured, $"{callsign} did not capture the localizer within {watchSeconds}s of JFAC");
    }

    // --- helpers ---------------------------------------------------------------------------------

    private static HashSet<string> RouteNames(AircraftState? ac) =>
        ac is null ? new HashSet<string>() : ac.Targets.NavigationRoute.Select(n => n.Name).ToHashSet();

    private static double CrossTrackNm(AircraftState ac)
    {
        var clearance = ac.Phases?.ActiveApproach;
        var runway = ac.Phases?.AssignedRunway;
        if (clearance is null || runway is null)
        {
            return double.NaN;
        }

        return Math.Abs(
            GeoMath.SignedCrossTrackDistanceNm(
                ac.Position,
                new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude),
                clearance.FinalApproachCourse
            )
        );
    }

    private static double HeadingErrToFacDeg(AircraftState ac)
    {
        var clearance = ac.Phases?.ActiveApproach;
        if (clearance is null)
        {
            return double.NaN;
        }

        double diff = ((ac.TrueHeading.Degrees - clearance.FinalApproachCourse.Degrees + 540) % 360) - 180;
        return Math.Abs(diff);
    }

    private static string FormatPhases(AircraftState ac) =>
        ac.Phases is null ? "null" : string.Join(", ", ac.Phases.Phases.Select(p => $"{p.Name}({p.Status})"));

    private static string FormatNotifications(AircraftState ac) =>
        ac.PendingNotifications.Count == 0 ? "none" : string.Join("; ", ac.PendingNotifications);
}
