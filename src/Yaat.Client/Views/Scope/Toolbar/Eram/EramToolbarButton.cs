using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Yaat.Client.Views.Scope.Toolbar.Eram;

/// <summary>
/// One ERAM Master Toolbar button drawn as CRC draws it: a gold tear-off strip down its left edge, then a pick area
/// drawing the button's two text lines over the fill its kind gives it — a menu's blue (CRC's BurntCoral while its menu
/// is the open one), a toggle's black (the bar's grey while it is on), an inc/dec's green, and a command's teal, all in
/// white text on a 1 DIP white border. A left or middle press raises <see cref="Invoked"/>; a button YAAT has no feature
/// for draws dimmed to <see cref="EramColors.DisabledOpacity"/> and takes no input.
/// </summary>
public sealed class EramToolbarButton : Grid
{
    private const double TextLineHeight = 14;
    private const double BorderWidth = 1;

    private bool _isOn;
    private bool _isOpenMenu;

    /// <summary>A control for <paramref name="type"/>, drawn for its kind and for whether YAAT can drive it.</summary>
    /// <param name="type">The button the control stands for.</param>
    /// <param name="pickAreaWidth">The pick area's minimum width in DIP, measured from the bar's toolbar font.</param>
    public EramToolbarButton(EramButtonType type, double pickAreaWidth)
    {
        Type = type;
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

        (string line1, string line2) = type.Lines();
        TextLines = new TextBlock
        {
            Inlines = [new Run(line1), new LineBreak(), new Run((line2.Length == 0) ? " " : line2)],
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

        AutomationProperties.SetName(this, string.Join(' ', new[] { line1, line2 }.Where(line => line.Length > 0)));
        IsHitTestVisible = type.IsBuilt();
        Opacity = type.IsBuilt() ? 1 : EramColors.DisabledOpacity;
        ApplyLook();
    }

    /// <summary>Raised when the button is pressed with the left or the middle button; a disabled button never raises it.</summary>
    public event EventHandler<EramButtonEventArgs>? Invoked;

    /// <summary>The button this control stands for.</summary>
    public EramButtonType Type { get; }

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
        Invoked?.Invoke(this, new EramButtonEventArgs(Type, requestedOn, delta));
    }

    private static int DeltaOf(PointerPointProperties props) => props.IsMiddleButtonPressed ? 1 : (props.IsLeftButtonPressed ? -1 : 0);

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

/// <summary>A press on an ERAM Master Toolbar button: which button, the toggle state it asks for, and its inc/dec step.</summary>
public sealed class EramButtonEventArgs(EramButtonType type, bool isOn, int delta) : EventArgs
{
    /// <summary>The button pressed.</summary>
    public EramButtonType Type { get; } = type;

    /// <summary>The state a toggle asks to be put in after the press; false on every other kind.</summary>
    public bool IsOn { get; } = isOn;

    /// <summary>-1 for the left button, +1 for the middle one.</summary>
    public int Delta { get; } = delta;
}
