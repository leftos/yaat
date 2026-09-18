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
    /// Scheduled distance-based approach speed an arrival flies absent any spacing constraint — the
    /// uncontrolled <see cref="Phases.Tower.FinalApproachSpeedSchedule"/> evaluated at the follower's distance
    /// (the same schedule <see cref="Scenarios.AircraftInitializer.InitializeOnFinal"/> spawns from). Used as
    /// the upper bound on the spacing ceiling so the manager never speeds a follower above its own normal
    /// profile, and so the ceiling window collapses toward Vref by the threshold.
    /// </summary>
    public static double ScheduledFinalSpeedKts(
        string aircraftType,
        AircraftCategory category,
        double vrefKts,
        string callsign,
        double distanceToThresholdNm
    ) => Phases.Tower.FinalApproachSpeedSchedule.SpeedAtDistanceKts(aircraftType, category, vrefKts, callsign, distanceToThresholdNm);

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

    /// <summary>
    /// Speed ceiling (kts) for a generator-stream follower behind a leader on final: the proportional
    /// <see cref="SpacingCeilingKts"/> ceiling, raised to a time-based allowance while the pair is farther apart than
    /// the target, so a follower far behind a leader on short final may close faster than the leader's speed plus the
    /// proportional correction. The target binds throughout, not only at the threshold: 7110.65 §5-5-4.h is only the
    /// TBL 5-5-2 wake minima, applied when the leader is over the landing threshold, while the 3 NM radar floor and the
    /// author's <c>IntervalDistance</c> apply continuously. Both hold because the leader's Vref, as the lower bound on its
    /// speed, keeps gap ≥ target for the whole of its remaining run.
    ///
    /// <para>The leader's slowest plausible remaining ground speed is
    /// <c>vMin = min(LeaderGs, LeaderVref × min(1, LeaderGs / LeaderIas))</c> — its Vref as the lower bound, in
    /// its own ground-speed frame — so it needs at least <c>LeaderDistance / vMin</c> to reach the threshold. In
    /// that time the follower may cover <c>FollowerDistance − Target</c>, which gives the ground-speed allowance
    /// <c>vMin × (FollowerDistance − Target) / max(LeaderDistance, 0.1)</c>; with the leader's Vref as the lower
    /// bound, gap ≥ target holds throughout the leader's remaining run. The allowance is converted to IAS with the
    /// follower's own IAS/GS ratio, which is conservative as the follower descends, and clamped to the follower's
    /// window [Vref, max(Vref, scheduled)]. The result is the larger of that and the proportional ceiling; at or
    /// inside the target gap it is the proportional ceiling alone. A non-positive leader IAS or follower GS
    /// treats the corresponding ratio as 1.</para>
    /// </summary>
    public static double InTrailCeilingKts(InTrailPair pair)
    {
        double gap = pair.FollowerDistanceNm - pair.LeaderDistanceNm;
        double baseline = SpacingCeilingKts(pair.LeaderIasKts, gap, pair.TargetNm, pair.FollowerVrefKts, pair.FollowerScheduledKts);
        if (gap <= pair.TargetNm)
        {
            return baseline;
        }

        double leaderGsPerIas = (pair.LeaderIasKts <= 0) ? 1.0 : pair.LeaderGsKts / pair.LeaderIasKts;
        double vMin = Math.Min(pair.LeaderGsKts, pair.LeaderVrefKts * Math.Min(1.0, leaderGsPerIas));
        double followerMaxGs = vMin * (pair.FollowerDistanceNm - pair.TargetNm) / Math.Max(pair.LeaderDistanceNm, MinLeaderDistanceNm);
        double followerIasPerGs = (pair.FollowerGsKts <= 0) ? 1.0 : pair.FollowerIasKts / pair.FollowerGsKts;
        double allowanceIas = followerMaxGs * followerIasPerGs;
        double upper = Math.Max(pair.FollowerVrefKts, pair.FollowerScheduledKts);
        return Math.Max(baseline, Math.Clamp(allowanceIas, pair.FollowerVrefKts, upper));
    }

    /// <summary>
    /// Floor on the leader's distance to the threshold in <see cref="InTrailCeilingKts"/>, so a leader at the threshold
    /// cannot divide by zero.
    /// </summary>
    private const double MinLeaderDistanceNm = 0.1;
}

/// <summary>
/// One leader/follower pair on the same final, as <see cref="ArrivalSpacingManager.InTrailCeilingKts"/> reads it:
/// both aircraft's indicated and ground speeds, their Vrefs, their along-final distances to the threshold (nm),
/// the follower's scheduled profile speed, and the in-trail target gap (nm). The gap is derived as
/// <c>FollowerDistanceNm − LeaderDistanceNm</c>.
/// </summary>
public readonly record struct InTrailPair
{
    public required double LeaderIasKts { get; init; }
    public required double LeaderGsKts { get; init; }
    public required double LeaderVrefKts { get; init; }
    public required double LeaderDistanceNm { get; init; }
    public required double FollowerDistanceNm { get; init; }
    public required double FollowerIasKts { get; init; }
    public required double FollowerGsKts { get; init; }
    public required double FollowerVrefKts { get; init; }
    public required double FollowerScheduledKts { get; init; }
    public required double TargetNm { get; init; }
}
