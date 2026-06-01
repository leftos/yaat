using Xunit;

namespace Yaat.Sim.Tests;

public class SpeciCriteriaTests
{
    private static ReportedConditions Cond(
        bool calm = false,
        int dir = 270,
        int speed = 12,
        double? vis = 10,
        int? ceiling = null,
        bool precip = false
    ) => new(calm, dir, speed, null, vis, [], ceiling, 29.92, precip);

    [Fact]
    public void NoChange_NotWorthy()
    {
        Assert.False(SpeciCriteria.IsSpeciWorthy(Cond(), Cond()));
    }

    [Theory]
    [InlineData(270, 12, 320, 12, true)] // 50 deg shift, both >= 10 kt
    [InlineData(270, 12, 305, 12, false)] // 35 deg shift, below 45
    [InlineData(270, 8, 320, 12, false)] // last below 10 kt
    [InlineData(10, 12, 320, 12, true)] // wrap: 10 vs 320 = 50 deg
    public void WindShift(int lastDir, int lastSpd, int curDir, int curSpd, bool expected)
    {
        var result = SpeciCriteria.IsSpeciWorthy(Cond(dir: lastDir, speed: lastSpd), Cond(dir: curDir, speed: curSpd));
        Assert.Equal(expected, result);
    }

    [Fact]
    public void WindShift_FromCalm_NotWorthy()
    {
        Assert.False(SpeciCriteria.IsSpeciWorthy(Cond(calm: true, speed: 0), Cond(dir: 90, speed: 15)));
    }

    [Theory]
    [InlineData(5.0, 2.5, true)] // crosses 3 downward
    [InlineData(5.0, 4.0, false)] // no threshold between
    [InlineData(2.5, 5.0, true)] // crosses 3 upward
    [InlineData(1.5, 0.75, true)] // crosses 1
    [InlineData(0.3, 0.2, true)] // crosses 1/4
    [InlineData(0.75, 0.6, false)] // both between 1/2 and 1
    public void VisibilityCrossing(double last, double current, bool expected)
    {
        var result = SpeciCriteria.IsSpeciWorthy(Cond(vis: last), Cond(vis: current));
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(null, 800, true)] // ceiling forms below 1000
    [InlineData(800, null, true)] // ceiling dissipates
    [InlineData(2000, 4000, true)] // crosses 3000
    [InlineData(2000, 2500, false)] // no threshold between
    [InlineData(600, 400, true)] // crosses 500
    public void CeilingCrossing(int? last, int? current, bool expected)
    {
        var result = SpeciCriteria.IsSpeciWorthy(Cond(ceiling: last), Cond(ceiling: current));
        Assert.Equal(expected, result);
    }

    [Fact]
    public void PrecipitationOnset_Worthy()
    {
        Assert.True(SpeciCriteria.IsSpeciWorthy(Cond(precip: false), Cond(precip: true)));
    }

    [Fact]
    public void PrecipitationCessation_Worthy()
    {
        Assert.True(SpeciCriteria.IsSpeciWorthy(Cond(precip: true), Cond(precip: false)));
    }
}
