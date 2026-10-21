using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Yaat.Sim.Phases;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// A ring of round buttons about the commanded aircraft's arrow, one per label, the first at 12 o'clock and the rest clockwise,
/// with a dashed nose line up from the arrow; the one whose label is the value is lit, and an amber dashed ring shows while no
/// value is set. A click hands its label to <see cref="Picked"/>.
/// </summary>
public class AdvisoryRingPicker : Canvas
{
    /// <summary>The picker's side, in px.</summary>
    public const double Side = 124;

    private const double Centre = Side / 2;

    private static readonly IImmutableSolidColorBrush NoseBrush = new ImmutableSolidColorBrush(Color.Parse("#8F969E"));
    private static readonly IImmutableSolidColorBrush UnsetBrush = new ImmutableSolidColorBrush(Color.Parse("#E0B070"));
    private readonly Dictionary<string, Button> _buttons = new(StringComparer.Ordinal);
    private readonly Ellipse _unsetRing = new()
    {
        Width = Side - 8,
        Height = Side - 8,
        Stroke = UnsetBrush,
        StrokeThickness = 1,
        StrokeDashArray = [3, 3],
    };
    private readonly TextBlock _caption = new()
    {
        FontSize = 9.5,
        Foreground = NoseBrush,
        Width = Side,
        TextAlignment = TextAlignment.Center,
    };

    /// <summary>
    /// Builds the ring of <paramref name="labels"/>, round buttons <paramref name="diameter"/> px across set
    /// <paramref name="radius"/> px from the centre, named <paramref name="name"/> for UI Automation.
    /// </summary>
    public AdvisoryRingPicker(IReadOnlyList<string> labels, double radius, double diameter, string name)
    {
        Width = Side;
        Height = Side;
        AutomationProperties.SetName(this, name);
        SetLeft(_unsetRing, 4);
        SetTop(_unsetRing, 4);
        Children.Add(_unsetRing);
        Children.Add(
            new Line
            {
                StartPoint = new Point(Centre, Centre - 8),
                EndPoint = new Point(Centre, 26),
                Stroke = NoseBrush,
                StrokeThickness = 1,
                StrokeDashArray = [2, 3],
            }
        );
        for (int i = 0; i < labels.Count; i++)
        {
            double angle = i * 2 * Math.PI / labels.Count;
            string label = labels[i];
            Button button = RingButton(label, diameter);
            SetLeft(button, Centre + (Math.Sin(angle) * radius) - (diameter / 2));
            SetTop(button, Centre - (Math.Cos(angle) * radius) - (diameter / 2));
            Children.Add(button);
            _buttons[label] = button;
        }

        Children.Add(new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse("M62 52 l6 16 l-6 -4 l-6 4z"), Fill = Brushes.White });
        SetTop(_caption, Centre + 16);
        Children.Add(_caption);
    }

    /// <summary>Raised with the label of the button clicked.</summary>
    public event Action<string>? Picked;

    /// <summary>The lit label; null while none is set.</summary>
    public string? Value { get; private set; }

    /// <summary>The buttons, by label.</summary>
    public IReadOnlyDictionary<string, Button> Buttons => _buttons;

    /// <summary>Lights <paramref name="value"/>'s button, or none, and sets the caption under the arrow.</summary>
    public void Show(string? value, string caption)
    {
        Value = value;
        _unsetRing.IsVisible = value is null;
        _caption.Text = caption;
        foreach ((string label, Button button) in _buttons)
        {
            FlyoutPanelParts.PaintChoice(button, label == value);
        }
    }

    private Button RingButton(string label, double diameter)
    {
        var button = new Button
        {
            Content = label,
            FontSize = (label.Length > 2) ? 8 : 11,
            Width = diameter,
            Height = diameter,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(diameter / 2),
        };
        FlyoutPanelParts.Mono(button);
        FlyoutPanelParts.PaintChoice(button, false);
        AutomationProperties.SetName(button, label);
        button.Click += (_, e) =>
        {
            e.Handled = true;
            Picked?.Invoke(label);
        };
        return button;
    }
}

/// <summary>The clock dial: twelve hours about the nose, 12 at the top, as the radar draws them round the aircraft.</summary>
public sealed class AdvisoryClockDial : AdvisoryRingPicker
{
    /// <summary>The hours, 12 first.</summary>
    public static IReadOnlyList<string> Hours { get; } = ["12", .. Enumerable.Range(1, 11).Select(h => h.ToString(CultureInfo.InvariantCulture))];

    /// <summary>A clock dial named <c>Clock dial</c>.</summary>
    public AdvisoryClockDial()
        : base(Hours, 47, 21, "Clock dial") { }
}

/// <summary>The octants off the nose (<see cref="SoloAdvisoryText.Positions"/>), <c>NOSE</c> at the top.</summary>
public sealed class AdvisoryOctantDial : AdvisoryRingPicker
{
    /// <summary>An octant dial named <c>Octants off the nose</c>.</summary>
    public AdvisoryOctantDial()
        : base(SoloAdvisoryText.Positions, 44, 24, "Octants off the nose") { }
}

/// <summary>
/// A whole-number stepper, one outlined control 26 px tall: <c>−</c>, the value (<c>?</c> while unset), <c>+</c>. A step from
/// unset starts at the minimum; steps stop at <see cref="Minimum"/> and <see cref="Maximum"/>. A step hands the new value to
/// <see cref="Changed"/>.
/// </summary>
public sealed class AdvisoryStepper : Border
{
    private static readonly IImmutableSolidColorBrush StepText = new ImmutableSolidColorBrush(Color.Parse("#CFD5DC"));
    private static readonly IImmutableSolidColorBrush StepHover = new ImmutableSolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
    private readonly TextBlock _value = new()
    {
        MinWidth = 40,
        Margin = new Thickness(0, 2, 0, 0),
        FontSize = 12.5,
        FontWeight = FontWeight.Medium,
        Foreground = Brushes.White,
        TextAlignment = TextAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly Func<int, string> _format;

    /// <summary>
    /// A stepper from <paramref name="minimum"/> to <paramref name="maximum"/> by <paramref name="step"/>, showing its value
    /// through <paramref name="format"/>.
    /// </summary>
    public AdvisoryStepper(string name, int minimum, int maximum, int step, Func<int, string> format)
    {
        Minimum = minimum;
        Maximum = maximum;
        _format = format;
        Height = 26;
        HorizontalAlignment = HorizontalAlignment.Left;
        BorderBrush = FlyoutPanelParts.ToggleLine;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(4);
        Background = FlyoutPanelParts.ToggleFill;
        ClipToBounds = true;
        AutomationProperties.SetName(this, name);
        FlyoutPanelParts.Mono(_value);
        Less = StepButton("−", "Less", -step);
        More = StepButton("+", "More", step);
        Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { Less, _value, More } };
        Show(null);
    }

    /// <summary>Raised with the value a step reaches.</summary>
    public event Action<int>? Changed;

    /// <summary>The lowest value.</summary>
    public int Minimum { get; }

    /// <summary>The highest value.</summary>
    public int Maximum { get; }

    /// <summary>The value; null while unset.</summary>
    public int? Value { get; private set; }

    /// <summary>The step down.</summary>
    public Button Less { get; }

    /// <summary>The step up.</summary>
    public Button More { get; }

    /// <summary>The value as shown: its text, or <c>?</c> while unset.</summary>
    public string Shown => _value.Text ?? "";

    /// <summary>Shows <paramref name="value"/>, or <c>?</c> for none.</summary>
    public void Show(int? value)
    {
        Value = value;
        _value.Text = (value is { } v) ? _format(v) : "?";
    }

    private Button StepButton(string text, string name, int by)
    {
        var button = new Button
        {
            Content = text,
            FontSize = 14,
            Width = 22,
            Height = 24,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(0),
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = StepText,
        };
        button.Resources["ButtonBackgroundPointerOver"] = StepHover;
        button.Resources["ButtonForegroundPointerOver"] = Brushes.White;
        button.Resources["ButtonBackgroundPressed"] = StepHover;
        button.Resources["ButtonForegroundPressed"] = Brushes.White;
        AutomationProperties.SetName(button, name);
        button.Click += (_, e) =>
        {
            e.Handled = true;
            int next = (Value is { } v) ? Math.Clamp(v + by, Minimum, Maximum) : Minimum;
            Show(next);
            Changed?.Invoke(next);
        };
        return button;
    }
}

/// <summary>
/// The direction pad: the eight directions of flight as outlined 28×22 buttons in a 3×3 grid about a dot
/// (<c>NW N NE / W · E / SW S SE</c>), and beside it the value as a word (<c>southeastbound</c>); the value's button is lit. A
/// click hands its direction to <see cref="Picked"/>.
/// </summary>
public sealed class AdvisoryDirectionPad : Grid
{
    private static readonly string[] Cells = ["NW", "N", "NE", "W", "", "E", "SW", "S", "SE"];
    private static readonly IImmutableSolidColorBrush DotBrush = new ImmutableSolidColorBrush(Color.Parse("#5B6570"));
    private static readonly IImmutableSolidColorBrush WordBrush = new ImmutableSolidColorBrush(Color.Parse("#8F969E"));
    private readonly Dictionary<string, Button> _buttons = new(StringComparer.Ordinal);
    private readonly TextBlock _word = new()
    {
        FontSize = 11,
        Foreground = WordBrush,
        Margin = new Thickness(6, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>A pad named <c>Direction pad</c>.</summary>
    public AdvisoryDirectionPad()
    {
        ColumnDefinitions = new ColumnDefinitions("28,28,28,Auto");
        RowDefinitions = new RowDefinitions("22,22,22");
        ColumnSpacing = 2;
        RowSpacing = 2;
        AutomationProperties.SetName(this, "Direction pad");
        for (int i = 0; i < Cells.Length; i++)
        {
            Control cell = (Cells[i].Length == 0) ? CentreDot() : DirectionButton(Cells[i]);
            SetColumn(cell, i % 3);
            SetRow(cell, i / 3);
            Children.Add(cell);
        }

        SetColumn(_word, 3);
        SetRowSpan(_word, 3);
        Children.Add(_word);
    }

    /// <summary>Raised with the direction clicked.</summary>
    public event Action<string>? Picked;

    /// <summary>The buttons, by direction.</summary>
    public IReadOnlyDictionary<string, Button> Buttons => _buttons;

    /// <summary>Lights <paramref name="value"/>'s button, or none, and names it beside the pad.</summary>
    public void Show(string? value)
    {
        foreach ((string direction, Button button) in _buttons)
        {
            FlyoutPanelParts.PaintChoice(button, direction == value);
        }

        int index = (value is null) ? -1 : SoloAdvisoryText.Directions.ToList().IndexOf(value);
        _word.Text = (index < 0) ? "" : AirborneTrafficRows.DirectionWords[index];
    }

    private static TextBlock CentreDot() =>
        new()
        {
            Text = "·",
            Foreground = DotBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

    private Button DirectionButton(string direction)
    {
        var button = new Button
        {
            Content = direction,
            FontSize = 10.5,
            Width = 28,
            Height = 22,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(3),
        };
        FlyoutPanelParts.Mono(button);
        FlyoutPanelParts.PaintChoice(button, false);
        AutomationProperties.SetName(button, direction);
        button.Click += (_, e) =>
        {
            e.Handled = true;
            Picked?.Invoke(direction);
        };
        _buttons[direction] = button;
        return button;
    }
}

/// <summary>
/// The pattern sketch: the runway across the middle with its designator, and the legs flown about it on the chosen side (upwind,
/// crosswind, downwind, base, final) as buttons placed where each leg lies, the value's leg lit, captioned with the side's
/// traffic. A click hands its leg to <see cref="Picked"/>.
/// </summary>
public sealed class AdvisoryPatternSketch : Canvas
{
    private readonly Dictionary<PatternEntryLeg, Button> _buttons = [];
    private readonly TextBlock _runway = new() { FontSize = 10, Foreground = FlyoutPanelParts.TitleBrush };
    private readonly TextBlock _caption = new() { FontSize = 10, Foreground = FlyoutPanelParts.TitleBrush };

    /// <summary>A sketch named <c>Pattern legs</c>.</summary>
    public AdvisoryPatternSketch()
    {
        Width = AdvisoryRingPicker.Side;
        Height = AdvisoryRingPicker.Side;
        AutomationProperties.SetName(this, "Pattern legs");
        Children.Add(
            new Line
            {
                StartPoint = new Point(38, 62),
                EndPoint = new Point(86, 62),
                Stroke = Brushes.Gray,
                StrokeThickness = 3,
            }
        );
        SetLeft(_runway, 52);
        SetTop(_runway, 44);
        Children.Add(_runway);
        foreach (PatternEntryLeg leg in Enum.GetValues<PatternEntryLeg>())
        {
            string word = SoloAdvisoryText.LegWord(leg);
            var button = new Button
            {
                Content = word,
                FontSize = 9,
                Padding = new Thickness(3, 0),
            };
            AutomationProperties.SetName(button, word);
            button.Click += (_, e) =>
            {
                e.Handled = true;
                Picked?.Invoke(leg);
            };
            _buttons[leg] = button;
            Children.Add(button);
        }

        SetLeft(_caption, 30);
        Children.Add(_caption);
        Show(null, "", PatternDirection.Left);
    }

    /// <summary>Raised with the leg clicked.</summary>
    public event Action<PatternEntryLeg>? Picked;

    /// <summary>The buttons, by leg.</summary>
    public IReadOnlyDictionary<PatternEntryLeg, Button> Buttons => _buttons;

    /// <summary>
    /// Lights <paramref name="leg"/>, labels the runway <paramref name="runway"/> and lays the downwind, crosswind and base on the
    /// <paramref name="side"/> (landing right to left, final at the right and upwind at the left: left traffic flies them below
    /// the runway).
    /// </summary>
    public void Show(PatternEntryLeg? leg, string runway, PatternDirection side)
    {
        double legY = (side == PatternDirection.Left) ? 98 : 14;
        Place(PatternEntryLeg.Upwind, 2, 54);
        Place(PatternEntryLeg.Crosswind, 2, (legY + 62) / 2 - 8);
        Place(PatternEntryLeg.Downwind, 46, legY - 8);
        Place(PatternEntryLeg.Base, 92, (legY + 62) / 2 - 8);
        Place(PatternEntryLeg.Final, 88, 66);
        _runway.Text = runway;
        _caption.Text = (side == PatternDirection.Left) ? "left traffic" : "right traffic";
        SetTop(_caption, (side == PatternDirection.Left) ? 4 : 110);
        foreach ((PatternEntryLeg each, Button button) in _buttons)
        {
            button.Background = (each == leg) ? FlyoutPanelParts.NowEdge : null;
        }
    }

    private void Place(PatternEntryLeg leg, double left, double top)
    {
        SetLeft(_buttons[leg], left);
        SetTop(_buttons[leg], top);
    }
}
