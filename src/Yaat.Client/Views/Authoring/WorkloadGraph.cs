using System.Collections.Immutable;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
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
/// The spawn planner's workload graph: minutes of the planning length across, the previewed spawns per bucket as bars
/// against the left axis, and in Workload mode the curve as a line with its control points as dots against the percent
/// axis on the right. It copies the planner into a <see cref="WorkloadGraphSnapshot"/> whenever the planner changes, and a
/// frame draws from that snapshot alone. In Workload mode a drag from a control point's dot writes that row's time and
/// percent, so the curve and the row stay in step.
/// </summary>
public sealed class WorkloadGraph : Control
{
    private const double LeftMargin = 34;
    private const double RightMargin = 40;
    private const double TopMargin = 10;
    private const double BottomMargin = 22;
    private const double LabelSize = 10;
    private const double DotRadius = 3.5;

    // The tap size used when the platform reports none, as the airport editor's canvas does.
    private const double ClickSlop = 4;

    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);
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
    private PointDrag? _drag;

    /// <summary>
    /// Initializes a new instance of the <see cref="WorkloadGraph"/> class; it takes the focus on a press so Escape
    /// reaches a drag, without becoming a Tab stop.
    /// </summary>
    public WorkloadGraph()
    {
        Focusable = true;
        IsTabStop = false;
    }

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

    /// <summary>Stops following the planner; the last frame stays, and a drag in flight is dropped.</summary>
    public void Detach()
    {
        DropDrag();
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

    /// <inheritdoc/>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPointerPressed(e);

        // One pointer owns a drag: a second press while one runs is not a grab.
        if ((_drag is not null) || (e.GetCurrentPoint(this).Properties.PointerUpdateKind.GetMouseButton() != MouseButton.Left))
        {
            return;
        }

        Point at = e.GetPosition(this);
        if (PointAt(at) is not { } row)
        {
            return;
        }

        _drag = new PointDrag(row, at, TapSize(e.Pointer.Type), e.Pointer);
        Focus();
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    /// <inheritdoc/>
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPointerMoved(e);
        Point at = e.GetPosition(this);
        if (_drag is { } drag)
        {
            if (e.Pointer == drag.Pointer)
            {
                UpdateDrag(drag, at);
                e.Handled = true;
            }

            return;
        }

        Cursor = (PointAt(at) is null) ? null : HandCursor;
    }

    /// <inheritdoc/>
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPointerReleased(e);
        if ((_drag is not { } drag) || (e.Pointer != drag.Pointer) || (e.InitialPressMouseButton != MouseButton.Left))
        {
            return;
        }

        UpdateDrag(drag, e.GetPosition(this));
        _drag = null;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    /// <inheritdoc/>
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPointerCaptureLost(e);
        CancelDrag();
    }

    /// <inheritdoc/>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnKeyDown(e);
        if ((_drag is null) || (e.Key != Key.Escape))
        {
            return;
        }

        CancelDrag();
        e.Handled = true;
    }

    // The row whose dot is under the pixel; the snapshot's points are the planner's rows in their order.
    private WorkloadPointRowViewModel? PointAt(Point at)
    {
        if (_planner is not { } planner)
        {
            return null;
        }

        GraphPlot plot = PlotOf(_snapshot.Width, _snapshot.Height);
        int? index = WorkloadGraphGeometry.HitPoint(plot, _snapshot.LengthSec, _snapshot.Points, at.X, at.Y);
        return ((index is { } hit) && (hit < planner.ControlPoints.Count)) ? planner.ControlPoints[hit] : null;
    }

    // Past the tap size the row follows the pointer; its setters redraw the curve and the row's text boxes.
    private void UpdateDrag(PointDrag drag, Point at)
    {
        Vector travel = at - drag.PressedAt;
        bool past = (Math.Abs(travel.X) > drag.TapSize.Width / 2) || (Math.Abs(travel.Y) > drag.TapSize.Height / 2);
        if ((!drag.Moving) && (!past))
        {
            return;
        }

        drag.Moving = true;
        GraphPlot plot = PlotOf(_snapshot.Width, _snapshot.Height);
        SpawnPlanPoint point = WorkloadGraphGeometry.DragPoint(plot, _snapshot.LengthSec, at.X, at.Y);
        drag.Row.TimeMinutes = point.TimeMinutes;
        drag.Row.Percent = point.Percent;
    }

    // Drops a drag, releasing the pointer only where the graph still holds it: a capture another element took belongs
    // to that element now, and the loss that brought us here left it in place.
    private void DropDrag()
    {
        if (_drag is not { } drag)
        {
            return;
        }

        _drag = null;
        if (drag.Pointer.Captured == this)
        {
            drag.Pointer.Capture(null);
        }
    }

    // Drops a drag that is abandoned rather than released, putting its row back where the pointer grabbed it.
    private void CancelDrag()
    {
        if (_drag is not { } drag)
        {
            return;
        }

        DropDrag();
        if (!drag.Moving)
        {
            return;
        }

        drag.Row.TimeMinutes = drag.FromMinutes;
        drag.Row.Percent = drag.FromPercent;
    }

    // The platform's tap size for the pointer, 4 px square without platform settings.
    private static Size TapSize(PointerType type) => Application.Current?.PlatformSettings?.GetTapSize(type) ?? new Size(ClickSlop, ClickSlop);

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
        DropDrag();
        UnwatchPoints();
        WatchPoints();
        Refresh();
    }

    // One control point's dot being dragged: its row, where the pointer pressed, the tap size, and the values the row
    // held then, to put back when the drag is dropped rather than released.
    private sealed class PointDrag(WorkloadPointRowViewModel row, Point pressedAt, Size tapSize, IPointer pointer)
    {
        public WorkloadPointRowViewModel Row { get; } = row;

        public double FromMinutes { get; } = row.TimeMinutes;

        public double FromPercent { get; } = row.Percent;

        public Point PressedAt { get; } = pressedAt;

        public Size TapSize { get; } = tapSize;

        public IPointer Pointer { get; } = pointer;

        public bool Moving { get; set; }
    }
}
