using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Yaat.Client.Views.Scope.Toolbar.Eram;

/// <summary>
/// One ERAM toolbar button, of the Master Toolbar or of one of its menus, drawn as CRC draws it: a gold tear-off strip
/// down its left edge, then a pick area drawing the button's two text lines over the fill its kind gives it — a menu's
/// blue (CRC's BurntCoral while its menu is the open one), a toggle's black (the bar's grey while it is on), an inc/dec's
/// green, and a command's teal, all in white text on a 1 DIP white border. A left or middle press raises
/// <see cref="Invoked"/> and the wheel <see cref="Wheeled"/>; a button YAAT has no feature for draws dimmed to
/// <see cref="EramColors.DisabledOpacity"/> and takes no input, and a suppressed one (<see cref="IsSuppressed"/>) keeps
/// its space but draws nothing and takes no input.
/// </summary>
public sealed class EramToolbarButton : Grid
{
    private const double TextLineHeight = 14;
    private const double BorderWidth = 1;

    private bool _isOn;
    private bool _isOpenMenu;
    private bool _isSuppressed;

    /// <summary>A control for <paramref name="type"/>, drawn for its kind and for whether YAAT can drive it.</summary>
    /// <param name="type">The button the control stands for.</param>
    /// <param name="pickAreaWidth">The pick area's minimum width in DIP, measured from the bar's toolbar font.</param>
    /// <param name="lines">The button's two text lines, or null for its type's own (<see cref="EramButtonTypeExtensions.Lines"/>).</param>
    /// <param name="key">What tells the button apart from others of its type (<see cref="EramMenuButton.Key"/>), or null.</param>
    public EramToolbarButton(EramButtonType type, double pickAreaWidth, (string Line1, string Line2)? lines, string? key)
    {
        Type = type;
        Key = key;
        Height = EramMasterLayout.ButtonHeight;
        Background = Brushes.Transparent;
        ColumnDefinitions = [new(EramMasterLayout.StripWidth, GridUnitType.Pixel), new(1, GridUnitType.Star)];

        Strip = new Border
        {
            Background = EramColors.Solid(EramColors.TearoffGold),
            BorderBrush = EramColors.Solid(EramColors.Border),
            BorderThickness = new Thickness(BorderWidth),
        };
        Children.Add(Strip);

        TextLines = new TextBlock
        {
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            LineHeight = TextLineHeight,
        };
        PickArea = new Border
        {
            MinWidth = pickAreaWidth,
            BorderBrush = EramColors.Solid(EramColors.Border),
            BorderThickness = new Thickness(BorderWidth),
            Child = TextLines,
        };
        SetColumn(PickArea, 1);
        Children.Add(PickArea);

        (string line1, string line2) = lines ?? type.Lines();
        SetLines(line1, line2);
        ApplyInput();
        ApplyLook();
    }

    /// <summary>Raised when the button is pressed with the left or the middle button; a disabled button never raises it.</summary>
    public event EventHandler<EramButtonEventArgs>? Invoked;

    /// <summary>
    /// Raised when the wheel turns over the button, carrying +1 for a turn up and -1 for a turn down; a disabled button
    /// never raises it.
    /// </summary>
    public event EventHandler<EramButtonEventArgs>? Wheeled;

    /// <summary>The button this control stands for.</summary>
    public EramButtonType Type { get; }

    /// <summary>What tells the button apart from others of its type: a GeoMap filter's STARS id, a pref set's name; null for any other.</summary>
    public string? Key { get; }

    /// <summary>The button's first text line.</summary>
    public string Line1 { get; private set; } = "";

    /// <summary>The button's second text line; empty draws blank.</summary>
    public string Line2 { get; private set; } = "";

    /// <summary>The 0-based row the button sits in, in the Master Toolbar or in its menu's table.</summary>
    public int CellRow { get; init; }

    /// <summary>What a press does.</summary>
    public EramButtonKind Kind => Type.Kind();

    /// <summary>Whether YAAT has a feature behind the button.</summary>
    public bool IsBuilt => Type.IsBuilt();

    /// <summary>The gold tear-off strip down the button's left edge.</summary>
    public Border Strip { get; }

    /// <summary>The pick area: the button's fill, its border and its two text lines.</summary>
    public Border PickArea { get; }

    /// <summary>The pick area's two text lines.</summary>
    public TextBlock TextLines { get; }

    /// <summary>Whether a toggle button is on; its fill follows, and only the button's owner sets it.</summary>
    public bool IsOn
    {
        get => _isOn;
        set
        {
            _isOn = value;
            ApplyLook();
        }
    }

    /// <summary>Whether a menu button is the one whose menu is open: it then draws CRC's BurntCoral fill.</summary>
    public bool IsOpenMenu
    {
        get => _isOpenMenu;
        set
        {
            _isOpenMenu = value;
            ApplyLook();
        }
    }

    /// <summary>
    /// Whether the button is hidden while another button's menu is open: it keeps its space, draws nothing and takes no
    /// input, as CRC hides the other Master Toolbar buttons.
    /// </summary>
    public bool IsSuppressed
    {
        get => _isSuppressed;
        set
        {
            _isSuppressed = value;
            ApplyInput();
        }
    }

    /// <summary>Draws <paramref name="line1"/> over <paramref name="line2"/> on the pick area; an empty line draws blank.</summary>
    /// <param name="line1">The first line.</param>
    /// <param name="line2">The second line.</param>
    public void SetLines(string line1, string line2)
    {
        if ((TextLines.Inlines is { Count: > 0 }) && (line1 == Line1) && (line2 == Line2))
        {
            return;
        }

        Line1 = line1;
        Line2 = line2;
        TextLines.Inlines = [new Run(line1), new LineBreak(), new Run((line2.Length == 0) ? " " : line2)];
        AutomationProperties.SetName(this, string.Join(' ', new[] { line1, line2 }.Where(line => line.Length > 0)));
    }

    /// <summary>
    /// Presses the button: the left or the middle button acts and a right or other press is ignored, the left counting
    /// as -1 and the middle as +1. A toggle asks for the state it is not in without flipping itself; its owner sets
    /// <see cref="IsOn"/>.
    /// </summary>
    /// <param name="e">The press.</param>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        int delta = DeltaOf(e.GetCurrentPoint(this).Properties);
        if (!IsBuilt || (delta == 0))
        {
            return;
        }

        e.Handled = true;
        bool requestedOn = (Kind == EramButtonKind.Toggle) && !IsOn;
        Invoked?.Invoke(this, new EramButtonEventArgs(Type, requestedOn, delta, Key));
    }

    /// <summary>Turns the wheel over the button: a turn up counts as +1 and a turn down as -1.</summary>
    /// <param name="e">The wheel turn.</param>
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        int delta = Math.Sign(e.Delta.Y);
        if (!IsBuilt || (delta == 0))
        {
            return;
        }

        e.Handled = true;
        Wheeled?.Invoke(this, new EramButtonEventArgs(Type, false, delta, Key));
    }

    private static int DeltaOf(PointerPointProperties props) => props.IsMiddleButtonPressed ? 1 : (props.IsLeftButtonPressed ? -1 : 0);

    private void ApplyInput()
    {
        IsHitTestVisible = IsBuilt && !_isSuppressed;
        Opacity = _isSuppressed ? 0 : (IsBuilt ? 1 : EramColors.DisabledOpacity);
    }

    private void ApplyLook()
    {
        (Color fill, Color text) = Look();
        PickArea.Background = EramColors.Solid(fill);
        TextLines.Foreground = EramColors.Solid(text);
    }

    private (Color Fill, Color Text) Look() =>
        Kind switch
        {
            EramButtonKind.Menu => _isOpenMenu ? (EramColors.BurntCoral, EramColors.Text) : (EramColors.MenuBlue, EramColors.Text),
            EramButtonKind.Toggle => _isOn ? (EramColors.Toolbar, EramColors.Text) : (EramColors.ToggleBlack, EramColors.Text),
            EramButtonKind.IncDec => (EramColors.IncDecGreen, EramColors.Text),
            EramButtonKind.Command => (EramColors.CommandTeal, EramColors.Text),
            _ => throw new ArgumentOutOfRangeException(nameof(Kind), Kind, "Unknown ERAM Master Toolbar button kind"),
        };
}

/// <summary>A press on an ERAM toolbar button: which button, the toggle state it asks for, its inc/dec step and its key.</summary>
public sealed class EramButtonEventArgs(EramButtonType type, bool isOn, int delta, string? key) : EventArgs
{
    /// <summary>The button pressed.</summary>
    public EramButtonType Type { get; } = type;

    /// <summary>The state a toggle asks to be put in after the press; false on every other kind.</summary>
    public bool IsOn { get; } = isOn;

    /// <summary>-1 for the left button, +1 for the middle one; for a wheel turn, +1 up and -1 down.</summary>
    public int Delta { get; } = delta;

    /// <summary>The pressed button's key (<see cref="EramToolbarButton.Key"/>), or null.</summary>
    public string? Key { get; } = key;
}
