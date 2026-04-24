using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E test for the follow-adjusted-speed runaway bug.
///
/// Recording: S2-OAK-3 (1) "VFR Sequencing" (ZOA). Three-way VFR follow chain:
/// N9225L (lead) ← N436MS ← N346G, all C172s. During the original run, each
/// follower's <c>TargetSpeed</c> compounded tick-over-tick because the pattern
/// phases passed <c>ctx.Targets.TargetSpeed</c> (the previous tick's adjusted
/// value) as <c>normalSpeed</c> into <see cref="AirborneFollowHelper"/>, letting
/// the +<see cref="AirborneFollowHelper.MaxSpeedAdjustKts"/> clamp ratchet every
/// tick. N346G reached 167 KIAS on short final and triggered an unstabilized
/// go-around at t=445s — absurd for a C172 whose Vref is ~62 kt.
///
/// Fix: the pattern phases now pass the fixed phase baseline
/// (<c>DownwindSpeed</c>/<c>BaseSpeed</c>/<c>ApproachSpeed</c>) as
/// <c>normalSpeed</c>; <see cref="Phases.Tower.FinalApproachPhase"/> also
/// stops chasing entirely inside a ~60s stabilization window (committed to
/// land or go around — don't chase).
/// </summary>
public class FollowRunawayIasTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/s2-oak3-follow-runaway-ias-recording.yaat-bug-report-bundle.zip";
    private const string Follower = "N346G";
    private const string MidFollower = "N436MS";
    private const string Leader = "N9225L";

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
            .EnableCategory("AirborneFollowHelper", LogLevel.Debug)
            .EnableCategory("DownwindPhase", LogLevel.Debug)
            .EnableCategory("FinalApproachPhase", LogLevel.Debug)
            .EnableCategory("LandingPhase", LogLevel.Information)
            .InitializeSimLog();

        return new SimulationEngine(groundData);
    }

    /// <summary>
    /// Replay the recorded bug window (FOLLOW → CLAND → short final) and assert
    /// that N346G never reaches the unstabilized-go-around gate (IAS &gt; 1.3·Vref)
    /// and no "going around (unstabilized)" warning fires.
    /// </summary>
    [Fact]
    public void N346G_DoesNotTripUnstabilizedGate_DuringFollowedApproach()
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

            // Replay from t=0 up to t=265 (just before FOLLOW at t=269) so physics
            // state matches the recorded world, then step tick-by-tick through
            // CLAND at t=381 and past the original-code GA at t=445.
            engine.Replay(recording, 265);

            double maxIas = 0;
            int maxIasTick = 265;
            bool sawFinalApproach = false;
            double vref = 0;
            double stabilizedGate = 0;

            for (int t = 266; t <= 470; t++)
            {
                engine.ReplayOneSecond();

                var ac = engine.FindAircraft(Follower);
                if (ac is null)
                {
                    continue;
                }

                if (ac.IndicatedAirspeed > maxIas)
                {
                    maxIas = ac.IndicatedAirspeed;
                    maxIasTick = t;
                }

                string phase = ac.Phases?.CurrentPhase?.Name ?? "";
                if (phase == "FinalApproach")
                {
                    sawFinalApproach = true;
                    if (vref == 0)
                    {
                        var cat = AircraftCategorization.Categorize(ac.AircraftType);
                        vref = AircraftPerformance.ApproachSpeed(ac.AircraftType, cat);
                        stabilizedGate = vref * 1.3;
                    }
                    Assert.True(
                        ac.IndicatedAirspeed <= stabilizedGate,
                        $"t={t}s: {Follower} IAS {ac.IndicatedAirspeed:F1} exceeds stabilized-approach gate "
                            + $"{stabilizedGate:F1} (1.3*Vref={vref:F0})"
                    );
                }
            }

            var follower = engine.FindAircraft(Follower);
            Assert.NotNull(follower);
            output.WriteLine($"Max {Follower} IAS: {maxIas:F1} kt at t={maxIasTick}s (gate={stabilizedGate:F1})");
            Assert.True(sawFinalApproach, $"{Follower} never reached FinalApproach in the replay window");

            Assert.DoesNotContain(
                follower.PendingWarnings,
                w => w.Contains("unstabilized", StringComparison.OrdinalIgnoreCase) && w.Contains("going around", StringComparison.OrdinalIgnoreCase)
            );
        }
    }

    /// <summary>
    /// Regression: N346G's <c>TargetSpeed</c> must never exceed any reasonable
    /// C172 pattern speed (DownwindSpeed + MaxSpeedAdjustKts = 110 kt). In the
    /// original buggy code this field reached 578 kt due to per-tick compounding.
    /// </summary>
    [Fact]
    public void N346G_TargetSpeedStaysWithinFollowCeiling()
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

            engine.Replay(recording, 265);

            // Hard ceiling: DownwindSpeed(C172)=90 + MaxSpeedAdjustKts=20 = 110.
            // A tiny epsilon absorbs any numerical jitter in the clamp.
            const double HardCeiling = 110.0 + 0.5;

            double maxTgt = 0;
            int maxTgtTick = 265;

            for (int t = 266; t <= 470; t++)
            {
                engine.ReplayOneSecond();
                var ac = engine.FindAircraft(Follower);
                if (ac?.Targets.TargetSpeed is { } tgt)
                {
                    if (tgt > maxTgt)
                    {
                        maxTgt = tgt;
                        maxTgtTick = t;
                    }
                    Assert.True(tgt <= HardCeiling, $"t={t}s: {Follower} TargetSpeed {tgt:F1} exceeds hard ceiling {HardCeiling:F1}");
                }
            }

            output.WriteLine($"Max {Follower} TargetSpeed seen: {maxTgt:F1} kt at t={maxTgtTick}s");
        }
    }
}
