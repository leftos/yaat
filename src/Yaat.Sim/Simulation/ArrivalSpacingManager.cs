using Yaat.Sim.Phases;

namespace Yaat.Sim.Simulation;

/// <summary>
/// Pure in-trail spacing math for the arrival-generator stream — the simulated approach
/// controller (TRACON) that feeds correctly-spaced traffic to the tower (LC) student.
/// <see cref="SimulationEngine"/> owns the per-tick orchestration (pairing each follower with
/// the aircraft immediately ahead on the same final via the corridor query, the override
/// latch, and stamping <see cref="ControlTargets.SpeedCeiling"/>); these helpers compute the
/// numbers and are unit-tested directly.
///
/// <para>It also owns the policy for giving a speed back, because more than one caller applies it: how far below the
/// speed an arrival should be flying is worth a restore (<see cref="SpeedRestoreDeadbandKts"/>) and how close to the
/// threshold it stops being worth one (<see cref="SpeedRestoreGateNm"/>). The generator stream's own restore and the
/// instructor's <c>RNS</c> on an arrival already on final are the same judgement and read the same two figures.</para>
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

    /// <summary>
    /// How far (kt) below the speed it should be flying an arrival may be before its speed is given back. A pilot
    /// complying with a speed adjustment holds it within ±10 kt (AIM 4-4-12.c; 7110.65 §5-7-1.g NOTE 1), so a
    /// difference inside that band is not one a controller would correct. Shared by the generator stream's own restore
    /// (<c>SimulationEngine.RestoreManagedSpeed</c>) and by <c>RNS</c>
    /// (<see cref="Commands.FlightCommandHandler.ApplyResumeNormalSpeed"/>), which are the same judgement.
    /// </summary>
    public const double SpeedRestoreDeadbandKts = 10.0;

    /// <summary>
    /// Margin (nm) kept outside the first deceleration stage <see cref="Phases.Tower.FinalApproachPhase"/> would start
    /// on its own, inside which no speed is restored, so an arrival is never sped up only to be slowed again moments
    /// later. A judgement call under §5-7-1.a.2(b) (speed adjustments are not achieved instantaneously) and
    /// §5-7-1.a.3(c) (allow increased time and distance for a speed adjustment at greater speed and in a clean
    /// configuration).
    /// </summary>
    private const double RestoreGateMarginNm = 5.0;

    /// <summary>
    /// Distance from the threshold (nm) inside which an arrival's speed is no longer restored: the latest point the
    /// phase could start its first deceleration stage, plus <see cref="RestoreGateMarginNm"/>. That stage is the clean →
    /// approach-flap bleed, which starts no farther out than the aircraft's approach-flap reach gate plus
    /// <see cref="Phases.Tower.FinalApproachPhase.ApproachFlapTriggerHeadroomNm"/>. A category with no approach-flap
    /// stage (a piston) starts with the configuration bleed, bounded here by
    /// <see cref="Phases.Tower.FinalApproachPhase.MaxConfigTriggerNm"/>: the phase's own cap slides outward with a
    /// larger FAS reach gate, but a piston's clean-to-configuration bleed is under ~20 kt and starts no farther out than
    /// about 7.3 nm, so the gate still keeps more than 5 nm of margin.
    /// </summary>
    public static double SpeedRestoreGateNm(AircraftCategory category, string callsign)
    {
        double firstStageTriggerCapNm = Phases.Tower.FinalApproachSpeedSchedule.ApproachFlapReachGateNm(category, callsign) is { } flapGate
            ? flapGate + Phases.Tower.FinalApproachPhase.ApproachFlapTriggerHeadroomNm
            : Phases.Tower.FinalApproachPhase.MaxConfigTriggerNm;
        return firstStageTriggerCapNm + RestoreGateMarginNm;
    }
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
