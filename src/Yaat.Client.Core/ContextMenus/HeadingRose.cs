using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// The heading flyout's north-up compass rose, <see cref="Size"/> px square: the rim of radius <see cref="RimRadius"/> with a
/// tick every 5° (longer every 10° and 30°), the 30° labels (N, E, S, W in bold), the needle at the present heading
/// (<c>#5B8DEF</c>), and the pick (the heading under the pointer while pointing, else the one picked) in the flight blue
/// (<c>#A8CAFF</c>) with an inner arc drawing the turn the verb makes (unless <see cref="ShowTurnArc"/> is off), and
/// <see cref="CentreCaption"/> on a disc in the centre when set. Pointing previews; pressing picks, dragging moves the
/// pick and the release sends it (<see cref="Chosen"/>), unless it comes more than <see cref="ReleaseSlack"/> px outside the
/// rim. The panel's keys step the pick (<see cref="StepBy"/>).
/// </summary>
public sealed class HeadingRose : Control
{
    /// <summary>The rose's width and height.</summary>
    public const double Size = 256;

    /// <summary>The rose's centre, on both axes.</summary>
    public const double Centre = Size / 2;

    /// <summary>The rim's radius.</summary>
    public const double RimRadius = 116;

    /// <summary>How far outside the rim a drag's release still sends.</summary>
    public const double ReleaseSlack = 30;

    private const double ArcRadius = 46;
    private static readonly Cursor CrossCursor = new(StandardCursorType.Cross);
    private static readonly IImmutableSolidColorBrush FaceBrush = new ImmutableSolidColorBrush(Color.Parse("#1D2024"));
    private static readonly IImmutableSolidColorBrush NeedleBrush = new ImmutableSolidColorBrush(Color.Parse("#5B8DEF"));
    private static readonly IImmutableSolidColorBrush PickBrush = new ImmutableSolidColorBrush(
        QuickCommandStrip.FamilyColor(QuickCommandGlyphFamily.Flight)
    );
    private static readonly IImmutableSolidColorBrush CardinalBrush = new ImmutableSolidColorBrush(Colors.White);
    private static readonly IImmutableSolidColorBrush LabelBrush = new ImmutableSolidColorBrush(Color.Parse("#B8C0C9"));
    private static readonly ImmutablePen RimPen = new(new ImmutableSolidColorBrush(Color.Parse("#3A414B")), 1);
    private static readonly ImmutablePen MajorTickPen = new(new ImmutableSolidColorBrush(Color.Parse("#9AA3AD")), 1.6);
    private static readonly ImmutablePen MinorTickPen = new(new ImmutableSolidColorBrush(Color.Parse("#4F5762")), 1);
    private static readonly ImmutablePen NeedlePen = new(NeedleBrush, 3, null, PenLineCap.Round);
    private static readonly ImmutablePen ArcPen = new(PickBrush, 2.5, null, PenLineCap.Round);
    private static readonly ImmutablePen PickPen = new(PickBrush, 2);
    private static readonly ImmutablePen PointingPen = new(PickBrush, 2, new ImmutableDashStyle([2, 1.5], 0));
    private static readonly ImmutablePen KnobPen = new(new ImmutableSolidColorBrush(Color.Parse("#0B0D10")), 1.5);
    private static readonly ImmutablePen FocusPen = new(new ImmutableSolidColorBrush(Color.Parse("#8FC1FF")), 2);
    private static readonly ImmutablePen CaptionPen = new(NeedleBrush, 1.2);
    private static readonly IImmutableSolidColorBrush CaptionDiscBrush = new ImmutableSolidColorBrush(Color.Parse("#0B0D10"));

    private double _present = 360;
    private int? _pick;
    private int? _pointing;
    private HeadingVerb _verb;
    private bool _dragging;
    private bool _showTurnArc = true;
    private string? _centreCaption;

    /// <summary>A rose, focusable, with nothing picked.</summary>
    public HeadingRose()
    {
        Width = Size;
        Height = Size;
        Focusable = true;
        Cursor = CrossCursor;
        AutomationProperties.SetName(this, "Heading rose");
    }

    /// <summary>The aircraft's magnetic heading, where the needle points.</summary>
    public double PresentHeading
    {
        get => _present;
        set
        {
            _present = value;
            InvalidateVisual();
        }
    }

    /// <summary>The heading picked (by a press, a drag or the keys), or null.</summary>
    public int? Pick
    {
        get => _pick;
        set
        {
            if (_pick == value)
            {
                return;
            }

            _pick = value;
            InvalidateVisual();
            ShownChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The heading the rose shows: the one under the pointer while pointing, else the pick.</summary>
    public int? Shown => _pointing ?? _pick;

    /// <summary>The verb whose turn the arc draws.</summary>
    public HeadingVerb Verb
    {
        get => _verb;
        set
        {
            _verb = value;
            InvalidateVisual();
        }
    }

    /// <summary>Whether the rose draws the inner arc of the verb's turn to the heading shown; the heading flyout's does, the radial's not.</summary>
    public bool ShowTurnArc
    {
        get => _showTurnArc;
        set
        {
            _showTurnArc = value;
            InvalidateVisual();
        }
    }

    /// <summary>What the rose writes on a disc in its centre (the radial flyout's navaid); null for nothing.</summary>
    public string? CentreCaption
    {
        get => _centreCaption;
        set
        {
            _centreCaption = value;
            InvalidateVisual();
        }
    }

    /// <summary>Raised when <see cref="Shown"/> may have changed.</summary>
    public event EventHandler? ShownChanged;

    /// <summary>Raised with the pick when a press or a drag is released on the rose.</summary>
    public event EventHandler<int>? Chosen;

    /// <summary>Moves the pick by <paramref name="degrees"/>, from the present heading rounded to 5 when nothing is picked.</summary>
    public void StepBy(int degrees)
    {
        SetPointing(null);
        Pick = HeadingFlyout.RoundedToFive((Pick ?? HeadingFlyout.RoundedToFive(_present)) + degrees);
    }

    /// <inheritdoc />
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        int? heading = HeadingUnder(e.GetPosition(this));
        if (!_dragging)
        {
            SetPointing(heading);
        }
        else if (heading is not null)
        {
            Pick = heading;
        }
    }

    /// <inheritdoc />
    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (!_dragging)
        {
            SetPointing(null);
        }
    }

    /// <inheritdoc />
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || (HeadingUnder(e.GetPosition(this)) is not { } heading))
        {
            return;
        }

        e.Handled = true;
        e.Pointer.Capture(this);
        _dragging = true;
        SetPointing(null);
        Pick = heading;
        Focus(NavigationMethod.Pointer);
    }

    /// <inheritdoc />
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging)
        {
            return;
        }

        e.Handled = true;
        _dragging = false;
        e.Pointer.Capture(null);
        Point at = e.GetPosition(this);
        double fromCentre = Math.Sqrt(Math.Pow(at.X - Centre, 2) + Math.Pow(at.Y - Centre, 2));
        if ((fromCentre <= RimRadius + ReleaseSlack) && (Pick is { } pick))
        {
            Chosen?.Invoke(this, pick);
        }
    }

    /// <inheritdoc />
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _dragging = false;
    }

    /// <inheritdoc />
    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        InvalidateVisual();
    }

    /// <inheritdoc />
    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        InvalidateVisual();
    }

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var centre = new Point(Centre, Centre);
        context.DrawEllipse(FaceBrush, RimPen, centre, RimRadius, RimRadius);
        if (IsFocused)
        {
            context.DrawEllipse(null, FocusPen, centre, RimRadius + 4, RimRadius + 4);
        }

        DrawTicks(context);
        DrawLabels(context);
        if (_showTurnArc && (Shown is { } shown))
        {
            DrawTurnArc(context, shown);
        }

        DrawNeedle(context, centre);
        if (Shown is { } pick)
        {
            Point knob = At(pick, RimRadius - 6);
            context.DrawLine((_pointing is null) ? PickPen : PointingPen, centre, knob);
            context.DrawEllipse(PickBrush, KnobPen, knob, 6.5, 6.5);
        }

        if (_centreCaption is { } caption)
        {
            DrawCentreCaption(context, centre, caption);
        }
    }

    /// <summary>The centre disc, edged in the needle's blue, with <paramref name="caption"/> on it in bold.</summary>
    private void DrawCentreCaption(DrawingContext context, Point centre, string caption)
    {
        context.DrawEllipse(CaptionDiscBrush, CaptionPen, centre, 17, 17);
        var text = new FormattedText(
            caption,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(MonoFamily(), FontStyle.Normal, FontWeight.Bold),
            10.5,
            CardinalBrush
        );
        context.DrawText(text, new Point(centre.X - (text.Width / 2), centre.Y - (text.Height / 2)));
    }

    private FontFamily MonoFamily() =>
        (this.TryFindResource(QuickCommandStrip.MonoFontKey, out object? font) && (font is FontFamily mono)) ? mono : FontFamily.Default;

    /// <summary>Forgets the heading under the pointer and any drag under way, as the flyout opens again.</summary>
    internal void ClearPointing()
    {
        _dragging = false;
        SetPointing(null);
    }

    private int? HeadingUnder(Point point) => HeadingFlyout.HeadingAt(point.X - Centre, point.Y - Centre);

    private void SetPointing(int? heading)
    {
        if (_pointing == heading)
        {
            return;
        }

        _pointing = heading;
        InvalidateVisual();
        ShownChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The point <paramref name="radius"/> from the centre at <paramref name="degrees"/>, north up, clockwise.</summary>
    private static Point At(double degrees, double radius)
    {
        double radians = degrees * Math.PI / 180;
        return new Point(Centre + (radius * Math.Sin(radians)), Centre - (radius * Math.Cos(radians)));
    }

    private static void DrawTicks(DrawingContext context)
    {
        for (int heading = HeadingFlyout.Step; heading <= 360; heading += HeadingFlyout.Step)
        {
            bool major = ((heading % 30) == 0);
            double length = major ? 14 : (((heading % 10) == 0) ? 9 : 5);
            context.DrawLine(major ? MajorTickPen : MinorTickPen, At(heading, RimRadius - 1), At(heading, RimRadius - length));
        }
    }

    private void DrawLabels(DrawingContext context)
    {
        FontFamily family = MonoFamily();
        for (int heading = 30; heading <= 360; heading += 30)
        {
            string? cardinal = heading switch
            {
                90 => "E",
                180 => "S",
                270 => "W",
                360 => "N",
                _ => null,
            };
            var text = new FormattedText(
                cardinal ?? heading.ToString("D3", CultureInfo.InvariantCulture),
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface(family, FontStyle.Normal, (cardinal is null) ? FontWeight.Normal : FontWeight.Bold),
                (cardinal is null) ? 10.5 : 12,
                (cardinal is null) ? LabelBrush : CardinalBrush
            );
            Point at = At(heading, RimRadius - 26);
            context.DrawText(text, new Point(at.X - (text.Width / 2), at.Y - (text.Height / 2)));
        }
    }

    /// <summary>The inner arc from the needle round to <paramref name="heading"/> the way the verb turns, with an arrowhead.</summary>
    private void DrawTurnArc(DrawingContext context, int heading)
    {
        (int side, int degrees) = HeadingFlyout.Turn(HeadingFlyout.WholeHeading(_present), _verb, heading);
        if ((side == 0) || (degrees >= 360))
        {
            return;
        }

        var arc = new StreamGeometry();
        using (StreamGeometryContext path = arc.Open())
        {
            path.BeginFigure(At(_present, ArcRadius), false);
            path.ArcTo(
                At(heading, ArcRadius),
                new Avalonia.Size(ArcRadius, ArcRadius),
                0,
                degrees > 180,
                (side > 0) ? SweepDirection.Clockwise : SweepDirection.CounterClockwise
            );
            path.EndFigure(false);
        }

        using (context.PushOpacity(0.85))
        {
            context.DrawGeometry(null, ArcPen, arc);
            context.DrawGeometry(PickBrush, null, Arrowhead(heading, side));
        }
    }

    /// <summary>An arrowhead on the arc's end at <paramref name="degrees"/>, pointing along the turn's way round.</summary>
    private static StreamGeometry Arrowhead(double degrees, int side)
    {
        double radians = degrees * Math.PI / 180;
        var along = new Vector(side * Math.Cos(radians), side * Math.Sin(radians));
        var across = new Vector(-along.Y, along.X);
        Point tip = At(degrees, ArcRadius) + (along * 4);
        Point back = At(degrees, ArcRadius) - (along * 4);
        var head = new StreamGeometry();
        using StreamGeometryContext path = head.Open();
        path.BeginFigure(tip, true);
        path.LineTo(back + (across * 4));
        path.LineTo(back - (across * 4));
        path.EndFigure(true);
        return head;
    }

    private void DrawNeedle(DrawingContext context, Point centre)
    {
        context.DrawLine(NeedlePen, centre, At(_present, RimRadius - 52));
        var tip = new StreamGeometry();
        using (StreamGeometryContext path = tip.Open())
        {
            path.BeginFigure(At(_present, RimRadius - 45), true);
            path.LineTo(At(_present - 8, RimRadius - 60));
            path.LineTo(At(_present + 8, RimRadius - 60));
            path.EndFigure(true);
        }

        context.DrawGeometry(NeedleBrush, null, tip);
        context.DrawEllipse(NeedleBrush, null, centre, 5, 5);
    }
}
