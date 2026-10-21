using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// One aircraft row of the solo advisory flyout: the pointed mark, the callsign with its type and leg, where the aircraft is
/// (clock, miles, altitude difference and direction of flight, or, with values left for the student, the altitude difference and
/// <see cref="SoloAdvisoryPanel.FindOnScope"/>), and <see cref="SoloAdvisoryPanel.YourCall"/> on the row the call resolves to. A
/// click points the advisory at it; it never sends.
/// </summary>
public sealed class SoloTrafficRowButton : Button
{
    private readonly TextBlock _mark = new()
    {
        FontSize = 10,
        Foreground = FlyoutPanelParts.NowEdge,
        VerticalAlignment = VerticalAlignment.Center,
    };
    private static readonly IImmutableSolidColorBrush PointedFill = new ImmutableSolidColorBrush(Color.FromArgb(0x21, 0x8F, 0xC1, 0xFF));
    private static readonly IImmutableSolidColorBrush TypeBrush = new ImmutableSolidColorBrush(Color.Parse("#9AA3AD"));
    private static readonly IImmutableSolidColorBrush LegBrush = new ImmutableSolidColorBrush(Color.Parse("#E4E7EB"));
    private static readonly IImmutableSolidColorBrush YourCallBrush = new ImmutableSolidColorBrush(Color.Parse("#7FD1B9"));
    private readonly TextBlock _name = new() { FontSize = 13, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _type = new() { FontSize = 12, Foreground = TypeBrush };
    private readonly TextBlock _leg = new() { FontSize = 11.5, Foreground = LegBrush };
    private readonly TextBlock _where = GroundTrafficLook.Subtle("", 11);
    private readonly TextBlock _command = FlyoutPanelParts.CommandText("");
    private string _notes = "";

    internal SoloTrafficRowButton(string callsign)
    {
        Callsign = callsign;
        MinHeight = AirborneTrafficRowButton.RowHeight;
        Padding = new Thickness(0);
        Background = Brushes.Transparent;
        BorderThickness = new Thickness(0);
        CornerRadius = new CornerRadius(0);
        HorizontalAlignment = HorizontalAlignment.Stretch;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("14,*,Auto"),
            ColumnSpacing = 6,
            Margin = new Thickness(11, 3),
        };
        grid.Children.Add(_mark);
        var column = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
        column.Children.Add(
            new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 5,
                Children = { _name, _type, _leg },
            }
        );
        _name.VerticalAlignment = VerticalAlignment.Bottom;
        _type.VerticalAlignment = VerticalAlignment.Bottom;
        _leg.VerticalAlignment = VerticalAlignment.Bottom;
        FlyoutPanelParts.Mono(_name);
        FlyoutPanelParts.Mono(_type);
        _command.Foreground = YourCallBrush;
        _command.Opacity = 1;
        column.Children.Add(_where);
        Grid.SetColumn(column, 1);
        grid.Children.Add(column);
        Grid.SetColumn(_command, 2);
        grid.Children.Add(_command);
        Content = grid;
        _name.Text = callsign;
    }

    /// <summary>The aircraft's callsign.</summary>
    public string Callsign { get; }

    /// <summary>The second line.</summary>
    public string Where => _where.Text ?? "";

    /// <summary>The command column: <see cref="SoloAdvisoryPanel.YourCall"/> or nothing.</summary>
    public string CommandColumn => _command.Text ?? "";

    /// <summary>Whether the advisory is pointed at this row.</summary>
    public bool IsPointed => _mark.Text == SoloAdvisoryPanel.PointedMark;

    /// <inheritdoc />
    protected override Type StyleKeyOverride => typeof(Button);

    /// <inheritdoc />
    public override string ToString()
    {
        string mark = IsPointed ? $"{SoloAdvisoryPanel.PointedMark} " : "";
        string command = (CommandColumn.Length > 0) ? $" — {CommandColumn}" : "";
        return $"{mark}{Callsign}{_notes} — {Where}{command}";
    }

    private static string Spaced(string note) => (note.Length > 0) ? $" {note}" : "";

    /// <summary>
    /// Fills the row as the one the advisory is pointed at (under the pointer, focused or picked) when <paramref name="lit"/>,
    /// under the pointer too.
    /// </summary>
    internal void Light(bool lit)
    {
        Background = lit ? PointedFill : Brushes.Transparent;
        if (lit)
        {
            Resources["ButtonBackgroundPointerOver"] = PointedFill;
            Resources["ButtonBackgroundPressed"] = PointedFill;
        }
        else
        {
            Resources.Remove("ButtonBackgroundPointerOver");
            Resources.Remove("ButtonBackgroundPressed");
        }
    }

    /// <summary>
    /// Shows <paramref name="row"/>, with clock, miles and direction of flight when <paramref name="trueValues"/>, marked pointed
    /// and resolved as told.
    /// </summary>
    internal void Show(MenuAirborneTrafficRow row, bool trueValues, bool pointed, bool resolved)
    {
        string type = (row.Traffic.AircraftType.Length > 0) ? $"· {row.Traffic.AircraftType}" : "";
        string leg = (row.Leg is { } legWord) ? $"· {legWord}" : "";
        _type.Text = type;
        _type.IsVisible = type.Length > 0;
        _leg.Text = leg;
        _leg.IsVisible = leg.Length > 0;
        _notes = $"{Spaced(type)}{Spaced(leg)}";
        string delta = RelativeGeometry.AltitudeDeltaColumn(row.Traffic.AltitudeDeltaFeet);
        _where.Text = trueValues
            ? $"{row.Traffic.ClockPosition} o'clock · {RelativeGeometry.WholeNm(row.Traffic.DistanceNm)} nm · {delta} · {row.Direction}"
            : $"{delta} · {SoloAdvisoryPanel.FindOnScope}";
        _mark.Text = pointed ? SoloAdvisoryPanel.PointedMark : "";
        _command.Text = resolved ? SoloAdvisoryPanel.YourCall : "";
        AutomationProperties.SetName(this, ToString());
    }
}
