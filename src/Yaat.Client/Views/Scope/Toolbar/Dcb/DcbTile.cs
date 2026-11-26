using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Yaat.Client.Views.Scope.Toolbar.Dcb;

/// <summary>A tile's interaction, CRC's DcbButtonType: a press, a flip, a latch or nothing at all.</summary>
public enum DcbTileKind
{
    /// <summary>Draws pushed while held and runs <see cref="DcbTile.OnClick"/> on a release inside it.</summary>
    Normal,

    /// <summary>Flips <see cref="DcbTile.IsPushed"/> on a press and runs <see cref="DcbTile.OnToggle"/> with the new state.</summary>
    Toggle,

    /// <summary>Latches on a press and holds the pointer until a second press accepts or Esc cancels.</summary>
    Spinner,

    /// <summary>Drawn with every colour dimmed, taking no input.</summary>
    Disabled,
}

/// <summary>
/// One tile of a STARS DCB page at YAAT size: a filled quad with CRC's bevel and its label in up to three centred lines of
/// bold monospace. A <see cref="DcbTileKind.Normal"/> tile draws pushed while held and clicks on a release inside it, a
/// <see cref="DcbTileKind.Toggle"/> flips on a press, a <see cref="DcbTileKind.Spinner"/> latches a
/// <see cref="DcbSpinnerLatch"/> and holds the pointer until a second press on it accepts or Esc cancels, and a
/// <see cref="DcbTileKind.Disabled"/> tile is dimmed and takes no input.
/// </summary>
public sealed class DcbTile : Control
{
    /// <summary>The most label lines a tile draws.</summary>
    private const int MaxLines = 3;

    /// <summary>How many characters of a label line a tile draws, as CRC cuts a line to 9 at its smallest font.</summary>
    private const int MaxLineChars = 9;

    /// <summary>The label's font size, in DIPs.</summary>
    private const double LabelFontSize = 9;

    /// <summary>The height of one label line, in DIPs.</summary>
    private const double LineHeight = 10;

    /// <summary>The key of the app's monospace font family, which the Scope toolbar draws in too.</summary>
    private const string MonoFontKey = "MonoFont";

    private string _label;
    private DcbSpinnerLatch? _spinner;
    private TopLevel? _held;
    private FontFamily? _fontFamily;
    private bool _isHovering;
    private bool _isPushed;
    private bool _isAvailable;
    private double _value;
    private double _minValue;
    private double _maxValue = 100;
    private double _step = 1;

    /// <summary>A tile of <paramref name="shape"/> with <paramref name="kind"/>'s interaction, labelled <paramref name="label"/>.</summary>
    public DcbTile(DcbTileShape shape, DcbTileKind kind, string label)
    {
        Shape = shape;
        Kind = kind;
        _label = label;
        Width = shape.Width();
        Height = shape.Height();
        Focusable = kind != DcbTileKind.Disabled;
        AutomationProperties.SetName(this, label);
    }

    /// <summary>The tile's shape and size on the bar.</summary>
    public DcbTileShape Shape { get; }

    /// <summary>The tile's interaction.</summary>
    public DcbTileKind Kind { get; }

    /// <summary>
    /// The label the tile draws: up to three lines split by newlines, cut to nine characters each, with a spinner's
    /// <c>%value%</c> replaced by the value it shows.
    /// </summary>
    public string Label
    {
        get => _label;
        set
        {
            _label = value;
            AutomationProperties.SetName(this, value);
            InvalidateVisual();
        }
    }

    /// <summary>A spinner's value: what its latch starts at, and what an accept leaves behind.</summary>
    public double Value
    {
        get => _value;
        set
        {
            _value = value;
            InvalidateVisual();
        }
    }

    /// <summary>The lowest value a spinner's wheel reaches.</summary>
    public double MinValue
    {
        get => _minValue;
        set
        {
            _minValue = value;
            InvalidateVisual();
        }
    }

    /// <summary>The highest value a spinner's wheel reaches.</summary>
    public double MaxValue
    {
        get => _maxValue;
        set
        {
            _maxValue = value;
            InvalidateVisual();
        }
    }

    /// <summary>What one wheel notch adds to or takes from a spinner's value.</summary>
    public double Step
    {
        get => _step;
        set
        {
            _step = value;
            InvalidateVisual();
        }
    }

    /// <summary>Whether a toggle or weather tile is on, and whether a normal tile is held down.</summary>
    public bool IsPushed
    {
        get => _isPushed;
        set
        {
            _isPushed = value;
            InvalidateVisual();
        }
    }

    /// <summary>Whether a weather tile's data is available, drawing its background in CRC's blue until it is pushed.</summary>
    public bool IsAvailable
    {
        get => _isAvailable;
        set
        {
            _isAvailable = value;
            InvalidateVisual();
        }
    }

    /// <summary>Whether the spinner holds the pointer.</summary>
    public bool IsLatched => _spinner is not null;

    /// <summary>What a click on a normal tile runs.</summary>
    public Action? OnClick { get; set; }

    /// <summary>What a press on a toggle tile runs, with the state it flipped to.</summary>
    public Action<bool>? OnToggle { get; set; }

    /// <summary>What accepting a latched spinner runs, with the value the wheel left.</summary>
    public Action<double>? OnAccept { get; set; }

    /// <summary>The colours the tile draws with, after the dimming a disabled tile gets: its fill and its two bevel colours.</summary>
    public (Color Fill, Color TopLeft, Color BottomRight) Palette =>
        Dimmed
            ? (DcbColors.Dim(FillOf(Pressed)), DcbColors.Dim(BevelTopLeft()), DcbColors.Dim(BevelBottomRight()))
            : (FillOf(Pressed), BevelTopLeft(), BevelBottomRight());

    /// <summary>The colour the tile fills its quad with.</summary>
    public Color FillColor => Palette.Fill;

    /// <summary>The colour the tile draws its label in.</summary>
    public Color TextColor => Dimmed ? DcbColors.Dim(DcbColors.Text) : (Hovered ? DcbColors.HoverText : DcbColors.Text);

    private bool Dimmed => Kind == DcbTileKind.Disabled;

    private bool Pressed => IsLatched || IsPushed;

    private bool Hovered => _isHovering && !Dimmed;

    private double ShownValue => _spinner?.Value ?? Value;

    private Typeface Typeface => new(_fontFamily ?? FontFamily.Default, FontStyle.Normal, FontWeight.Bold);

    private string LabelText =>
        (Kind == DcbTileKind.Spinner) ? Label.Replace("%value%", ShownValue.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal) : Label;

    /// <inheritdoc/>
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        Rect bounds = new(Bounds.Size);
        context.FillRectangle(new ImmutableSolidColorBrush(FillColor), bounds);
        DrawBevel(context, bounds);
        DrawLabel(context);
    }

    /// <inheritdoc/>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (this.TryFindResource(MonoFontKey, out object? value) && (value is FontFamily family))
        {
            _fontFamily = family;
        }
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        // A latched tile that leaves the tree stops holding the pointer: its Cancel restores the value the latch took.
        if (_spinner is { } spinner)
        {
            spinner.Escape();
            Unlatch(spinner.Value);
        }

        base.OnDetachedFromVisualTree(e);
    }

    /// <inheritdoc/>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        PointerPoint point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed)
        {
            // The latch holds the pointer, so a right press on it is swallowed rather than opening a menu.
            e.Handled = (Kind == DcbTileKind.Spinner) && IsLatched && point.Properties.IsRightButtonPressed;
            return;
        }

        if (Kind == DcbTileKind.Disabled)
        {
            return;
        }

        e.Handled = true;
        switch (Kind)
        {
            case DcbTileKind.Normal:
                IsPushed = true;
                break;
            case DcbTileKind.Toggle:
                Flip();
                break;
            default:
                PressSpinner(e);
                break;
        }
    }

    /// <inheritdoc/>
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if ((Kind != DcbTileKind.Normal) || !IsPushed)
        {
            return;
        }

        IsPushed = false;
        e.Handled = true;
        if (new Rect(Bounds.Size).Contains(e.GetPosition(this)))
        {
            OnClick?.Invoke();
        }
    }

    /// <inheritdoc/>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if ((e.Key != Key.Escape) || (_spinner is not { } spinner))
        {
            return;
        }

        if (spinner.Escape() is { } cancelled)
        {
            Unlatch(cancelled.Value);
            e.Handled = true;
        }
    }

    /// <inheritdoc/>
    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        _isHovering = true;
        InvalidateVisual();
    }

    /// <inheritdoc/>
    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _isHovering = false;
        if (Kind == DcbTileKind.Normal)
        {
            // CRC's DcbButton.HandleMouseLeave: a normal tile held down and left stops drawing pushed, so its release
            // elsewhere never clicks.
            IsPushed = false;
        }

        InvalidateVisual();
    }

    private void Flip()
    {
        IsPushed = !IsPushed;
        OnToggle?.Invoke(IsPushed);
    }

    /// <summary>
    /// A left press on a spinner: the first latches it and holds the pointer on it through the top level's tunnel handlers,
    /// and while latched a press inside it accepts the value, so a press anywhere else is swallowed.
    /// </summary>
    private void PressSpinner(PointerPressedEventArgs e)
    {
        if (_spinner is not { } spinner)
        {
            _spinner = new DcbSpinnerLatch(Value, MinValue, MaxValue, Step);
            _spinner.Press();
            HoldPresses();
            Focus();
            InvalidateVisual();
            return;
        }

        if (!new Rect(Bounds.Size).Contains(e.GetPosition(this)))
        {
            return;
        }

        DcbLatchResult accepted = spinner.Press();
        Unlatch(accepted.Value);
        OnAccept?.Invoke(accepted.Value);
    }

    /// <summary>Ends the latch at <paramref name="value"/> and lets the pointer go.</summary>
    private void Unlatch(double value)
    {
        _spinner = null;
        Value = value;
        ReleasePresses();
        InvalidateVisual();
    }

    /// <summary>
    /// Holds the pointer on this tile while it is latched: the top level's tunnel handlers swallow every press and release
    /// elsewhere and send the wheel to the latch wherever the pointer is, so no other control sees the pointer meanwhile.
    /// </summary>
    private void HoldPresses()
    {
        _held = TopLevel.GetTopLevel(this);
        if (_held is null)
        {
            return;
        }

        _held.AddHandler(PointerPressedEvent, OnHeldPress, RoutingStrategies.Tunnel);
        _held.AddHandler(PointerReleasedEvent, OnHeldRelease, RoutingStrategies.Tunnel);
        _held.AddHandler(PointerWheelChangedEvent, OnHeldWheel, RoutingStrategies.Tunnel);
    }

    private void ReleasePresses()
    {
        if (_held is not { } top)
        {
            return;
        }

        top.RemoveHandler(PointerPressedEvent, OnHeldPress);
        top.RemoveHandler(PointerReleasedEvent, OnHeldRelease);
        top.RemoveHandler(PointerWheelChangedEvent, OnHeldWheel);
        _held = null;
    }

    private void OnHeldPress(object? sender, PointerPressedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, this))
        {
            e.Handled = true;
        }
    }

    private void OnHeldRelease(object? sender, PointerReleasedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, this))
        {
            e.Handled = true;
        }
    }

    /// <summary>While latched the wheel steps the spinner wherever the pointer is.</summary>
    private void OnHeldWheel(object? sender, PointerWheelEventArgs e)
    {
        if (_spinner is { } spinner)
        {
            spinner.Wheel(Math.Sign(e.Delta.Y));
            InvalidateVisual();
        }

        e.Handled = true;
    }

    private Color BevelTopLeft() => Pressed ? DcbColors.Shadow : DcbColors.Highlight;

    private Color BevelBottomRight() => Pressed ? DcbColors.Highlight : DcbColors.Shadow;

    private Color FillOf(bool pushed) =>
        ((Shape == DcbTileShape.Weather) && IsAvailable)
            ? (pushed ? DcbColors.WeatherSelected : DcbColors.WeatherAvailable)
            : (pushed ? DcbColors.Pushed : DcbColors.Back);

    private void DrawBevel(DrawingContext context, Rect bounds)
    {
        var topLeft = new ImmutableSolidColorBrush(Palette.TopLeft);
        var bottomRight = new ImmutableSolidColorBrush(Palette.BottomRight);
        context.FillRectangle(topLeft, new Rect(0, 0, bounds.Width, 1));
        context.FillRectangle(topLeft, new Rect(0, 0, 1, bounds.Height));
        context.FillRectangle(bottomRight, new Rect(0, bounds.Height - 1, bounds.Width, 1));
        context.FillRectangle(bottomRight, new Rect(bounds.Width - 1, 0, 1, bounds.Height));
    }

    private void DrawLabel(DrawingContext context)
    {
        string text = LabelText;
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        string[] lines = text.Split('\n');
        int count = Math.Min(lines.Length, MaxLines);
        double top = (Bounds.Height - (count * LineHeight)) / 2;
        double offset = Pressed ? 1 : 0;
        var brush = new ImmutableSolidColorBrush(TextColor);
        for (int i = 0; i < count; i++)
        {
            string line = (lines[i].Length > MaxLineChars) ? lines[i][..MaxLineChars] : lines[i];
            var formatted = new FormattedText(line, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface, LabelFontSize, brush);
            double x = ((Bounds.Width - formatted.Width) / 2) + offset;
            context.DrawText(formatted, new Point(x, (top + (i * LineHeight)) + offset));
        }
    }
}
