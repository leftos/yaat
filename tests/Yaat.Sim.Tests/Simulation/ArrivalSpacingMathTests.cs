using Xunit;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Unit tests for the pure in-trail spacing math (<see cref="ArrivalSpacingManager"/>).
/// </summary>
public class ArrivalSpacingMathTests
{
    [Theory]
    [InlineData(3, 148)] // <= 4 NM → Vref + 8
    [InlineData(5, 182)] // 4-6 NM → 1.3·Vref
    [InlineData(8, 185)] // 6-10.5 NM → approach-flap speed (Vref + 45)
    [InlineData(12, 210)] // >= 10.5 NM → clean (Vref + 70, ±5 jitter)
    public void ScheduledFinalSpeed_FollowsOnFinalDistanceProfile(double dist, double expected)
    {
        // B738-class Vref 140; the clean-speed jitter is ±5 kt per callsign, so every band is checked as a window.
        double actual = ArrivalSpacingManager.ScheduledFinalSpeedKts("B738", AircraftCategory.Jet, 140, "TST123", dist);
        Assert.InRange(actual, expected - 5, expected + 5);
    }

    [Fact]
    public void SpacingCeiling_AtTargetGap_EqualsLeaderSpeed()
    {
        // gap == target → zero correction → follower equalizes to the leader's speed.
        double ceiling = ArrivalSpacingManager.SpacingCeilingKts(leaderIasKts: 180, gapNm: 5, targetNm: 5, vrefKts: 140, scheduledKts: 224);
        Assert.Equal(180, ceiling, 3);
    }

    [Fact]
    public void SpacingCeiling_WhenTooClose_SlowsBelowLeader()
    {
        // gap < target → negative correction → below the leader's speed (but never below Vref).
        double ceiling = ArrivalSpacingManager.SpacingCeilingKts(leaderIasKts: 180, gapNm: 4, targetNm: 5, vrefKts: 140, scheduledKts: 224);
        Assert.True(ceiling < 180, $"expected slowing below leader, got {ceiling}");
        Assert.True(ceiling >= 140, $"expected at/above Vref, got {ceiling}");
    }

    [Fact]
    public void SpacingCeiling_FloorsAtFollowerVref()
    {
        // Very close behind a slow leader → would command below Vref, clamps to Vref (the source
        // of the unavoidable last-mile residual when a faster-Vref jet trails a slower one).
        double ceiling = ArrivalSpacingManager.SpacingCeilingKts(leaderIasKts: 130, gapNm: 1, targetNm: 5, vrefKts: 144, scheduledKts: 230);
        Assert.Equal(144, ceiling, 3);
    }

    [Fact]
    public void SpacingCeiling_CapsAtScheduledProfileSpeed()
    {
        // Large gap → wants to speed up to re-close, but never above its own scheduled speed.
        double ceiling = ArrivalSpacingManager.SpacingCeilingKts(leaderIasKts: 220, gapNm: 20, targetNm: 5, vrefKts: 140, scheduledKts: 224);
        Assert.Equal(224, ceiling, 3);
    }

    private static double Baseline(InTrailPair p) =>
        ArrivalSpacingManager.SpacingCeilingKts(
            p.LeaderIasKts,
            p.FollowerDistanceNm - p.LeaderDistanceNm,
            p.TargetNm,
            p.FollowerVrefKts,
            p.FollowerScheduledKts
        );

    [Fact]
    public void InTrailCeiling_FarBehindLeaderOnShortFinal_AllowsScheduledSpeed()
    {
        // The S2-OAK-P case: a follower 26 nm behind a 144 kt leader on 2.3 nm final. The leader crosses the threshold
        // long before the follower can close to 5 nm, so the +20 kt cap on the proportional term must not bind.
        var pair = new InTrailPair
        {
            LeaderIasKts = 144,
            LeaderGsKts = 144,
            LeaderVrefKts = 140,
            LeaderDistanceNm = 2.3,
            FollowerDistanceNm = 28.3,
            FollowerIasKts = 180,
            FollowerGsKts = 180,
            FollowerVrefKts = 140,
            FollowerScheduledKts = 205,
            TargetNm = 5,
        };

        Assert.Equal(205, ArrivalSpacingManager.InTrailCeilingKts(pair), 3);
    }

    [Fact]
    public void InTrailCeiling_ClosePair_EqualsProportionalCeiling()
    {
        // Leader 10 nm out, gap 6 against a 5 nm target: the time allowance (140 kt Vref over 11 nm of closing room
        // in 10 nm of leader run, 154 kt) sits below the proportional ceiling, so the proportional ceiling stands.
        var pair = new InTrailPair
        {
            LeaderIasKts = 180,
            LeaderGsKts = 180,
            LeaderVrefKts = 140,
            LeaderDistanceNm = 10,
            FollowerDistanceNm = 16,
            FollowerIasKts = 190,
            FollowerGsKts = 190,
            FollowerVrefKts = 140,
            FollowerScheduledKts = 210,
            TargetNm = 5,
        };

        Assert.Equal(Baseline(pair), ArrivalSpacingManager.InTrailCeilingKts(pair), 6);
    }

    [Fact]
    public void InTrailCeiling_GapInsideTarget_EqualsProportionalCeiling()
    {
        var pair = new InTrailPair
        {
            LeaderIasKts = 180,
            LeaderGsKts = 180,
            LeaderVrefKts = 140,
            LeaderDistanceNm = 10,
            FollowerDistanceNm = 14,
            FollowerIasKts = 190,
            FollowerGsKts = 190,
            FollowerVrefKts = 140,
            FollowerScheduledKts = 210,
            TargetNm = 5,
        };

        Assert.Equal(Baseline(pair), ArrivalSpacingManager.InTrailCeilingKts(pair), 6);
    }

    [Fact]
    public void InTrailCeiling_AllowanceIsConvertedToIasWithFollowersOwnRatio()
    {
        // Leader 3 nm out at 120 kt with a 110 kt Vref; the follower 8 nm behind flies a ground speed 1.15 x its IAS.
        // Ground-speed allowance = 110 x (11 - 5) / 3 = 220 kt, which in the follower's IAS is 220 / 1.15.
        var pair = new InTrailPair
        {
            LeaderIasKts = 120,
            LeaderGsKts = 120,
            LeaderVrefKts = 110,
            LeaderDistanceNm = 3,
            FollowerDistanceNm = 11,
            FollowerIasKts = 180,
            FollowerGsKts = 180 * 1.15,
            FollowerVrefKts = 130,
            FollowerScheduledKts = 210,
            TargetNm = 5,
        };

        double groundSpeedAllowance = 110.0 * (11 - 5) / 3;
        Assert.Equal(groundSpeedAllowance / 1.15, ArrivalSpacingManager.InTrailCeilingKts(pair), 6);
    }

    [Fact]
    public void InTrailCeiling_LeaderAtThreshold_AllowsScheduledSpeed()
    {
        var pair = new InTrailPair
        {
            LeaderIasKts = 140,
            LeaderGsKts = 140,
            LeaderVrefKts = 140,
            LeaderDistanceNm = 0,
            FollowerDistanceNm = 10,
            FollowerIasKts = 190,
            FollowerGsKts = 190,
            FollowerVrefKts = 140,
            FollowerScheduledKts = 200,
            TargetNm = 5,
        };

        Assert.Equal(200, ArrivalSpacingManager.InTrailCeilingKts(pair), 6);
    }

    [Fact]
    public void InTrailCeiling_LeaderIasZero_IsFiniteAndWithinFollowerWindow()
    {
        // A leader reporting no speed at all (IAS and GS zero) must not turn the GS/IAS ratio into NaN.
        var pair = new InTrailPair
        {
            LeaderIasKts = 0,
            LeaderGsKts = 0,
            LeaderVrefKts = 140,
            LeaderDistanceNm = 3,
            FollowerDistanceNm = 12,
            FollowerIasKts = 190,
            FollowerGsKts = 190,
            FollowerVrefKts = 140,
            FollowerScheduledKts = 210,
            TargetNm = 5,
        };

        double ceiling = ArrivalSpacingManager.InTrailCeilingKts(pair);
        Assert.True(double.IsFinite(ceiling), $"expected a finite ceiling, got {ceiling}");
        Assert.InRange(ceiling, 140, 210);
    }
}
