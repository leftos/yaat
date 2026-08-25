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
}
