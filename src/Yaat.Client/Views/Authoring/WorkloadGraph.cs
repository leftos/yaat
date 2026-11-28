using System.Collections.Immutable;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Yaat.Client.ViewModels.Authoring;

namespace Yaat.Client.Views.Authoring;

/// <summary>
/// What one workload graph frame draws, copied from the planner when it changes: the control's size, the planning length,
/// the bucket size, the previewed spawns per bucket, and in Workload mode the curve's samples and control points.
/// </summary>
/// <param name="Width">The control's width.</param>
/// <param name="Height">The control's height.</param>
/// <param name="LengthSec">The planning length in seconds.</param>
/// <param name="BucketSec">The bucket size in seconds.</param>
/// <param name="Bars">The previewed spawns per bucket; empty with no preview.</param>
/// <param name="Curve">The workload curve's samples in percent; empty outside Workload mode.</param>
/// <param name="Points">The curve's control points; empty outside Workload mode.</param>
public sealed record WorkloadGraphSnapshot(
    double Width,
    double Height,
    int LengthSec,
    int BucketSec,
    ImmutableArray<int> Bars,
    ImmutableArray<double> Curve,
    ImmutableArray<SpawnPlanPoint> Points
)
{
    /// <summary>Gets the frame with nothing to draw.</summary>
    public static WorkloadGraphSnapshot Empty { get; } = new(0, 0, 0, SpawnPlanWorkload.Default.BucketSizeSec, [], [], []);

    /// <summary>The planner's previewed spawns per bucket of its bucket size over its planning length.</summary>
    /// <param name="planner">The planner.</param>
    /// <returns>The counts; empty with no preview.</returns>
    public static ImmutableArray<int> BarsOf(SpawnPlannerViewModel planner)
    {
        ArgumentNullException.ThrowIfNull(planner);
        return planner.HasPreview
            ? WorkloadGraphGeometry.Buckets(planner.PreviewRows.Select(row => row.NewDelaySec), planner.PlanningLengthSec, planner.BucketSizeSec)
            : [];
    }

    /// <summary>Copies what the planner shows now into a frame of the given size.</summary>
    /// <param name="planner">The planner.</param>
    /// <param name="width">The control's width.</param>
    /// <param name="height">The control's height.</param>
    /// <returns>The frame.</returns>
    public static WorkloadGraphSnapshot Of(SpawnPlannerViewModel planner, double width, double height)
    {
        ArgumentNullException.ThrowIfNull(planner);
        int lengthSec = planner.PlanningLengthSec;
        int bucketSec = planner.BucketSizeSec;
        ImmutableArray<int> bars = BarsOf(planner);
        if (planner.Mode != SpawnPlannerMode.Workload)
        {
            return new WorkloadGraphSnapshot(width, height, lengthSec, bucketSec, bars, [], []);
        }

        ImmutableArray<double> curve = (planner.BuildCurve() is { } built) ? [.. built.Samples] : [];
        ImmutableArray<SpawnPlanPoint> points = [.. planner.ControlPoints.Select(point => point.ToPlan())];
        return new WorkloadGraphSnapshot(width, height, lengthSec, bucketSec, bars, curve, points);
    }
}

/// <summary>
/// The spawn planner's read-only workload graph: minutes of the planning length across, the previewed spawns per bucket as
/// bars against the left axis, and in Workload mode the curve as a line with its control points as dots against the
/// percent axis on the right. It copies the planner into a <see cref="WorkloadGraphSnapshot"/> whenever the planner
/// changes, and a frame draws from that snapshot alone.
/// </summary>
public sealed class WorkloadGraph : Control
{
    private const double LeftMargin = 34;
    private const double RightMargin = 40;
    private const double TopMargin = 10;
    private const double BottomMargin = 22;
    private const double LabelSize = 10;
    private const double DotRadius = 3.5;

    private static readonly IImmutableSolidColorBrush BackgroundBrush = new ImmutableSolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A));
    private static readonly IImmutableSolidColorBrush BarBrush = new ImmutableSolidColorBrush(Color.FromRgb(0x6C, 0xB4, 0xEE));
    private static readonly IImmutableSolidColorBrush CurveBrush = new ImmutableSolidColorBrush(Color.FromRgb(0xE0, 0xA3, 0x3A));
    private static readonly IImmutableSolidColorBrush LabelBrush = new ImmutableSolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xB0));
    private static readonly ImmutablePen AxisPen = new(new ImmutableSolidColorBrush(Color.FromRgb(0x5A, 0x5A, 0x5A)), 1);
    private static readonly ImmutablePen CurvePen = new(CurveBrush, 2);

    private readonly List<WorkloadPointRowViewModel> _watched = [];
    private SpawnPlannerViewModel? _planner;
    private WorkloadGraphSnapshot _snapshot = WorkloadGraphSnapshot.Empty;
    private Size _size;

    /// <summary>Gets the frame the next render draws.</summary>
    public WorkloadGraphSnapshot Snapshot => _snapshot;

    /// <summary>Gets the plot area inside a control of the given size, inside the axis margins.</summary>
    /// <param name="width">The control's width.</param>
    /// <param name="height">The control's height.</param>
    /// <returns>The plot area.</returns>
    public static GraphPlot PlotOf(double width, double height) =>
        new(LeftMargin, TopMargin, Math.Max(0, width - LeftMargin - RightMargin), Math.Max(0, height - TopMargin - BottomMargin));

    /// <summary>Follows a planner, replacing the one followed before, and copies it into a new frame.</summary>
    /// <param name="planner">The planner, or null to follow none and draw nothing but the background.</param>
    public void Attach(SpawnPlannerViewModel? planner)
    {
        Detach();
        _planner = planner;
        if (planner is not null)
        {
            planner.PropertyChanged += OnPlannerChanged;
            planner.PreviewRows.CollectionChanged += OnPreviewRowsChanged;
            planner.ControlPoints.CollectionChanged += OnCollectionChanged;
            WatchPoints();
        }

        Refresh();
    }

    /// <summary>Stops following the planner; the last frame stays.</summary>
    public void Detach()
    {
        if (_planner is { } planner)
        {
            planner.PropertyChanged -= OnPlannerChanged;
            planner.PreviewRows.CollectionChanged -= OnPreviewRowsChanged;
            planner.ControlPoints.CollectionChanged -= OnCollectionChanged;
        }

        UnwatchPoints();
        _planner = null;
    }

    /// <inheritdoc/>
    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        WorkloadGraphSnapshot frame = _snapshot;
        context.FillRectangle(BackgroundBrush, new Rect(0, 0, frame.Width, frame.Height));
        if (frame.LengthSec <= 0)
        {
            return;
        }

        GraphPlot plot = PlotOf(frame.Width, frame.Height);
        int maxCount = frame.Bars.IsEmpty ? 1 : Math.Max(1, frame.Bars.Max());
        DrawBars(context, frame, plot, maxCount);
        DrawAxes(context, frame, plot, maxCount);
        DrawCurve(context, frame, plot);
        DrawPoints(context, frame, plot);
    }

    /// <inheritdoc/>
    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnSizeChanged(e);
        _size = e.NewSize;
        Refresh();
    }

    private static FormattedText Label(string text) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, LabelSize, LabelBrush);

    private static void DrawBars(DrawingContext context, WorkloadGraphSnapshot frame, GraphPlot plot, int maxCount)
    {
        var scale = new BarScale(frame.BucketSec, frame.LengthSec, maxCount);
        for (int i = 0; i < frame.Bars.Length; i++)
        {
            GraphPlot box = WorkloadGraphGeometry.BarBox(plot, scale, i, frame.Bars[i]);
            if (box.Height > 0)
            {
                context.FillRectangle(BarBrush, new Rect(box.Left, box.Top, box.Width, box.Height));
            }
        }
    }

    private static void DrawAxes(DrawingContext context, WorkloadGraphSnapshot frame, GraphPlot plot, int maxCount)
    {
        context.DrawLine(AxisPen, new Point(plot.Left, plot.Top), new Point(plot.Left, plot.Bottom));
        context.DrawLine(AxisPen, new Point(plot.Left, plot.Bottom), new Point(plot.Right, plot.Bottom));
        foreach (int minute in WorkloadGraphGeometry.TickMinutes(frame.LengthSec))
        {
            double x = WorkloadGraphGeometry.TimeToX(plot, frame.LengthSec, minute * 60.0);
            context.DrawLine(AxisPen, new Point(x, plot.Bottom), new Point(x, plot.Bottom + 4));
            FormattedText label = Label(minute.ToString(CultureInfo.InvariantCulture));
            context.DrawText(label, new Point(x - (label.Width / 2), plot.Bottom + 5));
        }

        FormattedText top = Label(maxCount.ToString(CultureInfo.InvariantCulture));
        context.DrawText(top, new Point(plot.Left - top.Width - 4, plot.Top - (top.Height / 2)));
        FormattedText zero = Label("0");
        context.DrawText(zero, new Point(plot.Left - zero.Width - 4, plot.Bottom - (zero.Height / 2)));
        if (!frame.Curve.IsEmpty)
        {
            context.DrawLine(AxisPen, new Point(plot.Right, plot.Top), new Point(plot.Right, plot.Bottom));
            FormattedText full = Label("100 %");
            context.DrawText(full, new Point(plot.Right + 4, plot.Top - (full.Height / 2)));
            FormattedText none = Label("0 %");
            context.DrawText(none, new Point(plot.Right + 4, plot.Bottom - (none.Height / 2)));
        }
    }

    private static void DrawCurve(DrawingContext context, WorkloadGraphSnapshot frame, GraphPlot plot)
    {
        if (frame.Curve.Length < 2)
        {
            return;
        }

        var line = new StreamGeometry();
        using (StreamGeometryContext path = line.Open())
        {
            (double x0, double y0) = WorkloadGraphGeometry.SamplePoint(plot, 0, frame.Curve.Length, frame.Curve[0]);
            path.BeginFigure(new Point(x0, y0), false);
            for (int i = 1; i < frame.Curve.Length; i++)
            {
                (double x, double y) = WorkloadGraphGeometry.SamplePoint(plot, i, frame.Curve.Length, frame.Curve[i]);
                path.LineTo(new Point(x, y));
            }

            path.EndFigure(false);
        }

        context.DrawGeometry(null, CurvePen, line);
    }

    // The control points are the author's rows, drawn even when the curve cannot be built from them.
    private static void DrawPoints(DrawingContext context, WorkloadGraphSnapshot frame, GraphPlot plot)
    {
        foreach (SpawnPlanPoint point in frame.Points)
        {
            if (WorkloadGraphGeometry.PointPixel(plot, frame.LengthSec, point) is { } at)
            {
                context.DrawEllipse(CurveBrush, null, new Point(at.X, at.Y), DotRadius, DotRadius);
            }
        }
    }

    private void Refresh()
    {
        _snapshot =
            (_planner is { } planner)
                ? WorkloadGraphSnapshot.Of(planner, _size.Width, _size.Height)
                : WorkloadGraphSnapshot.Empty with
                {
                    Width = _size.Width,
                    Height = _size.Height,
                };
        InvalidateVisual();
    }

    private void WatchPoints()
    {
        if (_planner is not { } planner)
        {
            return;
        }

        foreach (WorkloadPointRowViewModel point in planner.ControlPoints)
        {
            point.PropertyChanged += OnPointChanged;
            _watched.Add(point);
        }
    }

    private void UnwatchPoints()
    {
        foreach (WorkloadPointRowViewModel point in _watched)
        {
            point.PropertyChanged -= OnPointChanged;
        }

        _watched.Clear();
    }

    private void OnPlannerChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

    private void OnPointChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

    // A preview row changes only the bars, so the curve and the point hooks stay as they are.
    private void OnPreviewRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_planner is { } planner)
        {
            _snapshot = _snapshot with { Bars = WorkloadGraphSnapshot.BarsOf(planner) };
            InvalidateVisual();
        }
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UnwatchPoints();
        WatchPoints();
        Refresh();
    }
}
