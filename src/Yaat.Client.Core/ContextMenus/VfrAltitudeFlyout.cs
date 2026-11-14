using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Yaat.Sim;
using Yaat.Sim.Data;
using Yaat.Sim.Scenarios;

namespace Yaat.Client.ContextMenus;

/// <summary>How the Altitude… flyout holds the picked altitude.</summary>
public enum VfrAltitudeMode
{
    /// <summary>Maintain it: <c>CM</c> at or above the current altitude, <c>DM</c> below.</summary>
    Maintain,

    /// <summary>At or above it: <c>CM A</c>.</summary>
    AtOrAbove,

    /// <summary>At or below it: <c>CM B</c>.</summary>
    AtOrBelow,
}

/// <summary>What the Altitude… flyout reads from the aircraft on each refresh.</summary>
/// <param name="Current">The aircraft's altitude in feet; 0 without an aircraft.</param>
/// <param name="MagneticTrack">The magnetic ground track in degrees: the true track less the declination where the aircraft is.</param>
/// <param name="NearestFieldElevation">The nearest airport's elevation in feet; 0 where no airport is within 100 NM.</param>
internal sealed record VfrAltitudeReading(double Current, double MagneticTrack, double NearestFieldElevation);

/// <summary>
/// The Altitude… flyout for a VFR aircraft: the title line (the present altitude and magnetic track), the Altitude box, the mode
/// toggle (Maintain | At or above | At or below), the cruising-altitude hint, the value header (the value and the command it
/// sends) and two columns: the thousands, from the thousand holding the destination's field elevation up to 17, and the hundreds
/// 000-900. Picking a thousand then a hundred sends. A thousand is outlined when its +500 is a VFR cruising altitude (14 CFR
/// 91.159: odd thousands + 500 on a magnetic track of 0-179, even on 180-359) more than 3,000 ft above the nearest airport, and a
/// hundred when its altitude is; the current altitude's thousand is marked <c>now</c>. A value under the field elevation (rounded
/// up to 100 ft, at least 100 ft) or above 17,500 ft is shown and disabled. While open, it refreshes once a second
/// (<see cref="FlyoutLiveTick"/>): the title, the hint, the outlines, the now mark and the commands; the picked thousand, the mode
/// and the box's text stay.
/// </summary>
public static class VfrAltitudeFlyout
{
    /// <summary>The highest altitude the flyout offers: no VFR altitude reaches 18,000 ft (Class A).</summary>
    public const int TopFeet = 17500;

    /// <summary>The highest thousand the flyout lists.</summary>
    public const int TopThousand = 17;

    /// <summary>
    /// The Altitude… entry: one menu row holding the flyout over <paramref name="aircraft"/>'s altitude.
    /// </summary>
    /// <param name="label">The entry's label.</param>
    /// <param name="aircraft">The aircraft whose altitude and track the flyout follows.</param>
    /// <param name="context">The menu's aircraft and initials.</param>
    /// <param name="host">The host the flyout sends through, reads the field elevation from, and refreshes on.</param>
    public static MenuItem Build(string label, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        double field = host.GetFieldElevation(aircraft?.Destination);
        var entry = new MenuItem { Header = label };
        PanelFlyoutContent.Attach(
            entry,
            new AltitudeFlyoutHost("vfr altitude flyout"),
            context,
            host,
            (choose, _) => new VfrAltitudePanel(field, () => Read(aircraft), choose)
        );
        return entry;
    }

    /// <summary>The lowest altitude offered: <paramref name="fieldElevation"/> rounded up to 100 ft, at least 100 ft.</summary>
    internal static int FloorFeet(double fieldElevation) => Math.Max(100, (int)(Math.Ceiling(fieldElevation / 100.0) * 100));

    /// <summary>The lowest thousand listed: the one holding <paramref name="fieldElevation"/>, at least 0.</summary>
    internal static int LowThousand(double fieldElevation) => Math.Clamp((int)Math.Floor(fieldElevation / 1000.0), 0, TopThousand);

    /// <summary>
    /// True when <paramref name="feet"/> is a VFR cruising altitude for <paramref name="reading"/>'s magnetic track and more than
    /// 3,000 ft above the nearest airport.
    /// </summary>
    internal static bool IsCruising(VfrAltitudeReading reading, int feet) =>
        HemisphericAltitude.IsConforming(reading.MagneticTrack, feet) && ((feet - reading.NearestFieldElevation) > HemisphericAltitude.AglFloorFt);

    /// <summary>
    /// The command for <paramref name="feet"/> in <paramref name="mode"/>, in full feet: <c>CM 5500</c> at or above
    /// <paramref name="current"/> and <c>DM 5500</c> below it, <c>CM A5500</c>, <c>CM B5500</c>.
    /// </summary>
    public static string Command(VfrAltitudeMode mode, int feet, double current) =>
        mode switch
        {
            VfrAltitudeMode.AtOrAbove => $"CM A{feet}",
            VfrAltitudeMode.AtOrBelow => $"CM B{feet}",
            _ => (feet >= current) ? $"CM {feet}" : $"DM {feet}",
        };

    /// <summary>The mode's verb, for the command column of a value not picked yet: <c>CM</c>, <c>CM A</c> or <c>CM B</c>.</summary>
    internal static string Verb(VfrAltitudeMode mode) =>
        mode switch
        {
            VfrAltitudeMode.AtOrAbove => "CM A",
            VfrAltitudeMode.AtOrBelow => "CM B",
            _ => "CM",
        };

    /// <summary>The toggle's label for <paramref name="mode"/>.</summary>
    public static string Label(VfrAltitudeMode mode) =>
        mode switch
        {
            VfrAltitudeMode.AtOrAbove => "At or above",
            VfrAltitudeMode.AtOrBelow => "At or below",
            _ => "Maintain",
        };

    /// <summary>The code the toggle shows beside <paramref name="mode"/>'s label; none for Maintain.</summary>
    internal static string Code(VfrAltitudeMode mode) =>
        mode switch
        {
            VfrAltitudeMode.AtOrAbove => "A",
            VfrAltitudeMode.AtOrBelow => "B",
            _ => "",
        };

    /// <summary><paramref name="feet"/> with thousands separators: <c>5,500</c>.</summary>
    internal static string Feet(double feet) => Math.Round(feet).ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>The magnetic track as whole degrees 000-359, rounded down so the shown track and the hemisphere agree.</summary>
    internal static string Course(double magneticTrack) => CourseDegrees(magneticTrack).ToString("D3", CultureInfo.InvariantCulture);

    /// <summary>The magnetic track as whole degrees 0-359, rounded down.</summary>
    private static int CourseDegrees(double magneticTrack)
    {
        double course = magneticTrack % 360.0;
        course = (course < 0) ? course + 360.0 : course;
        return (int)Math.Floor(course) % 360;
    }

    /// <summary>
    /// The hint over the columns: <c>VFR cruising, 095° odd + 500, above 3,600 (3,000 AGL)</c>, the floor the nearest airport's
    /// elevation plus 3,000 ft.
    /// </summary>
    internal static string Hint(VfrAltitudeReading reading)
    {
        int course = CourseDegrees(reading.MagneticTrack);
        string parity = (course < 180) ? "odd" : "even";
        string shown = course.ToString("D3", CultureInfo.InvariantCulture);
        string floor = Feet(reading.NearestFieldElevation + HemisphericAltitude.AglFloorFt);
        return $"VFR cruising, {shown}° {parity} + 500, above {floor} ({Feet(HemisphericAltitude.AglFloorFt)} AGL)";
    }

    private static VfrAltitudeReading Read(IMenuAircraft? aircraft)
    {
        if (aircraft is null)
        {
            return new VfrAltitudeReading(0, 0, 0);
        }

        double magnetic = aircraft.TrueTrack - MagneticDeclination.GetDeclination(aircraft.Position);
        double nearest = NavigationDatabase.InstanceOrNull?.FindNearestAirportElevation(aircraft.Position) ?? 0;
        return new VfrAltitudeReading(aircraft.AltitudeFeet, magnetic, nearest);
    }
}

/// <summary>
/// The Altitude… flyout's one menu row (<see cref="VfrAltitudeFlyout"/>): the title line, the Altitude box, the mode toggle, the
/// hint, the value header and the thousands and hundreds columns.
/// </summary>
public sealed class VfrAltitudePanel : StackPanel, IFlyoutPanel
{
    /// <summary>The style class of a cell whose altitude is a highlighted VFR cruising altitude, drawn outlined.</summary>
    public const string CruiseClass = "cruise";

    /// <summary>The style class of the thousand holding the current altitude.</summary>
    public const string NowClass = "now";

    /// <summary>The style class of the picked thousand.</summary>
    public const string PickedClass = "picked";

    private static readonly IImmutableSolidColorBrush OutlineBrush = new ImmutableSolidColorBrush(Color.Parse("#8FC1FF"));
    private static readonly IImmutableSolidColorBrush PickedFill = new ImmutableSolidColorBrush(Color.Parse("#24406A"));
    private static readonly IImmutableSolidColorBrush HoverFill = new ImmutableSolidColorBrush(Color.Parse("#16FFFFFF"));
    private static readonly IImmutableSolidColorBrush PressedFill = new ImmutableSolidColorBrush(Color.Parse("#24FFFFFF"));
    private static readonly IImmutableSolidColorBrush PreviewBrush = new ImmutableSolidColorBrush(Color.Parse("#9AA3AD"));

    private readonly int _floor;
    private readonly Func<VfrAltitudeReading> _read;
    private readonly Func<string, Task> _choose;
    private readonly List<int> _thousands = [];
    private readonly List<(Button Button, Cell Parts)> _thousandCells = [];
    private readonly List<(Button Button, Cell Parts)> _hundredCells = [];
    private readonly List<ToggleButton> _toggles = [];
    private readonly TextBlock _titleNow = new() { FontSize = 11, Foreground = FlyoutPanelParts.TitleBrush };
    private readonly TextBlock _hint = new() { FontSize = 11, Foreground = FlyoutPanelParts.TitleBrush };
    private VfrAltitudeReading _reading;
    private int? _hovered;

    internal VfrAltitudePanel(double fieldElevation, Func<VfrAltitudeReading> read, Func<string, Task> choose)
    {
        _floor = VfrAltitudeFlyout.FloorFeet(fieldElevation);
        _read = read;
        _choose = choose;
        MaxWidth = MenuCommandRow.RowMaxWidth;
        MinWidth = 344;
        var title = new TextBlock
        {
            Text = "Altitude",
            FontSize = 11,
            Foreground = FlyoutPanelParts.TitleBrush,
        };
        Children.Add(FlyoutPanelParts.TitleLine(title, _titleNow));
        Box = AltitudePanelParts.Box(AltitudeFlyout.BoxPlaceholder, 150);
        Box.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
            {
                ShowBoxCommand();
            }
        };
        Children.Add(AltitudePanelParts.BoxRow(AltitudePanelParts.MaintainGlyph(), Box, BoxCommandText));
        foreach (VfrAltitudeMode mode in Enum.GetValues<VfrAltitudeMode>())
        {
            _toggles.Add(
                FlyoutPanelParts.VerbToggle(
                    VfrAltitudeFlyout.Label(mode),
                    VfrAltitudeFlyout.Code(mode),
                    mode == VfrAltitudeMode.Maintain,
                    () => SetMode(mode)
                )
            );
        }

        Children.Add(FlyoutPanelParts.ToggleStrip(_toggles));
        Children.Add(HintRow());
        Children.Add(FlyoutPanelParts.SeparatorLine());
        Children.Add(HeaderRow());
        Children.Add(Columns(VfrAltitudeFlyout.LowThousand(fieldElevation)));
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        _reading = _read();
        SetMode(VfrAltitudeMode.Maintain);
    }

    /// <summary>The Altitude box: Enter sends its altitude in the current mode.</summary>
    public TextBox Box { get; }

    /// <summary>The command column beside the box: what Enter sends, the verb with <c>···</c> while empty, or the refusal.</summary>
    public TextBlock BoxCommandText { get; } = new();

    /// <summary>The command the box sends now; null while it holds no altitude the flyout offers.</summary>
    public string? BoxCommand { get; private set; }

    /// <summary>The value header's value: the picked thousand plus the hundred under the pointer, or <c>—</c> before a pick.</summary>
    public TextBlock HeaderValue { get; } = new() { FontSize = 20, FontWeight = FontWeight.SemiBold };

    /// <summary>The value header's mode note: <c>maintain</c>, <c>at or above</c> or <c>at or below</c>.</summary>
    public TextBlock HeaderNote { get; } = new() { FontSize = 11.5, Foreground = FlyoutPanelParts.TitleBrush };

    /// <summary>The value header's command: what the header's value sends, or the verb with <c>···</c> before a pick.</summary>
    public TextBlock HeaderCommand { get; } = new() { FontSize = 12, Opacity = 0.85 };

    /// <summary>The value header as one line: <c>5,000 at or above · CM A5000</c>.</summary>
    public string Header => $"{HeaderValue.Text} {HeaderNote.Text} · {HeaderCommand.Text}";

    /// <summary>The title line's right side: <c>now 4,200 · 095°</c>.</summary>
    public string TitleNow => _titleNow.Text ?? "";

    /// <summary>The cruising-altitude hint over the columns.</summary>
    public string HintText => _hint.Text ?? "";

    /// <summary>How the picked altitude is held now, which the toggle sets.</summary>
    public VfrAltitudeMode Mode { get; private set; }

    /// <summary>The picked thousand; null before a thousand is picked.</summary>
    public int? PickedThousand { get; private set; }

    /// <summary>The listed thousands, highest first.</summary>
    public IReadOnlyList<int> Thousands => _thousands;

    /// <summary>The toggle button of <paramref name="mode"/>.</summary>
    public ToggleButton Toggle(VfrAltitudeMode mode) => _toggles[(int)mode];

    /// <summary>The cell of <paramref name="thousand"/> (5 for 5,000).</summary>
    public Button ThousandCell(int thousand)
    {
        int index = _thousands.IndexOf(thousand);
        return (index >= 0)
            ? _thousandCells[index].Button
            : throw new ArgumentOutOfRangeException(nameof(thousand), thousand, "No cell lists this thousand.");
    }

    /// <summary>The cell of <paramref name="hundred"/> (500 for 500).</summary>
    public Button HundredCell(int hundred) => _hundredCells[hundred / 100].Button;

    Control IFlyoutPanel.View => this;

    void IFlyoutPanel.Reset()
    {
        Box.Text = "";
        PickedThousand = null;
        _hovered = null;
        SetMode(VfrAltitudeMode.Maintain);
    }

    void IFlyoutPanel.Refresh()
    {
        _reading = _read();
        Present();
    }

    void IFlyoutPanel.FocusOpeningLater(Func<bool> isOpen) => FlyoutPanelParts.FocusLater(Box, isOpen, BringNowIntoView);

    private Grid HintRow()
    {
        var swatch = new Border
        {
            Width = 22,
            Height = 12,
            BorderBrush = OutlineBrush,
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(3),
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(11, 3, 11, 2) };
        row.Children.Add(swatch);
        _hint.TextWrapping = TextWrapping.Wrap;
        Grid.SetColumn(_hint, 1);
        row.Children.Add(_hint);
        return row;
    }

    private Grid HeaderRow()
    {
        FlyoutPanelParts.Mono(HeaderValue);
        FlyoutPanelParts.Mono(HeaderCommand);
        HeaderNote.Margin = new Thickness(8, 0, 0, 0);
        HeaderNote.VerticalAlignment = VerticalAlignment.Bottom;
        var value = new StackPanel { Orientation = Orientation.Horizontal, Children = { HeaderValue, HeaderNote } };
        HeaderCommand.HorizontalAlignment = HorizontalAlignment.Right;
        HeaderCommand.VerticalAlignment = VerticalAlignment.Bottom;
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(11, 4, 11, 2) };
        row.Children.Add(value);
        Grid.SetColumn(HeaderCommand, 1);
        row.Children.Add(HeaderCommand);
        return row;
    }

    /// <summary>The thousands, from <see cref="VfrAltitudeFlyout.TopThousand"/> down to <paramref name="low"/>, and the hundreds.</summary>
    private Grid Columns(int low)
    {
        var thousands = new StackPanel();
        for (int thousand = VfrAltitudeFlyout.TopThousand; thousand >= low; thousand--)
        {
            int picked = thousand;
            (Button button, Cell parts) = CellButton(() => Pick(picked));
            AutomationProperties.SetName(button, VfrAltitudeFlyout.Feet(thousand * 1000));
            _thousands.Add(thousand);
            _thousandCells.Add((button, parts));
            thousands.Children.Add(button);
        }

        var hundreds = new StackPanel();
        for (int hundred = 0; hundred <= 900; hundred += 100)
        {
            int sent = hundred;
            (Button button, Cell parts) = CellButton(async () => await SendHundred(sent));
            button.PointerEntered += (_, _) => Hover(sent);
            button.PointerExited += (_, _) => Hover(null);
            parts.Value.Text = hundred.ToString("D3", CultureInfo.InvariantCulture);
            _hundredCells.Add((button, parts));
            hundreds.Children.Add(button);
        }

        var scroll = new ScrollViewer
        {
            Content = thousands,
            MaxHeight = 288,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        AutomationProperties.SetName(scroll, "Thousands");
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,8,*"), Margin = new Thickness(6, 2, 6, 4) };
        grid.Children.Add(Column("Thousands", scroll));
        Control right = Column("Hundreds · sends", hundreds);
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);
        return grid;
    }

    private static StackPanel Column(string heading, Control content) =>
        new()
        {
            Children =
            {
                new TextBlock
                {
                    Text = heading,
                    FontSize = 11,
                    Foreground = FlyoutPanelParts.TitleBrush,
                    Margin = new Thickness(4, 0, 4, 2),
                },
                content,
            },
        };

    /// <summary>One cell: the mark, the value and the note, in the outline a cruising altitude draws.</summary>
    private static (Button Button, Cell Parts) CellButton(Func<Task> onClick)
    {
        var parts = new Cell();
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("16,*,Auto") };
        parts.Glyph.FontSize = 12;
        parts.Glyph.HorizontalAlignment = HorizontalAlignment.Center;
        parts.Glyph.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(parts.Glyph);
        parts.Value.FontSize = 13;
        parts.Value.VerticalAlignment = VerticalAlignment.Center;
        FlyoutPanelParts.Mono(parts.Value);
        Grid.SetColumn(parts.Value, 1);
        grid.Children.Add(parts.Value);
        parts.Note.FontSize = 11;
        parts.Note.Margin = new Thickness(6, 0, 0, 0);
        parts.Note.VerticalAlignment = VerticalAlignment.Center;
        FlyoutPanelParts.Mono(parts.Note);
        Grid.SetColumn(parts.Note, 2);
        grid.Children.Add(parts.Note);
        parts.Fill.Child = grid;
        parts.Outline.Child = parts.Fill;
        var button = new Button
        {
            Content = parts.Outline,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        button.Resources["ButtonBackgroundPointerOver"] = HoverFill;
        button.Resources["ButtonBackgroundPressed"] = PressedFill;
        button.Resources["ButtonBackgroundDisabled"] = Brushes.Transparent;
        button.Click += async (_, e) =>
        {
            e.Handled = true;
            await onClick();
        };
        return (button, parts);
    }

    private void SetMode(VfrAltitudeMode mode)
    {
        Mode = mode;
        foreach (VfrAltitudeMode each in Enum.GetValues<VfrAltitudeMode>())
        {
            Toggle(each).IsChecked = each == mode;
        }

        Present();
    }

    private Task Pick(int thousand)
    {
        PickedThousand = thousand;
        Present();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Shows the header for <paramref name="hundred"/> under the pointer (null when it leaves); a test calls it in the pointer's place.
    /// </summary>
    internal void Hover(int? hundred)
    {
        _hovered = hundred;
        ShowHeader();
    }

    /// <summary>Sends the picked thousand plus <paramref name="hundred"/> in the current mode; nothing before a pick or when disabled.</summary>
    private Task SendHundred(int hundred)
    {
        if ((PickedThousand is not { } thousand) || !IsOffered((thousand * 1000) + hundred))
        {
            return Task.CompletedTask;
        }

        return _choose(VfrAltitudeFlyout.Command(Mode, (thousand * 1000) + hundred, _reading.Current));
    }

    private bool IsOffered(int feet) => (feet >= _floor) && (feet <= VfrAltitudeFlyout.TopFeet);

    /// <summary>Shows the reading and the picks: the title, the hint, every cell, the header and the box's command.</summary>
    private void Present()
    {
        _titleNow.Text = $"now {AltitudeFlyout.PresentText(_reading.Current)} · {VfrAltitudeFlyout.Course(_reading.MagneticTrack)}°";
        _hint.Text = VfrAltitudeFlyout.Hint(_reading);
        int now = (int)Math.Floor(_reading.Current / 1000);
        for (int i = 0; i < _thousands.Count; i++)
        {
            ShowThousand(_thousandCells[i].Button, _thousandCells[i].Parts, _thousands[i], _thousands[i] == now);
        }

        for (int i = 0; i < _hundredCells.Count; i++)
        {
            ShowHundred(_hundredCells[i].Button, _hundredCells[i].Parts, i * 100);
        }

        ShowHeader();
        ShowBoxCommand();
    }

    private void ShowThousand(Button button, Cell parts, int thousand, bool now)
    {
        bool cruise = VfrAltitudeFlyout.IsCruising(_reading, (thousand * 1000) + 500);
        bool picked = thousand == PickedThousand;
        button.Classes.Set(CruiseClass, cruise);
        button.Classes.Set(NowClass, now);
        button.Classes.Set(PickedClass, picked);
        parts.Outline.BorderBrush = cruise ? OutlineBrush : Brushes.Transparent;
        parts.Fill.Background = picked ? PickedFill : (now ? FlyoutPanelParts.NowFill : Brushes.Transparent);
        parts.Fill.BorderBrush = now ? FlyoutPanelParts.NowEdge : Brushes.Transparent;
        parts.Glyph.Text = now ? "●" : "";
        parts.Value.Text = VfrAltitudeFlyout.Feet(thousand * 1000);
        parts.Value.Foreground = AltitudePanelParts.ValueBrush;
        parts.Value.FontWeight = now ? FontWeight.SemiBold : FontWeight.Normal;
        parts.Note.Text = now ? $"now {AltitudeFlyout.PresentText(_reading.Current)}" : "";
        parts.Note.Foreground = FlyoutPanelParts.TitleBrush;
    }

    private void ShowHundred(Button button, Cell parts, int hundred)
    {
        HundredState state = HundredStateOf(hundred);
        button.IsEnabled = state.Offered;
        button.Classes.Set(CruiseClass, state.Cruise);
        parts.Outline.BorderBrush = state.Cruise ? OutlineBrush : Brushes.Transparent;
        parts.Value.Foreground = state.Offered ? AltitudePanelParts.ValueBrush : AltitudePanelParts.GreyedBrush;
        parts.Note.Text = state.Preview;
        parts.Note.Foreground = state.Offered ? PreviewBrush : AltitudePanelParts.GreyedBrush;
        MenuCommandText.SetCommand(button, state.Command);
        string label = hundred.ToString("D3", CultureInfo.InvariantCulture);
        AutomationProperties.SetName(button, (state.Command is null) ? label : $"{label} — {state.Command}");
    }

    /// <summary>What the cell of <paramref name="hundred"/> shows under the picked thousand: nothing offered before a pick.</summary>
    private HundredState HundredStateOf(int hundred)
    {
        if (PickedThousand is not { } thousand)
        {
            return new HundredState("", false, false, null);
        }

        int feet = (thousand * 1000) + hundred;
        bool offered = IsOffered(feet);
        string? command = offered ? VfrAltitudeFlyout.Command(Mode, feet, _reading.Current) : null;
        return new HundredState(VfrAltitudeFlyout.Feet(feet), offered, VfrAltitudeFlyout.IsCruising(_reading, feet), command);
    }

    /// <summary>
    /// The header: the picked thousand plus the hovered hundred and its command; <c>—</c> and the verb with <c>···</c> before a
    /// pick, or while that value is not offered (below the floor or above 17,500 ft).
    /// </summary>
    private void ShowHeader()
    {
        HeaderNote.Text = VfrAltitudeFlyout.Label(Mode).ToLowerInvariant();
        int? feet = (PickedThousand is { } thousand) ? (thousand * 1000) + (_hovered ?? 0) : null;
        if ((feet is not { } value) || !IsOffered(value))
        {
            HeaderValue.Text = "—";
            HeaderCommand.Text = $"{VfrAltitudeFlyout.Verb(Mode)} {TakeoffFlyout.Unfilled}";
            return;
        }

        HeaderValue.Text = VfrAltitudeFlyout.Feet(value);
        HeaderCommand.Text = VfrAltitudeFlyout.Command(Mode, value, _reading.Current);
    }

    /// <summary>
    /// The box's command: the typed altitude in the current mode while the flyout offers it, the verb with <c>···</c> while
    /// blank, else the refusal (<see cref="AltitudeFlyout.InvalidAltitude"/>, or the range offered).
    /// </summary>
    private void ShowBoxCommand()
    {
        string text = Box.Text ?? "";
        int? feet = MenuCatalog.ParseAltitudeFeet(text);
        BoxCommand = BoxCommandFor(feet);
        bool blank = string.IsNullOrWhiteSpace(text);
        BoxCommandText.Text = BoxCommand ?? (blank ? $"{VfrAltitudeFlyout.Verb(Mode)} {TakeoffFlyout.Unfilled}" : BoxRefusal(feet));
        if ((BoxCommand is null) && !blank)
        {
            BoxCommandText.Foreground = FlyoutPanelParts.RefusalBrush;
        }
        else
        {
            BoxCommandText.ClearValue(TextBlock.ForegroundProperty);
        }

        BoxCommandText.Opacity = BoxCommandOpacity(BoxCommand is not null, blank);
        MenuCommandText.SetCommand(Box, BoxCommand);
    }

    /// <summary>The command for the typed <paramref name="feet"/> in the current mode; null for no altitude or one not offered.</summary>
    private string? BoxCommandFor(int? feet) =>
        ((feet is { } typed) && IsOffered(typed)) ? VfrAltitudeFlyout.Command(Mode, typed, _reading.Current) : null;

    /// <summary>Why the box sends nothing: no altitude (<see cref="AltitudeFlyout.InvalidAltitude"/>), else the range offered.</summary>
    private string BoxRefusal(int? feet) =>
        (feet is null)
            ? AltitudeFlyout.InvalidAltitude
            : $"{VfrAltitudeFlyout.Feet(_floor)} to {VfrAltitudeFlyout.Feet(VfrAltitudeFlyout.TopFeet)} only";

    /// <summary>The box command's opacity: a command's 0.7, dimmed further while blank, full for a refusal.</summary>
    private static double BoxCommandOpacity(bool sends, bool blank)
    {
        if (sends)
        {
            return 0.7;
        }

        return blank ? 0.7 * MenuGlyphRowTemplate.DimmedOpacity : 1;
    }

    private void BringNowIntoView()
    {
        int now = _thousands.IndexOf((int)Math.Floor(_reading.Current / 1000));
        if (now >= 0)
        {
            _thousandCells[now].Button.BringIntoView();
        }
    }

    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if ((e.Key != Key.Enter) || !Box.IsKeyboardFocusWithin)
        {
            return;
        }

        e.Handled = true;
        if (BoxCommand is { } command)
        {
            await _choose(command);
        }
    }

    /// <summary>The parts of one cell that each refresh rewrites.</summary>
    /// <summary>A hundred cell under the picked thousand: its full-altitude preview, whether it is offered and outlined, and its command.</summary>
    private sealed record HundredState(string Preview, bool Offered, bool Cruise, string? Command);

    private sealed class Cell
    {
        public TextBlock Glyph { get; } = new();

        public TextBlock Value { get; } = new();

        public TextBlock Note { get; } = new();

        public Border Fill { get; } =
            new()
            {
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(3, 0, 0, 0),
                Padding = new Thickness(5, 1, 9, 1),
                MinHeight = 24,
            };

        public Border Outline { get; } =
            new()
            {
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(2),
                BorderBrush = Brushes.Transparent,
            };
    }
}
