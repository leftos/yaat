using System.Collections.Immutable;
using Xunit;
using Yaat.Client.ViewModels.Authoring;
using Yaat.Client.Views.Authoring;

namespace Yaat.Client.Tests.Authoring;

/// <summary>
/// The workload graph's geometry: the preview's spawn delays counted into buckets of the bucket size over the planning
/// length, time and percent mapped to pixels and back, and the time axis's tick step.
/// </summary>
public class WorkloadGraphGeometryTests
{
    private static readonly GraphPlot Plot = new(40, 10, 600, 200);

    [Fact]
    public void Buckets_300s_Slots10_100_310_CountsTwoThenOne()
    {
        ImmutableArray<int> buckets = WorkloadGraphGeometry.Buckets([10, 100, 310], 600, 300);

        Assert.Equal([2, 1], [.. buckets]);
    }

    [Fact]
    public void Buckets_SlotAtLengthEnd_FallsInLastBucket()
    {
        ImmutableArray<int> buckets = WorkloadGraphGeometry.Buckets([600], 600, 300);

        Assert.Equal([0, 1], [.. buckets]);
    }

    [Fact]
    public void Buckets_EmptyPreview_AllZero()
    {
        ImmutableArray<int> buckets = WorkloadGraphGeometry.Buckets([], 3600, 300);

        Assert.Equal(12, buckets.Length);
        Assert.All(buckets, count => Assert.Equal(0, count));
    }

    [Theory]
    [InlineData(0, 0, 40, 210)]
    [InlineData(3600, 100, 640, 10)]
    [InlineData(1800, 50, 340, 110)]
    public void PointMapping_RoundTrip(int timeSec, double percent, double expectedX, double expectedY)
    {
        const int lengthSec = 3600;

        double x = WorkloadGraphGeometry.TimeToX(Plot, lengthSec, timeSec);
        double y = WorkloadGraphGeometry.PercentToY(Plot, percent);
        double backX = WorkloadGraphGeometry.TimeToX(Plot, lengthSec, WorkloadGraphGeometry.XToTime(Plot, lengthSec, x));
        double backY = WorkloadGraphGeometry.PercentToY(Plot, WorkloadGraphGeometry.YToPercent(Plot, y));

        Assert.InRange(x, expectedX - 1, expectedX + 1);
        Assert.InRange(y, expectedY - 1, expectedY + 1);
        Assert.InRange(backX, x - 1, x + 1);
        Assert.InRange(backY, y - 1, y + 1);
    }

    [Fact]
    public void BarBox_InsetsTheBucketSpanByOnePixelEachSide()
    {
        GraphPlot box = WorkloadGraphGeometry.BarBox(Plot, new BarScale(300, 600, 2), 0, 2);

        Assert.Equal(40 + WorkloadGraphGeometry.BarInset, box.Left, 6);
        Assert.Equal(300 - (2 * WorkloadGraphGeometry.BarInset), box.Width, 6);
        Assert.Equal(10, box.Top, 6);
        Assert.Equal(200, box.Height, 6);
    }

    [Fact]
    public void PointPixel_PastTheLength_ClampsToTheRightEdge()
    {
        (double X, double Y)? past = WorkloadGraphGeometry.PointPixel(Plot, 600, new SpawnPlanPoint(15, 50));
        (double X, double Y)? over = WorkloadGraphGeometry.PointPixel(Plot, 600, new SpawnPlanPoint(5, 150));

        Assert.NotNull(past);
        Assert.Equal(Plot.Right, past.Value.X, 6);
        Assert.Equal(110, past.Value.Y, 6);
        Assert.NotNull(over);
        Assert.Equal(340, over.Value.X, 6);
        Assert.Equal(Plot.Top, over.Value.Y, 6);
        Assert.Null(WorkloadGraphGeometry.PointPixel(Plot, 600, new SpawnPlanPoint(double.NaN, 50)));
    }

    [Fact]
    public void TickStep_10MinOver30Min_5MinUnder()
    {
        Assert.Equal(10, WorkloadGraphGeometry.TickStepMinutes(3600));
        Assert.Equal(10, WorkloadGraphGeometry.TickStepMinutes(1800));
        Assert.Equal(5, WorkloadGraphGeometry.TickStepMinutes(1200));
        Assert.Equal([0, 10, 20, 30, 40, 50, 60], [.. WorkloadGraphGeometry.TickMinutes(3600)]);
        Assert.Equal([0, 5, 10, 15, 20], [.. WorkloadGraphGeometry.TickMinutes(1200)]);
    }
}
