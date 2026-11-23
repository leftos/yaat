using Xunit;
using Yaat.Sim.Authoring.Spawns;

namespace Yaat.Sim.Tests.Authoring;

/// <summary>
/// <see cref="WorkloadCurve"/> samples a workload percentage over a scenario's length: its control points clamped, sorted
/// and interpolated with a cosine ease, and the evenly-spaced peaks a planner offers to fill them in.
/// </summary>
public class WorkloadCurveTests
{
    [Fact]
    public void ValueAt_FollowsTheControlPoints()
    {
        var curve = new WorkloadCurve(3600, 50, 50, [new WorkloadPoint(1200, 100), new WorkloadPoint(1800, 30), new WorkloadPoint(2400, 100)]);

        Assert.Equal(3600, curve.LengthSec);
        Assert.Equal(500, curve.Samples.Count);
        Assert.Equal(50, curve.ValueAt(0), 6);
        Assert.Equal(100, curve.ValueAt(1.0 / 3.0), 6);
        Assert.Equal(75, curve.ValueAt(1.0 / 6.0), 6);
        Assert.Equal(30, curve.ValueAt(0.5), 6);
        Assert.Equal(65, curve.ValueAt(5.0 / 12.0), 6);
        Assert.Equal(100, curve.ValueAt(2.0 / 3.0), 6);
        Assert.Equal(50, curve.ValueAt(1), 6);
    }

    [Fact]
    public void ControlPoints_AreClamped_AndTheLastAtATimeWins()
    {
        var curve = new WorkloadCurve(
            3600,
            50,
            50,
            [new WorkloadPoint(-10, 130), new WorkloadPoint(2400, 40), new WorkloadPoint(2400, 80), new WorkloadPoint(5000, 20)]
        );

        Assert.Equal(100, curve.ValueAt(0), 6);
        Assert.Equal(80, curve.ValueAt(2.0 / 3.0), 6);
        Assert.Equal(50, curve.ValueAt(1), 6);
        Assert.Equal(500, curve.Samples.Count);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Constructor_NonFiniteLength_Throws(double lengthSec) => AssertCurveRejects(lengthSec, 50, 50, []);

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.NegativeInfinity)]
    public void Constructor_NonFiniteStartPercent_Throws(double startPercent) => AssertCurveRejects(3600, startPercent, 50, []);

    [Theory]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NaN)]
    public void Constructor_NonFiniteEndPercent_Throws(double endPercent) => AssertCurveRejects(3600, 50, endPercent, []);

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Constructor_NonFinitePointTime_Throws(double timeSec) => AssertCurveRejects(3600, 50, 50, [new WorkloadPoint(timeSec, 50)]);

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.NegativeInfinity)]
    public void Constructor_NonFinitePointPercent_Throws(double percent) => AssertCurveRejects(3600, 50, 50, [new WorkloadPoint(600, percent)]);

    [Fact]
    public void AutoPoints_PutsPeaksEvenly_WithATroughBetweenNeighbours()
    {
        IReadOnlyList<WorkloadPoint> two = WorkloadCurve.AutoPoints(3600, 2, 100, 20);

        Assert.Collection(
            two,
            p =>
            {
                Assert.Equal(1200, p.TimeSec);
                Assert.Equal(100, p.Percent);
            },
            p =>
            {
                Assert.Equal(1800, p.TimeSec);
                Assert.Equal(20, p.Percent);
            },
            p =>
            {
                Assert.Equal(2400, p.TimeSec);
                Assert.Equal(100, p.Percent);
            }
        );

        WorkloadPoint single = Assert.Single(WorkloadCurve.AutoPoints(3600, 1, 100, 20));
        Assert.Equal(1800, single.TimeSec);
        Assert.Equal(100, single.Percent);
        Assert.Empty(WorkloadCurve.AutoPoints(3600, 0, 100, 20));
    }

    private static void AssertCurveRejects(double lengthSec, double startPercent, double endPercent, WorkloadPoint[] points) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new WorkloadCurve(lengthSec, startPercent, endPercent, points));
}
