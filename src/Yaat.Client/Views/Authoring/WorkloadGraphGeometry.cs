using System.Collections.Immutable;
using Yaat.Client.ViewModels.Authoring;

namespace Yaat.Client.Views.Authoring;

/// <summary>The workload graph's plot area in pixels, inside the control's bounds.</summary>
/// <param name="Left">The plot's left edge, where the planning length starts.</param>
/// <param name="Top">The plot's top edge, where the counts and the curve reach their top.</param>
/// <param name="Width">The plot's width.</param>
/// <param name="Height">The plot's height.</param>
public readonly record struct GraphPlot(double Left, double Top, double Width, double Height)
{
    /// <summary>Gets the plot's right edge, where the planning length ends.</summary>
    public double Right => Left + Width;

    /// <summary>Gets the plot's bottom edge, the zero line of the counts and the curve.</summary>
    public double Bottom => Top + Height;
}

/// <summary>
/// The workload graph's maths, with no drawing: spawn delays counted into buckets over the planning length, a time in
/// seconds or a workload percent mapped to a pixel of the plot and back, and the time axis's ticks.
/// </summary>
public static class WorkloadGraphGeometry
{
    /// <summary>The planning length from which the time axis ticks every 10 minutes rather than every 5.</summary>
    public const int LongLengthSec = 30 * 60;

    /// <summary>The gap in pixels a bar keeps from each side of its bucket, so neighbouring bars stay apart.</summary>
    public const double BarInset = 1;

    /// <summary>How many buckets of the bucket size cover the planning length, the last one possibly short.</summary>
    /// <param name="lengthSec">The planning length in seconds.</param>
    /// <param name="bucketSec">The bucket size in seconds.</param>
    /// <returns>The bucket count; zero when the length or the bucket size is not above zero.</returns>
    public static int BucketCount(int lengthSec, int bucketSec) =>
        ((lengthSec <= 0) || (bucketSec <= 0)) ? 0 : (int)Math.Ceiling(lengthSec / (double)bucketSec);

    /// <summary>
    /// Counts the spawn delays into buckets of the bucket size over the planning length. A delay at or after the length
    /// falls in the last bucket and one before zero in the first, so every delay is counted.
    /// </summary>
    /// <param name="delaysSec">The spawn delays in seconds.</param>
    /// <param name="lengthSec">The planning length in seconds.</param>
    /// <param name="bucketSec">The bucket size in seconds.</param>
    /// <returns>The count of each bucket, from the start; empty when there is no bucket.</returns>
    public static ImmutableArray<int> Buckets(IEnumerable<int> delaysSec, int lengthSec, int bucketSec)
    {
        ArgumentNullException.ThrowIfNull(delaysSec);
        int count = BucketCount(lengthSec, bucketSec);
        if (count == 0)
        {
            return [];
        }

        int[] counts = new int[count];
        foreach (int delay in delaysSec)
        {
            counts[Math.Clamp(delay / bucketSec, 0, count - 1)]++;
        }

        return [.. counts];
    }

    /// <summary>The pixel column of a time.</summary>
    /// <param name="plot">The plot area.</param>
    /// <param name="lengthSec">The planning length in seconds, which spans the plot's width.</param>
    /// <param name="timeSec">The time in seconds from the start.</param>
    /// <returns>The x pixel; the plot's left edge when the length is not above zero.</returns>
    public static double TimeToX(GraphPlot plot, int lengthSec, double timeSec) =>
        (lengthSec <= 0) ? plot.Left : plot.Left + (plot.Width * timeSec / lengthSec);

    /// <summary>The time a pixel column shows.</summary>
    /// <param name="plot">The plot area.</param>
    /// <param name="lengthSec">The planning length in seconds, which spans the plot's width.</param>
    /// <param name="x">The x pixel.</param>
    /// <returns>The time in seconds from the start; zero when the plot has no width.</returns>
    public static double XToTime(GraphPlot plot, int lengthSec, double x) => (plot.Width <= 0) ? 0 : (x - plot.Left) / plot.Width * lengthSec;

    /// <summary>The pixel row of a workload percent, 0 % at the bottom and 100 % at the top.</summary>
    /// <param name="plot">The plot area.</param>
    /// <param name="percent">The workload percent.</param>
    /// <returns>The y pixel.</returns>
    public static double PercentToY(GraphPlot plot, double percent) => plot.Bottom - (plot.Height * percent / 100.0);

    /// <summary>The workload percent a pixel row shows.</summary>
    /// <param name="plot">The plot area.</param>
    /// <param name="y">The y pixel.</param>
    /// <returns>The percent; zero when the plot has no height.</returns>
    public static double YToPercent(GraphPlot plot, double y) => (plot.Height <= 0) ? 0 : (plot.Bottom - y) / plot.Height * 100.0;

    /// <summary>The pixel row of a bucket count, zero at the bottom and the largest count at the top.</summary>
    /// <param name="plot">The plot area.</param>
    /// <param name="maxCount">The largest count the axis shows; below one counts as one.</param>
    /// <param name="count">The count.</param>
    /// <returns>The y pixel.</returns>
    public static double CountToY(GraphPlot plot, int maxCount, int count) => plot.Bottom - (plot.Height * count / Math.Max(1, maxCount));

    /// <summary>
    /// The box one bucket's bar fills: its time span across, less <see cref="BarInset"/> each side, and its count up from
    /// the bottom.
    /// </summary>
    /// <param name="plot">The plot area.</param>
    /// <param name="frame">The bucket size, planning length and largest count the frame draws with.</param>
    /// <param name="index">The bucket's index from the start.</param>
    /// <param name="count">The bucket's count.</param>
    /// <returns>The bar as a box in pixels.</returns>
    public static GraphPlot BarBox(GraphPlot plot, BarScale frame, int index, int count)
    {
        double left = TimeToX(plot, frame.LengthSec, (double)index * frame.BucketSec);
        double right = TimeToX(plot, frame.LengthSec, Math.Min((double)(index + 1) * frame.BucketSec, frame.LengthSec));
        double top = CountToY(plot, frame.MaxCount, count);
        return new GraphPlot(left + BarInset, top, Math.Max(0, right - left - (2 * BarInset)), Math.Max(0, plot.Bottom - top));
    }

    /// <summary>The pixel of one control point, its time clamped to the planning length and its percent to 0-100.</summary>
    /// <param name="plot">The plot area.</param>
    /// <param name="lengthSec">The planning length in seconds.</param>
    /// <param name="point">The control point, its time in minutes.</param>
    /// <returns>The x and y pixels, or null when the point's time or percent is not a finite number.</returns>
    public static (double X, double Y)? PointPixel(GraphPlot plot, int lengthSec, SpawnPlanPoint point)
    {
        ArgumentNullException.ThrowIfNull(point);
        if ((!double.IsFinite(point.TimeMinutes)) || (!double.IsFinite(point.Percent)))
        {
            return null;
        }

        double timeSec = Math.Clamp(point.TimeMinutes * 60, 0, Math.Max(0, lengthSec));
        return (TimeToX(plot, lengthSec, timeSec), PercentToY(plot, Math.Clamp(point.Percent, 0, 100)));
    }

    /// <summary>The pixel of one curve sample, the samples spread evenly across the plot's width.</summary>
    /// <param name="plot">The plot area.</param>
    /// <param name="index">The sample's index.</param>
    /// <param name="sampleCount">How many samples the curve holds.</param>
    /// <param name="percent">The sample's workload percent.</param>
    /// <returns>The x and y pixels.</returns>
    public static (double X, double Y) SamplePoint(GraphPlot plot, int index, int sampleCount, double percent)
    {
        double fraction = (sampleCount <= 1) ? 0 : index / (double)(sampleCount - 1);
        return (plot.Left + (plot.Width * fraction), PercentToY(plot, percent));
    }

    /// <summary>The time axis's tick step: 10 minutes from a 30-minute planning length, 5 minutes under it.</summary>
    /// <param name="lengthSec">The planning length in seconds.</param>
    /// <returns>The step in minutes.</returns>
    public static int TickStepMinutes(int lengthSec) => (lengthSec < LongLengthSec) ? 5 : 10;

    /// <summary>The minutes the time axis labels: every tick step from zero up to the planning length.</summary>
    /// <param name="lengthSec">The planning length in seconds.</param>
    /// <returns>The tick minutes; empty when the length is not above zero.</returns>
    public static ImmutableArray<int> TickMinutes(int lengthSec)
    {
        if (lengthSec <= 0)
        {
            return [];
        }

        int step = TickStepMinutes(lengthSec);
        ImmutableArray<int>.Builder ticks = ImmutableArray.CreateBuilder<int>();
        for (int minute = 0; minute * 60 <= lengthSec; minute += step)
        {
            ticks.Add(minute);
        }

        return ticks.ToImmutable();
    }
}

/// <summary>What a bar's box is scaled by: the bucket size, the planning length and the largest count.</summary>
/// <param name="BucketSec">The bucket size in seconds.</param>
/// <param name="LengthSec">The planning length in seconds.</param>
/// <param name="MaxCount">The largest bucket count, the top of the count axis.</param>
public readonly record struct BarScale(int BucketSec, int LengthSec, int MaxCount);
