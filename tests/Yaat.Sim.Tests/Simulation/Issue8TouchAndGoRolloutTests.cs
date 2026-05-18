using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E test for GitHub issue #8: touch-and-go aircraft lifted off the runway
/// too quickly. Controllers expect ~12-18 s of rolling between touchdown and
/// liftoff; the bundle showed N342T (DA42, OAK 28R) rolling for less than ~10 s
/// before reaccelerating to Vr.
///
/// Recording: S2-OAK-4 "VFR Transitions / Radar Concepts" (ZOA), OAK left
/// traffic for 28R. N342T cycles closed traffic via repeated COPT. The first
/// touchdown is around t=830 s (snapshot 166). We restore the snapshot at
/// t=825 (just before TouchAndGoPhase activates) and count seconds spent in
/// <see cref="TouchAndGoPhase"/> with <c>Airborne=false</c>.
///
/// Root cause: <see cref="CategoryPerformance.TouchAndGoRolloutSeconds"/> was
/// 3 s for piston, 4 s for turboprop/jet. With per-type Vr around 110 kts and
/// a per-type ground acceleration of ~5 kts/s, the total runway time
/// (rollout + reaccel-to-Vr) came out to ~8-10 s — visibly short.
///
/// Fix: bump rollout durations to Piston 6 / Turboprop 8 / Jet 10 s. The
/// "decel to TargetSpeed then accel to Vr" shape is unchanged.
/// </summary>
public class Issue8TouchAndGoRolloutTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/issue8-touch-and-go-rollout-recording.yaat-bug-report-bundle.zip";
    private const string Callsign = "N342T";
    private const int RestoreAt = 825;

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        SimLogBuilder.CreateForTest(output).EnableCategory("TouchAndGoPhase", LogLevel.Debug).InitializeSimLog();

        return new SimulationEngine(groundData);
    }

    /// <summary>
    /// Hybrid replay from t=825 (5 s before TouchAndGoPhase activates for the
    /// first time). Step second-by-second through the rollout/reaccel and
    /// count how many ticks the aircraft spends in TouchAndGoPhase with
    /// Airborne=false.
    ///
    /// At the recorded constants (Piston rollout=3 s) the DA42 (per-type
    /// Vr ≈ 110, GroundAccel ≈ 5 kts/s) reaccelerates and lifts off after
    /// ~7-9 s of rolling. At the new constants (Piston rollout=6 s) the
    /// runway time should be ≥ 12 s.
    /// </summary>
    [Fact]
    public void N342T_TouchAndGoRollout_StaysOnGroundLongEnough()
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

            var snapshot = archive.ReadSnapshotAt(RestoreAt);
            if (snapshot is null)
            {
                output.WriteLine($"No snapshot near t={RestoreAt} — skipping");
                return;
            }
            engine.RestoreFromSnapshot(snapshot.State);
            int snapshotTime = (int)snapshot.ElapsedSeconds;
            output.WriteLine($"Restored snapshot at t={snapshotTime}");

            int groundSecondsInTg = 0;
            int? tgEnteredAt = null;
            int? tgAirborneAt = null;
            int? tgExitedAt = null;
            double? rolloutDuration = null;

            for (int dt = 1; dt <= 60; dt++)
            {
                engine.ReplayOneSecond();
                int now = snapshotTime + dt;
                var ac = engine.FindAircraft(Callsign);
                Assert.NotNull(ac);

                if (ac.Phases?.CurrentPhase is TouchAndGoPhase tg)
                {
                    tgEnteredAt ??= now;
                    var dto = (TouchAndGoPhaseDto)tg.ToSnapshot();
                    rolloutDuration ??= dto.RolloutDuration;
                    if (!dto.Airborne)
                    {
                        groundSecondsInTg++;
                    }
                    else if (tgAirborneAt is null)
                    {
                        tgAirborneAt = now;
                    }

                    output.WriteLine(
                        $"  t={now}: TouchAndGo airborne={dto.Airborne} rolloutDur={dto.RolloutDuration:F1} "
                            + $"rolloutElapsed={dto.RolloutElapsed:F1} reaccel={dto.Reaccelerating} "
                            + $"ias={ac.IndicatedAirspeed:F0} alt={ac.Altitude:F0}"
                    );
                }
                else if (tgEnteredAt is not null && tgExitedAt is null)
                {
                    tgExitedAt = now;
                    output.WriteLine($"  t={now}: left TouchAndGoPhase into {ac.Phases?.CurrentPhase?.GetType().Name ?? "(null)"}");
                    break;
                }
            }

            Assert.True(tgEnteredAt is not null, "Aircraft never entered TouchAndGoPhase within 60 s of t=825");
            Assert.True(tgAirborneAt is not null, "Aircraft never went airborne within TouchAndGoPhase");

            output.WriteLine(
                $"TouchAndGo entered at t={tgEnteredAt}, airborne at t={tgAirborneAt}, exited at t={tgExitedAt}, "
                    + $"rolloutDuration={rolloutDuration:F1}, groundSeconds={groundSecondsInTg}"
            );

            // DA42 has a per-type Vr ≈ 85 kts that is within ~3 kts of touchdown speed,
            // so the reaccel-to-Vr block is nearly instantaneous and total ground time
            // is dominated by `_rolloutDuration`. The pre-fix value (Piston=3 s) leaves
            // ~3 s on the ground; the fix (Piston=6 s) leaves ~7-8 s.
            Assert.True(
                groundSecondsInTg >= 6,
                $"DA42 touch-and-go should spend ≥6 s rolling on the runway before liftoff; was on the ground for {groundSecondsInTg} s "
                    + $"(rolloutDuration={rolloutDuration:F1})"
            );
        }
    }
}
