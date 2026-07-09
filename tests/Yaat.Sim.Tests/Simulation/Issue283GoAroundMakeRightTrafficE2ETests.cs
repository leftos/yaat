using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E test for GitHub issue #283: MRT does not seem to work after a go-around.
///
/// Recording: "S2-OAK-P | S2 Rating Practical Exam" (ZOA). N131WF is a VFR P28A that entered
/// the right downwind for OAK 28R (<c>ERD 28R</c> at t=2328), was cleared to land (<c>CLAND</c>
/// at t=2444), then given <c>GA</c> at t=2529 and <c>MRT</c> at t=2533.
///
/// Observed bug: the aircraft climbed straight ahead on runway heading for 150 seconds and never
/// turned crosswind. <c>CLAND</c> nulls <c>PhaseList.TrafficDirection</c> (full-stop intent), and
/// unlike the automatic go-around path (<see cref="GoAroundHelper.Trigger"/>) the <c>GA</c> command
/// handler applied no VFR pattern default — so the <see cref="GoAroundPhase"/> was built with
/// <c>ReenterPattern=false</c> and a null <c>TargetAltitude</c>, self-clearing at 2000 ft AGL
/// instead of pattern altitude − 300 ft. A P28A climbing 700 fpm from 121 ft needs 2 m 42 s to
/// get there; the controller re-issued <c>ERD 28R</c> at t=2680, 9 seconds before it would have
/// completed.
///
/// Expected: <c>GA</c> on a VFR aircraft re-enters the traffic pattern, climbing to pattern
/// altitude − 300 ft (AIM 4-3-2 — the same gate <see cref="UpwindPhase"/> uses to release the
/// crosswind turn). For OAK 28R and a piston that is 9 + 1000 − 300 = 709 ft.
///
/// Replay strategy: hybrid. The fix changes phase behavior at the moment of the <c>GA</c>, so the
/// snapshot at t=2520 (N131WF on final) pins the setup and <c>GA</c>/<c>MRT</c> are replayed with
/// current code. Ticking stops at t=2679 — the controller's corrective <c>ERD 28R</c> at t=2680
/// would mask the automatic behavior under test.
/// </summary>
public class Issue283GoAroundMakeRightTrafficE2ETests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/issue283-ga-mrt-straight-out-recording.zip";
    private const string Callsign = "N131WF";

    /// <summary>Snapshot restore point: N131WF is on final, 9 seconds before the GA.</summary>
    private const int RestoreTime = 2520;

    /// <summary>Just past the recorded MRT at t=2533, so both commands have been applied.</summary>
    private const int AfterMrtTime = 2536;

    /// <summary>Last second before the controller's corrective ERD 28R at t=2680.</summary>
    private const int LastFaithfulTime = 2679;

    /// <summary>OAK 28R threshold elevation (9 ft) + piston TPA (1000 ft AGL) − 300 ft (AIM 4-3-2).</summary>
    private const int ExpectedClimbOutAltitude = 709;

    /// <summary>True heading of OAK 28R.</summary>
    private const double RunwayTrueHeading = 292.256;

    /// <summary>Signed difference between two headings, normalized to (-180, 180]. Positive = right of reference.</summary>
    private static double SignedHeadingDiff(double heading, double reference)
    {
        double diff = (heading - reference) % 360.0;
        if (diff > 180.0)
        {
            diff -= 360.0;
        }
        if (diff <= -180.0)
        {
            diff += 360.0;
        }
        return diff;
    }

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        SimLogBuilder
            .CreateForTest(output)
            .EnableCategory("GoAroundPhase", LogLevel.Debug)
            .EnableCategory("UpwindPhase", LogLevel.Debug)
            .InitializeSimLog();

        return new SimulationEngine(groundData);
    }

    [Fact]
    public void N131WF_GoAroundThenMakeRightTraffic_ClimbsToPatternAltitudeAndTurnsCrosswind()
    {
        var archive = RecordingLoader.OpenArchive(RecordingPath);
        if (archive is null)
        {
            return;
        }

        using (archive)
        {
            var recording = archive.ToBaseSessionRecording();
            var engine = BuildEngine();
            if (engine is null)
            {
                return;
            }

            engine.Replay(recording, 0);

            var snapshot = archive.ReadSnapshotAt(RestoreTime);
            if (snapshot is null)
            {
                return;
            }
            engine.RestoreFromSnapshot(snapshot.State);

            // Sanity: N131WF is on final for 28R, and CLAND has already wiped the pattern direction.
            var pre = engine.FindAircraft(Callsign);
            Assert.NotNull(pre);
            Assert.IsType<FinalApproachPhase>(pre.Phases?.CurrentPhase);
            Assert.Equal("28R", pre.Phases?.AssignedRunway?.Designator);
            Assert.Null(pre.Phases?.TrafficDirection);

            // Replay the recorded GA (t=2529) and MRT (t=2533) with current code.
            engine.FastForwardTo(AfterMrtTime, recording.Actions);

            var ac = engine.FindAircraft(Callsign);
            Assert.NotNull(ac);
            var goAround = Assert.IsType<GoAroundPhase>(ac.Phases?.CurrentPhase);
            output.WriteLine(
                $"t={AfterMrtTime}: GoAroundPhase ReenterPattern={goAround.ReenterPattern} "
                    + $"TargetAltitude={goAround.TargetAltitude?.ToString() ?? "none"} alt={ac.Altitude:F0}"
            );

            Assert.True(goAround.ReenterPattern, "A VFR go-around followed by MRT must re-enter the traffic pattern");
            Assert.Equal(ExpectedClimbOutAltitude, goAround.TargetAltitude);

            // Fly the climb-out. The go-around must complete and hand off to the pattern well
            // before the controller gave up at t=2680.
            int? leftGoAroundAt = null;
            int? crosswindTurnStartedAt = null;
            for (int t = AfterMrtTime + 1; t <= LastFaithfulTime; t++)
            {
                engine.ReplayOneSecond();
                ac = engine.FindAircraft(Callsign);
                if (ac is null)
                {
                    break;
                }

                string phaseName = ac.Phases?.CurrentPhase?.GetType().Name ?? "(none)";
                if (leftGoAroundAt is null && ac.Phases?.CurrentPhase is not GoAroundPhase)
                {
                    leftGoAroundAt = t;
                    output.WriteLine($"t={t}: left GoAroundPhase at alt={ac.Altitude:F0} -> {phaseName}");
                }

                double offRunwayHeading = SignedHeadingDiff(ac.TrueHeading.Degrees, RunwayTrueHeading);
                if (crosswindTurnStartedAt is null && offRunwayHeading > 20.0)
                {
                    crosswindTurnStartedAt = t;
                    output.WriteLine($"t={t}: turning right, {offRunwayHeading:F0} deg off runway heading, alt={ac.Altitude:F0}");
                }

                if (t % 15 == 0)
                {
                    output.WriteLine($"t={t} alt={ac.Altitude:F0} hdg={ac.TrueHeading.Degrees:F0} phase={phaseName}");
                }
            }

            ac = engine.FindAircraft(Callsign);
            Assert.NotNull(ac);

            Assert.True(
                leftGoAroundAt is not null,
                $"N131WF was still in GoAroundPhase at t={LastFaithfulTime} (alt {ac.Altitude:F0} ft) — it flew straight out instead of re-entering the pattern."
            );
            Assert.True(
                leftGoAroundAt <= 2600,
                $"Go-around completed at t={leftGoAroundAt}; a 700 fpm climb from 121 ft to {ExpectedClimbOutAltitude} ft should finish by t=2600."
            );
            Assert.True(
                crosswindTurnStartedAt is not null,
                $"N131WF never turned crosswind by t={LastFaithfulTime} (heading {ac.TrueHeading.Degrees:F0}) — MRT had no effect."
            );

            // Right traffic: the turn must be to the right of the runway heading, not left.
            double finalOffHeading = SignedHeadingDiff(ac.TrueHeading.Degrees, RunwayTrueHeading);
            Assert.True(finalOffHeading > 0, $"N131WF turned left ({finalOffHeading:F0} deg off runway heading) after MRT — expected a right turn.");
        }
    }
}
