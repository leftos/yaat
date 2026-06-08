using Yaat.Sim.Phases;

namespace Yaat.Sim.Simulation;

/// <summary>
/// Pure in-trail spacing math for the arrival-generator stream — the simulated approach
/// controller (TRACON) that feeds correctly-spaced traffic to the tower (LC) student.
/// <see cref="SimulationEngine"/> owns the per-tick orchestration (pairing each follower with
/// the aircraft immediately ahead on the same final via the corridor query, the override
/// latch, and stamping <see cref="ControlTargets.SpeedCeiling"/>); these helpers compute the
/// numbers and are unit-tested directly.
/// </summary>
public static class ArrivalSpacingManager
{
    /// <summary>
    /// Scheduled distance-based approach speed an arrival flies absent any spacing constraint —
    /// mirrors <see cref="Scenarios.AircraftInitializer.InitializeOnFinal"/>'s OnFinal formula
    /// (<c>&lt;= 5 NM → Vref, &lt;= 10 NM → 1.4·Vref, else 1.6·Vref</c>). Used as the upper
    /// bound on the spacing ceiling so the manager never speeds a follower above its own normal
    /// profile, and so the ceiling window collapses to exactly Vref by the threshold.
    /// </summary>
    public static double ScheduledFinalSpeedKts(double vrefKts, double distanceToThresholdNm) =>
        distanceToThresholdNm switch
        {
            <= 5 => vrefKts,
            <= 10 => vrefKts * 1.4,
            _ => vrefKts * 1.6,
        };

    /// <summary>
    /// Speed ceiling (kts) for a follower so it equalizes to its leader's speed while holding
    /// the in-trail spacing <paramref name="targetNm"/>. The follower tracks the leader's
    /// current speed plus a proportional correction on the gap error (gap &gt; target → open up
    /// toward the scheduled speed; gap &lt; target → slow to re-open), floored at the follower's
    /// own Vref (it cannot fly slower than that — the source of the unavoidable last-mile
    /// residual when a faster-Vref jet trails a slower one) and capped at its scheduled profile
    /// speed (never sped up beyond normal).
    /// </summary>
    public static double SpacingCeilingKts(double leaderIasKts, double gapNm, double targetNm, double vrefKts, double scheduledKts)
    {
        double correction = Math.Clamp(
            (gapNm - targetNm) * AirborneFollowHelper.SpeedGainPerNm,
            -AirborneFollowHelper.MaxSpeedAdjustKts,
            AirborneFollowHelper.MaxSpeedAdjustKts
        );
        double desired = leaderIasKts + correction;
        double upper = Math.Max(vrefKts, scheduledKts);
        return Math.Clamp(desired, vrefKts, upper);
    }
}
