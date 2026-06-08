using Xunit;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Unit tests for the pure in-trail spacing math (<see cref="ArrivalSpacingManager"/>).
/// </summary>
public class ArrivalSpacingMathTests
{
    [Theory]
    [InlineData(140, 3, 140)] // <= 5 NM → Vref
    [InlineData(140, 5, 140)] // == 5 NM → Vref
    [InlineData(140, 8, 196)] // <= 10 NM → 1.4·Vref
    [InlineData(140, 12, 224)] // > 10 NM → 1.6·Vref
    public void ScheduledFinalSpeed_FollowsOnFinalDistanceProfile(double vref, double dist, double expected)
    {
        Assert.Equal(expected, ArrivalSpacingManager.ScheduledFinalSpeedKts(vref, dist), 3);
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
}
