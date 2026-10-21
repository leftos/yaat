using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Yaat.Sim.Commands;
using Yaat.Sim.Phases;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// The unfolded builder's controls for one form, each setting a part of the call (<see cref="SoloAdvisoryPanel"/>): the clock
/// dial, miles stepper, direction pad, type and altitude with its <c>unknown</c> box; the octant dial, miles and type; the
/// pattern sketch, side, miles, runway and type; the reporting points within the matcher's landmark radius and the type.
/// </summary>
public sealed class AdvisoryBuilderView : Grid
{
    /// <summary>How small the <c>unknown</c> box draws against the theme's check box, which has no smaller size.</summary>
    private const double UnknownScale = 0.7;

    private readonly IMenuAircraft? _aircraft;
    private readonly Action<AdvisoryPart, string?> _set;
    private readonly StackPanel _fields = new() { Spacing = 3, Margin = new Thickness(10, 0, 0, 0) };
    private readonly TextBox _type = new()
    {
        Width = 70,
        FontSize = 11,
        HorizontalAlignment = HorizontalAlignment.Left,
    };
    private readonly List<Button> _fixButtons = [];
    private bool _showing;

    internal AdvisoryBuilderView(
        AdvisoryForm form,
        string pointed,
        IReadOnlyList<string> points,
        IMenuAircraft? aircraft,
        Action<AdvisoryPart, string?> set
    )
    {
        _aircraft = aircraft;
        _set = set;
        ColumnDefinitions = new ColumnDefinitions("Auto,*");
        Margin = new Thickness(0, 4, 0, 0);
        Control left = form switch
        {
            AdvisoryForm.Radar => Clock,
            AdvisoryForm.Nose => Octants,
            AdvisoryForm.Pattern => Sketch,
            _ => FixList(pointed, points),
        };
        Children.Add(left);
        SetColumn(_fields, 1);
        Children.Add(_fields);
        Clock.Picked += hour => Set(AdvisoryPart.Clock, hour);
        Octants.Picked += position => Set(AdvisoryPart.Position, position);
        Sketch.Picked += leg => Set(AdvisoryPart.Leg, SoloAdvisoryText.LegWord(leg));
        Miles.Changed += miles => Set(AdvisoryPart.Miles, miles.ToString(CultureInfo.InvariantCulture));
        Directions.Picked += direction => Set(AdvisoryPart.Direction, direction);
        Altitude.Changed += hundreds => Set(AdvisoryPart.Altitude, Hundreds(hundreds));
        _type.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
            {
                string type = (_type.Text ?? "").Trim().ToUpperInvariant();
                Set(AdvisoryPart.Type, (type.Length == 0) ? null : type);
            }
        };
        AltitudeUnknown.IsCheckedChanged += (_, _) => AltitudeUnknownChanged();
        AltitudeUnknown.IsEnabled = aircraft is not null;
        if (aircraft is null)
        {
            ToolTip.SetTip(AltitudeUnknown, NoAircraftAltitudeTip);
        }

        AutomationProperties.SetName(_type, "Type");
        AddFields(form);
    }

    /// <summary>The clock dial (radar form).</summary>
    public AdvisoryClockDial Clock { get; } = new();

    /// <summary>The octant dial (nose form).</summary>
    public AdvisoryOctantDial Octants { get; } = new();

    /// <summary>The pattern sketch (pattern form).</summary>
    public AdvisoryPatternSketch Sketch { get; } = new();

    /// <summary>The miles stepper, 1 to 20.</summary>
    public AdvisoryStepper Miles { get; } = new("Miles", 1, 20, 1, miles => miles.ToString(CultureInfo.InvariantCulture));

    /// <summary>The direction pad (radar form).</summary>
    public AdvisoryDirectionPad Directions { get; } = new();

    /// <summary>The altitude stepper in hundreds of feet (radar form).</summary>
    public AdvisoryStepper Altitude { get; } = new("Altitude", 0, 600, 1, hundreds => (hundreds * 100).ToString("N0", CultureInfo.InvariantCulture));

    /// <summary>The <c>unknown</c> box's tooltip while it is disabled for want of a commanded aircraft.</summary>
    public const string NoAircraftAltitudeTip = "No commanded aircraft to take an altitude from";

    /// <summary>
    /// The altitude's <c>unknown</c> box (radar form): checked while the call gives no altitude; unchecking it gives the
    /// commanded aircraft's own altitude, which the stepper then moves. Disabled with no commanded aircraft, which leaves no
    /// altitude to give.
    /// </summary>
    public CheckBox AltitudeUnknown { get; } =
        new()
        {
            Content = "unknown",
            FontSize = 11 / UnknownScale,
            Foreground = FlyoutPanelParts.TitleBrush,
            MinHeight = 0,
            Padding = new Thickness(6, 0, 0, 0),
        };

    /// <summary>The side buttons, left then right (pattern form).</summary>
    public IReadOnlyList<Button> Sides { get; private set; } = [];

    /// <summary>The reporting point buttons (over form).</summary>
    public IReadOnlyList<Button> FixButtons => _fixButtons;

    /// <summary>Shows <paramref name="parts"/> on the controls without raising their changes.</summary>
    internal void Show(IReadOnlyDictionary<AdvisoryPart, string> parts)
    {
        _showing = true;
        ShowPickers(parts);
        ShowNumbers(parts);
        ShowChoices(parts);
        _showing = false;
    }

    private static string? PartOf(IReadOnlyDictionary<AdvisoryPart, string> parts, AdvisoryPart part) =>
        parts.TryGetValue(part, out string? word) ? word : null;

    private static string Hundreds(int hundreds) => hundreds.ToString("000", CultureInfo.InvariantCulture);

    private void ShowPickers(IReadOnlyDictionary<AdvisoryPart, string> parts)
    {
        int heading = (int)Math.Round(_aircraft?.MagneticHeadingDegrees ?? 0, MidpointRounding.AwayFromZero) % 360;
        Clock.Show(PartOf(parts, AdvisoryPart.Clock), $"hdg {((heading == 0) ? 360 : heading):000}");
        Octants.Show(PartOf(parts, AdvisoryPart.Position), "");
        PatternDirection side = (PartOf(parts, AdvisoryPart.Side) == "R") ? PatternDirection.Right : PatternDirection.Left;
        Sketch.Show(SoloAdvisoryText.LegOf(PartOf(parts, AdvisoryPart.Leg) ?? ""), PartOf(parts, AdvisoryPart.Runway) ?? "", side);
        Directions.Show(PartOf(parts, AdvisoryPart.Direction));
    }

    private void ShowNumbers(IReadOnlyDictionary<AdvisoryPart, string> parts)
    {
        Miles.Show(int.TryParse(PartOf(parts, AdvisoryPart.Miles), out int miles) ? miles : null);
        int? altitude = (PartOf(parts, AdvisoryPart.Altitude) is { } typed) ? AltitudeResolver.Resolve(typed) / 100 : null;
        Altitude.Show(altitude);
        AltitudeUnknown.IsChecked = altitude is null;
        if (!_type.IsKeyboardFocusWithin)
        {
            _type.Text = PartOf(parts, AdvisoryPart.Type) ?? "";
        }
    }

    private void ShowChoices(IReadOnlyDictionary<AdvisoryPart, string> parts)
    {
        foreach (Button fix in _fixButtons)
        {
            fix.Background = Equals(fix.Tag, PartOf(parts, AdvisoryPart.Fix)) ? FlyoutPanelParts.NowEdge : null;
        }

        foreach (Button sideButton in Sides)
        {
            sideButton.Background = Equals(sideButton.Tag, PartOf(parts, AdvisoryPart.Side)) ? FlyoutPanelParts.NowEdge : null;
        }
    }

    /// <summary>The altitude unchecking <c>unknown</c> gives: the commanded aircraft's own, in hundreds, within the stepper's range.</summary>
    private int KnownAltitude(IMenuAircraft aircraft) =>
        Math.Clamp((int)Math.Round(aircraft.AltitudeFeet / 100.0, MidpointRounding.AwayFromZero), Altitude.Minimum, Altitude.Maximum);

    private void AltitudeUnknownChanged()
    {
        if (AltitudeUnknown.IsChecked == true)
        {
            Set(AdvisoryPart.Altitude, null);
        }
        else if (_aircraft is { } aircraft)
        {
            Set(AdvisoryPart.Altitude, Hundreds(KnownAltitude(aircraft)));
        }
    }

    private void Set(AdvisoryPart part, string? value)
    {
        if (!_showing)
        {
            _set(part, value);
        }
    }

    private void AddFields(AdvisoryForm form)
    {
        switch (form)
        {
            case AdvisoryForm.Radar:
                AddField("Miles", Miles);
                AddField("Direction", Directions);
                AddField("Type", _type);
                AddField(
                    "Altitude",
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Children =
                        {
                            Altitude,
                            new LayoutTransformControl
                            {
                                LayoutTransform = new ScaleTransform(UnknownScale, UnknownScale),
                                Margin = new Thickness(8, 0, 0, 0),
                                VerticalAlignment = VerticalAlignment.Center,
                                Child = AltitudeUnknown,
                            },
                        },
                    }
                );
                break;
            case AdvisoryForm.Nose:
                AddField("Miles", Miles);
                AddField("Type", _type);
                break;
            case AdvisoryForm.Pattern:
                Sides = [SideButton("left", "L"), SideButton("right", "R")];
                AddField(
                    "Side",
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 3,
                        Children = { Sides[0], Sides[1] },
                    }
                );
                AddField("Miles", Miles);
                AddField("Type", _type);
                break;
            default:
                AddField("Type", _type);
                break;
        }
    }

    private void AddField(string label, Control control) =>
        _fields.Children.Add(
            new DockPanel
            {
                Children =
                {
                    new TextBlock
                    {
                        Text = label,
                        Width = 64,
                        FontSize = 11,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                    control,
                },
            }
        );

    private Button SideButton(string label, string token)
    {
        var button = new Button
        {
            Content = label,
            Tag = token,
            FontSize = 10,
            Padding = new Thickness(5, 0),
        };
        AutomationProperties.SetName(button, label);
        button.Click += (_, e) =>
        {
            e.Handled = true;
            Set(AdvisoryPart.Side, token);
        };
        return button;
    }

    private StackPanel FixList(string pointed, IReadOnlyList<string> points)
    {
        var list = new StackPanel { Spacing = 2, Width = AdvisoryRingPicker.Side };
        foreach (string point in points)
        {
            var button = new Button
            {
                Content = point,
                Tag = point,
                FontSize = 10,
                Padding = new Thickness(5, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            AutomationProperties.SetName(button, point);
            button.Click += (_, e) =>
            {
                e.Handled = true;
                Set(AdvisoryPart.Fix, point);
            };
            _fixButtons.Add(button);
            list.Children.Add(button);
        }

        list.Children.Add(
            new TextBlock
            {
                Text = $"points within {SoloAdvisoryModel.LandmarkRadiusText} NM of {pointed}",
                FontSize = 10,
                Foreground = FlyoutPanelParts.TitleBrush,
                TextWrapping = TextWrapping.Wrap,
            }
        );
        return list;
    }
}
