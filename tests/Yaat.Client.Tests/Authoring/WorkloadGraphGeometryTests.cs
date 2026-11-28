using System.Collections.Immutable;
using Xunit;
using Yaat.Client.ViewModels.Authoring;
using Yaat.Client.Views.Authoring;

namespace Yaat.Client.Tests.Authoring;

/// <summary>
/// The workload graph's geometry: the preview's spawn delays counted into buckets of the bucket size over the planning
/// length, time and percent mapped to pixels and back, a press's pixel hit on a control point's dot and a dragged pixel
/// back to a control point, and the time axis's tick step.
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

    [Theory]
    [InlineData(0.0)]
    [InlineData(7.9)]
    public void HitPoint_Within8PxOfADot_GrabsIt(double dx)
    {
        ImmutableArray<SpawnPlanPoint> points = [Dot(30, 50)];

        Assert.Equal(0, WorkloadGraphGeometry.HitPoint(Plot, 3600, points, 340 + dx, 110));
    }

    [Fact]
    public void HitPoint_Past8PxOfEveryDot_FindsNothing()
    {
        ImmutableArray<SpawnPlanPoint> points = [Dot(30, 50)];

        Assert.Null(WorkloadGraphGeometry.HitPoint(Plot, 3600, points, 348.1, 110));
    }

    [Fact]
    public void HitPoint_TwoDots_GrabsTheNearer()
    {
        ImmutableArray<SpawnPlanPoint> points = [Dot(30, 50), Dot(30.5, 50)];

        Assert.Equal(1, WorkloadGraphGeometry.HitPoint(Plot, 3600, points, 343, 110));
    }

    [Fact]
    public void HitPoint_TwoCoincidentDots_GrabsTheOneDrawnLast()
    {
        ImmutableArray<SpawnPlanPoint> points = [Dot(30, 50), Dot(30, 50)];

        Assert.Equal(1, WorkloadGraphGeometry.HitPoint(Plot, 3600, points, 340, 110));
    }

    [Fact]
    public void HitPoint_WithNoPoints_FindsNothing() => Assert.Null(WorkloadGraphGeometry.HitPoint(Plot, 3600, [], 340, 110));

    [Fact]
    public void HitPoint_WithNoPlanningLength_FindsNothing() => Assert.Null(WorkloadGraphGeometry.HitPoint(Plot, 0, [Dot(0, 50)], 40, 110));

    [Fact]
    public void HitPoint_AnUnreadablePoint_IsSkipped()
    {
        ImmutableArray<SpawnPlanPoint> points = [Dot(double.NaN, 50), Dot(30, 50)];

        Assert.Equal(1, WorkloadGraphGeometry.HitPoint(Plot, 3600, points, 340, 110));
    }

    [Theory]
    [InlineData(40, 210, 0, 0)]
    [InlineData(640, 10, 60, 100)]
    [InlineData(340, 110, 30, 50)]
    public void DragPoint_MapsBackToThePixelItCameFrom(double x, double y, double expectedMinutes, double expectedPercent)
    {
        SpawnPlanPoint point = WorkloadGraphGeometry.DragPoint(Plot, 3600, x, y);

        Assert.Equal(expectedMinutes, point.TimeMinutes, 6);
        Assert.Equal(expectedPercent, point.Percent, 6);
        (double X, double Y) back = WorkloadGraphGeometry.PointPixel(Plot, 3600, point)!.Value;
        Assert.Equal(x, back.X, 2);
        Assert.Equal(y, back.Y, 2);
    }

    [Fact]
    public void DragPoint_RoundsTheTimeToATenthOfAMinute_AndThePercentToAWholeNumber()
    {
        SpawnPlanPoint point = WorkloadGraphGeometry.DragPoint(Plot, 3600, 245.5, 63.7);

        Assert.Equal(20.6, point.TimeMinutes, 6);
        Assert.Equal(73, point.Percent, 6);
    }

    [Fact]
    public void DragPoint_LeftOfThePlot_ClampsTheTimeToZero() => Assert.Equal(0, WorkloadGraphGeometry.DragPoint(Plot, 3600, 12, 110).TimeMinutes, 6);

    [Fact]
    public void DragPoint_RightOfThePlot_ClampsTheTimeToTheLength() =>
        Assert.Equal(60, WorkloadGraphGeometry.DragPoint(Plot, 3600, 1200, 110).TimeMinutes, 6);

    [Fact]
    public void DragPoint_AboveThePlot_ClampsThePercentToHundred() =>
        Assert.Equal(100, WorkloadGraphGeometry.DragPoint(Plot, 3600, 340, -30).Percent, 6);

    [Fact]
    public void DragPoint_BelowThePlot_ClampsThePercentToZero() => Assert.Equal(0, WorkloadGraphGeometry.DragPoint(Plot, 3600, 340, 400).Percent, 6);

    [Fact]
    public void DragPoint_WithNoPlanningLength_YieldsTimeZero()
    {
        SpawnPlanPoint point = WorkloadGraphGeometry.DragPoint(Plot, 0, 640, 110);

        Assert.Equal(0, point.TimeMinutes, 6);
        Assert.Equal(50, point.Percent, 6);
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

    private static SpawnPlanPoint Dot(double timeMinutes, double percent) => new(timeMinutes, percent);
}
